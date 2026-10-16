namespace SwarmCore.Knowledge;

/// <summary>
/// A durable, independently addressable piece of evidence. Artifacts link to
/// evidence rows instead of embedding coarse source-reference strings, which
/// allows exact source deletion and citation rendering without deleting an
/// artifact merely because one supporting source disappeared.
/// </summary>
public sealed record KnowledgeEvidenceRecord
{
    /// <summary>
    /// Deterministic from partition + full source identity + locator. Callers
    /// may leave this empty; normalization fills it and rejects any mismatch.
    /// </summary>
    public Guid Id { get; init; }
    public required KnowledgePartition Partition { get; init; }
    public required KnowledgeSourceAnchor Source { get; init; }
    public required string Locator { get; init; }
    public string? ContentFingerprintSha256 { get; init; }
    public required KnowledgeEvidenceAssessment Assessment { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>
/// Describes what kind of authority the evidence itself carries. This is not
/// organizational approval and must never be inferred from an approver role.
/// </summary>
public sealed record KnowledgeEvidenceAssessment
{
    public required KnowledgeEvidenceAuthority Authority { get; init; }
    public required double Reliability { get; init; }
    public bool IsMachineExtracted { get; init; }
    public string? AssessedBy { get; init; }
}

public enum KnowledgeEvidenceAuthority
{
    Unverified = 0,
    DerivedAnalysis = 1,
    DirectParticipant = 2,
    BusinessRecord = 3,
    PrimarySource = 4
}

public static class KnowledgeEvidenceNormalizer
{
    public static KnowledgeSourceAnchor NormalizeAnchor(KnowledgeSourceAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(anchor.SourceId);
        var title = KnowledgeContractValidation.NullIfWhiteSpace(anchor.Title);
        var uri = KnowledgeContractValidation.NullIfWhiteSpace(anchor.Uri);
        var excerpt = KnowledgeContractValidation.NullIfWhiteSpace(anchor.Excerpt);
        KnowledgeContractValidation.RequireOptionalBounded(
            title,
            KnowledgeContractLimits.SourceTitleLength,
            nameof(anchor.Title));
        KnowledgeContractValidation.RequireOptionalBounded(
            uri,
            KnowledgeContractLimits.SourceUriLength,
            nameof(anchor.Uri));
        KnowledgeContractValidation.RequireOptionalBounded(
            excerpt,
            KnowledgeContractLimits.SourceExcerptLength,
            nameof(anchor.Excerpt));
        return anchor with
        {
            SourceId = KnowledgeSourceId.Create(
                anchor.SourceId.Kind,
                anchor.SourceId.StableId,
                anchor.SourceId.InstanceId),
            Title = title,
            Uri = uri,
            Excerpt = excerpt,
            ObservedAt = anchor.ObservedAt?.ToUniversalTime()
        };
    }

    public static KnowledgeEvidenceRecord Normalize(KnowledgeEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Partition);
        ArgumentNullException.ThrowIfNull(evidence.Assessment);
        var partition = KnowledgePartition.Create(evidence.Partition.Kind, evidence.Partition.Id);
        var source = NormalizeAnchor(evidence.Source);
        var locator = KnowledgeContractValidation.RequiredTrimmed(evidence.Locator, nameof(evidence.Locator));
        var fingerprint = KnowledgeContractValidation.NullIfWhiteSpace(evidence.ContentFingerprintSha256)
            ?.ToLowerInvariant();
        var assessedBy = KnowledgeContractValidation.NullIfWhiteSpace(evidence.Assessment.AssessedBy);
        KnowledgeContractValidation.RequireBounded(
            locator,
            KnowledgeContractLimits.EvidenceLocatorLength,
            nameof(evidence.Locator));
        KnowledgeContractValidation.RequireOptionalBounded(
            assessedBy,
            KnowledgeContractLimits.EvidenceAssessorLength,
            nameof(evidence.Assessment.AssessedBy));
        if (fingerprint is not null)
            KnowledgeContractValidation.RequireSha256(fingerprint, nameof(evidence.ContentFingerprintSha256));
        KnowledgeContractValidation.RequireDefined(
            evidence.Assessment.Authority,
            nameof(evidence.Assessment.Authority));
        if (!double.IsFinite(evidence.Assessment.Reliability)
            || evidence.Assessment.Reliability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidence.Assessment.Reliability),
                "Evidence reliability must be between zero and one.");
        }
        KnowledgeContractValidation.RequireTimestamp(evidence.RecordedAt, nameof(evidence.RecordedAt));
        var deterministicId = KnowledgeEvidenceIdentity.CreateDeterministicId(
            partition,
            source.SourceId,
            locator);
        if (evidence.Id != Guid.Empty && evidence.Id != deterministicId)
            throw new ArgumentException(
                "Evidence id does not match its canonical source coordinate.",
                nameof(evidence.Id));

        return evidence with
        {
            Id = deterministicId,
            Partition = partition,
            Source = source,
            Locator = locator,
            ContentFingerprintSha256 = fingerprint,
            Assessment = evidence.Assessment with { AssessedBy = assessedBy },
            RecordedAt = evidence.RecordedAt.ToUniversalTime()
        };
    }
}

public static class KnowledgeEvidenceIdentity
{
    public static Guid CreateDeterministicId(
        KnowledgePartition partition,
        KnowledgeSourceId source,
        string locator)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(source);
        var normalizedPartition = KnowledgePartition.Create(partition.Kind, partition.Id);
        var normalizedSource = KnowledgeSourceId.Create(source.Kind, source.StableId, source.InstanceId);
        var normalizedLocator = KnowledgeContractValidation.RequiredTrimmed(locator, nameof(locator));
        KnowledgeContractValidation.RequireBounded(
            normalizedLocator,
            KnowledgeContractLimits.EvidenceLocatorLength,
            nameof(locator));
        var stableKey = KnowledgeStableKey.Encode(
            normalizedPartition.StableKey,
            normalizedSource.StableKey,
            normalizedLocator);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(stableKey));
        // RFC 4122 variant with a name-derived (v5-shaped) identifier. SHA-256
        // supplies the digest; the stable coordinate, not random caller state,
        // supplies idempotency.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}
