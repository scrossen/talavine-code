namespace SwarmCore.Mcp.Models;

/// <summary>
/// Result of invoking an MCP tool.
/// </summary>
public record McpToolResult(
    bool IsError,
    string Content);
