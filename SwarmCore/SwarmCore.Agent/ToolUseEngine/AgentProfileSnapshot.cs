using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Runtime snapshot of an agent profile used for a single chat turn. Host
/// applications can hydrate this from their own persisted profile record.
/// <para>
/// Kept in the orchestration layer so the engine has no dependency on EF Core.
/// </para>
/// </summary>
public sealed record AgentProfileSnapshot
{
    /// <summary>Host-owned function for physical tool-use routing. Ordinary agents use agent-chat.</summary>
    public string RoutingFunctionType { get; init; } = FunctionTypes.AgentChat;
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Tool ids enabled for this profile. Empty = all tools in the catalog.
    /// </summary>
    public required IReadOnlyList<string> EnabledToolIds { get; init; }

    /// <summary>Optional preferred provider override for tool-use calls.</summary>
    public string? Provider { get; init; }

    /// <summary>Optional preferred model override for tool-use calls.</summary>
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Who supplied the complete provider/model route. Only an explicitly
    /// user-pinned persisted profile uses <see cref="LlmOverrideProvenance.Profile"/>;
    /// a paired per-turn picker override uses <see cref="LlmOverrideProvenance.User"/>.
    /// Incomplete, legacy, or automatically selected routes remain unspecified
    /// and never acquire pin authority merely because strings are present.
    /// </summary>
    public LlmOverrideProvenance RouteProvenance { get; init; } = LlmOverrideProvenance.Unspecified;

    /// <summary>Optional legacy iteration cutoff. Zero or less means no
    /// count-based cutoff, which is the default: cancellation, progress, and
    /// verified completion govern work instead of an arbitrary call count.</summary>
    public int MaxIterations { get; init; }

    /// <summary>Optional legacy total wall-clock budget. Zero or
    /// <see cref="Timeout.InfiniteTimeSpan"/> means no total deadline and is
    /// the default; individual calls remain cancellable and progress-aware
    /// tools are governed by their renewable liveness lease.</summary>
    public TimeSpan HardTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Max number of recent transcript messages to send to the model. The
    /// full transcript stays in the DB; only the trailing window plus the
    /// current turn's new messages are included in each LLM request. Keeps
    /// cost bounded on long conversations. Zero keeps the complete history;
    /// provider context overflow triggers adaptive compaction instead.
    /// </summary>
    public int MaxTranscriptMessages { get; init; }

    /// <summary>
    /// Optional explicit ceiling on a single provider-facing tool result.
    /// Zero, the default, keeps the complete result. Positive values are
    /// caller policy and append a visible truncation marker when applied.
    /// </summary>
    public int MaxToolResultBytes { get; init; }

    /// <summary>
    /// Optional caller-provided payload estimate at which provider-facing
    /// compaction begins. Zero, the default, delegates to the provider's real
    /// context contract. Exceeding a positive estimate never creates a
    /// product-level refusal when no safe compaction remains.
    /// </summary>
    public int MaxRequestBytes { get; init; }

    /// <summary>
    /// Optional explicit per-turn ceiling on provider-reported input plus
    /// output tokens. Zero, the default, has no cumulative-token cutoff.
    /// </summary>
    public int MaxCumulativeTokens { get; init; }
}
