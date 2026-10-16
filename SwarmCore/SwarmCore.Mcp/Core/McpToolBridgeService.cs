using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Models;
using SwarmCore.Skills.Abstractions;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Mcp.Core;

/// <summary>
/// Bridges all connected MCP tools into the SwarmCore.Skills system.
/// Singleton — caches bridged skills, invalidates whenever the connector manager
/// reports a connection or tool-list change, and refreshes on demand.
/// </summary>
public class McpToolBridgeService : IMcpToolBridge, IDisposable
{
    /// <summary>
    /// How long an empty discovery result is trusted before the next call
    /// re-queries the connector manager. Keeps a host with zero servers from
    /// paying a full refresh on every lookup, without hiding a fresh connect
    /// for long (connects also invalidate via <see cref="IMcpConnectorManager.ConnectionsChanged"/>).
    /// </summary>
    internal static readonly TimeSpan EmptyCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IMcpConnectorManager _connectorManager;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly ILogger<McpToolBridgeService> _logger;
    private readonly object _lock = new();
    private List<ISkill>? _cachedSkills;
    private DateTime _cachedAtUtc;
    private readonly TimeProvider _clock;

    public McpToolBridgeService(
        IMcpConnectorManager connectorManager,
        ILogger<McpToolBridgeService> logger,
        ISkillRegistry? skillRegistry = null)
        : this(connectorManager, logger, skillRegistry, TimeProvider.System)
    {
    }

    internal McpToolBridgeService(
        IMcpConnectorManager connectorManager,
        ILogger<McpToolBridgeService> logger,
        ISkillRegistry? skillRegistry,
        TimeProvider clock)
    {
        _connectorManager = connectorManager;
        _logger = logger;
        _skillRegistry = skillRegistry;
        _clock = clock;
        _connectorManager.ConnectionsChanged += OnConnectionsChanged;
    }

    public async Task<IReadOnlyList<ISkill>> GetBridgedSkillsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cachedSkills is { } cached
                && (cached.Count > 0 || _clock.GetUtcNow().UtcDateTime - _cachedAtUtc < EmptyCacheTtl))
            {
                return cached;
            }
        }

        await RefreshAsync(ct);

        lock (_lock)
        {
            return _cachedSkills ?? [];
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var skills = new List<ISkill>();

        try
        {
            var connections = await _connectorManager.GetConnectionsAsync(ct);

            foreach (var connection in connections.Where(c => c.IsConnected))
            {
                try
                {
                    var tools = await _connectorManager.ListToolsAsync(connection.ConnectionId, ct);

                    foreach (var tool in tools)
                    {
                        WarnOnNativeNameCollision(connection.Name, tool.Name);
                        skills.Add(new McpBridgedSkill(connection.ConnectionId, tool, _connectorManager, connection.Name));
                    }

                    _logger.LogDebug(
                        "Bridged {Count} tools from MCP server '{Name}'",
                        tools.Count, connection.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to list tools from MCP server '{Name}'", connection.Name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh MCP tool bridge");
        }

        lock (_lock)
        {
            _cachedSkills = skills;
            _cachedAtUtc = _clock.GetUtcNow().UtcDateTime;
        }

        _logger.LogInformation("MCP tool bridge refreshed: {Count} total bridged skills", skills.Count);
    }

    /// <summary>Drops the cache so the next lookup re-discovers tools.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedSkills = null;
        }
    }

    public void Dispose()
    {
        _connectorManager.ConnectionsChanged -= OnConnectionsChanged;
    }

    private void OnConnectionsChanged(object? sender, McpConnectionChangedEventArgs e)
    {
        _logger.LogDebug(
            "MCP connection change ({Kind}) on '{Name}' — invalidating bridged skill cache",
            e.Kind, e.Name);
        Invalidate();
    }

    private void WarnOnNativeNameCollision(string connectorName, string toolName)
    {
        if (_skillRegistry is null) return;

        IReadOnlyList<ISkill> native;
        try
        {
            native = _skillRegistry.GetAllSkills();
        }
        catch
        {
            return;
        }

        foreach (var skill in native)
        {
            if (skill is McpBridgedSkill) continue;
            var clashesOnName = string.Equals(skill.Name, toolName, StringComparison.OrdinalIgnoreCase);
            var clashesOnFunction = false;
            try
            {
                clashesOnFunction = skill.GetFunctions()
                    .Any(f => string.Equals(f.Name, toolName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // A misbehaving native skill must not block bridging.
            }

            if (clashesOnName || clashesOnFunction)
            {
                _logger.LogWarning(
                    "MCP tool '{Tool}' from connector '{Connector}' shares its name with native skill '{SkillId}' ('{SkillName}'); the bridged skill is exposed as '{Bridged}'",
                    toolName, connectorName, skill.SkillId, skill.Name, $"{connectorName}:{toolName}");
                return;
            }
        }
    }
}
