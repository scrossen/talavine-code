using System.Text.Json;
using SwarmCore.Abstractions.Knowledge;

namespace SwarmCore.Knowledge;

/// <summary>The persisted evidence contract, shared by ingestion, sync and upgrades.</summary>
public static class KnowledgeEvidenceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<KnowledgeEvidence> Read(string json) =>
        JsonSerializer.Deserialize<List<KnowledgeEvidence>>(json, Options)
        ?? throw new InvalidDataException("Knowledge evidence must be an array.");

    public static bool IsEmptyLegacyRecord(KnowledgeEvidence evidence) =>
        evidence.SourceAuthority is null && evidence.SourceKind is null
        && evidence.SourceTitle is null && evidence.SourceAccountId is null
        && evidence.SourceMessageId is null && evidence.SourceRecordId is null
        && evidence.SourceUri is null && evidence.Excerpt is null
        && evidence.ObservedAt == default;

    public static string Write(IEnumerable<KnowledgeEvidence> evidence)
    {
        var rows = evidence.ToArray();
        if (rows.Any(e => e is null || string.IsNullOrWhiteSpace(e.SourceAuthority)
            || e.SourceKind is null || e.SourceTitle is null || e.ObservedAt == default))
            throw new InvalidDataException("Knowledge evidence is missing source provenance; source reprojection is required.");
        foreach (var row in rows)
            KnowledgeMessageIdentity.Validate(row.SourceAccountId, row.SourceMessageId);
        return JsonSerializer.Serialize(rows, Options);
    }
}
