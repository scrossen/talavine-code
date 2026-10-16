using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Optional pre-invocation safety guard for the tool-use loop. The engine consults
/// the guard before running any tool whose effective side effect is not
/// <see cref="ToolSideEffect.ReadOnly"/>, and refuses the call when the guard vetoes it.
/// <para>
/// This is the seam where a host application plugs in content-level threat detection
/// (prompt-injection / data-exfiltration heuristics) without the engine knowing any
/// domain specifics. The guard runs <i>before</i> the approval prompt, so a clearly
/// malicious action is blocked outright rather than offered to the user to approve.
/// </para>
/// </summary>
public interface IToolInvocationGuard
{
    Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default);
}

/// <summary>A tool invocation the engine is about to run, presented to the guard.</summary>
/// <param name="ToolId">The tool's stable id.</param>
/// <param name="EffectiveSideEffect">The argument-resolved side effect that will gate it.</param>
/// <param name="Input">The deserialized tool input.</param>
/// <param name="ConversationId">The owning conversation/session id, if any.</param>
/// <param name="Transcript">The session transcript as of this call — lets a
/// guard compare the action's arguments against conversation provenance
/// (what the user actually typed vs. what arrived inside untrusted content).</param>
/// <param name="SystemPrompt">The effective system prompt for the turn,
/// including injected context blocks (fenced untrusted content can live here
/// too — e.g. a document-chat snapshot).</param>
public sealed record ToolInvocationRequest(
    string ToolId,
    ToolSideEffect EffectiveSideEffect,
    object Input,
    string? ConversationId,
    IReadOnlyList<ChatMessage>? Transcript = null,
    string? SystemPrompt = null);

/// <summary>
/// The guard's verdict for a single tool invocation. Three outcomes:
/// allow (run normally), block (refuse outright), or escalate
/// (<see cref="RequiresApproval"/>: run only after an explicit user approval —
/// even on a turn whose user-initiated authority would normally skip the
/// approval ceremony). Escalation is the right verdict for fuzzy heuristics
/// where a hard block would punish legitimate requests.
/// A recovery redirect still refuses the call, but asks for another authorized
/// approach instead of implying that the user must approve a failed retry.
/// </summary>
public sealed record ToolGuardDecision(bool Allowed, string? BlockReason, bool RequiresApproval = false)
{
    /// <summary>A failed-action loop should change approach, without inventing a new user approval requirement.</summary>
    public bool IsRecoveryRedirect { get; init; }
    public static ToolGuardDecision Allow() => new(true, null);
    public static ToolGuardDecision Block(string reason) => new(false, reason);
    public static ToolGuardDecision Redirect(string reason) => new(false, reason) { IsRecoveryRedirect = true };
    public static ToolGuardDecision Escalate(string reason) => new(true, reason, RequiresApproval: true);
}
