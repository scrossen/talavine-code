namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// App-supplied tool-use validator. The app owns the canonical tool schemas and the
/// pass/fail criteria — different applications have different tool surfaces, so the
/// generic SwarmCore layer can't define what "passes" looks like for them. The
/// validator is invoked by <see cref="IToolUseCalibrationService"/> on cache miss.
///
/// Implementations should:
///   1. Send a fixed set of canonical prompts to (provider, model) via the router's
///      tool-use channel (ILlmRouter.ChatWithToolsAsync).
///   2. Score each turn: did the model call the right tool? Did the arguments parse?
///      Did the model recover after a tool result was returned?
///   3. Return a <see cref="ToolUseCalibrationResult"/>.
///
/// SwarmCore registers a no-op default (<c>NullToolUseValidator</c>) so the calibration
/// service can short-circuit when no validator is wired — important for tests and for
/// minimal hosts that don't need tool-use validation.
/// </summary>
public interface IToolUseValidator
{
    Task<ToolUseCalibrationResult> RunAsync(string providerId, string modelId, CancellationToken ct = default);
}

/// <summary>
/// No-op validator that marks every (provider, model) as untested. Apps that want real
/// tool-use validation register their own <see cref="IToolUseValidator"/> over this.
/// </summary>
public sealed class NullToolUseValidator : IToolUseValidator
{
    public static readonly NullToolUseValidator Instance = new();

    public Task<ToolUseCalibrationResult> RunAsync(string providerId, string modelId, CancellationToken ct = default)
        => Task.FromResult(new ToolUseCalibrationResult(
            Status: "untested",
            IsUsable: false,
            PassRate: 0,
            AverageLatencyMs: 0,
            CriticalFailures: 0,
            LastEvaluatedAt: DateTime.UtcNow,
            LastIssueSummary: "No IToolUseValidator registered; calibration skipped."));
}
