namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// The routing policy lane that produced or considered a recommendation.
/// Lower-level router constraints still win: explicit overrides and LocalOnly
/// privacy are enforced by the router before a recommendation is used.
/// </summary>
public enum LlmRoutePolicyKind
{
    General = 0,
    FunctionQuality = 10,
    Budget = 20,
    RuntimeHealth = 30,
    Fallback = 40,
    /// <summary>Per-call urgency boost — user asked for fast+smart on this call.</summary>
    Urgency = 50,
    /// <summary>Time-of-day routing — shift batch work to local during quiet hours.</summary>
    TimeOfDay = 60,
    /// <summary>Soft subscription quota — demote off a CLI before it hits the hard cooldown.</summary>
    SubscriptionQuota = 70,
}

/// <summary>
/// Shared priority bands for advisor ordering. Smaller numbers run first.
/// Keep these coarse so application advisors can slot between them without
/// turning ordering into trivia.
/// </summary>
public static class LlmRoutePolicyPriorities
{
    /// <summary>Urgency boosts run before any other advisor: when the user asks for fast+smart, that wins.</summary>
    public const int Urgency = 150;
    public const int FunctionQuality = 300;
    /// <summary>Subscription quota soft-demote sits between quality and hard budget.</summary>
    public const int SubscriptionQuota = 400;
    public const int Budget = 500;
    /// <summary>Time-of-day shifts run after budget — they only nudge batch work toward local.</summary>
    public const int TimeOfDay = 550;
    public const int RuntimeHealth = 600;
    public const int General = 1000;
}

/// <summary>
/// Optional metadata for <see cref="ILlmRouteAdvisor"/> implementations.
/// Advisors that do not implement this interface keep registration order at
/// the General priority.
/// </summary>
public interface ILlmRoutePolicyAdvisor : ILlmRouteAdvisor
{
    int Priority { get; }
    LlmRoutePolicyKind PolicyKind { get; }
    string PolicyName { get; }
}

/// <summary>
/// Marker for non-negotiable per-call safety gates that must still run for an
/// exact pin or while automatic optimization is disabled. The router invokes
/// only this lane at that boundary, so ordinary cost/quality optimizers cannot
/// perform network work or rewrite route intent.
/// </summary>
public interface ILlmHardSafetyRouteAdvisor : ILlmRoutePolicyAdvisor
{
}

public sealed class LlmRouteAdvisorEvaluation
{
    public required string AdvisorName { get; init; }
    public LlmRoutePolicyKind PolicyKind { get; init; } = LlmRoutePolicyKind.General;
    public int Priority { get; init; } = LlmRoutePolicyPriorities.General;
    public bool Recommended { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public bool PersistPreference { get; init; }
    public string? Reason { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class LlmRoutingGovernorCandidate
{
    public required string AdvisorName { get; init; }
    public LlmRoutePolicyKind PolicyKind { get; init; } = LlmRoutePolicyKind.General;
    public int Priority { get; init; } = LlmRoutePolicyPriorities.General;
    /// <summary>
    /// True only when the candidate came from a registered
    /// <see cref="ILlmHardSafetyRouteAdvisor"/>. The router uses this trusted
    /// provenance instead of accepting a recommendation's self-description.
    /// </summary>
    public bool IsHardSafetyPolicy { get; init; }
    public required LlmRouteAdvisorRecommendation Recommendation { get; init; }
}

public sealed class LlmRoutingGovernorDecision
{
    public LlmRouteAdvisorRecommendation? Recommendation { get; init; }
    public string? AdvisorName { get; init; }
    public LlmRoutePolicyKind PolicyKind { get; init; } = LlmRoutePolicyKind.General;
    public int Priority { get; init; } = LlmRoutePolicyPriorities.General;
    public IReadOnlyList<LlmRoutingGovernorCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<LlmRouteAdvisorEvaluation> Evaluations { get; init; } = [];

    public bool HasRecommendation => Recommendation is not null || Candidates.Count > 0;

    public static LlmRoutingGovernorDecision None(IReadOnlyList<LlmRouteAdvisorEvaluation>? evaluations = null) => new()
    {
        Evaluations = evaluations ?? []
    };
}

/// <summary>
/// Coordinates dynamic route advisors so cost, quality, and runtime policy have
/// one explicit ordering point instead of relying on dependency-injection order.
/// </summary>
public interface ILlmRoutingGovernor
{
    Task<LlmRoutingGovernorDecision> GetRecommendationAsync(
        LlmRouteAdvisorContext context,
        CancellationToken cancellationToken = default);
}
