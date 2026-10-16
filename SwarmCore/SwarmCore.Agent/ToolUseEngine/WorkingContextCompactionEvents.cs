namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Explicit lifecycle signal emitted before working-context compaction. The
/// runtime/UI mapper may project this into its durable item vocabulary without
/// coupling the provider-neutral compactor to a particular host.
/// </summary>
public sealed record WorkingContextCompactionStartedEvent(
    WorkingContextCompactionReason Reason,
    int OriginalCharacters) : AgentChatEvent;

/// <summary>
/// Explicit lifecycle signal emitted after working-context compaction.
/// </summary>
public sealed record WorkingContextCompactionCompletedEvent(
    WorkingContextCompactionReason Reason,
    int OriginalCharacters,
    int CompactedCharacters,
    int CompactedMessageCount,
    bool UsedDeterministicFallback,
    bool Applied = true) : AgentChatEvent
{
    public int ReclaimedCharacters => Math.Max(0, OriginalCharacters - CompactedCharacters);
}

/// <summary>Safe diagnostic; contains only generation/mode/reconstruction reason, never opaque data.</summary>
public sealed record WorkingContextContinuationEvent(long Generation, string Mode, string? Reason) : AgentChatEvent;
