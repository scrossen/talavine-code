namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Host seam supplying evidence-backed Model Scout candidates for one
/// recommendation request. Hosts own candidate discovery and persistence
/// (benchmark rows, pricing, catalogs, observed usage); SwarmCore owns the
/// request/decision contract, ranking semantics, and the reusable
/// recommendation engine built on this seam.
/// </summary>
/// <remarks>
/// Implementations should return every known provider/model with whatever
/// evidence exists — including unavailable or under-evidenced routes — so the
/// engine can disclose rejections instead of silently hiding options. Unknown
/// speed, cost, and reliability must stay null; the ranker penalizes and
/// discloses uncertainty rather than inventing precision.
/// </remarks>
public interface IModelScoutCandidateSource
{
    Task<IReadOnlyList<ModelScoutCandidate>> LoadCandidatesAsync(
        ModelScoutRecommendationRequest request,
        CancellationToken cancellationToken = default);
}
