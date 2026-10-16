using System.Globalization;
using System.Text.RegularExpressions;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Best-effort parser that turns a rate-limit error message into a
/// "wait this long before retrying" duration. Providers expose retry-after
/// hints in wildly inconsistent ways:
///
///   - HTTP <c>Retry-After</c> header: seconds, or HTTP-date.
///   - OpenAI: "Please try again in 23.4s." / "Retry after 1m23s".
///   - Anthropic: "Resets at 8:42 PM" / numeric <c>retry-after-ms</c> header.
///   - Gemini: "Quota exceeded for ... Try again later."
///   - Claude Code CLI: "You've reached your usage limit. Try again in 2 hours."
///   - ChatGPT subscription: similar phrasing, often with a wall-clock time.
///
/// We pattern-match the common phrasings and HTTP semantics. Callers should
/// supply a sensible default (e.g. 1 hour) when this returns null — better
/// to over-cool a route briefly than ignore a hint entirely.
/// </summary>
public static class RateLimitRetryAfterParser
{
    private static readonly TimeSpan DefaultFallback = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Longest cooldown any parsed hint can produce. Provider messages have
    /// carried absurd values ("in 99999999999 days", 19-digit Retry-After
    /// echoes); a cap keeps the arithmetic finite and a route from being parked
    /// for years. Parsing runs inside the primary provider try, so it must
    /// never throw — an overflow there would replace the real provider error.
    /// </summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    // "Try again in 23.4 seconds" / "retry after 5m" / "in 2 hours".
    // The capture group is the duration token; the unit follows.
    // Each branch self-contains the whitespace that *separates* it from the
    // number — earlier attempts at putting `\s+` after the alternation broke
    // "wait another N" because the optional `another\s+` would eat the only
    // space and leave the outer `\s+` to fail. Self-contained branches sidestep that.
    private static readonly Regex DurationPattern = new(
        @"(?:try\s+again\s+in\s+|retry\s+(?:after|in)\s+|wait(?:\s+another)?\s+|available\s+(?:again\s+)?in\s+|come\s+back\s+in\s+)(\d+(?:\.\d+)?)\s*(ms|millisecond|s|sec|second|m|min|minute|h|hour|hr|d|day)s?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Compound durations like "1m23s" / "2h 15m" / "3 hours and 5 minutes".
    private static readonly Regex CompoundPattern = new(
        @"(\d+)\s*(h|hour|hr)s?(?:\s+and\s+|\s+)?(\d+)\s*(m|min|minute)s?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MinuteSecondCompoundPattern = new(
        @"(\d+)\s*(m|min|minute)s?(?:\s+and\s+|\s+)?(\d+(?:\.\d+)?)\s*(s|sec|second)s?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Wall-clock resets: "resets at 8:42 PM" / "resets 9am" /
    // "available at 23:00 UTC". Claude Code 2.1.234 dropped "at" from its
    // weekly-limit message, so accept either form.
    // The meridiem group owns its own optional whitespace: a bare `\s*` before
    // it consumed the space in "23:00 UTC" and left the zone group unmatched.
    private static readonly Regex ResetAtPattern = new(
        @"(?:resets?|available|unlocks?|comes?\s+back)\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?(?:\s*(am|pm|a\.m\.|p\.m\.))?(?:\s+(UTC|GMT|PT|ET|CT|MT)\b|\s*\((?<iana>[A-Za-z_]+/[A-Za-z_/]+)\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extract a retry-after duration from <paramref name="message"/>. Returns
    /// the default fallback when no pattern matches — the caller usually
    /// wants *some* cooldown rather than burning the next 50 calls beating
    /// against the same wall.
    /// </summary>
    public static TimeSpan ParseOrDefault(string? message, TimeSpan? fallback = null)
    {
        return TryParse(message) ?? fallback ?? DefaultFallback;
    }

    /// <summary>
    /// Strict parse — returns null when no signal is found, so callers can
    /// distinguish "explicit hint" from "we guessed."
    /// </summary>
    public static TimeSpan? TryParse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        try
        {
            return TryParseCore(message);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or RegexMatchTimeoutException)
        {
            // Never let a hint-parsing failure replace the real provider error.
            return null;
        }
    }

    // HTTP Retry-After date form ("Retry-After: Wed, 21 Oct 2026 07:28:00 GMT").
    // RFC 7231 allows either delta-seconds or an HTTP-date; the numeric branch
    // below cannot see the date form, which previously fell through to the
    // 60-minute default regardless of how far away the reset actually was.
    private static readonly Regex HttpDateHeaderPattern = new(
        @"retry-after\s*[:=]\s*(?<date>(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),\s*\d{1,2}\s+[A-Za-z]{3}\s+\d{4}\s+\d{1,2}:\d{2}:\d{2}(?:\s*(?:GMT|UTC))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static TimeSpan? TryParseCore(string message)
    {
        // HTTP Retry-After date form. A date in the past is not a usable hint
        // (the reset has already happened), so it yields null and the caller's
        // default applies rather than a zero-length cooldown.
        var dateMatch = HttpDateHeaderPattern.Match(message);
        if (dateMatch.Success
            && DateTimeOffset.TryParse(
                dateMatch.Groups["date"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var resetAt))
        {
            var untilReset = resetAt - DateTimeOffset.UtcNow;
            return untilReset > TimeSpan.Zero ? Clamp(untilReset) : null;
        }

        // HTTP Retry-After numeric form ("Retry-After: 120"). Look for the
        // header echoed into the error body; some providers do this.
        var headerMatch = Regex.Match(
            message,
            @"retry-after(?:-ms)?[:\s]+(\d+)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
        if (headerMatch.Success)
        {
            // A digit run too long for a long (19+ digits) is certainly not a
            // usable hint; clamp rather than throw.
            if (!long.TryParse(headerMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var headerValue))
            {
                return MaxCooldown;
            }

            // RFC Retry-After delta values are seconds. Only the explicit
            // non-standard retry-after-ms spelling changes the unit; guessing
            // from magnitude can turn a long server cooldown into minutes.
            var asMs = message.Contains("retry-after-ms", StringComparison.OrdinalIgnoreCase);
            return Clamp(asMs
                ? FromUnitsSafe(headerValue, TimeSpan.TicksPerMillisecond)
                : FromUnitsSafe(headerValue, TimeSpan.TicksPerSecond));
        }

        // Compound "Xm Ys" — try before the single-duration expression so a
        // compact provider hint such as "1m23s" retains both components.
        var minuteSecondCompound = MinuteSecondCompoundPattern.Match(message);
        if (minuteSecondCompound.Success
            && double.TryParse(
                minuteSecondCompound.Groups[3].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            var minutes = ParseDigitsSafe(minuteSecondCompound.Groups[1].Value);
            return Clamp(
                FromUnitsSafe(minutes, TimeSpan.TicksPerMinute)
                + FromUnitsSafe(seconds, TimeSpan.TicksPerSecond));
        }

        // Compound "Xh Ym" — try first so "1h 30m" doesn't get matched as
        // just "1h" by the simpler pattern below.
        var compound = CompoundPattern.Match(message);
        if (compound.Success)
        {
            var hours = ParseDigitsSafe(compound.Groups[1].Value);
            var minutes = ParseDigitsSafe(compound.Groups[3].Value);
            // Long arithmetic on clamped inputs cannot overflow (each input is
            // capped well below long.MaxValue / 60).
            return Clamp(FromUnitsSafe(hours * 60 + minutes, TimeSpan.TicksPerMinute));
        }

        // "Try again in X <unit>".
        var single = DurationPattern.Match(message);
        if (single.Success
            && double.TryParse(single.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            var unit = single.Groups[2].Value.ToLowerInvariant();
            var ticksPerUnit = unit switch
            {
                "ms" or "millisecond" => TimeSpan.TicksPerMillisecond,
                "s" or "sec" or "second" => TimeSpan.TicksPerSecond,
                "m" or "min" or "minute" => TimeSpan.TicksPerMinute,
                "h" or "hour" or "hr" => TimeSpan.TicksPerHour,
                "d" or "day" => TimeSpan.TicksPerDay,
                _ => 0L
            };
            if (ticksPerUnit == 0) return null;
            return Clamp(FromUnitsSafe(amount, ticksPerUnit));
        }

        // "Resets at 8:42 PM" — interpret as a wall-clock moment and return the
        // distance to it. If the time has passed today, assume tomorrow. When
        // the message names a timezone token, honour it: "resets at 23:00 UTC"
        // is a UTC wall clock, not a local one.
        var resetMatch = ResetAtPattern.Match(message);
        if (resetMatch.Success
            && int.TryParse(resetMatch.Groups[1].Value, out var hour))
        {
            var minute = resetMatch.Groups[2].Success && int.TryParse(resetMatch.Groups[2].Value, out var m)
                ? m : 0;
            var meridiem = resetMatch.Groups[3].Value.ToLowerInvariant();
            if (meridiem.StartsWith("p") && hour < 12) hour += 12;
            if (meridiem.StartsWith("a") && hour == 12) hour = 0;

            var zone = resetMatch.Groups[4].Success ? resetMatch.Groups[4].Value.ToUpperInvariant() : null;
            var zoneOffset = ResolveZoneOffset(zone);
            if (resetMatch.Groups["iana"].Success)
            {
                try
                {
                    zoneOffset = TimeZoneInfo.FindSystemTimeZoneById(resetMatch.Groups["iana"].Value)
                        .GetUtcOffset(DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    return null;
                }
            }
            var now = zoneOffset is { } offset ? DateTimeOffset.UtcNow.ToOffset(offset) : DateTimeOffset.Now;
            var target = new DateTimeOffset(
                now.Year, now.Month, now.Day,
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), 0,
                now.Offset);
            if (target <= now) target = target.AddDays(1);
            return Clamp(target - now);
        }

        return null;
    }

    /// <summary>
    /// Fixed offsets for the zone tokens the reset pattern captures. Only UTC/GMT
    /// are exact; the US zones use their standard offsets adjusted for the
    /// current daylight-saving state where the platform can tell us.
    /// </summary>
    private static TimeSpan? ResolveZoneOffset(string? zone)
    {
        if (string.IsNullOrEmpty(zone)) return null;
        if (zone is "UTC" or "GMT") return TimeSpan.Zero;

        var ianaId = zone switch
        {
            "PT" => "America/Los_Angeles",
            "MT" => "America/Denver",
            "CT" => "America/Chicago",
            "ET" => "America/New_York",
            _ => null,
        };
        if (ianaId is null) return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId).GetUtcOffset(DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return zone switch
            {
                "PT" => TimeSpan.FromHours(-8),
                "MT" => TimeSpan.FromHours(-7),
                "CT" => TimeSpan.FromHours(-6),
                "ET" => TimeSpan.FromHours(-5),
                _ => null,
            };
        }
    }

    private static long ParseDigitsSafe(string digits)
        => long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Min(value, MaxCooldown.Ticks)
            : MaxCooldown.Ticks;

    private static TimeSpan FromUnitsSafe(long units, long ticksPerUnit)
    {
        if (units <= 0) return TimeSpan.Zero;
        var maxUnits = MaxCooldown.Ticks / ticksPerUnit;
        return units >= maxUnits ? MaxCooldown : TimeSpan.FromTicks(units * ticksPerUnit);
    }

    private static TimeSpan FromUnitsSafe(double units, long ticksPerUnit)
    {
        if (double.IsNaN(units) || units <= 0) return TimeSpan.Zero;
        var maxUnits = (double)MaxCooldown.Ticks / ticksPerUnit;
        return units >= maxUnits ? MaxCooldown : TimeSpan.FromTicks((long)(units * ticksPerUnit));
    }

    private static TimeSpan Clamp(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxCooldown ? MaxCooldown : value;
}
