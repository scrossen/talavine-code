namespace SwarmCore.Knowledge;

/// <summary>
/// Host persistence boundary for evidence-backed artifacts. Implementations
/// must authorize the resolved partition independently; values in these
/// contracts scope data but never grant access to it.
/// </summary>
public interface IKnowledgeArtifactStore
{
    /// <summary>
    /// Upserts by the normalized deterministic evidence id and returns the
    /// canonical id persisted by the host.
    /// </summary>
    Task<KnowledgeEvidenceUpsertResult> UpsertEvidenceAsync(
        KnowledgeEvidenceRecord evidence,
        CancellationToken ct = default);
    Task<KnowledgeEvidenceRecord?> GetEvidenceAsync(
        KnowledgePartition partition,
        Guid evidenceId,
        CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeEvidenceRecord>> GetEvidenceBatchAsync(
        KnowledgePartition partition,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically resolves every evidence, approval-evidence, artifact
    /// relation, and typed-facet artifact reference (including a commitment's
    /// originating decision) inside <paramref name="artifact"/>'s authorized partition.
    /// Missing or cross-partition references must reject the entire write.
    /// Identity is (partition, kind, canonical key), represented by the
    /// deterministic artifact id. Approval events are atomically merged by
    /// event id; this operation never removes omitted history. Reusing an event
    /// id with different content is a conflict and rejects the write. Incoming
    /// EvidenceSupport is never trusted: after link resolution, persist the
    /// result of KnowledgeEvidenceSupportPolicy.Compute.
    /// </summary>
    Task<KnowledgeArtifactUpsertResult> UpsertArtifactAsync(
        KnowledgeArtifact artifact,
        CancellationToken ct = default);
    /// <summary>
    /// Atomically creates an artifact and fails when its deterministic identity
    /// already exists. Hosts must not implement this as a check followed by an
    /// upsert; concurrent callers must never overwrite one another.
    /// </summary>
    Task<KnowledgeArtifactCreateResult> CreateArtifactAsync(
        KnowledgeArtifact artifact,
        CancellationToken ct = default);
    /// <summary>
    /// Atomically appends one normalized approval event. The store rejects an
    /// event-id conflict and a duplicate (approval key, decided-at) coordinate.
    /// Concurrent independent appends must both survive.
    /// </summary>
    Task<KnowledgeApprovalAppendResult> AppendApprovalEventAsync(
        KnowledgePartition partition,
        Guid artifactId,
        KnowledgeOrganizationalApproval approval,
        CancellationToken ct = default);
    Task<KnowledgeArtifact?> GetArtifactAsync(
        KnowledgePartition partition,
        Guid artifactId,
        CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeArtifact>> QueryArtifactsAsync(
        KnowledgeArtifactQuery query,
        CancellationToken ct = default);
    /// <summary>Returns one stable page plus the total count for the normalized filter.</summary>
    Task<KnowledgeArtifactPage> QueryArtifactPageAsync(
        KnowledgeArtifactQuery query,
        CancellationToken ct = default);
    /// <summary>
    /// Returns a bounded relevance-ranked artifact/evidence projection from one
    /// aggregate-consistent host snapshot. Every returned evidence record must
    /// still resolve inside the same authorized partition as its artifact.
    /// </summary>
    Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalQuery query,
        CancellationToken ct = default);
    Task<KnowledgeSourceEvidenceRemovalResult> RemoveEvidenceBySourceAsync(
        KnowledgePartition partition,
        KnowledgeSourceId source,
        CancellationToken ct = default);
}

public sealed record KnowledgeEvidenceUpsertResult(Guid EvidenceId, bool Created, bool Updated);

public sealed record KnowledgeArtifactUpsertResult(Guid ArtifactId, bool Created, bool Updated);

public sealed record KnowledgeArtifactCreateResult(Guid ArtifactId);

public sealed record KnowledgeApprovalAppendResult(Guid ArtifactId, Guid ApprovalEventId, bool Appended);

public sealed record KnowledgeArtifactQuery
{
    public required KnowledgePartition Partition { get; init; }
    public string? SearchText { get; init; }
    public IReadOnlySet<KnowledgeArtifactKind> Kinds { get; init; } = new HashSet<KnowledgeArtifactKind>();
    public IReadOnlySet<KnowledgeArtifactLifecycle> Lifecycles { get; init; } = new HashSet<KnowledgeArtifactLifecycle>();
    public IReadOnlySet<KnowledgeEvidenceSupportStatus> SupportStatuses { get; init; } = new HashSet<KnowledgeEvidenceSupportStatus>();
    public string? Domain { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveUntil { get; init; }
    public bool RequireSupportingEvidence { get; init; } = true;
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public sealed record KnowledgeArtifactPage(
    IReadOnlyList<KnowledgeArtifact> Items,
    int TotalCount,
    int Offset,
    int Limit);

public sealed class KnowledgeArtifactAlreadyExistsException(Guid artifactId)
    : InvalidOperationException("A knowledge artifact with this identity already exists.")
{
    public Guid ArtifactId { get; } = artifactId;
}

/// <summary>
/// Result of one atomic source-removal transaction. Implementations delete all
/// matching evidence, remove artifact evidence links, clear approval evidence
/// references, and set only artifacts with no remaining positive supporting
/// evidence to the result of KnowledgeEvidenceSupportPolicy.Compute over the
/// surviving resolved links. Organizational lifecycle is never overwritten;
/// removing the only contradiction can therefore move Contested to Supported.
/// </summary>
public sealed record KnowledgeSourceEvidenceRemovalResult(
    int RemovedEvidenceCount,
    IReadOnlyList<Guid> AffectedArtifactIds,
    IReadOnlyList<Guid> UnsupportedArtifactIds);
