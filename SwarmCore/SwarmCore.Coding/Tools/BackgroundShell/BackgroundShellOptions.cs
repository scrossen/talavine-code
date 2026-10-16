namespace SwarmCore.Coding.Tools.BackgroundShell;

/// <summary>Limits for background shells (<c>Coding:BackgroundShells</c>). Values below 1 fall back to the defaults.</summary>
public sealed class BackgroundShellOptions
{
    public const string SectionName = "Coding:BackgroundShells";

    /// <summary>Running shells one conversation may have at once.</summary>
    public int MaxRunningPerConversation { get; set; } = 4;

    /// <summary>Running shells across every conversation in this host.</summary>
    public int MaxRunningTotal { get; set; } = 16;

    /// <summary>Finished shells kept per conversation for output and listing; the oldest beyond this are forgotten.</summary>
    public int RetainFinishedPerConversation { get; set; } = 10;

    /// <summary>Characters kept from the start of a shell's output (startup banners, the first error).</summary>
    public int HeadCharacters { get; set; } = 16 * 1024;

    /// <summary>Characters kept from the end of a shell's output (a ring buffer; older middle output is dropped).</summary>
    public int TailCharacters { get; set; } = 112 * 1024;

    /// <summary>Most characters one output read returns; longer unread output keeps its head and tail.</summary>
    public int MaxReadCharacters { get; set; } = 32 * 1024;

    /// <summary>Wait used when a read names a pattern but no timeout.</summary>
    public int DefaultPatternWaitSeconds { get; set; } = 30;

    /// <summary>Cap on any single read's wait, so a turn is never parked on a quiet process.</summary>
    public int MaxWaitSeconds { get; set; } = 120;

    internal BackgroundShellOptions Normalized() => new()
    {
        MaxRunningPerConversation = Positive(MaxRunningPerConversation, 4),
        MaxRunningTotal = Positive(MaxRunningTotal, 16),
        RetainFinishedPerConversation = Math.Max(0, RetainFinishedPerConversation),
        HeadCharacters = Positive(HeadCharacters, 16 * 1024),
        TailCharacters = Positive(TailCharacters, 112 * 1024),
        MaxReadCharacters = Math.Max(256, Positive(MaxReadCharacters, 32 * 1024)),
        DefaultPatternWaitSeconds = Positive(DefaultPatternWaitSeconds, 30),
        MaxWaitSeconds = Positive(MaxWaitSeconds, 120),
    };

    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;
}
