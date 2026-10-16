using SwarmCore.Skills.Abstractions;

namespace SwarmCore.Mcp.Abstractions;

/// <summary>
/// Bridges MCP tools into the SwarmCore.Skills system so agents can discover and invoke them.
/// </summary>
public interface IMcpToolBridge
{
    /// <summary>
    /// Get all MCP tools as SwarmCore Skills for use in the agentic pipeline.
    /// </summary>
    Task<IReadOnlyList<ISkill>> GetBridgedSkillsAsync(CancellationToken ct = default);

    /// <summary>
    /// Refresh tool discovery from all connected MCP servers.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}
