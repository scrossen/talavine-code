namespace SwarmCore.Mcp.Models;

/// <summary>
/// Current state of an MCP server connection.
/// </summary>
public record McpConnectionInfo(
    Guid ConnectionId,
    string Name,
    string TransportType,
    bool IsConnected,
    DateTime? ConnectedAt,
    int ToolCount);
