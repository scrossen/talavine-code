using SwarmCore.Abstractions.Context;

namespace SwarmCore.Knowledge;

public static class ContextEvidenceRevalidationPolicy
{
    public static string SourceSnapshotKey(ContextSource source)
        => System.Text.Json.JsonSerializer.Serialize(source with { Alias = null });

    public static ContextPack AssignAliases(ContextPack pack, int firstAlias = 1)
    {
        var reserved = pack.Sources.Where(s => s.Alias is not null).Select(s => s.Alias!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicting = pack.Sources.Where(s => s.Alias is not null).GroupBy(s => s.Alias!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = Math.Max(1, firstAlias);
        var assigned = pack.Sources.Where(s => s.Alias is null || !conflicting.Contains(s.Alias)).Select(s =>
        {
            if (s.Alias is not null) return s;
            while (!reserved.Add($"S{next}")) next++;
            return s with { Alias = $"S{next++}" };
        }).ToArray();
        var retained = assigned.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return pack with
        {
            Sources = assigned,
            Items = pack.Items.Where(i => i.SourceIds.All(retained.Contains)).ToArray(),
            Coverage = conflicting.Count == 0 ? pack.Coverage : pack.Coverage with
            { Status = "incomplete", SupportsAbsence = false, Gaps = pack.Coverage.Gaps.Append("conflicting-source-alias").Distinct().ToArray() }
        };
    }

    public static ContextPack Apply(ContextPack pack, IReadOnlyList<ContextEvidenceValidation> sources,
        IReadOnlyList<ContextEvidenceValidation> items)
    {
        var conflictingIds = pack.Sources.GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Select(SourceSnapshotKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var effectiveSources = pack.Sources.Select(s => conflictingIds.Contains(s.Id)
            ? new ContextEvidenceValidation(s.Id, false, "conflicting-source-payload") : Check(s.Id, sources)).ToArray();
        var effectiveItems = pack.Items.Select(i => i.SourceIds.Count == 0
            ? new ContextEvidenceValidation(i.Id, false, "item-without-source-support") : Check(i.Id, items)).ToArray();
        var validSources = effectiveSources.Where(s => s.IsValid).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var invalidItems = effectiveItems.Where(i => !i.IsValid).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        // An aggregate whose support changed must be rebuilt, not presented with a partial citation list.
        var retainedItems = pack.Items.Where(i => !invalidItems.Contains(i.Id)
            && i.SourceIds.All(validSources.Contains)).ToArray();
        var usedSources = retainedItems.SelectMany(i => i.SourceIds).ToHashSet(StringComparer.Ordinal);
        var retainedSources = pack.Sources.Where(s => validSources.Contains(s.Id)
            && (pack.Items.Count == 0 || usedSources.Contains(s.Id))).ToArray();
        return pack with
        {
            Items = retainedItems, Sources = retainedSources,
            EstimatedTokens = (int)Math.Ceiling(retainedItems.Sum(i => i.Text.Length) / 4d),
            Coverage = pack.Coverage with
            {
                SourceValidation = "revalidated",
                Status = checksFailed() ? "incomplete" : pack.Coverage.Status,
                SupportsAbsence = !checksFailed() && pack.Coverage.SupportsAbsence,
                Gaps = pack.Coverage.Gaps.Concat(effectiveSources.Concat(effectiveItems).Where(c => !c.IsValid).Select(c => c.Reason)).Distinct(StringComparer.Ordinal).ToArray()
            }
        };
        bool checksFailed() => effectiveSources.Any(s => !s.IsValid) || effectiveItems.Any(i => !i.IsValid);
    }

    private static ContextEvidenceValidation Check(string id, IReadOnlyList<ContextEvidenceValidation> checks)
    {
        var matches = checks.Where(c => c.Id == id).ToArray();
        return matches.Length == 1 ? matches[0] : new(id, false, "missing-or-ambiguous-validation");
    }
}
