using System.Globalization;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Scheduling;

namespace SwarmCore.Coding.Scheduling;

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleRecurrenceKind>))]
public enum ScheduleRecurrenceKind
{
    /// <summary>A 5-field cron expression in the schedule's zone.</summary>
    Cron,
    /// <summary>Every day at <see cref="ScheduleRecurrence.Hour"/>:<see cref="ScheduleRecurrence.Minute"/>.</summary>
    Daily,
    /// <summary>Monday to Friday at <see cref="ScheduleRecurrence.Hour"/>:<see cref="ScheduleRecurrence.Minute"/>.</summary>
    Weekdays,
    /// <summary>Every hour at minute <see cref="ScheduleRecurrence.Minute"/>.</summary>
    Hourly,
    /// <summary>Every <see cref="ScheduleRecurrence.IntervalMinutes"/> minutes, counted from the schedule's creation.</summary>
    EveryMinutes,
}

/// <summary>When a schedule fires. Presets are wall-clock times in the schedule's zone (local by default).</summary>
public sealed record ScheduleRecurrence
{
    public ScheduleRecurrenceKind Kind { get; init; }
    public string? Cron { get; init; }
    public int Hour { get; init; }
    public int Minute { get; init; }
    public int IntervalMinutes { get; init; }

    public static ScheduleRecurrence DailyAt(int hour, int minute) => new() { Kind = ScheduleRecurrenceKind.Daily, Hour = hour, Minute = minute };
    public static ScheduleRecurrence WeekdaysAt(int hour, int minute) => new() { Kind = ScheduleRecurrenceKind.Weekdays, Hour = hour, Minute = minute };
    public static ScheduleRecurrence HourlyAt(int minute) => new() { Kind = ScheduleRecurrenceKind.Hourly, Minute = minute };
    public static ScheduleRecurrence Every(int minutes) => new() { Kind = ScheduleRecurrenceKind.EveryMinutes, IntervalMinutes = minutes };
    public static ScheduleRecurrence FromCron(string expression) => new() { Kind = ScheduleRecurrenceKind.Cron, Cron = expression.Trim() };

    /// <summary>The equivalent cron expression; null for <see cref="ScheduleRecurrenceKind.EveryMinutes"/>.</summary>
    public string? ToCron() => Kind switch
    {
        ScheduleRecurrenceKind.Cron => Cron?.Trim(),
        ScheduleRecurrenceKind.Daily => $"{Minute} {Hour} * * *",
        ScheduleRecurrenceKind.Weekdays => $"{Minute} {Hour} * * 1-5",
        ScheduleRecurrenceKind.Hourly => $"{Minute} * * * *",
        _ => null,
    };

    /// <summary>
    /// The first occurrence strictly after <paramref name="afterUtc"/> (UTC), or null when the recurrence is invalid.
    /// Wall-clock fields are read in <paramref name="zone"/>; a time inside a spring-forward gap runs just after the gap.
    /// </summary>
    public DateTime? NextAfter(DateTime afterUtc, TimeZoneInfo zone, DateTime anchorUtc)
    {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        if (Kind == ScheduleRecurrenceKind.EveryMinutes)
        {
            if (IntervalMinutes <= 0) return null;
            var interval = TimeSpan.FromMinutes(IntervalMinutes);
            var anchor = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc);
            if (anchor > afterUtc) return anchor;
            var steps = (long)Math.Floor((afterUtc - anchor).Ticks / (double)interval.Ticks) + 1;
            return anchor.AddTicks(steps * interval.Ticks);
        }
        if (ToCron() is not { } cron) return null;
        var next = CronScheduleHelper.GetNextOccurrence(cron, afterUtc, zone);
        // A fall-back hour can map the next wall-clock occurrence onto an instant already passed: step past it.
        var guard = 0;
        while (next is { } n && n <= afterUtc && guard++ < 4)
            next = CronScheduleHelper.GetNextOccurrence(cron, n.AddMinutes(1), zone);
        return next is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }

    /// <summary>Null when valid; otherwise a plain reason. Runs closer together than <paramref name="minimumInterval"/> are refused.</summary>
    public string? Validate(TimeSpan minimumInterval)
    {
        switch (Kind)
        {
            case ScheduleRecurrenceKind.Daily or ScheduleRecurrenceKind.Weekdays:
                if (Hour is < 0 or > 23 || Minute is < 0 or > 59) return "The time must be between 00:00 and 23:59.";
                break;
            case ScheduleRecurrenceKind.Hourly:
                if (Minute is < 0 or > 59) return "The minute past the hour must be between 0 and 59.";
                if (TimeSpan.FromHours(1) < minimumInterval) return $"Runs must be at least {Describe(minimumInterval)} apart.";
                break;
            case ScheduleRecurrenceKind.EveryMinutes:
                if (IntervalMinutes <= 0) return "The interval must be a positive number of minutes.";
                if (IntervalMinutes > 60 * 24 * 31) return "The interval must be at most 31 days.";
                if (TimeSpan.FromMinutes(IntervalMinutes) < minimumInterval) return $"Runs must be at least {Describe(minimumInterval)} apart.";
                break;
            case ScheduleRecurrenceKind.Cron:
                if (!CronScheduleHelper.IsValid(Cron)) return "The cron expression is not valid (use 5 fields: minute hour day-of-month month day-of-week).";
                // Sample the next few gaps in UTC; the zone does not change the spacing of a cron's fields.
                var probe = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
                DateTime? previous = null;
                for (var i = 0; i < 6; i++)
                {
                    var next = CronScheduleHelper.GetNextOccurrence(Cron!, probe);
                    if (next is null) return "The cron expression never runs.";
                    if (previous is { } p && next.Value - p < minimumInterval) return $"Runs must be at least {Describe(minimumInterval)} apart.";
                    previous = next;
                    probe = next.Value;
                }
                break;
            default:
                return "Unknown recurrence.";
        }
        return null;
    }

    /// <summary>The recurrence in words, e.g. "Weekdays at 8:00 AM".</summary>
    public string Describe() => Kind switch
    {
        ScheduleRecurrenceKind.EveryMinutes => IntervalMinutes % 60 == 0 && IntervalMinutes >= 60
            ? IntervalMinutes == 60 ? "Every hour" : $"Every {IntervalMinutes / 60} hours"
            : $"Every {IntervalMinutes} minutes",
        ScheduleRecurrenceKind.Cron => CronScheduleHelper.Describe(Cron ?? ""),
        _ => CronScheduleHelper.Describe(ToCron()!),
    };

    /// <summary>
    /// Builds a recurrence from loose input (tools, forms): <paramref name="preset"/> is daily, weekdays, hourly,
    /// every_minutes or cron; <paramref name="time"/> is "HH:mm" (24-hour) for daily/weekdays; a bare
    /// <paramref name="cron"/> wins when no preset is named.
    /// </summary>
    public static (ScheduleRecurrence? Recurrence, string? Error) Parse(string? preset, string? time, int? minute, int? intervalMinutes, string? cron)
    {
        var kind = (preset ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        if (kind.Length == 0) kind = !string.IsNullOrWhiteSpace(cron) ? "cron" : intervalMinutes is > 0 ? "every_minutes" : "";
        switch (kind)
        {
            case "daily" or "weekdays":
                if (!TryParseTime(time, out var h, out var m)) return (null, "Give the time as HH:mm (24-hour), for example 08:00.");
                return (kind == "daily" ? DailyAt(h, m) : WeekdaysAt(h, m), null);
            case "hourly":
                return (HourlyAt(minute ?? (TryParseTime(time, out _, out var mm) ? mm : 0)), null);
            case "every_minutes" or "interval" or "every":
                return intervalMinutes is > 0 ? (Every(intervalMinutes.Value), null) : (null, "Give intervalMinutes for an every-N-minutes schedule.");
            case "cron":
                return string.IsNullOrWhiteSpace(cron) ? (null, "Give a cron expression.") : (FromCron(cron), null);
            default:
                return (null, "Choose a recurrence: daily, weekdays, hourly, every_minutes, or cron.");
        }
    }

    public static bool TryParseTime(string? text, out int hour, out int minute)
    {
        hour = minute = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            && !TimeOnly.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            return false;
        hour = t.Hour;
        minute = t.Minute;
        return true;
    }

    private static string Describe(TimeSpan span) => span.TotalMinutes >= 60 && span.TotalMinutes % 60 == 0
        ? $"{(int)span.TotalHours} hour{(span.TotalHours == 1 ? "" : "s")}"
        : $"{(int)span.TotalMinutes} minute{(span.TotalMinutes == 1 ? "" : "s")}";
}
