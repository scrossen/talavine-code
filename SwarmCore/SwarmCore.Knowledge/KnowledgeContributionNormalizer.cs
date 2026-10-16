using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Knowledge;

/// <summary>
/// Canonicalizes deterministic source projections before persistence or sync.
/// The JSON fingerprint contract intentionally matches the current
/// Talavine graph ledger so hosts can migrate without graph drift.
/// </summary>
public static class KnowledgeContributionNormalizer
{
    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web);

    public static KnowledgeContributionBundle Normalize(KnowledgeContributionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(bundle.Partition);
        ArgumentNullException.ThrowIfNull(bundle.Source);
        ArgumentNullException.ThrowIfNull(bundle.Entities);
        ArgumentNullException.ThrowIfNull(bundle.Relations);

        var partition = KnowledgePartition.Create(bundle.Partition.Kind, bundle.Partition.Id);
        var source = KnowledgeEvidenceNormalizer.NormalizeAnchor(bundle.Source);
        if (bundle.ProjectionVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(bundle.ProjectionVersion), "Projection version must be positive.");
        var inputFingerprint = KnowledgeContractValidation.RequiredTrimmed(
            bundle.InputFingerprintSha256,
            nameof(bundle.InputFingerprintSha256)).ToLowerInvariant();
        KnowledgeContractValidation.RequireSha256(inputFingerprint, nameof(bundle.InputFingerprintSha256));
        var sourceFingerprint = KnowledgeContractValidation.NullIfWhiteSpace(bundle.SourceContentFingerprintSha256)?.ToLowerInvariant();
        if (sourceFingerprint is not null)
            KnowledgeContractValidation.RequireSha256(sourceFingerprint, nameof(bundle.SourceContentFingerprintSha256));
        if (bundle.Entities.Count > KnowledgeContractLimits.EntitiesPerBundle)
            throw new ArgumentException(
                $"A contribution cannot contain more than {KnowledgeContractLimits.EntitiesPerBundle} entities.",
                nameof(bundle.Entities));
        if (bundle.Relations.Count > KnowledgeContractLimits.RelationsPerBundle)
            throw new ArgumentException(
                $"A contribution cannot contain more than {KnowledgeContractLimits.RelationsPerBundle} relations.",
                nameof(bundle.Relations));

        var entities = bundle.Entities
            .Where(static entity => !string.IsNullOrWhiteSpace(entity.CanonicalKey)
                && !string.IsNullOrWhiteSpace(entity.Name))
            .Select(NormalizeEntity)
            .GroupBy(static entity => entity.CanonicalKey.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(entity => !string.IsNullOrWhiteSpace(entity.Description)).First() with
            {
                CanonicalKey = group.Key,
                Name = group.OrderByDescending(entity => !string.IsNullOrWhiteSpace(entity.Name)).First().Name.Trim(),
                MentionCount = Math.Max(1, group.Max(entity => entity.MentionCount))
            })
            .OrderBy(static entity => entity.CanonicalKey, StringComparer.Ordinal)
            .ToList();

        var relations = bundle.Relations
            .Where(static relation => !string.IsNullOrWhiteSpace(relation.SubjectCanonicalKey)
                && !string.IsNullOrWhiteSpace(relation.ObjectCanonicalKey))
            .Select(NormalizeRelation)
            .Where(static relation => !string.Equals(
                relation.SubjectCanonicalKey,
                relation.ObjectCanonicalKey,
                StringComparison.Ordinal))
            .GroupBy(static relation => (
                relation.SubjectCanonicalKey,
                relation.ObjectCanonicalKey,
                relation.RelationType))
            .Select(static group => group.OrderByDescending(relation => !string.IsNullOrWhiteSpace(relation.Predicate)).First() with
            {
                MentionCount = Math.Max(1, group.Max(relation => relation.MentionCount))
            })
            .OrderBy(static relation => relation.SubjectCanonicalKey, StringComparer.Ordinal)
            .ThenBy(static relation => relation.ObjectCanonicalKey, StringComparer.Ordinal)
            .ThenBy(static relation => relation.RelationType)
            .ToList();

        return bundle with
        {
            Partition = partition,
            Source = source,
            InputFingerprintSha256 = inputFingerprint,
            SourceContentFingerprintSha256 = sourceFingerprint,
            ProjectionFingerprintSha256 = null,
            Entities = entities,
            Relations = relations
        };
    }

    public static KnowledgeEntityContribution NormalizeEntity(KnowledgeEntityContribution entity)
    {
        var canonicalKey = entity.CanonicalKey.Trim().ToLowerInvariant();
        var name = entity.Name.Trim();
        // Preserve the legacy projection contract for optional entity payloads;
        // only identity/display fields are canonicalized. Bounds still prevent
        // a host or model from smuggling an unbounded source body into a row.
        var description = entity.Description;
        var aliasesJson = entity.AliasesJson;
        KnowledgeContractValidation.RequireBounded(
            canonicalKey,
            KnowledgeContractLimits.EntityCanonicalKeyLength,
            nameof(entity.CanonicalKey));
        KnowledgeContractValidation.RequireBounded(
            name,
            KnowledgeContractLimits.EntityNameLength,
            nameof(entity.Name));
        KnowledgeContractValidation.RequireOptionalBounded(
            description,
            KnowledgeContractLimits.EntityDescriptionLength,
            nameof(entity.Description));
        KnowledgeContractValidation.RequireOptionalBounded(
            aliasesJson,
            KnowledgeContractLimits.EntityAliasesJsonLength,
            nameof(entity.AliasesJson));
        return entity with
        {
            CanonicalKey = canonicalKey,
            Name = name,
            Description = description,
            AliasesJson = aliasesJson,
            MentionCount = Math.Max(1, entity.MentionCount)
        };
    }

    public static KnowledgeRelationContribution NormalizeRelation(KnowledgeRelationContribution relation)
    {
        var subject = relation.SubjectCanonicalKey.Trim().ToLowerInvariant();
        var obj = relation.ObjectCanonicalKey.Trim().ToLowerInvariant();
        if (relation.RelationType is KnowledgeRelationType.CorrespondsWith or KnowledgeRelationType.MentionsWith
            && string.CompareOrdinal(subject, obj) > 0)
        {
            (subject, obj) = (obj, subject);
        }

        var predicate = KnowledgeContractValidation.NullIfWhiteSpace(relation.Predicate);
        KnowledgeContractValidation.RequireBounded(
            subject,
            KnowledgeContractLimits.EntityCanonicalKeyLength,
            nameof(relation.SubjectCanonicalKey));
        KnowledgeContractValidation.RequireBounded(
            obj,
            KnowledgeContractLimits.EntityCanonicalKeyLength,
            nameof(relation.ObjectCanonicalKey));
        KnowledgeContractValidation.RequireOptionalBounded(
            predicate,
            KnowledgeContractLimits.RelationPredicateLength,
            nameof(relation.Predicate));

        return relation with
        {
            SubjectCanonicalKey = subject,
            ObjectCanonicalKey = obj,
            Predicate = predicate,
            MentionCount = Math.Max(1, relation.MentionCount)
        };
    }

    public static KnowledgeContributionBundle NormalizeAndFingerprint(KnowledgeContributionBundle bundle)
    {
        var normalized = Normalize(bundle);
        return normalized with { ProjectionFingerprintSha256 = ComputeNormalizedFingerprint(normalized) };
    }

    public static string ComputeProjectionFingerprint(KnowledgeContributionBundle bundle)
    {
        var normalized = Normalize(bundle);
        return ComputeNormalizedFingerprint(normalized);
    }

    private static string ComputeNormalizedFingerprint(KnowledgeContributionBundle normalizedBundle)
    {
        var projection = new
        {
            normalizedBundle.ProjectionVersion,
            Entities = normalizedBundle.Entities.Select(static entity => new
            {
                Type = (int)entity.EntityType,
                entity.CanonicalKey,
                entity.Name,
                entity.Description,
                entity.AliasesJson,
                entity.MentionCount
            }),
            Relations = normalizedBundle.Relations.Select(static relation => new
            {
                relation.SubjectCanonicalKey,
                relation.ObjectCanonicalKey,
                Type = (int)relation.RelationType,
                relation.Predicate,
                relation.MentionCount
            })
        };
        // Preserve historical fingerprint bytes; version 5 adds captured source
        // evidence without reinterpreting bundles written by older peers.
        var canonical = normalizedBundle.ProjectionVersion >= 5
            ? JsonSerializer.Serialize(new { normalizedBundle.SourceContentFingerprintSha256, Projection = projection }, FingerprintJson)
            : JsonSerializer.Serialize(projection, FingerprintJson);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static DateTimeOffset? EnsureUtc(DateTimeOffset? value)
        => value?.ToUniversalTime();

}
