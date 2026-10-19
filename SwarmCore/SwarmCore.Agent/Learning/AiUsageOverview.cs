using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.Learning;

public sealed record AiUsageGroup(string Provider, string Model, int Calls, long InputTokens, long OutputTokens,
    int FailedCalls, decimal? RecordedCost);
public sealed record AiUsageDay(DateTime Date, long Tokens);
public sealed record AiUsageOverview(DateTime SinceUtc, int Calls, long InputTokens, long OutputTokens,
    int FailedCalls, decimal? RecordedCost, int CallsWithoutCost, bool AtReadLimit,
    IReadOnlyList<AiUsageGroup> Models, IReadOnlyList<AiUsageDay> Days)
{
    public long Tokens => InputTokens + OutputTokens;

    /// <summary>Metadata only. Does not retain prompts, responses, exception text, or account identities.</summary>
    public static AiUsageOverview From(IEnumerable<LlmUsageRecord> records, DateTime sinceUtc, DateTime untilUtc, int readLimit = 5000)
    {
        if (untilUtc < sinceUtc || untilUtc - sinceUtc > TimeSpan.FromDays(366)) throw new ArgumentOutOfRangeException(nameof(sinceUtc));
        // SQLite timestamps are UTC without a Kind; offset-bearing JSON may
        // deserialize as Local. Never compare their wall-clock ticks as UTC.
        static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        sinceUtc = Utc(sinceUtc); untilUtc = Utc(untilUtc);
        var source = records.ToArray();
        var selected = source.Select((record, index) => (record, index))
            .Where(x => Utc(x.record.RequestTimestamp) >= sinceUtc && Utc(x.record.RequestTimestamp) <= untilUtc)
            .GroupBy(x => x.record.PhysicalCallId == Guid.Empty ? "legacy:" + (x.record.UsageId?.ToString() ?? "row-" + x.index) : x.record.PhysicalCallId.ToString())
            .Select(g => g.First().record).ToArray();
        static decimal? Cost(LlmUsageRecord r) => r.ActualCost is >= 0 ? r.ActualCost : r.EstimatedCost > 0 ? r.EstimatedCost : null;
        static decimal? Costs(IEnumerable<LlmUsageRecord> rows)
        {
            var costs = rows.Select(Cost).Where(c => c is not null).ToArray();
            return costs.Length == 0 ? null : costs.Sum(c => c!.Value);
        }
        var groups = selected.GroupBy(r => (r.Provider, r.Model)).Select(g => new AiUsageGroup(g.Key.Provider, g.Key.Model,
            g.Count(), g.Sum(r => (long)Math.Max(0, r.InputTokens)), g.Sum(r => (long)Math.Max(0, r.OutputTokens)),
            g.Count(r => !r.WasSuccessful), Costs(g))).OrderByDescending(g => g.InputTokens + g.OutputTokens).ToArray();
        var days = Enumerable.Range(0, Math.Max(1, (untilUtc.Date - sinceUtc.Date).Days + 1)).Select(i =>
        {
            var date = sinceUtc.Date.AddDays(i);
            return new AiUsageDay(date, selected.Where(r => Utc(r.RequestTimestamp).Date == date)
                .Sum(r => (long)Math.Max(0, r.InputTokens) + Math.Max(0, r.OutputTokens)));
        }).ToArray();
        return new(sinceUtc, selected.Length, groups.Sum(g => g.InputTokens), groups.Sum(g => g.OutputTokens),
            selected.Count(r => !r.WasSuccessful), Costs(selected), selected.Count(r => Cost(r) is null),
            source.Length >= readLimit, groups, days);
    }
}
