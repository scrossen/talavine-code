namespace SwarmCore.Knowledge;

/// <summary>
/// Shared validation/orchestration boundary in front of host persistence.
/// Product code should resolve this service; host composition roots alone
/// should construct the persistence adapter it wraps.
/// </summary>
public sealed class KnowledgeArtifactService(IKnowledgeArtifactStore store)
{
    public Task<KnowledgeEvidenceUpsertResult> UpsertEvidenceAsync(
        KnowledgeEvidenceRecord evidence,
        CancellationToken ct = default)
        => store.UpsertEvidenceAsync(KnowledgeEvidenceNormalizer.Normalize(evidence), ct);

    public Task<KnowledgeEvidenceRecord?> GetEvidenceAsync(
        KnowledgePartition partition,
        Guid evidenceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (evidenceId == Guid.Empty)
            throw new ArgumentException("Evidence id cannot be empty.", nameof(evidenceId));
        return store.GetEvidenceAsync(
            KnowledgePartition.Create(partition.Kind, partition.Id),
            evidenceId,
            ct);
    }

    public Task<IReadOnlyList<KnowledgeEvidenceRecord>> GetEvidenceBatchAsync(
        KnowledgePartition partition,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        if (evidenceIds.Count > KnowledgeContractLimits.EvidenceLinksPerArtifact)
            throw new ArgumentException("Evidence batch is too large.", nameof(evidenceIds));
        if (evidenceIds.Any(static id => id == Guid.Empty))
            throw new ArgumentException("Evidence ids cannot be empty.", nameof(evidenceIds));
        return store.GetEvidenceBatchAsync(
            KnowledgePartition.Create(partition.Kind, partition.Id),
            evidenceIds.Distinct().ToArray(),
            ct);
    }

    public Task<KnowledgeArtifactUpsertResult> UpsertArtifactAsync(
        KnowledgeArtifact artifact,
        CancellationToken ct = default)
        => store.UpsertArtifactAsync(KnowledgeArtifactNormalizer.Normalize(artifact), ct);

    public Task<KnowledgeArtifactCreateResult> CreateArtifactAsync(
        KnowledgeArtifact artifact,
        CancellationToken ct = default)
        => store.CreateArtifactAsync(KnowledgeArtifactNormalizer.Normalize(artifact), ct);

    public Task<KnowledgeApprovalAppendResult> AppendApprovalEventAsync(
        KnowledgePartition partition,
        Guid artifactId,
        KnowledgeOrganizationalApproval approval,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (artifactId == Guid.Empty)
            throw new ArgumentException("Artifact id cannot be empty.", nameof(artifactId));
        return store.AppendApprovalEventAsync(
            KnowledgePartition.Create(partition.Kind, partition.Id),
            artifactId,
            KnowledgeArtifactNormalizer.NormalizeApproval(approval),
            ct);
    }

    public Task<KnowledgeArtifact?> GetArtifactAsync(
        KnowledgePartition partition,
        Guid artifactId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (artifactId == Guid.Empty)
            throw new ArgumentException("Artifact id cannot be empty.", nameof(artifactId));
        return store.GetArtifactAsync(KnowledgePartition.Create(partition.Kind, partition.Id), artifactId, ct);
    }

    public Task<IReadOnlyList<KnowledgeArtifact>> QueryArtifactsAsync(
        KnowledgeArtifactQuery query,
        CancellationToken ct = default)
        => store.QueryArtifactsAsync(KnowledgeArtifactNormalizer.NormalizeQuery(query), ct);

    public Task<KnowledgeArtifactPage> QueryArtifactPageAsync(
        KnowledgeArtifactQuery query,
        CancellationToken ct = default)
        => store.QueryArtifactPageAsync(KnowledgeArtifactNormalizer.NormalizeQuery(query), ct);

    public Task<KnowledgeRetrievalResult> RetrieveAsync(
        KnowledgeRetrievalQuery query,
        CancellationToken ct = default)
        => store.RetrieveAsync(KnowledgeRetrievalNormalizer.Normalize(query), ct);

    public Task<KnowledgeSourceEvidenceRemovalResult> RemoveEvidenceBySourceAsync(
        KnowledgePartition partition,
        KnowledgeSourceId source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(source);
        return store.RemoveEvidenceBySourceAsync(
            KnowledgePartition.Create(partition.Kind, partition.Id),
            KnowledgeSourceId.Create(source.Kind, source.StableId, source.InstanceId),
            ct);
    }
}
