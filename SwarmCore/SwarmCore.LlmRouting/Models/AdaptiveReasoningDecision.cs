namespace SwarmCore.LlmRouting.Models;

public enum AdaptiveFailure { None, Transport, RateLimited, Authentication, Configuration, Permission, QuotaExhausted, MalformedOutput, ToolProtocol, Acceptance, Synthesis }
public enum AdaptiveEvidence { NotRequired, AdmittedComplete, MissingCoverage, StaleIndex, IdentityAmbiguous, RetrievalIncorrect, Unverified }
public enum AdaptiveAction { Keep, RetryProvider, Diagnose, RepairEvidence, IncreaseEffort, RequestStrongerRoute, RequestCheaperRoute, DecreaseEffort }

/// <summary>Host-observed evidence, ordered within one work identity. No model confidence input.</summary>
public sealed record AdaptiveReasoningObservation
{
    public required string WorkId { get; init; }
    public required long Sequence { get; init; }
    public required string EvidenceId { get; init; }
    public AdaptiveFailure Failure { get; init; }
    public AdaptiveEvidence Evidence { get; init; } = AdaptiveEvidence.Unverified;
    public bool MadeProgress { get; init; }
    public bool NoProgressObserved { get; init; }
    public bool AcceptancePassed { get; init; }
    /// <summary>A host-verified operation receipt, not whole-task acceptance.</summary>
    public bool VerifiedStepCompleted { get; init; }
    public bool RequiredCapabilityMissing { get; init; }
    public bool BudgetPressure { get; init; }
    public bool LatencyAboveTarget { get; init; }
}

/// <summary>Canonical authority supplies action eligibility; this policy never admits a route.</summary>
public sealed record AdaptiveReasoningEligibility
{
    public string? EffortIncreaseBlockedReason { get; init; }
    public string? StrongerRouteBlockedReason { get; init; }
    public string? CheaperRouteBlockedReason { get; init; }
    public bool CanIncreaseEffort { get; init; }
    public bool CanRequestStrongerRoute { get; init; }
    public bool CanRequestCheaperRoute { get; init; }
    public bool CanDecreaseEffort { get; init; }
    public string? EffortDecreaseBlockedReason { get; init; }
}

public sealed record AdaptiveReasoningOptions
{
    public string Version { get; init; } = "adaptive-reasoning-v1";
    public int FailuresBeforeEscalation { get; init; } = 2;
    public int SuccessesBeforeDeescalation { get; init; } = 3;
    public int ProviderRetries { get; init; } = 2;
    public int? MaximumEscalations { get; init; }
    public TimeSpan ChangeCooldown { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Optional measured latency pressure threshold; never a request timeout.</summary>
    public double? LatencyTargetMs { get; init; }
}

/// <summary>Persist with the work's decisions; restart must not reset counters or cooldown.</summary>
public sealed record AdaptiveReasoningState
{
    public required string WorkId { get; init; }
    public long LastSequence { get; init; } = -1;
    public int ConsecutiveFailures { get; init; }
    public int ConsecutiveSuccesses { get; init; }
    public int ConsecutiveRetryFailures { get; init; }
    public int AppliedEscalations { get; init; }
    public long LastAppliedSequence { get; init; } = -1;
    public DateTimeOffset? LastChangeAt { get; init; }
}

public sealed record AdaptiveReasoningDecision(AdaptiveAction Action, string Reason,
    string WorkId, long ObservationSequence, string EvidenceId, string PolicyVersion);
public sealed record AdaptiveReasoningEvaluation(AdaptiveReasoningState State, AdaptiveReasoningDecision Decision);
