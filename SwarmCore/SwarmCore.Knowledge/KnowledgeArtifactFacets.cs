namespace SwarmCore.Knowledge;

/// <summary>Decision-specific context that must remain queryable after CRM convergence.</summary>
public sealed record KnowledgeDecisionFacet
{
    /// <summary>When the decision itself occurred; distinct from creation/effectivity.</summary>
    public required DateTimeOffset DecidedAt { get; init; }
    public required string Category { get; init; }
    public required string DecisionMakerCanonicalKey { get; init; }
    public string? DecisionMakerRole { get; init; }
    public string? Reasoning { get; init; }
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public IReadOnlyList<KnowledgeDecisionAlternative> Alternatives { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];
    public IReadOnlyList<string> StakeholderCanonicalKeys { get; init; } = [];
    public string? ExternalFactors { get; init; }
    public string? BusinessContext { get; init; }
    public KnowledgeUrgency? Urgency { get; init; }
    public bool? IsReversible { get; init; }
    public string? ReversalCost { get; init; }
    public bool IsAutomated { get; init; }
    public bool IsMarketingContent { get; init; }
}

public sealed record KnowledgeDecisionAlternative(
    string Description,
    string? WhyRejected = null,
    string? Pros = null,
    string? Cons = null);

public sealed record KnowledgeOutcomeFacet
{
    /// <summary>When the outcome occurred; distinct from artifact ingestion.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
    public required KnowledgeOutcomeType Type { get; init; }
    public bool? AchievedIntent { get; init; }
    public IReadOnlyList<string> UnintendedConsequences { get; init; } = [];
    public string? QuantifiableImpact { get; init; }
    public IReadOnlyList<string> LessonsLearned { get; init; } = [];
    public KnowledgeOutcomeRecommendation Recommendation { get; init; }
    public string? ReportedByCanonicalKey { get; init; }
}

public sealed record KnowledgePatternFacet
{
    public required string ApplicableWhen { get; init; }
    public required string RecommendedAction { get; init; }
    public int OccurrenceCount { get; init; }
    public double? SuccessRate { get; init; }
    public double? AverageImpact { get; init; }
    public string? HandlingGuidance { get; init; }
    public IReadOnlyList<string> Caveats { get; init; } = [];
}

public sealed record KnowledgeCommitmentFacet
{
    public Guid? OriginatingDecisionArtifactId { get; init; }
    public required string AssignedToCanonicalKey { get; init; }
    public string? AssignedByCanonicalKey { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required KnowledgeCommitmentStatus Status { get; init; }
    public required KnowledgePriority Priority { get; init; }
    public IReadOnlyList<KnowledgeCommitmentUpdate> Updates { get; init; } = [];
    public IReadOnlyList<string> Blockers { get; init; } = [];
}

public sealed record KnowledgeCommitmentUpdate(
    DateTimeOffset Timestamp,
    string UpdatedByCanonicalKey,
    string Content,
    KnowledgeCommitmentStatus? StatusChange = null);

public enum KnowledgeUrgency
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum KnowledgeOutcomeType
{
    Neutral = 0,
    Positive = 1,
    Negative = 2,
    Mixed = 3
}

public enum KnowledgeOutcomeRecommendation
{
    None = 0,
    Repeat = 1,
    Avoid = 2,
    Conditional = 3
}

public enum KnowledgeCommitmentStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Blocked = 3,
    Cancelled = 4
}

public enum KnowledgePriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
