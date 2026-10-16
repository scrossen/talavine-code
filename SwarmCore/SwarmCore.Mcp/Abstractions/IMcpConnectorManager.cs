using SwarmCore.Mcp.Models;

namespace SwarmCore.Mcp.Abstractions;

/// <summary>
/// Manages connections to external MCP (Model Context Protocol) servers.
/// Singleton service — maintains long-lived connections to MCP server subprocesses.
/// </summary>
public interface IMcpConnectorManager : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Connect to an MCP server using the provided options.
    /// Returns a unique connection ID for subsequent operations.
    /// </summary>
    Task<Guid> ConnectAsync(McpConnectorOptions options, CancellationToken ct = default);

    /// <summary>
    /// Disconnect from an MCP server and clean up its subprocess.
    /// </summary>
    Task DisconnectAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Get the current state of all connections. Entries whose transport has died
    /// report <see cref="McpConnectionInfo.IsConnected"/> = false.
    /// </summary>
    Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// List all tools available from a connected MCP server.
    /// </summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Invoke a tool on a connected MCP server.
    /// </summary>
    Task<McpToolResult> InvokeToolAsync(
        Guid connectionId,
        string toolName,
        IDictionary<string, object?>? arguments = null,
        CancellationToken ct = default);

    /// <summary>
    /// Raised when a connection is added, removed, detected dead, or when a server
    /// announces that its tool list changed. Consumers caching bridged tools should
    /// invalidate on this event.
    /// </summary>
    event EventHandler<McpConnectionChangedEventArgs>? ConnectionsChanged;
}
