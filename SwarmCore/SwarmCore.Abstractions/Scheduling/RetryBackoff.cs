namespace SwarmCore.Abstractions.Scheduling;

/// <summary>Spaces retries without turning an attempt count into a terminal outcome.</summary>
public static class RetryBackoff
{
    public static bool IsDue(int attempt, DateTime? lastAttemptAt, DateTime now,
        TimeSpan initialDelay, TimeSpan maximumDelay)
        => attempt <= 0 || lastAttemptAt is null
            || now - lastAttemptAt.Value >= Delay(attempt, initialDelay, maximumDelay);

    public static TimeSpan Delay(int attempt, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        if (initialDelay <= TimeSpan.Zero || maximumDelay < initialDelay)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));

        var ticks = initialDelay.Ticks;
        for (var remaining = attempt - 1; remaining > 0 && ticks < maximumDelay.Ticks; remaining--)
            ticks = ticks > maximumDelay.Ticks / 2 ? maximumDelay.Ticks : ticks * 2;
        return TimeSpan.FromTicks(ticks);
    }
}
