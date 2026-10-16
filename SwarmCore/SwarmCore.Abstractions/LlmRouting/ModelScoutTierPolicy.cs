namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// The one urgency→tier mapping every routing lane shares. Before this policy
/// existed the switch was copy-pasted into the router's model-selection paths
/// and the subscription-first advisor, and the advisor's extra
/// "Normal + UserWaiting → Frontier" promotion looked like accidental drift
/// instead of what it is: a deliberate property of the zero-marginal-cost
/// lane. The <paramref name="zeroMarginalCostLane"/> parameter names that
/// decision so future copies cannot diverge silently.
/// </summary>
public static class ModelScoutTierPolicy
{
    /// <summary>
    /// Map a call's urgency/interactivity to the Model Scout tier to request.
    ///
    /// All lanes: Urgent → Frontier, Batch → Economy.
    ///
    /// The remaining (Normal/Unspecified) urgencies split by lane:
    /// <list type="bullet">
    /// <item><paramref name="zeroMarginalCostLane"/> = false (metered API
    /// routing): everything else stays Balanced — frontier tokens cost real
    /// money, so only explicit urgency buys them.</item>
    /// <item><paramref name="zeroMarginalCostLane"/> = true (subscription
    /// CLI routing, already paid for): a user actively waiting on the reply
    /// is promoted to Frontier — the CLI exposes the frontier model at no
    /// marginal cost, so demoting UserWaiting turns free capability into a
    /// quality regression. Non-interactive work stays Balanced so the
    /// Frontier capability floor cannot starve background functions of
    /// eligible routes.</item>
    /// </list>
    /// </summary>
    public static ModelScoutTier DeriveTier(
        LlmUrgency urgency,
        LlmInteractivity interactivity,
        bool zeroMarginalCostLane)
        => urgency switch
        {
            LlmUrgency.Urgent => ModelScoutTier.Frontier,
            LlmUrgency.Batch => ModelScoutTier.Economy,
            _ => zeroMarginalCostLane && interactivity == LlmInteractivity.UserWaiting
                ? ModelScoutTier.Frontier
                : ModelScoutTier.Balanced,
        };
}
