using SwarmCore.Abstractions.AgentWork;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Business;

/// <summary>Host-registered postcondition kind and its required first tool identity.</summary>
public sealed record OperatorWorkResultTool(string Kind, string ToolId);

/// <summary>Task-specific effect limit. Hosts must also enforce live grants, scope and transport fences.</summary>
public static class BusinessOperatorWorkPolicy
{
    /// <summary>
    /// Select only from a host-supplied, currently admitted catalog. This maps a required
    /// result to a tool the work already permits; it neither grants a tool nor proves its result.
    /// </summary>
    public static ToolDescriptor? SelectRequiredFirstTool(OperatorWorkContract work,
        IReadOnlyList<ToolDescriptor> liveAdmittedTools, IReadOnlyList<OperatorWorkResultTool> hostResults)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(liveAdmittedTools);
        ArgumentNullException.ThrowIfNull(hostResults);
        var mappings = hostResults.Where(r => r is not null
            && string.Equals(r.Kind, work.RequiredLocalResult, StringComparison.Ordinal)).Take(2).ToArray();
        if (mappings.Length != 1 || string.IsNullOrWhiteSpace(mappings[0].ToolId)) return null;
        var candidates = liveAdmittedTools.Where(t => t is not null
            && string.Equals(t.ToolId, mappings[0].ToolId, StringComparison.Ordinal)).Take(2).ToArray();
        if (candidates.Length != 1) return null;
        var tool = candidates[0];
        if (tool.ResolveSideEffect is not null || !Enum.IsDefined(tool.SideEffect)
            || !AllowsDiscovery(work, tool)) return null;
        return work.AllowedEffects.Any(e => e.ToolId == tool.ToolId
            && e.SideEffect == tool.SideEffect.ToString()) ? tool : null;
    }

    public static bool AllowsDiscovery(OperatorWorkContract work, ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(tool);
        return Enum.IsDefined(tool.SideEffect)
            && work.AllowedEffects.Any(e => e.ToolId == tool.ToolId
                && (tool.ResolveSideEffect is not null || e.SideEffect == tool.SideEffect.ToString()));
    }

    /// <summary>
    /// Use at direct dispatch, procedure replay and host validation. The host resolves effectId,
    /// targetId and argument validity from trusted tool metadata, never from model prose.
    /// </summary>
    public static bool AllowsInvocation(OperatorWorkContract work, ToolDescriptor tool, object input,
        string effectId, string targetId, bool argumentsVerified, bool currentGrantAllows,
        bool globalFenceAllows)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(tool);
        if (input is null || !argumentsVerified || !currentGrantAllows || !globalFenceAllows
            || string.IsNullOrWhiteSpace(effectId) || string.IsNullOrWhiteSpace(targetId))
            return false;
        ToolSideEffect effective;
        try { effective = tool.EffectiveSideEffect(input); }
        catch { return false; }
        if (!Enum.IsDefined(effective)) return false;
        return work.AllowedEffects.Any(e => e.ToolId == tool.ToolId
            && e.EffectId == effectId && e.TargetId == targetId
            && e.SideEffect == effective.ToString());
    }
}
