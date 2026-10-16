using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Coding.Planning;

/// <summary>
/// Plan mode: the person asked the agent to investigate read-only and propose a plan before changing anything. It is
/// host state on <see cref="Routing.CodingTurnRequest.PlanMode"/>, never model output. Engine lanes carry it as
/// <see cref="MetadataKey"/> so <see cref="CodingPlanModeToolFilter"/> narrows the turn's tools; Claude Code runs with
/// <c>--permission-mode plan</c>; native lanes without a verified read-only mode are skipped for the turn.
/// </summary>
public static class CodingPlanMode
{
    public const string MetadataKey = "coding.plan-mode.v1";

    /// <summary>Why a native lane is passed over for a plan-mode request.</summary>
    public const string NativeLaneCannotPlan = "this native agent has no verified read-only plan mode";

    public static IReadOnlyDictionary<string, string> Metadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var copy = metadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        copy[MetadataKey] = "true";
        return copy;
    }

    public static bool IsActive(IReadOnlyDictionary<string, string>? metadata) => metadata?.ContainsKey(MetadataKey) == true;

    /// <summary>The host's framing of a plan-mode request; the person's own words stay the authenticated original.</summary>
    public static string FrameRequest(string message)
        => "[Plan mode] Investigate read-only and propose a plan; do not change anything yet. Only read-only tools are available "
           + "(file edits, commands and other side effects are blocked). Read the relevant code, record the steps in the task list (plan.update, or your own todo tool) "
           + "(all pending), then reply with a concise plan: goal, the files and changes you intend, how you will verify, and any open "
           + "questions. Stop after the plan; the person will approve it or keep discussing.\n\nRequest:\n" + message;

    /// <summary>The continuation the host sends when the person approves the plan.</summary>
    public const string ApprovalMessage = "Plan approved. Implement the approved plan now, step by step, updating the task list as you go, and verify the result.";

    /// <summary>Plan-mode admission: statically read-only tools with no input-dependent side effect, plus the task list.</summary>
    public static bool Admits(ToolDescriptor tool)
        => tool.ToolId is PlanTools.UpdateToolId or PlanTools.ReadToolId
           || (tool.SideEffect == ToolSideEffect.ReadOnly && tool.ResolveSideEffect is null);
}

/// <summary>Removes every side-effecting tool from a plan-mode engine turn (see <see cref="CodingPlanMode"/>).</summary>
public sealed class CodingPlanModeToolFilter : IAgentTurnToolFilter
{
    public ValueTask<IReadOnlyList<ToolDescriptor>> FilterAsync(AgentTurnRequest request, AgentProfileSnapshot profile,
        IReadOnlyList<ToolDescriptor> tools, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(CodingPlanMode.IsActive(request.Metadata)
            ? tools.Where(CodingPlanMode.Admits).ToList() : tools);
}
