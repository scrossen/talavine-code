namespace SwarmCore.Knowledge;

/// <summary>
/// Complete deterministic projection produced from one source. It contains no
/// raw message or document body and can be replaced or removed atomically.
/// </summary>
public sealed record KnowledgeContributionBundle
{
    public required KnowledgePartition Partition { get; init; }
    public required KnowledgeSourceAnchor Source { get; init; }
    public required string InputFingerprintSha256 { get; init; }
    /// <summary>Version of the authoritative source captured when this projection was produced.</summary>
    public string? SourceContentFingerprintSha256 { get; init; }
    public string? ProjectionFingerprintSha256 { get; init; }
    public int ProjectionVersion { get; init; } = KnowledgeContract.CurrentProjectionVersion;
    public bool IsUserAuthored { get; init; }
    public IReadOnlyList<KnowledgeEntityContribution> Entities { get; init; } = [];
    public IReadOnlyList<KnowledgeRelationContribution> Relations { get; init; } = [];
}

public sealed record KnowledgeEntityContribution(
    KnowledgeEntityType EntityType,
    string CanonicalKey,
    string Name,
    string? Description = null,
    string? AliasesJson = null,
    int MentionCount = 1);

public sealed record KnowledgeRelationContribution(
    string SubjectCanonicalKey,
    string ObjectCanonicalKey,
    KnowledgeRelationType RelationType,
    string? Predicate = null,
    int MentionCount = 1);

public sealed record KnowledgeContributionApplyResult(
    bool Applied,
    bool WasIdempotent,
    IReadOnlyList<Guid> AffectedEntityIds,
    IReadOnlyList<string> AffectedCanonicalKeys);

public sealed record KnowledgeContributionRemovalResult(
    bool Removed,
    IReadOnlyList<Guid> AffectedEntityIds,
    IReadOnlyList<string> AffectedCanonicalKeys);

public enum KnowledgeContributionBuildState
{
    Pending = 0,
    Building = 1,
    Complete = 2,
    Failed = 3
}

public sealed record KnowledgeContributionLedgerStatus
{
    public required KnowledgePartition Partition { get; init; }
    public required int LedgerVersion { get; init; }
    /// <summary>
    /// Projection version proved across every complete source counted by this status.
    /// A store must not report the current value until it has validated durable rows.
    /// </summary>
    public required int ValidatedProjectionVersion { get; init; }
    public required KnowledgeContributionBuildState State { get; init; }
    public int ScannedSourceCount { get; init; }
    public int EligibleSourceCount { get; init; }
    public int CompleteSourceCount { get; init; }
    public string? Cursor { get; init; }
    public DateTimeOffset? BuildStartedAt { get; init; }
    public DateTimeOffset? BuildCompletedAt { get; init; }
    public string? LastErrorCategory { get; init; }

    public bool IsComplete => LedgerVersion == KnowledgeContract.CurrentLedgerVersion
        && ValidatedProjectionVersion == KnowledgeContract.CurrentProjectionVersion
        && State == KnowledgeContributionBuildState.Complete
        && BuildCompletedAt.HasValue
        && ScannedSourceCount >= EligibleSourceCount
        && EligibleSourceCount == CompleteSourceCount;
}
