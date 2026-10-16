namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Record of a single LLM usage for tracking and cost analysis.
/// This is the database-agnostic DTO that applications can use.
/// </summary>
public class LlmUsageRecord
{
    public long? UsageId { get; set; }
    /// <summary>Stable identity for this measured physical call even if usage persistence fails.</summary>
    public Guid PhysicalCallId { get; init; } = Guid.NewGuid();
    public required string FunctionType { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? StrategyId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    /// <summary>Input tokens the provider served from its prompt cache; zero when the provider did not report it.</summary>
    public int CachedInputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public DateTime RequestTimestamp { get; set; } = DateTime.UtcNow;
    public int? ResponseTimeMs { get; set; }

    /// <summary>Server-reported prompt-prefill time (ms); null when the provider has no serving-layer timings.</summary>
    public int? PrefillDurationMs { get; set; }

    /// <summary>Server-reported token-generation time (ms).</summary>
    public int? DecodeDurationMs { get; set; }

    /// <summary>Server-reported model (re)load time (ms).</summary>
    public int? ModelLoadDurationMs { get; set; }

    /// <summary>Chars of stable prefix declared via the PromptCaching marker; 0 = none declared.</summary>
    public int DeclaredCachePrefixChars { get; set; }

    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public string? PromptHash { get; set; }
    /// <summary>Links this call to the append-only routing decision ledger.</summary>
    public string? RoutingAuditCorrelationId { get; set; }
    public bool WasSuccessful { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public bool FallbackUsed { get; set; } = false;

    /// <summary>Interactivity bucket ("background" / "interactive") for per-bucket A/B aggregation.</summary>
    public string? InteractivityContext { get; set; }

    /// <summary>
    /// Full prompt text. The router always populates this; whether it gets
    /// persisted is the tracker's decision based on a user-controlled toggle.
    /// </summary>
    public string? PromptText { get; set; }

    /// <summary>
    /// Full response text. Same persistence-is-tracker's-decision semantics
    /// as <see cref="PromptText"/>.
    /// </summary>
    public string? ResponseText { get; set; }
}

/// <summary>
/// Service for tracking LLM usage across requests.
/// Applications implement this to store usage data in their preferred storage.
/// </summary>
public interface ILlmUsageTracker
{
    /// <summary>
    /// Track a single LLM usage record
    /// </summary>
    Task TrackUsageAsync(LlmUsageRecord record);

    /// <summary>
    /// Get recent usage records, optionally filtered by function type
    /// </summary>
    Task<List<LlmUsageRecord>> GetRecentUsageAsync(string? functionType = null, int limit = 100);

    /// <summary>
    /// Get cost summary for the specified period
    /// </summary>
    Task<LlmCostSummary> GetCostSummaryAsync(string? functionType = null, DateTime? startDate = null, DateTime? endDate = null);

    /// <summary>
    /// Get cost breakdown by function or model
    /// </summary>
    Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(TimeSpan period, string groupBy = "function");

    /// <summary>
    /// Get cost breakdown for a specific date range
    /// </summary>
    Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(DateTime startDate, DateTime endDate, string groupBy = "function");
}

/// <summary>
/// Optional stronger contract for call paths that must not release a durable
/// spend reservation until the corresponding usage row is committed. Ordinary
/// telemetry may remain buffered through <see cref="ILlmUsageTracker"/>.
/// </summary>
public interface IDurableLlmUsageTracker : ILlmUsageTracker
{
    Task TrackUsageDurablyAsync(LlmUsageRecord record, CancellationToken ct = default);
}

