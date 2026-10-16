using System.Text.Json.Nodes;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// An event emitted by <see cref="IToolUseAgentEngine.RunAsync"/> as the
/// agent progresses through a turn. Mapped to higher-level UI updates by
/// the chat service.
/// </summary>
public abstract record AgentChatEvent;

public sealed record UsageReportedEvent(string Provider, string Model, long InputTokens, long OutputTokens,
    long CachedInputTokens = 0) : AgentChatEvent;

/// <summary>
/// The user's incoming message has been appended to the transcript. Emitted
/// once at the start of <see cref="IToolUseAgentEngine.RunAsync"/>.
/// </summary>
public sealed record UserMessageAppendedEvent(string Text) : AgentChatEvent;

/// <summary>
/// The assistant produced text. In the initial non-streaming implementation
/// this is the full text of one turn; streaming will deliver smaller deltas.
/// </summary>
public sealed record AssistantTextEvent(string Text) : AgentChatEvent;

/// <summary>Host progress such as retry/compaction; never part of the assistant answer.</summary>
public sealed record AgentStatusEvent(string Text) : AgentChatEvent;

/// <summary>
/// The assistant produced chain-of-thought (reasoning_content / thinking)
/// content. Streamed alongside <see cref="AssistantTextEvent"/> for
/// thinking-capable models. Kept as a distinct event so the UI can render
/// reasoning in its own collapsible widget — mixing it into AssistantText
/// would force the user to read past chain-of-thought to find the answer.
/// </summary>
public sealed record AssistantReasoningEvent(string Text) : AgentChatEvent;

/// <summary>
/// The agent is about to execute a tool. UI can render a collapsible block.
/// </summary>
public sealed record ToolCallStartedEvent(string Id, string Name, JsonObject Input) : AgentChatEvent
{
    public SwarmCore.Agent.Catalog.ToolDispatchPlan? DispatchPlan { get; init; }
}

/// <summary>
/// The tool finished. <see cref="Result"/> is serialized output, or the
/// exception message when <see cref="IsError"/> is true. <see cref="Elapsed"/>
/// is the duration of this tool call, not the cumulative turn duration.
/// </summary>
public sealed record ToolCallCompletedEvent(
    string Id,
    string Name,
    string Result,
    bool IsError,
    TimeSpan? Elapsed = null) : AgentChatEvent
{
    public bool WasDispatched { get; init; } = true;
    public SwarmCore.Agent.Catalog.ToolDispatchPlan? DispatchPlan { get; init; }
    public bool LocalProcessCompleted { get; init; }
}

/// <summary>An accepted user update was appended at a safe engine boundary.</summary>
public sealed record SteeringAppliedEvent(string SteeringId) : AgentChatEvent;

/// <summary>
/// The agent chose to end its turn. <see cref="FinalText"/> is the last
/// assistant text block, which the chat service presents as the visible
/// reply.
/// </summary>
public sealed record FinishedEvent(string FinalText) : AgentChatEvent;

/// <summary>
/// The agent failed. Carries a message suitable for UI display.
/// </summary>
public sealed record ErroredEvent(string Message) : AgentChatEvent
{
    /// <summary>The typed provider failure that ended the turn, when one did.</summary>
    public SwarmCore.Abstractions.LlmRouting.LlmFailureCategory? FailureCategory { get; init; }

    /// <summary>
    /// Only a person can clear this failure: the provider account has no
    /// credit left or rejected its credential. Repeating the same request on
    /// a retry backoff cannot succeed until someone acts.
    /// </summary>
    public bool RequiresAttention => FailureCategory
        is SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.QuotaExhausted
        or SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.AuthInvalid;
}

public sealed record SteeringDeliveryChangedEvent(SwarmCore.Abstractions.LlmRouting.ChatSteeringDeliveryEvent Receipt) : AgentChatEvent;
