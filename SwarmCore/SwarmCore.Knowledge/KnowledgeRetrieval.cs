namespace SwarmCore.Knowledge;

/// <summary>
/// Intent for organizational-knowledge retrieval. Current is the safe default;
/// historical results must be requested explicitly and remain lifecycle-labelled.
/// </summary>
public enum KnowledgeRetrievalScope
{
    Current = 0,
    Historical = 1,
    CurrentAndHistorical = 2
}

/// <summary>
/// One bounded, relevance-ranked, aggregate-consistent retrieval request. A host
/// store must read artifacts, their aggregate children, and returned evidence in
/// one snapshot/tenant lock so a citation can never outlive its support row.
/// </summary>
public sealed record KnowledgeRetrievalQuery
{
    public required KnowledgePartition Partition { get; init; }
    public required string QueryText { get; init; }
    public IReadOnlySet<KnowledgeArtifactKind> Kinds { get; init; } = new HashSet<KnowledgeArtifactKind>();
    public KnowledgeRetrievalScope Scope { get; init; } = KnowledgeRetrievalScope.Current;
    public int Limit { get; init; } = 6;
    public int EvidencePerArtifactLimit { get; init; } = 8;
    public int TotalEvidenceLimit { get; init; } = 32;
}

public sealed record KnowledgeRetrievalResult(
    IReadOnlyList<KnowledgeRetrievalHit> Hits,
    bool HasMore);

public sealed record KnowledgeRetrievalHit(
    KnowledgeArtifact Artifact,
    IReadOnlyList<KnowledgeEvidenceRecord> Evidence,
    double Relevance,
    int TotalEvidenceCount,
    bool EvidenceTruncated);

public static class KnowledgeRetrievalNormalizer
{
    public const int MaximumArtifacts = 8;
    public const int MaximumEvidencePerArtifact = 12;
    public const int MaximumTotalEvidence = 48;

    public static KnowledgeRetrievalQuery Normalize(KnowledgeRetrievalQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Partition);
        ArgumentNullException.ThrowIfNull(query.Kinds);
        KnowledgeContractValidation.RequireDefined(query.Scope, nameof(query.Scope));
        foreach (var kind in query.Kinds)
            KnowledgeContractValidation.RequireDefined(kind, nameof(query.Kinds));
        if (query.Limit is < 1 or > MaximumArtifacts)
            throw new ArgumentOutOfRangeException(nameof(query.Limit));
        if (query.EvidencePerArtifactLimit is < 1 or > MaximumEvidencePerArtifact)
            throw new ArgumentOutOfRangeException(nameof(query.EvidencePerArtifactLimit));
        if (query.TotalEvidenceLimit is < 1 or > MaximumTotalEvidence)
            throw new ArgumentOutOfRangeException(nameof(query.TotalEvidenceLimit));

        var queryText = KnowledgeContractValidation.RequiredTrimmed(query.QueryText, nameof(query.QueryText));
        KnowledgeContractValidation.RequireBounded(
            queryText,
            KnowledgeContractLimits.ArtifactSearchTextLength,
            nameof(query.QueryText));
        if (KnowledgeQueryTerms.Extract(queryText).Count == 0)
            throw new ArgumentException("A retrieval query needs at least one meaningful term.", nameof(query.QueryText));

        return query with
        {
            Partition = KnowledgePartition.Create(query.Partition.Kind, query.Partition.Id),
            QueryText = queryText,
            Kinds = new HashSet<KnowledgeArtifactKind>(query.Kinds)
        };
    }

    public static IReadOnlySet<KnowledgeArtifactLifecycle> Lifecycles(KnowledgeRetrievalScope scope)
        => scope switch
        {
            KnowledgeRetrievalScope.Current => new HashSet<KnowledgeArtifactLifecycle>
            {
                KnowledgeArtifactLifecycle.Active
            },
            KnowledgeRetrievalScope.Historical => new HashSet<KnowledgeArtifactLifecycle>
            {
                KnowledgeArtifactLifecycle.Superseded,
                KnowledgeArtifactLifecycle.Revoked,
                KnowledgeArtifactLifecycle.Expired
            },
            KnowledgeRetrievalScope.CurrentAndHistorical => new HashSet<KnowledgeArtifactLifecycle>
            {
                KnowledgeArtifactLifecycle.Active,
                KnowledgeArtifactLifecycle.Superseded,
                KnowledgeArtifactLifecycle.Revoked,
                KnowledgeArtifactLifecycle.Expired
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
}

/// <summary>Deterministic host-neutral lexical ranking used by retrieval adapters and tests.</summary>
public static class KnowledgeRetrievalRanking
{
    public static double Score(KnowledgeArtifact artifact, string queryText)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var query = queryText.Trim();
        var terms = KnowledgeQueryTerms.Extract(query);
        var score = artifact.EvidenceSupport == KnowledgeEvidenceSupportStatus.Supported ? 20d : 10d;
        if (artifact.CanonicalKey.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 1000;
        if (artifact.Title.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 900;
        if (artifact.CanonicalKey.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 180;
        if (artifact.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 160;
        if (artifact.Statement.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 80;
        foreach (var term in terms)
        {
            if (artifact.CanonicalKey.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 40;
            if (artifact.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 30;
            if (artifact.Domain?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) score += 20;
            if (artifact.Statement.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 8;
        }
        return score;
    }

    public static string BuildSearchText(KnowledgeArtifact artifact)
        => string.Join('\n', artifact.CanonicalKey, artifact.Title, artifact.Domain, artifact.Statement)
            .ToLowerInvariant();
}
