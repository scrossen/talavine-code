using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;
namespace SwarmCore.Agent.Runtime;

/// <summary>Actual profile-narrowing pipeline shared by execution and read-only admission evidence.</summary>
public static class AgentTurnToolPolicyPipeline
{
    public static async ValueTask<IReadOnlyList<ToolDescriptor>> FilterAsync(
        IEnumerable<IAgentTurnToolFilter> filters, AgentTurnRequest request,
        AgentProfileSnapshot profile, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct = default)
    {
        if (UserToolUseRestriction.WithholdsAllTools(request)) return [];
        foreach (var filter in filters)
        {
            if (tools.Count == 0) break;
            var kept = (await filter.FilterAsync(request, profile, tools, ct) ?? [])
                .Select(t => t.ToolId).ToHashSet(StringComparer.Ordinal);
            // Keep the profile's own descriptors: a filter selects, it never substitutes or adds.
            tools = tools.Where(t => kept.Contains(t.ToolId)).ToList();
        }
        return tools;
    }

}
