namespace SwarmCore.Abstractions.Context;

/// <summary>Bounded, source-neutral retrieval intent; never an authorization grant.</summary>
public sealed record ContextQueryPlan
{
    public string? QueryFingerprint { get; init; }
    public IReadOnlyList<string> Entities { get; init; } = [];
    public IReadOnlyList<string> Identifiers { get; init; } = [];
    public IReadOnlyList<string> RequiredClaimSlots { get; init; } = ["source-evidence"];
    public string TemporalIntent { get; init; } = "current";
    public DateTimeOffset? EffectiveAt { get; init; }
    public DateTimeOffset? KnownAt { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public IReadOnlyList<Guid> AccountIds { get; init; } = [];
    public int MaximumSources { get; init; } = 32;
    public int MaximumTokens { get; init; } = 2500;
    public int MaximumWireTokens { get; init; } = 8192;
    public int MaximumExpansionRounds { get; init; } = 1;
}

public sealed record ContextLaneAttempt
{
    public required string Lane { get; init; }
    public string Status { get; init; } = "not-attempted";
    public int Candidates { get; init; }
    public int Round { get; init; }
    public string? Reason { get; init; }
}

public sealed record ContextCollectionFreshness
{
    public required Guid AccountId { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    public string Status { get; init; } = "unknown";
}

public sealed record ContextCoverage
{
    public string Status { get; init; } = "unknown";
    public string StopReason { get; init; } = "not-measured";
    public DateTimeOffset CollectedAt { get; init; }
    public string SourceValidation { get; init; } = "not-checked";
    public IReadOnlyList<ContextLaneAttempt> Attempts { get; init; } = [];
    public IReadOnlyList<ContextCollectionFreshness> Collections { get; init; } = [];
    public IReadOnlyList<string> Gaps { get; init; } = [];
    // A sync timestamp alone never establishes exhaustive bounded collection coverage.
    public bool SupportsAbsence { get; init; }
}
