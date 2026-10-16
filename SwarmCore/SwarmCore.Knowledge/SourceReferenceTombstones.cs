using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Knowledge;

/// <summary>Updates canonical provenance JSON without discarding unknown fields.</summary>
public static class SourceReferenceTombstones
{
    public static JsonNode? Read(JsonObject reference, string name)
    {
        var matches = reference.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new JsonException($"Ambiguous source-reference field: {name}.");
        return matches.Length == 0 ? null : matches[0].Value;
    }

    public static bool Apply(JsonObject reference, string reason, DateTimeOffset at)
    {
        var changed = false;
        if (Read(reference, "IsTombstoned") is not JsonValue flag || !flag.TryGetValue<bool>(out var tombstoned) || !tombstoned)
        {
            Set(reference, "IsTombstoned", JsonValue.Create(true));
            Set(reference, "TombstoneReason", JsonValue.Create(reason));
            Set(reference, "TombstonedAt", JsonValue.Create(at));
            changed = true;
        }
        foreach (var field in new[] { "ResolvedRecordId", "ResolvedUri" })
        {
            if (Read(reference, field) is null) continue;
            Set(reference, field, null);
            changed = true;
        }
        return changed;
    }

    private static void Set(JsonObject reference, string name, JsonNode? value)
    {
        _ = Read(reference, name); // Reject ambiguous aliases before writing one spelling.
        var key = reference.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key
            ?? (reference.ContainsKey("kind") ? char.ToLowerInvariant(name[0]) + name[1..] : name);
        reference[key] = value;
    }
}
