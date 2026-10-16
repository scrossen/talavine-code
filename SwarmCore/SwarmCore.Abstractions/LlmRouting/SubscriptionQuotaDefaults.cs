namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// The single defaults table for subscription-CLI burn windows. These are
/// conservative guesses for the well-known quota windows — real caps are not
/// published as machine-readable numbers — and they used to be hard-coded
/// independently in every <see cref="ISubscriptionQuotaTracker"/>
/// implementation's constructor, where they could drift apart. Lives beside
/// <see cref="SubscriptionCliCatalog"/> so provider identity and quota policy
/// come from one place. Users with paid Pro/Plus tiers will rarely hit these —
/// that's the point: routing demotes long before the provider says "stop."
/// </summary>
public static class SubscriptionQuotaDefaults
{
    /// <summary>Claude Code: 5-hour rolling window, 45 messages.</summary>
    public static readonly SubscriptionQuotaConfig ClaudeCode = new(
        SubscriptionCliCatalog.ClaudeCodeProvider, TimeSpan.FromHours(5), MessageCap: 45);

    /// <summary>Codex CLI: 5-hour rolling window, 750 messages.</summary>
    public static readonly SubscriptionQuotaConfig CodexCli = new(
        SubscriptionCliCatalog.CodexCliProvider, TimeSpan.FromHours(5), MessageCap: 750);

    /// <summary>Gemini CLI: 24-hour rolling window, 1500 messages.</summary>
    public static readonly SubscriptionQuotaConfig GeminiCli = new(
        SubscriptionCliCatalog.GeminiCliProvider, TimeSpan.FromHours(24), MessageCap: 1500);

    /// <summary>All default configs, for tracker constructors to register.</summary>
    public static IReadOnlyList<SubscriptionQuotaConfig> All { get; } =
        [ClaudeCode, CodexCli, GeminiCli];
}
