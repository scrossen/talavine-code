namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Request to validate a single (provider, model) pair against the canonical tool-use
/// suite. Carries the schema hash so the calibration service can compute the
/// <see cref="RoutingCalibrationKey"/> and decide cache hit vs. re-run.
/// </summary>
public sealed record ToolUseCalibrationRequest(
    string ProviderId,
    string ModelId,
    string SchemaHash,
    string HardwareKey,
    /// <summary>When true, ignore any cached result and re-run.</summary>
    bool ForceRecalibrate = false,
    string? FunctionType = null,
    string? AuditCorrelationId = null,
    string? AuditSessionId = null,
    string? AuditTurnId = null,
    string? ModelDigest = null,
    string? RuntimeVersion = null,
    string? Quantization = null);

/// <summary>
/// Orchestrates "does this model emit valid tool calls" validation. Reads/writes the
/// <see cref="IToolUseCalibrationCatalog"/> using a stable <see cref="RoutingCalibrationKey"/>
/// so a passing model isn't re-evaluated on every dev rebuild — only when:
///
///   - the canonical schema content changes (PromptHash differs)
///   - the user clicks "retest" (<see cref="ToolUseCalibrationRequest.ForceRecalibrate"/>)
///   - a new provider/model is being scored for the first time
///   - the hardware fingerprint changed (only affects local models)
///   - a mutable local model artifact, runtime, or advertised quantization changed
///
/// Application-specific apps implement this with their own canonical schemas and
/// test runner; SwarmCore defines the contract.
/// </summary>
public interface IToolUseCalibrationService
{
    Task<ToolUseCalibrationResult> CalibrateAsync(
        ToolUseCalibrationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the cached calibration without running anything, or null when no cached
    /// result exists for this exact key. Used by route advisors that want to skip a
    /// model that has already failed validation without paying the calibration cost.
    /// </summary>
    Task<ToolUseCalibrationResult?> TryGetCachedAsync(
        ToolUseCalibrationRequest request,
        CancellationToken ct = default);
}
