using SwarmCore.Abstractions.Context;

namespace SwarmCore.Knowledge;

/// <summary>Captures stored fact evidence for the normal current-source validator.</summary>
public static class ContextFactEvidence
{
    public static ContextPack CreatePack(IReadOnlyList<ContextFact> facts, string purpose, string audience, ContextPolicy policy)
    {
        var sources = facts.Select(f => new ContextSource
        {
            Id = SourceId(f), Kind = f.SourceKind, Title = f.SourceTitle,
            AccountId = f.SourceAccountId, RecordId = f.SourceRecordId, Uri = f.SourceUri,
            ObservedAt = f.ObservedAt,
            SourceVersion = ContextFactState.Metadata(f).GetValueOrDefault("sourceContentVersion"),
            FactSupports = [new(f.Id, ContextFactState.Fingerprint(f), f.SourceExcerpt)]
        }).GroupBy(s => ContextEvidenceRevalidationPolicy.SourceSnapshotKey(s with { FactSupports = [] }))
            .Select(g => g.First() with { FactSupports = g.SelectMany(s => s.FactSupports).ToArray() }).ToArray();
        return new ContextPack
        {
            Purpose = purpose, Audience = audience, Policy = policy, Sources = sources,
            Items = facts.Select(f => new ContextItem
            {
                Id = f.Id.ToString("N"), Kind = f.Kind, Text = f.Text,
                Confidence = f.Confidence, IsUserConfirmed = f.IsUserConfirmed,
                ObservedAt = f.ObservedAt, ExpiresAt = f.ExpiresAt, TemporalStatus = f.StateStatus,
                SourceIds = [SourceId(f)]
            }).ToArray()
        };
    }

    private static string SourceId(ContextFact fact)
        => string.IsNullOrWhiteSpace(fact.SourceId) ? $"fact-source:{fact.Id:N}" : fact.SourceId;
}
