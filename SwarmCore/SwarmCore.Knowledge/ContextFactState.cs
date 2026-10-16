using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.Context;

namespace SwarmCore.Knowledge;

/// <summary>Compatible adapter: lifecycle evidence is versioned in the existing synced MetadataJson.</summary>
public static class ContextFactState
{
    public const string ProjectionVersion = "4";

    /// <summary>Source kind of a row the user confirmed or corrected; its id is the user's handle.</summary>
    public const string UserAssertionSourceKind = "user-assertion";

    /// <summary>
    /// A user assertion, or the pre-assertion copy retained beside it for
    /// knowledge-time reads. Source re-projection owns derived facts only and
    /// must never delete, re-create or overwrite these rows.
    /// </summary>
    public static bool IsUserOwned(ContextFact fact)
        => string.Equals(fact.SourceKind, UserAssertionSourceKind, StringComparison.Ordinal)
           || Metadata(fact).GetValueOrDefault("assertionHistory") == "true";
    /// <summary>Binds a claim to its current source and temporal authority. Retrieval scores
    /// and update bookkeeping do not change the underlying evidence.</summary>
    public static string Fingerprint(ContextFact fact)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { fact.Text, fact.Kind, fact.NormalizedKey, fact.StateKey, fact.StateStatus,
                fact.SupersededById, fact.SourceId, fact.SourceAccountId, fact.SourceKind, fact.SourceRecordId,
                fact.SourceUri, fact.SourceExcerpt, fact.IsSensitive, fact.ValidFrom, fact.ExpiresAt,
                fact.ObservedAt, fact.CreatedAt, fact.IsUserConfirmed,
                Metadata = new SortedDictionary<string, string>(Metadata(fact), StringComparer.Ordinal) })))).ToLowerInvariant();
    public static Dictionary<string, string> Metadata(ContextFact fact)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in MetadataObject(fact))
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                result[key] = text;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the full metadata object for mutation paths. Unlike the scalar
    /// compatibility view above, this preserves arrays and nested proof data.
    /// </summary>
    public static JsonObject MetadataObject(ContextFact fact)
    {
        try { return JsonNode.Parse(fact.MetadataJson) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    public static KnowledgeStateAssertion Assertion(ContextFact fact)
    {
        var metadata = Metadata(fact);
        var authority = fact.IsUserConfirmed ? 3 : metadata.GetValueOrDefault("sourceAuthority") switch
        {
            "user" or "source:user-authored" => 3,
            "trusted-contact" or "source:trusted-contact" => 2,
            _ => 1
        };
        var recorded = DateTimeOffset.TryParse(metadata.GetValueOrDefault("recordedAt"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var time) ? time : fact.CreatedAt;
        return new(fact.Id, fact.SourceId, fact.ObservedAt, fact.ValidFrom ?? fact.ObservedAt,
            recorded, metadata.GetValueOrDefault("paymentStatus") ?? metadata.GetValueOrDefault("status") ?? "unknown",
            authority, metadata.GetValueOrDefault("correctsSourceId"));
    }

    public static bool HasRemovalBarrier(ContextFact fact, IReadOnlyCollection<ContextFact> siblings)
    {
        var key = Metadata(fact).GetValueOrDefault("supersededByKey");
        return !string.IsNullOrWhiteSpace(key) ? !siblings.Any(s => s.NormalizedKey == key)
            : fact.SupersededById.HasValue && !siblings.Any(s => s.Id == fact.SupersededById.Value);
    }
}
