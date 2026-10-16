namespace SwarmCore.Coding.SelfImprovement;

/// <summary>Something the host knows is going on (a running turn, working agents, a background shell). Empty = quiet.</summary>
public interface ISelfImprovementActivitySource
{
    IReadOnlyList<string> BusyReasons();
}

/// <summary>
/// Decides when SwarmCode is idle enough to work on itself: the person has not touched it for
/// <see cref="SelfImprovementOptions.UserIdleAfter"/>, and no registered activity source reports work in progress.
/// Startup counts as activity, so a freshly opened app waits the full quiet period first.
/// </summary>
public sealed class SelfImprovementIdleGate
{
    private readonly Func<SelfImprovementOptions> _options;
    private readonly TimeProvider _time;
    private readonly List<ISelfImprovementActivitySource> _sources = [];
    private readonly object _gate = new();
    private long _lastUserActivityTicks;

    public SelfImprovementIdleGate(Func<SelfImprovementOptions> options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        _lastUserActivityTicks = Now.Ticks;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public DateTime LastUserActivityUtc => new(Interlocked.Read(ref _lastUserActivityTicks), DateTimeKind.Utc);

    public TimeSpan UserIdleFor => Now - LastUserActivityUtc;

    /// <summary>The person did something (typed, clicked, sent a message, opened a repository).</summary>
    public void ReportUserActivity() => Interlocked.Exchange(ref _lastUserActivityTicks, Now.Ticks);

    public void AddSource(ISelfImprovementActivitySource source)
    {
        lock (_gate) _sources.Add(source);
    }

    public bool RemoveSource(ISelfImprovementActivitySource source)
    {
        lock (_gate) return _sources.Remove(source);
    }

    /// <summary>True when the person has been away for at least the configured quiet period.</summary>
    public bool UserIsAway => UserIdleFor >= _options().UserIdleAfter;

    /// <summary>Why a cycle cannot start right now, in plain words; empty when everything is idle.</summary>
    public IReadOnlyList<string> BusyReasons()
    {
        var reasons = new List<string>();
        var quiet = _options().UserIdleAfter;
        var idleFor = UserIdleFor;
        if (idleFor < quiet)
            reasons.Add($"You used Talavine Code {Describe(idleFor)} ago; waiting for {Describe(quiet)} of quiet.");
        ISelfImprovementActivitySource[] sources;
        lock (_gate) sources = _sources.ToArray();
        foreach (var source in sources)
        {
            try { reasons.AddRange(source.BusyReasons().Where(r => !string.IsNullOrWhiteSpace(r))); }
            catch (Exception ex) { reasons.Add("An activity check failed (" + ex.GetType().Name + ")."); }
        }
        return reasons;
    }

    public bool IsIdle(out IReadOnlyList<string> reasons)
    {
        reasons = BusyReasons();
        return reasons.Count == 0;
    }

    internal static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return span.TotalSeconds < 5 ? "moments" : $"{(int)span.TotalSeconds} s";
        if (span < TimeSpan.FromHours(1)) return $"{(int)Math.Round(span.TotalMinutes)} min";
        return $"{span.TotalHours:0.#} h";
    }
}
