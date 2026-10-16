namespace SwarmCore.Knowledge;

using System.Text.Json.Serialization;

/// <summary>
/// A source-grounded knowledge unit. Decisions, policies, commitments,
/// outcomes, patterns, and ordinary facts share evidence and lifecycle
/// semantics while remaining distinguishable by <see cref="Kind"/>.
/// </summary>
public sealed record KnowledgeArtifact
{
    /// <summary>
    /// Deterministic from partition + kind + canonical key. Callers may leave
    /// this empty; normalization fills it and rejects any mismatch.
    /// </summary>
    public Guid Id { get; init; }
    public required KnowledgePartition Partition { get; init; }
    public required KnowledgeArtifactKind Kind { get; init; }
    public required string CanonicalKey { get; init; }
    public required string Title { get; init; }
    public required string Statement { get; init; }
    public string? Domain { get; init; }
    public KnowledgeArtifactLifecycle Lifecycle { get; init; } = KnowledgeArtifactLifecycle.Active;
    /// <summary>
    /// Evidence health is independent of organizational lifecycle. On writes,
    /// the shared service resets this to Unassessed; the store derives it from
    /// successfully resolved remaining evidence links.
    /// </summary>
    public KnowledgeEvidenceSupportStatus EvidenceSupport { get; init; }
    public double Confidence { get; init; } = 1;
    public bool IsMachineGenerated { get; init; }
    public DateTimeOffset? EffectiveAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public KnowledgeDecisionFacet? Decision { get; init; }
    public KnowledgeOutcomeFacet? Outcome { get; init; }
    public KnowledgePatternFacet? Pattern { get; init; }
    public KnowledgeCommitmentFacet? Commitment { get; init; }
    public IReadOnlyList<KnowledgeEvidenceLink> EvidenceLinks { get; init; } = [];
    public IReadOnlyList<KnowledgeOrganizationalApproval> Approvals { get; init; } = [];
    public IReadOnlyList<KnowledgeArtifactRelation> Relations { get; init; } = [];

    [JsonIgnore]
    public bool IsEvidenceBacked => EvidenceLinks.Any(static link => link.Weight > 0
        && link.Role is KnowledgeEvidenceRole.Supports or KnowledgeEvidenceRole.EstablishesOutcome);

    [JsonIgnore]
    public bool HasApprovalHistory => Approvals.Count > 0;

    public bool IsOrganizationallyApprovedAt(DateTimeOffset instant)
        => Approvals
            .Where(approval => approval.DecidedAt <= instant)
            .GroupBy(static approval => approval.ApprovalKey, StringComparer.Ordinal)
            .Select(static stream => stream
                .OrderByDescending(approval => approval.DecidedAt)
                .First())
            .Any(approval => approval.Status == KnowledgeApprovalStatus.Approved
                && (!approval.ExpiresAt.HasValue || approval.ExpiresAt > instant));
}

public sealed record KnowledgeEvidenceLink(
    Guid EvidenceId,
    KnowledgeEvidenceRole Role,
    double Weight = 1);

/// <summary>
/// An explicit organizational act. Approval does not increase evidence
/// reliability; evidence authority and organizational authority are separate.
/// </summary>
public sealed record KnowledgeOrganizationalApproval
{
    public required Guid Id { get; init; }
    /// <summary>Stable identity shared by proposal, approval, rejection, and revocation events.</summary>
    public required string ApprovalKey { get; init; }
    public required string ApproverCanonicalKey { get; init; }
    public string? AuthorityScope { get; init; }
    public required KnowledgeApprovalStatus Status { get; init; }
    public required DateTimeOffset DecidedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public Guid? SourceEvidenceId { get; init; }
}

public sealed record KnowledgeArtifactRelation(
    Guid TargetArtifactId,
    KnowledgeArtifactRelationType RelationType);

public enum KnowledgeArtifactKind
{
    Fact = 0,
    Decision = 1,
    Policy = 2,
    Exception = 3,
    Commitment = 4,
    Outcome = 5,
    Pattern = 6
}

public enum KnowledgeArtifactLifecycle
{
    Draft = 0,
    Active = 1,
    Superseded = 2,
    Revoked = 3,
    Expired = 4
}

public enum KnowledgeEvidenceSupportStatus
{
    Unassessed = 0,
    Supported = 1,
    Contested = 2,
    Unsupported = 3
}

public static class KnowledgeEvidenceSupportPolicy
{
    public static KnowledgeEvidenceSupportStatus Compute(
        IEnumerable<KnowledgeEvidenceLink> resolvedLinks)
    {
        ArgumentNullException.ThrowIfNull(resolvedLinks);
        var hasSupport = false;
        var hasContradiction = false;
        foreach (var link in resolvedLinks)
        {
            if (link.Weight <= 0)
                continue;
            hasSupport |= link.Role is KnowledgeEvidenceRole.Supports
                or KnowledgeEvidenceRole.EstablishesOutcome;
            hasContradiction |= link.Role == KnowledgeEvidenceRole.Contradicts;
        }

        return (hasSupport, hasContradiction) switch
        {
            (true, true) => KnowledgeEvidenceSupportStatus.Contested,
            (true, false) => KnowledgeEvidenceSupportStatus.Supported,
            _ => KnowledgeEvidenceSupportStatus.Unsupported
        };
    }
}

public static class KnowledgeArtifactIdentity
{
    public static Guid CreateDeterministicId(
        KnowledgePartition partition,
        KnowledgeArtifactKind kind,
        string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(partition);
        KnowledgeContractValidation.RequireDefined(kind, nameof(kind));
        var normalizedPartition = KnowledgePartition.Create(partition.Kind, partition.Id);
        var normalizedKey = KnowledgeContractValidation.RequiredTrimmed(canonicalKey, nameof(canonicalKey))
            .ToLowerInvariant();
        KnowledgeContractValidation.RequireBounded(
            normalizedKey,
            KnowledgeContractLimits.ArtifactCanonicalKeyLength,
            nameof(canonicalKey));
        var stableKey = KnowledgeStableKey.Encode(
            normalizedPartition.StableKey,
            ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalizedKey);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(stableKey));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}

public enum KnowledgeEvidenceRole
{
    Supports = 0,
    Contradicts = 1,
    ProvidesContext = 2,
    EstablishesOutcome = 3
}

public enum KnowledgeApprovalStatus
{
    Proposed = 0,
    Approved = 1,
    Rejected = 2,
    Revoked = 3
}

public enum KnowledgeArtifactRelationType
{
    Supersedes = 0,
    ExceptionTo = 1,
    OutcomeOf = 2,
    ExampleOf = 3,
    DerivedFrom = 4,
    Requires = 5
}
