namespace SwarmCore.Abstractions.Computer;

/// <summary>
/// Human-like pacing parameters for automated browser login and navigation. Purely
/// descriptive and deterministic given a seed; it holds no Playwright types, selectors,
/// element references, or site-specific paths. The host adapter applies these to its
/// physical input mechanics and remains responsible for authorization and bounds.
/// </summary>
public sealed record BrowserHumanizationProfile
{
    /// <summary>Fast, native-speed path (used by tests and model-driven surfaces).</summary>
    public static readonly BrowserHumanizationProfile Disabled = new() { Enabled = false };

    /// <summary>Production defaults tuned to read as an unhurried human without stalling.</summary>
    public static readonly BrowserHumanizationProfile Realistic = new();

    /// <summary>When false, physical actions run at native Playwright speed.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Per-keypress typing delay bounds, in milliseconds.</summary>
    public int MinKeyDelayMs { get; init; } = 30;
    public int MaxKeyDelayMs { get; init; } = 110;

    /// <summary>Probability (0..1) of a longer "thinking" pause while typing a longer value.</summary>
    public double TypingPauseProbability { get; init; } = 0.12;
    public int MinTypingPauseMs { get; init; } = 300;
    public int MaxTypingPauseMs { get; init; } = 950;

    /// <summary>Settle pause before a fill/click, in milliseconds.</summary>
    public int MinSettleMs { get; init; } = 250;
    public int MaxSettleMs { get; init; } = 1000;

    /// <summary>Hover dwell before a click, in milliseconds.</summary>
    public int MinClickDwellMs { get; init; } = 90;
    public int MaxClickDwellMs { get; init; } = 380;

    /// <summary>0 selects a non-deterministic process-random seed; any other value is reproducible.</summary>
    public int Seed { get; init; }

    public Random CreateRandom() => Seed == 0 ? new Random() : new Random(Seed);
}

/// <summary>Pure, deterministic pacing math shared by host browser adapters.</summary>
public static class BrowserPacing
{
    public static int Delay(Random rng, int min, int max) => min >= max ? min : rng.Next(min, max + 1);

    public static TimeSpan Settle(BrowserHumanizationProfile profile, Random rng)
        => TimeSpan.FromMilliseconds(Delay(rng, profile.MinSettleMs, profile.MaxSettleMs));

    public static TimeSpan ClickDwell(BrowserHumanizationProfile profile, Random rng)
        => TimeSpan.FromMilliseconds(Delay(rng, profile.MinClickDwellMs, profile.MaxClickDwellMs));

    public static float KeyDelay(BrowserHumanizationProfile profile, Random rng)
        => Delay(rng, profile.MinKeyDelayMs, profile.MaxKeyDelayMs);

    /// <summary>An occasional longer pause mid-value, never for short tokens such as a PIN.</summary>
    public static TimeSpan? TypingPause(BrowserHumanizationProfile profile, Random rng, string value)
        => value.Length >= 8 && rng.NextDouble() < profile.TypingPauseProbability
            ? TimeSpan.FromMilliseconds(Delay(rng, profile.MinTypingPauseMs, profile.MaxTypingPauseMs))
            : null;

    /// <summary>Upper bound on the time typing a value can take, used to size action timeouts.</summary>
    public static TimeSpan TypingBound(BrowserHumanizationProfile profile, string value)
        => TimeSpan.FromMilliseconds(value.Length * (long)profile.MaxKeyDelayMs + 3L * profile.MaxTypingPauseMs + 2000);
}
