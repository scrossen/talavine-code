using NCrontab;

namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// Utility for validating, computing, and describing cron expressions. Wraps NCrontab for
/// consistent usage across the codebase.
///
/// Lives in SwarmCore because BOTH runtimes schedule agents from user-authored cron: the
/// desktop worker loop (via the Talavine.Core forwarder that preserves its original call
/// sites) and the hosted CRM's scheduled-trigger producer. The timezone overload carries the
/// non-obvious part — DST gap/ambiguity handling — which is exactly the logic that must not
/// be reimplemented twice.
/// </summary>
public static class CronScheduleHelper
{
    /// <summary>Legacy schedules follow this device; new schedules can pin a named time zone.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
        => string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());

    /// <summary>
    /// Returns true if the expression is a valid 5-field cron expression.
    /// </summary>
    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        return CrontabSchedule.TryParse(expression) is not null;
    }

    /// <summary>
    /// Gets the next occurrence after <paramref name="after"/>.
    /// Returns null if the expression is invalid.
    /// </summary>
    /// <remarks>
    /// This overload is timezone-naive: it interprets the cron's wall-clock fields against
    /// whatever <see cref="DateTime"/> you pass. Passing a UTC anchor makes "0 9 * * *" mean
    /// 09:00 UTC. For user-authored schedules ("9 AM weekdays"), which mean 9 AM *local*, use the
    /// <see cref="GetNextOccurrence(string, DateTime, TimeZoneInfo)"/> overload instead.
    /// </remarks>
    public static DateTime? GetNextOccurrence(string expression, DateTime after)
    {
        var schedule = CrontabSchedule.TryParse(expression);
        return schedule?.GetNextOccurrence(after);
    }

    /// <summary>
    /// Gets the next occurrence after <paramref name="afterUtc"/>, interpreting the cron's
    /// wall-clock fields (hour/minute/day-of-week) in <paramref name="timeZone"/> and returning
    /// the result back in UTC. Use this for user-authored schedules: a worker set to "9 AM
    /// weekdays" means 9 AM in the user's local zone, not 09:00 UTC. Returns null if the
    /// expression is invalid.
    /// </summary>
    public static DateTime? GetNextOccurrence(string expression, DateTime afterUtc, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var schedule = CrontabSchedule.TryParse(expression);
        if (schedule is null) return null;

        var afterInZone = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), timeZone);
        var nextInZone = DateTime.SpecifyKind(schedule.GetNextOccurrence(afterInZone), DateTimeKind.Unspecified);

        // A wall-clock time that falls in the spring-forward gap doesn't exist; nudge it past the
        // gap so ConvertTimeToUtc doesn't throw. Ambiguous fall-back times resolve to standard time.
        if (timeZone.IsInvalidTime(nextInZone))
            nextInZone = nextInZone.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(nextInZone, timeZone);
    }

    /// <summary>
    /// Returns a human-readable description of common cron patterns.
    /// Falls back to "Custom schedule: {expression}" for complex patterns.
    /// </summary>
    public static string Describe(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "Invalid schedule";

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return $"Custom schedule: {expression}";

        var (minute, hour, dayOfMonth, month, dayOfWeek) = (parts[0], parts[1], parts[2], parts[3], parts[4]);

        // Every N minutes
        if (minute.StartsWith("*/") && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            var interval = minute[2..];
            return $"Every {interval} minutes";
        }

        // Every N minutes on specific days (e.g. "*/30 * * * 1-5")
        if (minute.StartsWith("*/") && hour == "*" && dayOfMonth == "*" && month == "*")
        {
            var interval = minute[2..];
            return dayOfWeek switch
            {
                "1-5" => $"Every {interval} minutes, weekdays",
                "0,6" or "6,0" => $"Every {interval} minutes, weekends",
                _ => $"Every {interval} minutes on {DescribeDayOfWeek(dayOfWeek)}"
            };
        }

        // Every N hours (e.g. "0 */4 * * *" or "0 */6 * * 1-5")
        if (!minute.Contains('*') && !minute.Contains('/') && hour.StartsWith("*/") &&
            dayOfMonth == "*" && month == "*")
        {
            var interval = hour[2..];
            return dayOfWeek switch
            {
                "*" => $"Every {interval} hours",
                "1-5" => $"Every {interval} hours, weekdays",
                "0,6" or "6,0" => $"Every {interval} hours, weekends",
                _ => $"Every {interval} hours on {DescribeDayOfWeek(dayOfWeek)}"
            };
        }

        // Hourly at specific minute
        if (!minute.Contains('*') && !minute.Contains('/') && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return $"Every hour at :{minute.PadLeft(2, '0')}";
        }

        // Fixed hour and minute
        if (!minute.Contains('*') && !minute.Contains('/') &&
            !hour.Contains('*') && !hour.Contains('/') &&
            dayOfMonth == "*" && month == "*")
        {
            var timeStr = FormatTime(hour, minute);

            return dayOfWeek switch
            {
                "*" => $"Every day at {timeStr}",
                "1-5" => $"Weekdays at {timeStr}",
                "0,6" or "6,0" => $"Weekends at {timeStr}",
                "0" => $"Every Sunday at {timeStr}",
                "1" => $"Every Monday at {timeStr}",
                "2" => $"Every Tuesday at {timeStr}",
                "3" => $"Every Wednesday at {timeStr}",
                "4" => $"Every Thursday at {timeStr}",
                "5" => $"Every Friday at {timeStr}",
                "6" => $"Every Saturday at {timeStr}",
                _ => $"{DescribeDayOfWeek(dayOfWeek)} at {timeStr}"
            };
        }

        return $"Custom schedule: {expression}";
    }

    private static string FormatTime(string hour, string minute)
    {
        if (int.TryParse(hour, out var h) && int.TryParse(minute, out var m))
        {
            var time = new TimeOnly(h, m);
            return time.ToString("h:mm tt");
        }
        return $"{hour}:{minute.PadLeft(2, '0')}";
    }

    private static string DescribeDayOfWeek(string dayOfWeek)
    {
        // Handle comma-separated days
        var days = dayOfWeek.Split(',');
        if (days.Length > 1)
        {
            var names = days.Select(DayName).Where(n => n is not null);
            return string.Join(", ", names);
        }
        return DayName(dayOfWeek) ?? dayOfWeek;
    }

    private static string? DayName(string day) => day.Trim() switch
    {
        "0" => "Sunday",
        "1" => "Monday",
        "2" => "Tuesday",
        "3" => "Wednesday",
        "4" => "Thursday",
        "5" => "Friday",
        "6" => "Saturday",
        _ => null
    };
}
