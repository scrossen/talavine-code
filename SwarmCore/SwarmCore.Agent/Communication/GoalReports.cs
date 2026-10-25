namespace SwarmCore.Agent.Communication;

/// <summary>Something the owner asked for that the worker held or could not carry out, with the reason and the way out.</summary>
public sealed record GoalHeldItem(string Request, string Reason)
{
    public string? Unblock { get; init; }
}

/// <summary>One day's owner-facing report as the worker writes it. The host supplies identity, attempt and time.</summary>
public sealed record GoalReportDraft(DateOnly Day, string Headline)
{
    /// <summary>Exactly what was achieved toward the goal that day, in plain prose with links. Empty when nothing moved.</summary>
    public string? Achieved { get; init; }
    /// <summary>Measurable movement, for example "stars" → "1 (+0 today)", "downloads" → "12".</summary>
    public IReadOnlyDictionary<string, string>? Metrics { get; init; }
    /// <summary>Owner requests the worker held or could not carry out that day.</summary>
    public IReadOnlyList<GoalHeldItem>? Held { get; init; }
    public IReadOnlyList<string>? NextActions { get; init; }
    /// <summary>Ids or external ids of the results recorded that day, so the report links to the results ledger.</summary>
    public IReadOnlyList<string>? ResultIds { get; init; }
    /// <summary>Where the day's proof lives (a dated report file, measurement receipts). Never credentials.</summary>
    public string? Evidence { get; init; }
}

/// <summary>A durable daily report. One per goal and day; a later record for the same day replaces the earlier one whole (every field, evidence included).</summary>
public sealed record GoalReport(string Id, Guid GoalId, int Attempt, long Sequence, DateTimeOffset RecordedUtc, DateOnly Day, string Headline,
    string? Achieved, IReadOnlyDictionary<string, string> Metrics, IReadOnlyList<GoalHeldItem> Held, IReadOnlyList<string> NextActions,
    IReadOnlyList<string> ResultIds, string Source, string? Evidence)
{
    public long Revision { get; init; } = 1;
}

public sealed record GoalReportPage(IReadOnlyList<GoalReport> Reports, int Total, int Offset, bool HasMore);

/// <summary>
/// The owner-facing daily reports of a goal. Hosts own persistence and admission: only a goal's bound worker may
/// record, a later record for the same day updates the earlier one, and nothing here resumes, pauses or replays work.
/// </summary>
public interface IGoalReportLedger
{
    Task<GoalReport> RecordAsync(Guid goalId, string conversationId, string leaseToken, GoalReportDraft draft, CancellationToken ct = default);
    Task<GoalReportPage> ListAsync(Guid goalId, int offset = 0, int take = 30, CancellationToken ct = default);
}

public static class GoalReports
{
    public const int MaximumHeadlineCharacters = 200;
    public const int MaximumAchievedCharacters = 4000;
    public const int MaximumMetrics = 20;
    public const int MaximumMetricNameCharacters = 40;
    public const int MaximumMetricValueCharacters = 100;
    public const int MaximumHeldItems = 20;
    public const int MaximumHeldRequestCharacters = 300;
    public const int MaximumHeldReasonCharacters = 600;
    public const int MaximumHeldUnblockCharacters = 300;
    public const int MaximumNextActions = 20;
    public const int MaximumNextActionCharacters = 300;
    public const int MaximumResultIds = 200;
    public const int MaximumResultIdCharacters = 200;
    public const int MaximumEvidenceCharacters = 500;

    public const string WorkerGuidance = "Before you end any turn, make sure today's UTC day has an owner report that reflects everything that has "
        + "happened so far: record it with goal_reports.record (omit day and the host fills in today; give day only to back-fill an earlier day) and "
        + "update it as the day goes on rather than waiting for the day to end. Give a headline, exactly what was achieved toward the goal in plain "
        + "prose with links (achieved), the ids of the results you recorded that day (resultIds), everything the owner asked for that you held or could "
        + "not do under held with the reason and what would unblock it, and the next concrete actions. Metrics are numbers you measured in this attempt "
        + "with a tool receipt (stars, downloads, replies received); omit any metric you did not measure, never estimate or carry a number forward. "
        + "A day's new record replaces its earlier one whole, so if today already has a report (goal_reports.list) resend everything that still "
        + "stands, held items included. The owner reads this report and the results ledger; tool receipts are not a report. When you decide not to "
        + "carry out, or cannot carry out, something the owner asked for, say so in your inbox reply at once with the reason, and list it under held "
        + "in today's report. Never report work that did not happen.";

    /// <summary>Normalizes and validates a draft; returns the validated draft or throws <see cref="ArgumentException"/>.</summary>
    public static GoalReportDraft Validate(GoalReportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Day == default) throw new ArgumentException("day is required (yyyy-MM-dd).");
        // Metric names are trimmed; names that collapse to the same text keep the last value given.
        var metrics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in draft.Metrics ?? new Dictionary<string, string>())
        {
            var key = Clean(name, MaximumMetricNameCharacters, "metric name", required: true)!;
            if (key.Contains('\n')) throw new ArgumentException("metric name must be a single line.");
            metrics[key] = Clean(value, MaximumMetricValueCharacters, "metric " + key, required: true)!;
        }
        if (metrics.Count > MaximumMetrics) throw new ArgumentException("At most " + MaximumMetrics + " metrics.");
        var held = (draft.Held ?? []).Select(item => new GoalHeldItem(
            Clean(item?.Request, MaximumHeldRequestCharacters, "held request", required: true)!,
            Clean(item?.Reason, MaximumHeldReasonCharacters, "held reason", required: true)!)
            { Unblock = Clean(item?.Unblock, MaximumHeldUnblockCharacters, "held unblock") }).ToArray();
        if (held.Length > MaximumHeldItems) throw new ArgumentException("At most " + MaximumHeldItems + " held items.");
        var next = (draft.NextActions ?? []).Select(action => Clean(action, MaximumNextActionCharacters, "next action", required: true)!).ToArray();
        if (next.Length > MaximumNextActions) throw new ArgumentException("At most " + MaximumNextActions + " next actions.");
        var ids = (draft.ResultIds ?? []).Select(id => Clean(id, MaximumResultIdCharacters, "result id", required: true)!).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length > MaximumResultIds) throw new ArgumentException("At most " + MaximumResultIds + " result ids.");
        return draft with
        {
            Headline = Clean(draft.Headline, MaximumHeadlineCharacters, "headline", required: true)!,
            Achieved = Clean(draft.Achieved, MaximumAchievedCharacters, "achieved"),
            Metrics = metrics, Held = held, NextActions = next, ResultIds = ids,
            Evidence = Clean(draft.Evidence, MaximumEvidenceCharacters, "evidence"),
        };
    }

    private static string? Clean(string? value, int maximum, string name, bool required = false)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (required) throw new ArgumentException(name + " is required.");
            return null;
        }
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new ArgumentException(name + " contains control characters.");
        text = string.Join('\n', text.Split('\n').Select(line => line.Replace("\r", "").Replace("\t", " ").Trim()));
        if (text.Length > maximum) throw new ArgumentException(name + " is longer than " + maximum + " characters.");
        return text;
    }
}
