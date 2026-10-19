using System.Text.Json;

namespace SwarmCore.HarnessLearning;

/// <summary>Read-only presentation evidence. A recommendation never implies host activation.</summary>
public sealed record HarnessRevisionResult(string RevisionId, int Trials, double Score, long ReportedTokens,
    int UnknownUsageTrials, int FailedTrials);
public sealed record HarnessExperimentOverview(Guid Id, string Workload, DateTimeOffset StartedAt, string Status,
    string Decision, int Trials, long ReportedTokens, int UnknownUsageTrials,
    IReadOnlyList<HarnessRevisionResult> Revisions, IReadOnlyList<HarnessComparison> Comparisons)
{
    public bool HasRecommendation { get; init; }
    public static IReadOnlyList<HarnessExperimentOverview> From(IEnumerable<HarnessExperimentReport> reports)
        => reports.GroupBy(r => r.Id).Select(g =>
        {
            if (g.Select(r => JsonSerializer.Serialize(r)).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidDataException("Conflicting experiment reports share an identity.");
            var report = g.First();
            var revisions = new[] { report.Baseline }.Concat(report.Candidates).Select(revision =>
            {
                var trials = report.Trials.Where(t => t.RevisionHash == revision.Fingerprint).ToArray();
                return new HarnessRevisionResult(revision.Id, trials.Length,
                    trials.Length == 0 ? 0 : trials.Average(t => t.Score),
                    trials.Sum(t => Math.Max(0, t.Tokens ?? 0)), trials.Count(t => t.Tokens is null),
                    trials.Count(t => t.FailureCode is not null));
            }).ToArray();
            var skipped = report.Trials.Count == 0 && report.Comparisons.Count > 0 && report.Comparisons.All(c => c.Reason == "already_evaluated");
            return new HarnessExperimentOverview(report.Id, report.Scope.Workload, report.StartedAt,
                report.StopReason is not null ? "Stopped" : skipped ? "Already evaluated" : "Completed",
                report.StopReason is not null ? Explain(report.StopReason)
                    : report.RecommendedRevisionId is { } winner ? $"{winner} recommended; activation is not recorded here."
                    : skipped ? "Retained history avoided repeat model calls." : "Baseline retained. " + Explain(report.Comparisons.FirstOrDefault()?.Reason),
                report.Trials.Count, report.Trials.Sum(t => Math.Max(0, t.Tokens ?? 0)),
                report.Trials.Count(t => t.Tokens is null), revisions, report.Comparisons)
                { HasRecommendation = report.StopReason is null && report.RecommendedRevisionId is not null };
        }).OrderByDescending(r => r.StartedAt).ToArray();

    public static string Explain(string? reason) => reason switch
    {
        "development_gain_below_floor" => "Development gain did not clear the minimum and noise checks.",
        "heldout_gain_below_floor" => "Held-out gain did not clear the minimum and noise checks.",
        "token_cost_regression" => "Token use increased beyond the allowed limit.",
        "latency_regression" => "Execution time increased beyond the allowed limit.",
        "invalid_usage" => "Reported usage was invalid or exceeded the per-trial token limit.",
        "unknown_usage" => "Token usage was unavailable; further trials stopped.",
        "invariant_failed" => "An isolation or correctness boundary failed.",
        "trial_timeout" or "deadline" => "The execution time limit was reached.",
        "token_budget" => "The experiment token budget was reached.",
        "execution_failed" => "Execution failed before a complete receipt was available.",
        "already_evaluated" => "This candidate already has retained evidence.",
        "cancelled" => "The experiment was cancelled.",
        null => "No selection result was recorded.",
        _ => reason.Replace('_', ' ')
    };
}
