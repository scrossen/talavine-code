using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Models;
using SwarmCore.Missions;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Mcp;

/// <summary>
/// Gives API lanes the tools of the external MCP servers the user configured
/// (<see cref="NativeAgentOptions.ExternalMcpServers"/>, the same list native lanes mount). Each
/// server is started once per workspace through SwarmCore's <see cref="IMcpConnectorManager"/> the
/// first time an API-lane turn resolves its profile, and its tools are published to the capability
/// catalog by <see cref="McpCatalogProjection"/> as <c>mcp.&lt;server&gt;.&lt;tool&gt;</c>. A server that
/// fails to start is logged and retried after <see cref="RetryAfter"/>; the turn proceeds without it.
/// A server whose process dies has its tools withdrawn until it is restarted. Disposing the
/// workspace's service provider disposes the connector manager, which stops the processes.
/// </summary>
public sealed class ExternalMcpToolCatalog : IDisposable
{
    /// <summary>How long one server may take to start and answer <c>tools/list</c>.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Per-call limit for a tool call. Some data-hub tools run a model per call.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(3);

    /// <summary>A server that failed to start is not retried before this much time has passed.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    private readonly IOptions<CodingOptions> _options;
    private readonly IMcpConnectorManager _connectors;
    private readonly ICapabilityCatalog _catalog;
    private readonly ILogger<ExternalMcpToolCatalog> _logger;
    private readonly TimeProvider _clock;
    private readonly ISecretVault? _vault;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ServerState> _servers = new(StringComparer.OrdinalIgnoreCase);

    public ExternalMcpToolCatalog(IOptions<CodingOptions> options, IMcpConnectorManager connectors, ICapabilityCatalog catalog,
        ILogger<ExternalMcpToolCatalog> logger, ISecretVault? vault = null)
        : this(options, connectors, catalog, logger, TimeProvider.System, vault) { }

    internal ExternalMcpToolCatalog(IOptions<CodingOptions> options, IMcpConnectorManager connectors, ICapabilityCatalog catalog,
        ILogger<ExternalMcpToolCatalog> logger, TimeProvider clock, ISecretVault? vault = null)
    {
        _options = options;
        _connectors = connectors;
        _catalog = catalog;
        _logger = logger;
        _clock = clock;
        _vault = vault;
        _connectors.ConnectionsChanged += OnConnectionsChanged;
    }

    private sealed class ServerState
    {
        public Guid? ConnectionId;
        public DateTimeOffset? FailedAt;
        public string? Problem;
        public IReadOnlyList<string> ToolIds = [];
    }

    /// <summary>One line per configured server: published tool count, or why it is unavailable.</summary>
    public IReadOnlyList<(string Server, int Tools, string? Problem)> Status()
    {
        lock (_servers)
            return _servers.Select(kv => (kv.Key, kv.Value.ToolIds.Count, kv.Value.Problem)).ToList();
    }

    /// <summary>
    /// Starts and publishes every configured server that is not already published. Safe to call
    /// on every turn: published servers cost a dictionary lookup.
    /// </summary>
    public async Task EnsurePublishedAsync(CancellationToken ct = default)
    {
        var configured = _options.Value.NativeAgents.ExternalMcpServers.Where(s => s.IsConfigured && ExternalMcpServersFile.IsValidName(s.ServerName)).ToList();
        if (configured.Count == 0 || AllCurrent(configured)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = configured.Where(NeedsStart).ToList();
            await Task.WhenAll(pending.Select(server => StartAsync(server, ct))).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private bool AllCurrent(List<McpServerLaunch> configured)
    {
        lock (_servers) return configured.All(s => !NeedsStartLocked(s));
    }

    private bool NeedsStart(McpServerLaunch server)
    {
        lock (_servers) return NeedsStartLocked(server);
    }

    private bool NeedsStartLocked(McpServerLaunch server)
    {
        if (!_servers.TryGetValue(server.ServerName, out var state)) return true;
        if (state.ConnectionId is not null) return false;
        return state.FailedAt is not { } failed || _clock.GetUtcNow() - failed >= RetryAfter;
    }

    private async Task StartAsync(McpServerLaunch server, CancellationToken ct)
    {
        var name = server.ServerName;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(StartupTimeout);
        Guid? connectionId = null;
        string? bearer = null;
        try
        {
            bearer = server.Url is null ? null : await McpHttpLaunch.ResolveBearerAsync(server, _vault, startup.Token).ConfigureAwait(false);
            connectionId = await _connectors.ConnectAsync(new McpConnectorOptions
            {
                Name = name,
                TransportType = server.Url is null ? McpTransportTypes.Stdio : McpTransportTypes.StreamableHttp,
                Url = server.Url,
                ApiKey = bearer,
                Command = server.Command,
                Arguments = [.. server.Arguments],
                EnvironmentVariables = new Dictionary<string, string>(server.Environment),
                CallTimeout = CallTimeout,
            }, startup.Token).ConfigureAwait(false);
            // Recorded before publishing so a server that dies mid-publish is seen by the Faulted handler and restarted.
            lock (_servers) _servers[name] = new ServerState { ConnectionId = connectionId };
            var published = await McpCatalogProjection.PublishAsync(_catalog, _connectors, connectionId.Value, name, startup.Token, server.Trusted,
                isCurrent: () => IsCurrent(name, connectionId.Value)).ConfigureAwait(false);
            bool stillCurrent;
            lock (_servers)
            {
                stillCurrent = _servers.TryGetValue(name, out var current) && current.ConnectionId == connectionId;
                if (stillCurrent) current!.ToolIds = published;
            }
            if (!stillCurrent)
            {
                // The process died while its tools were being published; the Faulted handler already scheduled a restart.
                McpCatalogProjection.Unpublish(_catalog, name, connectionId: connectionId);
                return;
            }
            _logger.LogInformation("External MCP server {Server} published {Count} tools", name, published.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisconnectQuietlyAsync(connectionId).ConfigureAwait(false);
            // The turn was cancelled mid-start: start again on the next turn.
            lock (_servers) _servers.Remove(name);
            McpCatalogProjection.Unpublish(_catalog, name, connectionId: connectionId);
            throw;
        }
        catch (Exception ex)
        {
            await DisconnectQuietlyAsync(connectionId).ConfigureAwait(false);
            var problem = ex is OperationCanceledException
                ? $"did not start within {StartupTimeout.TotalSeconds:0} seconds"
                : RedactCredential(ex.Message, bearer);
            lock (_servers) _servers[name] = new ServerState { FailedAt = _clock.GetUtcNow(), Problem = problem };
            McpCatalogProjection.Unpublish(_catalog, name, connectionId: connectionId);
            _logger.LogWarning("External MCP server {Server} is unavailable: {Problem}", name, problem);
        }
    }

    private static string RedactCredential(string message, string? bearer)
    {
        if (string.IsNullOrEmpty(bearer)) return message;
        foreach (var value in new[] { bearer, Uri.EscapeDataString(bearer), Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bearer)) }.Distinct())
            message = message.Replace(value, "[redacted]", StringComparison.Ordinal);
        return message;
    }

    private async Task DisconnectQuietlyAsync(Guid? connectionId)
    {
        if (connectionId is not { } id) return;
        try { await _connectors.DisconnectAsync(id).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not disconnect MCP connection {Id}", id); }
    }

    private void OnConnectionsChanged(object? sender, McpConnectionChangedEventArgs e)
    {
        if (e.Kind is not (McpConnectionChangeKind.Faulted or McpConnectionChangeKind.Disconnected or McpConnectionChangeKind.ToolsChanged)) return;
        lock (_servers)
        {
            if (!_servers.TryGetValue(e.Name, out var state) || state.ConnectionId != e.ConnectionId) return;
            if (e.Kind == McpConnectionChangeKind.ToolsChanged)
            {
                // Republish on the next turn from a fresh list.
                _ = Task.Run(() => RepublishAsync(e.Name, e.ConnectionId));
                return;
            }
            _servers[e.Name] = new ServerState { FailedAt = _clock.GetUtcNow() - RetryAfter, Problem = "the server process stopped" };
        }
        McpCatalogProjection.Unpublish(_catalog, e.Name, connectionId: e.ConnectionId);
        if (e.Kind == McpConnectionChangeKind.Faulted) _ = DisconnectQuietlyAsync(e.ConnectionId);
        _logger.LogWarning("External MCP server {Server} stopped; its tools were withdrawn until it restarts", e.Name);
    }

    private async Task RepublishAsync(string name, Guid connectionId)
    {
        try
        {
            var trusted = _options.Value.NativeAgents.ExternalMcpServers
                .Any(s => s.Trusted && string.Equals(s.ServerName, name, StringComparison.OrdinalIgnoreCase));
            var published = await McpCatalogProjection.PublishAsync(_catalog, _connectors, connectionId, name, trustedServer: trusted,
                isCurrent: () => IsCurrent(name, connectionId)).ConfigureAwait(false);
            lock (_servers)
                if (_servers.TryGetValue(name, out var state) && state.ConnectionId == connectionId) state.ToolIds = published;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not refresh the tools of external MCP server {Server}", name); }
    }

    public void Dispose() => _connectors.ConnectionsChanged -= OnConnectionsChanged;

    private bool IsCurrent(string name, Guid connectionId)
    {
        lock (_servers) return _servers.TryGetValue(name, out var state) && state.ConnectionId == connectionId;
    }
}

/// <summary>
/// Makes sure the external MCP servers' tools are in the catalog before the coding profile is
/// resolved for an API-lane turn. Native lanes never resolve a profile, so they never start
/// these processes here (the native agent starts its own copies).
/// </summary>
internal sealed class ExternalMcpProfileResolver(IProfileResolver inner, ExternalMcpToolCatalog external) : IProfileResolver
{
    /// <summary>The host or harness resolver this decorator wraps (exposed for composition tests).</summary>
    internal IProfileResolver Inner => inner;

    public async Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
    {
        // A native agent's bridge lookup: the CLI mounts these servers itself, so never start host copies for it.
        if (!SwarmCore.Coding.Native.HostToolBridgeScope.Active) await external.EnsurePublishedAsync(ct).ConfigureAwait(false);
        return await inner.ResolveAsync(conversationId, explicitProfileId, ct).ConfigureAwait(false);
    }
}
