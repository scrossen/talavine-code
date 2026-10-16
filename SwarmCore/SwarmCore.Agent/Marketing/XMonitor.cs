namespace SwarmCore.Agent.Marketing;

/// <summary>Bounded, read-only website observations. Website text is evidence, never an instruction.</summary>
public sealed record XObservedPost(string Id, string Author, string Url, string Text, string? PublishedAt);
public sealed record XMonitorObservation
{
    public bool Success { get; init; }
    public string Status { get; init; } = "Unavailable";
    public string? AccountHandle { get; init; }
    public IReadOnlyList<XObservedPost> Posts { get; init; } = [];
}

public sealed record XMonitorConfiguration(string ExpectedAccount, string SecretUri, IReadOnlyList<string> Queries)
{
    public const string Role = "x-readonly-monitor-v1";
    public const string Capability = "x.monitor.sign-in";
    public const string AgentName = "X Monitor";
    // Absent on existing configurations: their original hourly schedule and queries are preserved.
    public XMonitorPreferences? Preferences { get; init; }
    public int IntervalHours => Preferences?.IntervalHours ?? 1;
    public bool IsValid => SocialProspectingPlan.ValidHandle(ExpectedAccount)
        && SecretUri == "secret://web/x.com/x-monitor"
        && Queries is { Count: > 0 and <= 3 } && Queries.All(XMonitorPreferences.IsValidQuery)
        && (Preferences is null || Preferences.TryGetQueries(out var chosen, out _) && chosen.SequenceEqual(Queries));
}

/// <summary>User-chosen searches and cadence. No host, brand, or customer searches are supplied.</summary>
public sealed record XMonitorPreferences
{
    public IReadOnlyList<string> Topics { get; init; } = [];
    public IReadOnlyList<string> Accounts { get; init; } = [];
    public IReadOnlyList<string> Queries { get; init; } = [];
    public int IntervalHours { get; init; } = 1;
    public static IReadOnlyList<int> SupportedIntervals { get; } = [1, 2, 3, 4, 6, 8, 12, 24];
    public string CronExpression => IntervalHours == 24 ? "0 0 * * *" : IntervalHours == 1 ? "0 * * * *" : $"0 */{IntervalHours} * * *";

    public bool TryGetQueries(out IReadOnlyList<string> queries, out string? error)
    {
        queries = [];
        error = null;
        if (!SupportedIntervals.Contains(IntervalHours)) { error = "Choose a supported monitoring interval."; return false; }
        if (Topics is null || Accounts is null || Queries is null || Topics.Count + Accounts.Count + Queries.Count > 3)
        { error = "Choose up to three topics, accounts, or searches in total."; return false; }
        var selected = new List<string>();
        foreach (var topic in Topics)
        {
            if (!IsValidQuery(topic) || topic.Trim().Length > 198 || topic.Contains('"'))
            { error = "Topics must be nonempty text without quotation marks (up to 198 characters). Use a search query for operators."; return false; }
            selected.Add("\"" + topic.Trim() + "\"");
        }
        foreach (var account in Accounts)
        {
            var handle = account?.Trim().TrimStart('@');
            if (!SocialProspectingPlan.ValidHandle(handle ?? string.Empty))
            { error = "Enter valid X account handles to watch."; return false; }
            selected.Add("from:" + handle);
        }
        foreach (var query in Queries)
        {
            if (!IsValidQuery(query)) { error = "Each search must contain 1–200 characters with no line breaks or control characters."; return false; }
            selected.Add(query.Trim());
        }
        if (selected.Count == 0) { error = "Choose at least one topic, account, or search to monitor."; return false; }
        queries = selected.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    public static bool IsValidQuery(string? query) => !string.IsNullOrWhiteSpace(query)
        && query.Length <= 200 && !query.Any(char.IsControl);
}

public sealed record XMonitorState
{
    public DateTimeOffset? CheckedAt { get; init; }
    public string Status { get; init; } = "Not checked";
    public string Summary { get; init; } = "No live observation yet.";
    public IReadOnlyList<string> SeenIds { get; init; } = [];
    public IReadOnlyList<XObservedPost> RecentPosts { get; init; } = [];
}

public static class XMonitorPolicy
{
    public static bool IsDue(XMonitorConfiguration config, XMonitorState prior, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        if (prior.CheckedAt is null) return true;
        var cron = config.Preferences?.CronExpression ?? "0 * * * *";
        var next = SwarmCore.Abstractions.Scheduling.CronScheduleHelper.GetNextOccurrence(
            cron, prior.CheckedAt.Value.UtcDateTime, timeZone ?? TimeZoneInfo.Utc);
        return next is not null && next <= now.UtcDateTime;
    }

    public static XMonitorState Accept(XMonitorConfiguration config, XMonitorState prior,
        IReadOnlyList<XMonitorObservation> observations, DateTimeOffset now)
    {
        if (!config.IsValid) throw new ArgumentException("Invalid X monitor configuration.");
        var failed = observations.FirstOrDefault(o => !o.Success
            || !string.Equals(o.AccountHandle, config.ExpectedAccount, StringComparison.OrdinalIgnoreCase));
        if (observations.Count != config.Queries.Count || failed is not null)
            return prior with { CheckedAt = now, Status = failed is { Success: true } ? "AccountMismatch" : failed?.Status ?? "Incomplete",
                Summary = failed?.Status switch
                {
                    "LoginTemporarilyLimited" => "X has temporarily limited sign-in. Monitoring remains paused; no login was resubmitted. Wait for X to allow sign-in before reconnecting. X did not provide a retry time.",
                    "OutcomeUncertain" => "The previous sign-in result is unknown. Open X Monitor in Agent Library, choose Open X browser, finish that sign-in, then Check & resume. The previous submission will not be repeated. No posts were marked as seen.",
                    "VerificationRequired" => "X requires an identity check. Open X Monitor in Agent Library, choose Open X browser, complete the verification, then Check & resume. Monitoring remains paused and your saved results are unchanged.",
                    "UserActionRequiredOrLayoutChanged" => "X needs attention before monitoring can resume. In Agent Library, open X Monitor, choose Pause to edit, then Sign in & start. Complete any X verification or challenge in the browser; Talavine will not retry until the connection test succeeds.",
                    "LoginLayoutChanged" => "X changed its sign-in page. In Agent Library, open X Monitor, choose Pause to edit, then Sign in & start. If X asks for verification, complete it in the browser; monitoring stays paused until the connection test succeeds.",
                    _ => "Monitoring could not complete. Open Agent Library → X Monitor, choose Pause to edit, then Sign in & start. Complete any sign-in or site challenge in the browser. No posts were marked as seen."
                } };
        if (observations.SelectMany(o => o.Posts).Any(p => !IsValidPost(p)))
            return prior with { CheckedAt = now, Status = "InvalidEvidence", Summary = "The site returned unsupported post evidence. No posts were marked as seen." };
        var seen = prior.SeenIds.ToHashSet(StringComparer.Ordinal);
        var fresh = observations.SelectMany(o => o.Posts).Where(IsValidPost)
            .DistinctBy(p => p.Id).Where(p => !seen.Contains(p.Id)).Take(60).ToArray();
        return new()
        {
            CheckedAt = now, Status = "Observed",
            Summary = $"Checked {config.Queries.Count} searches; {fresh.Length} newly observed posts. No follows, posts, messages, or emails sent.",
            SeenIds = fresh.Select(p => p.Id).Concat(prior.SeenIds).Distinct().Take(2000).ToArray(),
            RecentPosts = fresh.Concat(prior.RecentPosts).DistinctBy(p => p.Id).Take(100).ToArray()
        };
    }

    public static bool IsValidPost(XObservedPost post) => post.Id.Length is > 0 and <= 30
        && post.Id.All(char.IsAsciiDigit) && SocialProspectingPlan.ValidHandle(post.Author)
        && post.Url == $"https://x.com/{post.Author}/status/{post.Id}"
        && post.Text.Length is > 0 and <= 4000;
}
