namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Shared identity of the subscription-backed LLM CLIs (Claude Code, Codex,
/// Gemini). Their calls route through the user's logged-in subscription, so
/// usage has zero marginal cost and must never be priced as metered API
/// traffic. This lives in Abstractions so every layer — including projects
/// that cannot reference SwarmCore.LlmRouting — agrees on one detection: the
/// provider keys, the model-id families ("claude-code:opus", "gemini-cli:pro"),
/// and the user-facing brand names. The richer option classes in
/// SwarmCore.LlmRouting (<c>SubscriptionCliProviders</c>,
/// <c>SubscriptionCliModels</c>) alias their constants to these.
/// </summary>
public static class SubscriptionCliCatalog
{
    public const string ClaudeCodeProvider = "ClaudeCode";
    public const string CodexCliProvider = "CodexCli";
    public const string GeminiCliProvider = "GeminiCli";

    public const string ClaudeCodeModelPrefix = "claude-code";
    public const string CodexCliModelPrefix = "codex-cli";
    public const string GeminiCliModelPrefix = "gemini-cli";

    /// <summary>
    /// True for subscription-CLI model ids: a family prefix with a variant tag
    /// ("claude-code:default", "gemini-cli:pro") or the bare family name.
    /// </summary>
    public static bool IsSubscriptionCliModel(string? modelKey) =>
        ProviderForModel(modelKey) is not null;

    /// <summary>
    /// Provider key ("ClaudeCode"/"CodexCli"/"GeminiCli") for a
    /// subscription-CLI model id, or null for anything else.
    /// </summary>
    public static string? ProviderForModel(string? modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return null;
        }

        var key = modelKey.Trim();
        return MatchesFamily(key, ClaudeCodeModelPrefix) ? ClaudeCodeProvider
            : MatchesFamily(key, CodexCliModelPrefix) ? CodexCliProvider
            : MatchesFamily(key, GeminiCliModelPrefix) ? GeminiCliProvider
            : null;
    }

    public static bool IsSubscriptionCliProvider(string providerName) =>
        providerName.Equals(ClaudeCodeProvider, StringComparison.OrdinalIgnoreCase)
        || providerName.Equals(CodexCliProvider, StringComparison.OrdinalIgnoreCase)
        || providerName.Equals(GeminiCliProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The product the user signs in to ("Claude" / "ChatGPT" / "Gemini"), the
    /// raw provider key for everything else. Never a plan tier: the same CLI
    /// serves Pro, Max, Team and Enterprise plans.
    /// </summary>
    public static string FriendlyProviderName(string providerName) =>
        providerName.Equals(ClaudeCodeProvider, StringComparison.OrdinalIgnoreCase) ? "Claude"
        : providerName.Equals(CodexCliProvider, StringComparison.OrdinalIgnoreCase) ? "ChatGPT"
        : providerName.Equals(GeminiCliProvider, StringComparison.OrdinalIgnoreCase) ? "Gemini"
        : providerName;

    private static bool MatchesFamily(string key, string familyPrefix) =>
        key.Equals(familyPrefix, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(familyPrefix + ":", StringComparison.OrdinalIgnoreCase);
}
