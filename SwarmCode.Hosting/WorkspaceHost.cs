using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCode.Hosting;

/// <summary>
/// The runtime for one repository: its own service provider built by
/// <see cref="SwarmCodeHostComposer.AddSwarmCodeHost"/>, with hosted services started.
/// The sandbox root and lane configuration are fixed per host, so switching repositories
/// or changing lanes means building a new one.
/// </summary>
public sealed class WorkspaceHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IHostedService> _started = [];
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);

    private WorkspaceHost(string repositoryRoot, ServiceProvider provider)
    {
        RepositoryRoot = repositoryRoot;
        _provider = provider;
    }

    public string RepositoryRoot { get; }

    public IServiceProvider Services => _provider;

    public IReadOnlyList<CodingLane> Lanes => _provider.GetRequiredService<ICodingTurnOrchestrator>().Lanes;

    public CodingLaneOptions LaneOptions => _provider.GetRequiredService<IOptions<CodingLaneOptions>>().Value;

    public CodingOptions CodingOptions => _provider.GetRequiredService<IOptions<CodingOptions>>().Value;

    public static async Task<WorkspaceHost> CreateAsync(SwarmCodeHostOptions options, Action<ILoggingBuilder>? configureLogging = null, CancellationToken ct = default)
    {
        var repoRoot = Path.GetFullPath(options.RepositoryRoot);
        if (!Directory.Exists(repoRoot)) throw new DirectoryNotFoundException($"Repository path does not exist: {repoRoot}");

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (options.Configuration is not null) b.AddConfiguration(options.Configuration.GetSection("Logging"));
            configureLogging?.Invoke(b);
        });
        services.AddSwarmCodeHost(options);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new WorkspaceHost(repoRoot, provider);
        try
        {
            await host.DiscoverLocalModelsAsync(ct).ConfigureAwait(false);
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(ct).ConfigureAwait(false);
                host._started.Add(hosted);
            }
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return host;
    }

    public async Task DiscoverLocalModelsAsync(CancellationToken ct = default)
    {
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LocalCodingModelDiscovery.RefreshAsync(_provider, ct).ConfigureAwait(false);
        }
        finally { _discoveryGate.Release(); }
    }

    /// <summary>Creates a runner for a session in this workspace. Dispose it when the session closes.</summary>
    public ConversationRunner OpenSession(string conversationId) => new(_provider, conversationId);

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in Enumerable.Reverse(_started))
        {
            try { await hosted.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
        }
        _started.Clear();
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Holds the active workspace for an interactive host and swaps it when the repository changes.</summary>
public sealed class WorkspaceHostManager(SwarmCodeDataPaths paths, Func<string, SwarmCodeHostOptions> optionsFactory, Action<ILoggingBuilder>? configureLogging = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceHost? Current { get; private set; }

    public SwarmCodeDataPaths DataPaths { get; } = paths;

    public event Action? Changed;

    /// <summary>Opens the repository, replacing the current workspace. Reopening the same root rebuilds it (used after settings changes).</summary>
    public async Task<WorkspaceHost> OpenAsync(string repositoryRoot, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = await WorkspaceHost.CreateAsync(optionsFactory(repositoryRoot), configureLogging, ct).ConfigureAwait(false);
            var previous = Current;
            Current = next;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = Current;
            Current = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
