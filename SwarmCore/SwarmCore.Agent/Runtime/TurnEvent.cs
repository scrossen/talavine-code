using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// A single event in the public chat-event stream consumed by the UI. The
/// discriminated-union shape lets a chat surface render each state distinctly —
/// a token chunk appends to the in-progress bubble, a tool call spawns an
/// expandable pill, a permission request prompts the user — rather than
/// mushing everything into one string with a status label.
/// <para>
/// Emitted by <see cref="IAgentTurnRunner"/> and consumed directly by the UI.
/// </para>
/// </summary>
public abstract record TurnEvent(string TurnId);

/// <summary>One provider-reported usage delta. InputTokens includes cached input; cached is a subset. Missing reports mean unknown usage, never zero cost.</summary>
public sealed record TurnUsageReported(string TurnId, string Provider, string Model, long InputTokens,
    long OutputTokens, long CachedInputTokens = 0, decimal? CostUsd = null) : TurnEvent(TurnId);

/// <summary>
/// Emitted once at the start of a turn. Carries the resolved profile name
/// so the UI can render the agent badge before the first token arrives.
/// </summary>
public sealed record TurnStarted(string TurnId, Guid ProfileId, string ProfileName) : TurnEvent(TurnId)
{
    /// <summary>The presentation-store row, distinct from the stream correlation id.</summary>
    public Guid? ConversationTurnId { get; init; }
}

/// <summary>
/// Incremental assistant text to append to the in-progress bubble. With
/// SSE streaming this fires per-token; without streaming it fires once per
/// iteration with the full text of that iteration.
/// </summary>
public sealed record TextChunk(string TurnId, string Delta) : TurnEvent(TurnId);

/// <summary>
/// Incremental chain-of-thought from a thinking-capable model (DeepSeek
/// thinking mode, etc.). Distinct from <see cref="TextChunk"/> so the UI
/// can accumulate reasoning into its own collapsible widget rather than
/// letting it scroll past in the answer stream and disappear.
/// </summary>
public sealed record ReasoningChunk(string TurnId, string Delta) : TurnEvent(TurnId);

/// <summary>
/// The agent requested a tool invocation. The UI renders a collapsible
/// "tool call" block showing the tool name and input arguments.
/// </summary>
public sealed record ToolCallStarted(string TurnId, string ToolCallId, string ToolName, JsonObject Input) : TurnEvent(TurnId)
{
    /// <summary>Host-adapter classification for tools outside the host catalog, such as native CLI tools.</summary>
    public Catalog.ToolSideEffect? SideEffect { get; init; }
}

/// <summary>
/// Token-level streaming of a tool call's input arguments. Anthropic and
/// OpenAI both emit tool-input deltas over SSE; the UI can show the args
/// filling in character by character for a Claude-like feel. Optional — the
/// engine may skip these and jump straight to <see cref="ToolCallCompleted"/>.
/// </summary>
public sealed record ToolCallInputDelta(string TurnId, string ToolCallId, string JsonFragment) : TurnEvent(TurnId);

/// <summary>
/// The tool completed (successfully or not). <see cref="Result"/> is the
/// serialized output shown in the expandable block.
/// </summary>
public sealed record ToolCallCompleted(
    string TurnId,
    string ToolCallId,
    string ToolName,
    string Result,
    bool IsError,
    TimeSpan Elapsed) : TurnEvent(TurnId)
{
    public bool WasDispatched { get; init; } = true;
    public bool LocalProcessCompleted { get; init; }
    public Catalog.ToolSideEffect? SideEffect { get; init; }
}

/// <summary>
/// Lightweight "the agent is reasoning" signal. UI uses this to pulse a
/// thinking indicator between turns.
/// </summary>
public sealed record Thinking(string TurnId, string? Summary = null) : TurnEvent(TurnId);

/// <summary>
/// A skill has requested elevated permission mid-turn. The UI renders a
/// prompt; the user's response flows back via the chat facade.
/// </summary>
public sealed record PermissionRequested(
    string TurnId,
    string RequestId,
    string SkillId,
    string SkillName,
    string FunctionName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<SwarmCore.Skills.Models.PermissionLevel> PermissionLevels) : TurnEvent(TurnId);

/// <summary>
/// A user update was durably accepted for this active turn. The text is
/// user-authored transcript input, not hidden model reasoning.
/// </summary>
public sealed record TurnSteeringAccepted(
    string TurnId,
    string SteeringId,
    string Message) : TurnEvent(TurnId)
{
    public Guid? ProfileId { get; init; }
    public AgentTurnSteeringRequest? Request { get; init; }
    public long Sequence { get; init; }
    public int? ConstraintRevision { get; init; }
}

/// <summary>Native transport receipt; queued is explicitly not applied.</summary>
public sealed record TurnSteeringDeliveryChanged(string TurnId, string SteeringId,
    string Status, string? ProviderSteeringId = null, string? ResponseId = null,
    string? Reason = null) : TurnEvent(TurnId);

/// <summary>The accepted update reached the model at a safe turn boundary.</summary>
public sealed record TurnSteeringApplied(
    string TurnId,
    string SteeringId) : TurnEvent(TurnId);

/// <summary>
/// The active turn ended before an accepted update could be applied. The
/// message remains durable so a UI can place it into the next normal turn.
/// </summary>
public sealed record TurnSteeringDeferred(
    string TurnId,
    string SteeringId,
    string Message,
    string Reason) : TurnEvent(TurnId);

/// <summary>
/// The provider-facing working history is being replaced with a bounded
/// factual checkpoint. Only aggregate counts enter the reconnect ledger;
/// summary content remains in the transient provider request.
/// </summary>
public sealed record WorkingContextCompactionStarted(
    string TurnId,
    SwarmCore.Agent.ToolUseEngine.WorkingContextCompactionReason Reason,
    int OriginalCharacters) : TurnEvent(TurnId);

/// <summary>
/// The provider-facing checkpoint attempt finished. This records lifecycle
/// and aggregate reduction only, never checkpoint or reasoning content.
/// </summary>
public sealed record WorkingContextCompactionCompleted(
    string TurnId,
    SwarmCore.Agent.ToolUseEngine.WorkingContextCompactionReason Reason,
    int OriginalCharacters,
    int CompactedCharacters,
    int CompactedMessageCount,
    bool UsedDeterministicFallback,
    bool Applied) : TurnEvent(TurnId)
{
    public int ReclaimedCharacters => Math.Max(0, OriginalCharacters - CompactedCharacters);
}

/// <summary>
/// Emitted exactly once at the end of a successful turn. The default runner
/// publishes it only after the session transcript and presentation turn have
/// both reported successful persistence and required completion handlers have
/// succeeded. Hosts with a staged procedure candidate require a verified atomic
/// terminal/procedure commit before publication. Other hosts use the ledger's
/// ordinary best-effort terminal marker.
/// </summary>
public sealed record TurnCompleted(
    string TurnId,
    string FinalText,
    TimeSpan Duration,
    bool ReplacesStreamedText = false) : TurnEvent(TurnId);

/// <summary>
/// Emitted exactly once at the end of a failed turn. Surfaced to the user
/// as an inline error bubble — the caller should not retry automatically.
/// </summary>
public sealed record TurnErrored(string TurnId, string Message, string? Details = null) : TurnEvent(TurnId)
{
    /// <summary>Typed provider failure that ended the turn (content policy, rate limit, auth, timeout), when known.</summary>
    public SwarmCore.Abstractions.LlmRouting.LlmFailureCategory? FailureCategory { get; init; }

    /// <summary>Completion validation retries must inspect saved evidence, not replay actions.</summary>
    public TurnErrorKind ErrorKind { get; init; }

    /// <summary>
    /// Candidate retained for verification-only recovery. It is not an accepted
    /// answer and must pass output guards and acceptance before being delivered.
    /// Host observers receive it in-process; public serialization/replay omits
    /// the candidate because an output guard may have failed before redaction.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UnverifiedFinalText { get; init; }
}

public enum TurnErrorKind
{
    General,
    CompletionValidation,
}

/// <summary>
/// Durable-only terminal marker used when a stream consumer detaches or the
/// turn is cancelled before the engine reaches a normal terminal event. The
/// live iterator cannot reliably yield during teardown, but replay clients
/// need a stable outcome instead of a permanently running turn.
/// </summary>
public sealed record TurnAbandoned(string TurnId, string Reason) : TurnEvent(TurnId);

/// <summary>Safe continuation diagnostic, never opaque protocol payload.</summary>
public sealed record WorkingContextContinuation(string TurnId, long Generation, string Mode, string? Reason) : TurnEvent(TurnId);

public sealed record AdaptiveReasoningChanged(string TurnId, Guid DecisionId, string Action, string Outcome, string Reason,
    string? PreviousProvider, string? PreviousModel, string? Provider, string? Model, string? Effort) : TurnEvent(TurnId);
