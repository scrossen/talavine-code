using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Per-conversation runtime state for the tool-use engine. Carries the
/// profile snapshot, the (mutable) transcript accumulated across turns,
/// and the resolved tool set the model may invoke.
/// </summary>
public sealed class AgentSession
{
    /// <summary>Host research may require one exact route with no fallback attribution.</summary>
    public bool DisableRouteFallback { get; init; }
    /// <summary>The person asked for the working context to be compacted before this turn's first model call.</summary>
    public bool CompactWorkingContextFirst { get; init; }
    /// <summary>Host report contracts can expose canonical invocation correlation without changing retained raw results.</summary>
    public bool RequireCorrelatedToolResults { get; init; }
    /// <summary>Require all arguments in the advertised schema before invocation, in addition to normal typed validation.</summary>
    public bool RequireDeclaredToolArguments { get; init; }
    /// <summary>Host-bound reusable workflow, including current inputs and deterministic verification.</summary>
    public SwarmCore.Agent.Procedures.AgentProcedureContext? ProcedureContext { get; init; }
    public LlmModelRequest? ModelRequest { get; init; }
    public required string ConversationId { get; init; }
    /// <summary>
    /// User-visible turn identifier propagated into LLM routing audit records.
    /// It is ephemeral and is not part of transcript persistence.
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// The host has authenticated this turn as an explicit user request. Tool
    /// calls made to satisfy that request carry the user's authority directly
    /// instead of interrupting the turn with a redundant approval card.
    /// Background, delegated, and otherwise unproven turns leave this false.
    /// </summary>
    public bool UserRequestAuthorized { get; init; }

    /// <summary>
    /// Host-granted unattended-repair authority for this turn, or null. When
    /// set, side-effecting tool calls are decided by
    /// <see cref="Runtime.RepairAuthorityPolicy"/> instead of a consent card.
    /// </summary>
    public Runtime.AgentRepairAuthority? RepairAuthority { get; init; }

    public required AgentProfileSnapshot Profile { get; init; }

    /// <summary>
    /// The full transcript for this conversation. Mutated in place by the
    /// engine as new messages (user, assistant, tool_result) are appended.
    /// Hydrated from the session store at turn start, persisted back via the
    /// same store by the chat service.
    /// </summary>
    public required List<ChatMessage> Transcript { get; init; }

    /// <summary>
    /// Tools available to the model for this turn. The chat service filters
    /// the full catalog down to the profile's <see cref="AgentProfileSnapshot.EnabledToolIds"/>
    /// before constructing the session.
    /// </summary>
    public required IReadOnlyList<ToolDescriptor> Tools { get; init; }

    /// <summary>
    /// Requires a provider-native tool call on the first model step. This is
    /// set only by a pre-turn router that has identified an explicit evidence
    /// request and narrowed the authorized catalog to the relevant tool(s).
    /// </summary>
    public bool RequireFirstToolCall { get; init; }

    /// <summary>
    /// Optional host-owned first-step invariant. When set, the first model
    /// response must contain exactly one native call to this registered tool.
    /// This narrows an already authorized catalog; it does not grant a tool.
    /// Unlike <see cref="RequireFirstToolCall"/>, this is enforced after the
    /// provider responds and never invokes a fallback tool on its behalf.
    /// </summary>
    public string? RequiredFirstToolId { get; init; }

    /// <summary>
    /// Allows the engine to close a provider compatibility gap by invoking the
    /// sole routed read-only/no-required-input tool when the provider ignores
    /// the required first tool choice.
    /// </summary>
    public bool InvokeSoleReadOnlyToolOnFirstMiss { get; init; }

    /// <summary>
    /// Allow the sole initially selected tool's first response to finish the
    /// turn without model synthesis. Multi-tool and discovered-tool workflows
    /// always return to synthesis so intermediate findings cannot replace the answer.
    /// </summary>
    public bool AllowToolResultFinalAnswer { get; init; } = true;

    /// <summary>
    /// Complete profile-authorized tool set for this turn, before prompt-size
    /// routing chooses the smaller initial schema set in <see cref="Tools"/>.
    /// The engine may activate descriptors from this set after
    /// <c>catalog.search_tools</c> discovers them, or when a model legitimately
    /// remembers an exact tool id from earlier conversation context. This keeps
    /// schema routing an optimization rather than a permanent capability gate.
    /// Caller exclusions and profile restrictions have already been applied.
    /// </summary>
    public IReadOnlyList<ToolDescriptor> DiscoverableTools { get; init; } = [];

    /// <summary>
    /// Optional host-owned re-resolution after tool discovery. Must apply the original
    /// profile and caller exclusions. Tool output never supplies executable descriptors.
    /// Fixed-authority hosts (including missions) leave this null.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<ToolDescriptor>>>? RefreshDiscoverableToolsAsync { get; init; }

    /// <summary>
    /// The ambient turn attribution the engine re-asserts before every tool
    /// invocation. The runner also sets this on its AsyncLocal accessor, but
    /// AsyncLocal writes made inside an async iterator do not survive yield
    /// boundaries — every consumer resume replaces the iterator's execution
    /// context — so by the time a tool actually runs, the ambient value is
    /// gone and fail-closed guards (e.g. code execution) wrongly deny.
    /// Carrying it here lets the engine restore it in the same execution
    /// segment as the invocation.
    /// </summary>
    public CurrentTurnContext? TurnContext { get; init; }

    /// <summary>
    /// Rechecks a host-owned standing delegation against the actual tool and typed arguments.
    /// Null result leaves normal permission policy unchanged; denial fails promptly without a card.
    /// It never bypasses a tool invocation guard or the adapter's resource checks.
    /// </summary>
    public Func<ToolDescriptor, object, CancellationToken, Task<ToolDelegationDecision?>>? AuthorizeDelegatedToolAsync { get; init; }

    /// <summary>
    /// Optional user-update source. The engine reads it only between physical
    /// model calls or completed tool work, never while provider/tool code is
    /// mutating the transcript.
    /// </summary>
    public IAgentTurnSteeringSource? SteeringSource { get; init; }
    public WorkingContextState? WorkingContext { get; init; }
    public AdaptiveAgentContext? AdaptiveReasoning { get; init; }

    /// <summary>
    /// Whether a person is waiting on this turn. The runner sets Background for
    /// unattended work (scheduled agents, repair runs, continuations nobody is
    /// watching) so Model Scout keeps it on local routes and it yields shared
    /// local nodes to people who are waiting.
    /// </summary>
    public LlmInteractivity Interactivity { get; init; } = LlmInteractivity.UserWaiting;

    /// <summary>
    /// Host-declared demand for this turn's model calls (the surface it serves,
    /// its complexity). The engine adds the context the turn needs.
    /// </summary>
    public LlmTaskDemand? RoutingDemand { get; init; }
}
