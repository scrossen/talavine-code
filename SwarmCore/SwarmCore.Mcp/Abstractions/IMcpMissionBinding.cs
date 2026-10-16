using SwarmCore.Abstractions.Security;
using SwarmCore.Mcp.Models;

namespace SwarmCore.Mcp.Abstractions;

/// <summary>Trusted host contract for a connector's concrete resources/effects. Remote MCP
/// annotations and tool-supplied claims are not authorization. Null means no binding.</summary>
public interface IMcpMissionBinding
{
    IReadOnlyList<ExecutionResourceAction>? Resolve(McpConnectorOptions connector, string? tool,
        IReadOnlyDictionary<string, object?> arguments);
}
