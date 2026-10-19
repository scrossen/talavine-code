using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting;

/// <summary>Uses execution's provider registrations for a picker before a workspace is open.
/// No hosted services, agent turns, or workspace switching are started.</summary>
public sealed class ModelSelectionCatalog : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    public ModelSelectionCatalog(SwarmCodeHostOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(options);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public IReadOnlyList<CodingLane> Lanes => _services.GetRequiredService<ICodingLaneSource>().GetLanes().Lanes;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try { await LocalCodingModelDiscovery.RefreshAsync(_services, ct).ConfigureAwait(false); }
        finally { _discoveryGate.Release(); }
    }

    public string Provider(CodingLane lane)
    {
        using var scope = _services.CreateScope();
        return lane.IsNative
            ? scope.ServiceProvider.GetServices<INativeLaneRunner>().FirstOrDefault(r => r.Supports(lane))?.ModelCatalogProvider ?? lane.Provider
            : lane.Provider;
    }

    public async Task<LlmModelCatalog> GetModelsAsync(CodingLane lane, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodingModelCatalog>().GetCatalogAsync(lane, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
