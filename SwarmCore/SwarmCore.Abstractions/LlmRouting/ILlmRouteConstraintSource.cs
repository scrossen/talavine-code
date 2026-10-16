namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Workload policy without provider discovery, ranking or execution.</summary>
public interface ILlmRouteConstraintSource
{
    Task<LlmRouteConstraints?> GetConstraintsAsync(
        LlmRouteAdvisorContext context, CancellationToken cancellationToken = default);
}

public sealed record LlmRouteConstraints
{
    public decimal? MinimumCapabilityScore { get; init; }
    public ModelScoutTier? RequiredTier { get; init; }
    public bool RequireMeasuredCapabilityScore { get; init; }
    public bool RequireCurrentFunctionEvidence { get; init; }
    public bool BlockWeakerPolicyFallback { get; init; }
    public string? Reason { get; init; }

    // Compatibility projection for the governor's audit envelope. Empty route
    // identities are intentional: only Scout may choose the automatic route.
    public LlmRouteAdvisorRecommendation ToRecommendation() => new()
    {
        Provider = "", Model = "", PersistPreference = false,
        MinimumCapabilityScore = MinimumCapabilityScore, RequiredTier = RequiredTier,
        RequireMeasuredCapabilityScore = RequireMeasuredCapabilityScore,
        RequireCurrentFunctionEvidence = RequireCurrentFunctionEvidence,
        BlockWeakerPolicyFallback = BlockWeakerPolicyFallback, Reason = Reason,
    };
}

public static class LlmRouteAdvisorPolicy
{
    public static async Task<LlmRouteAdvisorRecommendation?> EvaluateAsync(
        ILlmRouteAdvisor advisor, LlmRouteAdvisorContext context, CancellationToken ct)
    {
        if (context.ConstraintsOnly && advisor is ILlmRouteConstraintSource source)
            return (await source.GetConstraintsAsync(context, ct).ConfigureAwait(false))?.ToRecommendation();
        // Older hosts can still contribute safety floors. Their route identities
        // are ignored by automatic selection; this path grants no execution.
        return await advisor.GetRecommendationAsync(context, ct).ConfigureAwait(false);
    }
}
