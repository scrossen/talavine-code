namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Records that a turn is being served by a route weaker than the one a safety
/// policy wanted for it. A safety advisor that would rather answer on a weak
/// route than strand the user writes one of these; the agent engine reads it at
/// the end of the turn and discloses the degradation in the answer itself.
/// </summary>
/// <param name="PolicyName">Advisor that degraded, for logs and audit joins.</param>
/// <param name="FunctionType">Function whose route was degraded.</param>
/// <param name="Provider">Provider the advisor settled on.</param>
/// <param name="Model">Model the advisor settled on.</param>
/// <param name="PreferredMinimumCapabilityScore">Capability floor the policy wanted.</param>
/// <param name="ActualCapabilityScore">Capability of the route it settled on, when known.</param>
/// <param name="IsLocalRoute">True when the settled route runs on a local runtime.</param>
/// <param name="Detail">Short human-readable reason, safe to show the user.</param>
public sealed record LlmDegradedRouteNotice(
    string PolicyName,
    string FunctionType,
    string Provider,
    string Model,
    decimal PreferredMinimumCapabilityScore,
    decimal? ActualCapabilityScore,
    bool IsLocalRoute,
    string Detail);

/// <summary>
/// Per-turn carrier for <see cref="LlmDegradedRouteNotice"/>. Routing writes,
/// the agent engine takes. Keyed by the caller's turn id so concurrent
/// conversations cannot read each other's notices; a notice for a turn that is
/// never consumed simply expires.
/// </summary>
public interface ILlmDegradedRouteNoticeSink
{
    /// <summary>Record (replacing any earlier notice for the same turn).</summary>
    void Record(string? turnId, LlmDegradedRouteNotice notice);

    /// <summary>
    /// Drop any notice for this turn. Called when a later attempt on the same
    /// turn did reach the preferred floor, so the answer is not mislabeled.
    /// </summary>
    void Clear(string? turnId);

    /// <summary>Remove and return the notice for this turn, if any.</summary>
    LlmDegradedRouteNotice? Take(string? turnId);
}
