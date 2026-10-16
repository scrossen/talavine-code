namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Function-scoped replacement plus the exact Scout proof the router must carry
/// into fallback validation and execution receipts.
/// </summary>
public sealed record LlmModelReplacementResult(
    string Model,
    string? AuditCorrelationId = null,
    bool HasCurrentFunctionEvidence = false,
    string? EvidenceFunctionType = null,
    string? EvidenceSource = null,
    string? EvidenceVersion = null,
    bool? SupportsToolUse = null,
    bool? SupportsStructuredOutput = null,
    int? MaxContextTokens = null,
    string? CapabilityEvidenceSource = null,
    ModelScoutRuntimeDecisionReceipt? ModelScoutDecisionReceipt = null);

/// <summary>
/// Service for self-healing model configuration when API calls fail due to model issues.
/// Implementations can automatically find replacement models and update preferences.
/// </summary>
public interface ILlmModelSelfHealer
{
    /// <summary>
    /// Called when a model-not-found error occurs. Implementation should attempt to
    /// find a replacement model and update the function preference.
    /// </summary>
    /// <param name="functionType">The function type that experienced the error</param>
    /// <param name="provider">The provider that returned the error</param>
    /// <param name="failedModel">The model that was not found</param>
    /// <param name="requiresVision">Whether the function requires vision capabilities</param>
    /// <returns>The replacement model ID if one was found and applied, null otherwise</returns>
    Task<string?> HealModelNotFoundAsync(string functionType, string provider, string failedModel, bool requiresVision = false);

    /// <summary>
    /// Cancellation-aware healing used by hosted startup/maintenance work so
    /// shutdown stops inventory probes, benchmarks, and multi-GB model pulls.
    /// Legacy implementations remain source-compatible through this default.
    /// </summary>
    async Task<string?> HealModelNotFoundAsync(
        string functionType,
        string provider,
        string failedModel,
        bool requiresVision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await HealModelNotFoundAsync(functionType, provider, failedModel, requiresVision)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Get a valid replacement model for a failed model (without updating preferences).
    /// Useful for immediate fallback without persistence.
    /// </summary>
    /// <param name="provider">The provider</param>
    /// <param name="failedModel">The model that failed</param>
    /// <param name="requiresVision">Whether vision is required</param>
    /// <returns>A replacement model ID, or null if none found</returns>
    Task<string?> GetReplacementModelAsync(string provider, string failedModel, bool requiresVision = false);

    /// <summary>
    /// Function-aware replacement discovery. New router paths use this overload
    /// so a fallback can be admitted and audited against current function proof.
    /// The compatibility default never claims evidence for legacy implementations.
    /// </summary>
    async Task<LlmModelReplacementResult?> GetFunctionScopedReplacementAsync(
        string functionType,
        string provider,
        string failedModel,
        bool requiresVision = false)
    {
        var model = await GetReplacementModelAsync(provider, failedModel, requiresVision)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(model)
            ? null
            : new LlmModelReplacementResult(model);
    }
}
