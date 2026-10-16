namespace SwarmCore.Mcp.Models;

/// <summary>
/// Metadata about an MCP tool discovered from a connected server.
/// </summary>
/// <param name="Annotations">
/// The server's behavior hints (<c>annotations</c> in <c>tools/list</c>), or null when the
/// server sent none. Hints are server-authored claims, never authorization.
/// </param>
public record McpToolInfo(
    string Name,
    string? Description,
    string? InputSchemaJson,
    McpToolAnnotationsInfo? Annotations = null);

/// <summary>
/// The MCP tool annotation hints. Every value is optional; the MCP specification defines the
/// defaults when absent (<c>readOnlyHint</c> false, <c>destructiveHint</c> true,
/// <c>idempotentHint</c> false, <c>openWorldHint</c> true). Hosts may use these to classify a
/// tool's likely side effect, but a hint never grants or removes authority on its own.
/// </summary>
public sealed record McpToolAnnotationsInfo(
    string? Title = null,
    bool? ReadOnlyHint = null,
    bool? DestructiveHint = null,
    bool? IdempotentHint = null,
    bool? OpenWorldHint = null);
