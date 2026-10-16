namespace SwarmCore.Knowledge;

/// <summary>Source assertions, not execution receipts. Host supplies authorized partition and stable obligation identity.</summary>
public sealed record KnowledgeStateAssertion(Guid Id, string SourceId, DateTimeOffset ObservedAt,
    DateTimeOffset EffectiveAt, DateTimeOffset RecordedAt, string Value, int Authority,
    string? CorrectsSourceId = null);

public sealed record KnowledgeStateResolution(IReadOnlySet<Guid> CurrentIds, string Status);

/// <summary>
/// Version 1 bitemporal policy. Due time is deliberately absent. A reminder
/// cannot reopen a settlement; an explicit source correction can. Equal-time
/// contradictory assertions remain contested; row/import order never resolves truth.
/// </summary>
public static class KnowledgeStateResolver
{
    public const int Version = 1;

    public static KnowledgeStateResolution Resolve(IEnumerable<KnowledgeStateAssertion> assertions,
        DateTimeOffset effectiveAt, DateTimeOffset? knownAt = null)
    {
        var rows = assertions.Where(a => a.EffectiveAt <= effectiveAt
            && (!knownAt.HasValue || a.RecordedAt <= knownAt.Value)).ToArray();
        if (rows.Length == 0) return new(new HashSet<Guid>(), "unknown");
        var bestAuthority = rows.Max(a => a.Authority);
        rows = rows.Where(a => a.Authority == bestAuthority).ToArray();
        var corrected = rows.Where(a => !string.IsNullOrWhiteSpace(a.CorrectsSourceId))
            .SelectMany(c => rows.Where(r => r.Id != c.Id && r.SourceId != c.SourceId
                && r.SourceId == c.CorrectsSourceId && r.EffectiveAt < c.EffectiveAt).Select(r => r.Id)).ToHashSet();
        rows = rows.Where(r => !corrected.Contains(r.Id)).ToArray();
        var known = rows.Where(r => NormalizeValue(r.Value) != "unknown").ToArray();
        if (known.Length > 0) rows = known;
        var terminal = rows.Where(r => NormalizeValue(r.Value) is "settled" or "cancelled").ToArray();
        if (terminal.Length > 0)
        {
            // Later open assertions without an explicit correction are conflicting
            // evidence, never sufficient to erase the terminal assertion.
            var lastTerminal = terminal.Max(r => r.EffectiveAt);
            rows = rows.Where(r => r.EffectiveAt >= lastTerminal).ToArray();
        }
        else
        {
            var latest = rows.Max(r => r.EffectiveAt);
            rows = rows.Where(r => r.EffectiveAt == latest).ToArray();
        }
        var values = rows.Select(r => NormalizeValue(r.Value)).Distinct(StringComparer.Ordinal).ToArray();
        var status = values.Length > 1 ? "contested" : values.SingleOrDefault() ?? "unknown";
        return new(rows.Select(r => r.Id).ToHashSet(), status);
    }

    public static string NormalizeValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "paid" or "settled" or "closed" or "completed" or "resolved" => "settled",
        "cancelled" or "canceled" => "cancelled",
        "open" or "pending" or "unpaid" or "due" => "open",
        "contested" or "disputed" => "contested",
        _ => "unknown"
    };
}
