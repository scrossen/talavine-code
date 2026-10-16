namespace SwarmCore.Abstractions.Hosting;

/// <summary>
/// Resolves how long a background service waits after host start before its
/// first pass, honoring the test/dev environment overrides every SwarmCore
/// host shares.
/// </summary>
public static class BackgroundStartupDelay
{
    public const string FastStartupEnvironmentVariable = "Talavine_FAST_BACKGROUND_STARTUP";
    public const string OverrideDelaySecondsEnvironmentVariable = "Talavine_BACKGROUND_STARTUP_DELAY_SECONDS";

    public static TimeSpan Resolve(TimeSpan normalDelay, TimeSpan? fastDelay = null)
    {
        var overrideValue = Environment.GetEnvironmentVariable(OverrideDelaySecondsEnvironmentVariable);
        if (int.TryParse(overrideValue, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return IsEnabled(FastStartupEnvironmentVariable)
            ? fastDelay ?? TimeSpan.FromSeconds(2)
            : normalDelay;
    }

    private static bool IsEnabled(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return value is not null
               && (value.Equals("1", StringComparison.Ordinal)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
