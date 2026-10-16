namespace SwarmCore.Knowledge;

/// <summary>
/// Immutable stable source coordinate behind a knowledge contribution.
/// </summary>
public sealed record KnowledgeSourceId
{
    public string Kind { get; }
    public string StableId { get; }
    public string? InstanceId { get; }

    public KnowledgeSourceId(string kind, string stableId, string? instanceId = null)
    {
        var normalizedKind = KnowledgeContractValidation.RequiredTrimmed(kind, nameof(kind)).ToLowerInvariant();
        var normalizedStableId = KnowledgeContractValidation.RequiredTrimmed(stableId, nameof(stableId));
        var normalizedInstanceId = KnowledgeContractValidation.NullIfWhiteSpace(instanceId);
        KnowledgeContractValidation.RequireBounded(
            normalizedKind,
            KnowledgeContractLimits.SourceKindLength,
            nameof(kind));
        KnowledgeContractValidation.RequireBounded(
            normalizedStableId,
            KnowledgeContractLimits.SourceStableIdLength,
            nameof(stableId));
        KnowledgeContractValidation.RequireOptionalBounded(
            normalizedInstanceId,
            KnowledgeContractLimits.SourceInstanceIdLength,
            nameof(instanceId));
        Kind = normalizedKind;
        StableId = normalizedStableId;
        InstanceId = normalizedInstanceId;
    }

    public static KnowledgeSourceId Create(string kind, string stableId, string? instanceId = null)
        => new(kind, stableId, instanceId);

    public string StableKey => KnowledgeStableKey.Encode(Kind, StableId, InstanceId ?? string.Empty);
}

/// <summary>
/// Bounded citation metadata for a source. Retention and privacy-deletion policy
/// are deliberately absent: hosts authorize deletion independently of evidence.
/// </summary>
public sealed record KnowledgeSourceAnchor
{
    public required KnowledgeSourceId SourceId { get; init; }
    public string? Uri { get; init; }
    public string? Title { get; init; }
    public string? Excerpt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}
