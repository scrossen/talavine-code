namespace SwarmCore.Knowledge;

public sealed record KnowledgeGraphEntity
{
    public required Guid Id { get; init; }
    public required KnowledgePartition Partition { get; init; }
    public required KnowledgeEntityType Type { get; init; }
    public required string CanonicalKey { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? NeighborhoodSummary { get; init; }
    public string? AliasesJson { get; init; }
    public int MentionCount { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
}

public sealed record KnowledgeGraphEdge
{
    public required Guid Id { get; init; }
    public required KnowledgePartition Partition { get; init; }
    public required Guid SubjectEntityId { get; init; }
    public required Guid ObjectEntityId { get; init; }
    public required KnowledgeRelationType RelationType { get; init; }
    public string? Predicate { get; init; }
    public int MentionCount { get; init; }
    public double Strength { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public IReadOnlyList<KnowledgeSourceAnchor> Sources { get; init; } = [];
}

public enum KnowledgeEntityType
{
    Person = 0,
    Company = 1,
    Topic = 2,
    Project = 3,
    Location = 4,
    Commitment = 5,
    Decision = 6,
    Event = 7,
    Other = 99
}

public enum KnowledgeRelationType
{
    MentionsWith = 0,
    WorksAt = 1,
    Discusses = 2,
    CorrespondsWith = 3,
    Committed = 4,
    OwedTo = 5,
    Concerns = 6,
    Decided = 7,
    ParticipatedIn = 8,
    Other = 99
}

public sealed record KnowledgeGraphPathHop
{
    public required KnowledgeGraphEntity From { get; init; }
    public required KnowledgeGraphEntity To { get; init; }
    public required KnowledgeRelationType RelationType { get; init; }
    public string? Predicate { get; init; }
    public required bool FollowsEdgeDirection { get; init; }
    public required int MentionCount { get; init; }
    public required double Strength { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public IReadOnlyList<KnowledgeSourceAnchor> Sources { get; init; } = [];

    public bool HasSourceEvidence => Sources.Count > 0;
}

public sealed record KnowledgeGraphPath
{
    public required KnowledgePartition Partition { get; init; }
    public required IReadOnlyList<KnowledgeGraphPathHop> Hops { get; init; }
    public required double Score { get; init; }

    public KnowledgeGraphEntity Start => Hops[0].From;
    public KnowledgeGraphEntity End => Hops[^1].To;
    public int Length => Hops.Count;
    public bool IsFullySourced => Hops.All(static hop => hop.HasSourceEvidence);
}

public sealed record KnowledgeGraphPathQuery
{
    public required IReadOnlyList<Guid> StartEntityIds { get; init; }
    public IReadOnlyList<Guid> TargetEntityIds { get; init; } = [];
    public int MaxDepth { get; init; } = 3;
    public int MinDepth { get; init; } = 1;
    public int MaxPaths { get; init; } = 8;
    public int MaxEdgesPerLevel { get; init; } = 2000;
    public int MaxTransitDegree { get; init; } = 250;
    public bool RequireFullSourceCoverage { get; init; } = true;
}
