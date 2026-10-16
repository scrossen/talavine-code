namespace SwarmCore.Mcp.Models;

/// <summary>
/// Kind of change reported by <see cref="McpConnectionChangedEventArgs"/>.
/// </summary>
public enum McpConnectionChangeKind
{
    /// <summary>A new connection was registered.</summary>
    Connected,

    /// <summary>A connection was removed via DisconnectAsync or dispose.</summary>
    Disconnected,

    /// <summary>The server's transport died; the entry now reports IsConnected=false.</summary>
    Faulted,

    /// <summary>The server sent notifications/tools/list_changed.</summary>
    ToolsChanged,
}

/// <summary>
/// Raised by <see cref="Abstractions.IMcpConnectorManager.ConnectionsChanged"/> whenever the
/// set of live connections, or the tool list behind one of them, changes. Consumers that
/// cache bridged tools should invalidate on every kind.
/// </summary>
public sealed class McpConnectionChangedEventArgs(Guid connectionId, string name, McpConnectionChangeKind kind) : EventArgs
{
    public Guid ConnectionId { get; } = connectionId;
    public string Name { get; } = name;
    public McpConnectionChangeKind Kind { get; } = kind;
}
