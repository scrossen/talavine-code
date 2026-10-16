namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// A measured calibration result for one (lane, prompt-hash, provider, model, hardware)
/// tuple. Stored by <see cref="IToolUseCalibrationCatalog"/> so the calibration service
/// can return cached results without rerunning a real LLM call when the staleness key
/// hasn't changed.
/// </summary>
/// <param name="Status">"passed" | "failed" | "untested" | "excluded"</param>
/// <param name="IsUsable">True when this model can drive the lane's workload acceptably.</param>
/// <param name="PassRate">0..1 share of calibration cases that passed.</param>
/// <param name="AverageLatencyMs">Average elapsed ms across the calibration run.</param>
/// <param name="CriticalFailures">Cases that crashed / returned invalid tool calls / failed schema parsing.</param>
/// <param name="LastEvaluatedAt">UTC of the most recent successful evaluation.</param>
/// <param name="LastIssueSummary">Short human-readable summary of failures, if any.</param>
public sealed record ToolUseCalibrationResult(
    string Status,
    bool IsUsable,
    double PassRate,
    double AverageLatencyMs,
    int CriticalFailures,
    DateTime LastEvaluatedAt,
    string? LastIssueSummary = null);

/// <summary>
/// Catalog of measured tool-use validation results, keyed by <see cref="RoutingCalibrationKey"/>.
/// Models that pass are eligible for agent-chat / agent-loop routing; models that fail are
/// excluded until the user forces a retest or the prompt/schema changes.
///
/// Why a separate catalog from the message-pipeline catalog: tool-use measures a different
/// capability ("does the model emit valid tool calls against our schemas?") and would
/// otherwise have to share `MessagePipelineModelCatalog` rows by overloading the
/// FunctionType column. Keeping it separate lets each catalog evolve its own scoring
/// dimensions (tool-use cares about JSON schema adherence; message-pipeline cares about
/// fact grounding) without one breaking the other.
/// </summary>
public interface IToolUseCalibrationCatalog
{
    /// <summary>
    /// Return the cached result for this key, or null if no result is stored. The catalog
    /// never returns a stale result — staleness is encoded in the key.
    /// </summary>
    Task<ToolUseCalibrationResult?> GetAsync(RoutingCalibrationKey key, CancellationToken ct = default);

    Task SetAsync(RoutingCalibrationKey key, ToolUseCalibrationResult result, CancellationToken ct = default);

    /// <summary>
    /// Clear results for a (provider, model) regardless of prompt hash. Called when the
    /// user clicks "retest this model" or when a provider is removed.
    /// </summary>
    Task ClearAsync(string providerId, string modelId, CancellationToken ct = default);

    /// <summary>
    /// Snapshot every stored result for diagnostics / settings UI.
    /// </summary>
    IReadOnlyList<(RoutingCalibrationKey Key, ToolUseCalibrationResult Result)> Snapshot();

    /// <summary>
    /// Asynchronous durable snapshot. Persistent catalogs override this so a fresh
    /// process observes stored failures and passes before making routing decisions.
    /// In-memory implementations can use the synchronous snapshot.
    /// </summary>
    Task<IReadOnlyList<(RoutingCalibrationKey Key, ToolUseCalibrationResult Result)>> SnapshotAsync(
        CancellationToken ct = default)
        => Task.FromResult(Snapshot());
}
