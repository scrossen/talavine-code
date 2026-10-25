using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Configuration;

/// <summary>
/// Configuration for local subscription-backed LLM CLIs such as Claude Code
/// and Codex. These are launched locally but send prompts through the user's
/// logged-in subscription account, so they are not treated as LocalOnly routes.
/// </summary>
public sealed class SubscriptionCliProviderOptions
{
    public int TimeoutSeconds { get; set; } = 300;
    public string? WorkingDirectory { get; set; }
    /// <summary>
    /// The host's own per-user data root. When set, a CLI installed in the
    /// host's bundled runtime (Windows <c>Tools/Node</c>) keeps its sign-in under
    /// <c>&lt;root&gt;/subscription-cli</c> instead of the user's general CLI home.
    /// Hosts without a bundled runtime leave it null.
    /// </summary>
    public string? PrivateCliDataRoot { get; set; }
    public SubscriptionCliExecutableOptions ClaudeCode { get; set; } = new()
    {
        DefaultModel = ProviderPackCatalog.GetRequired(SubscriptionCliProviders.ClaudeCode)
            .SubscriptionCli!.DefaultModel,
        EnvironmentVariables = ProviderPackCatalog.GetRequired(SubscriptionCliProviders.ClaudeCode)
            .SubscriptionCli!.EnvironmentVariables.ToList()
    };

    public SubscriptionCliExecutableOptions CodexCli { get; set; } = new()
    {
        DefaultModel = ProviderPackCatalog.GetRequired(SubscriptionCliProviders.CodexCli)
            .SubscriptionCli!.DefaultModel,
        EnvironmentVariables = ProviderPackCatalog.GetRequired(SubscriptionCliProviders.CodexCli)
            .SubscriptionCli!.EnvironmentVariables.ToList()
    };

    public SubscriptionCliExecutableOptions GeminiCli { get; set; } = new()
    {
        DefaultModel = ProviderPackCatalog.GetRequired(SubscriptionCliProviders.GeminiCli)
            .SubscriptionCli!.DefaultModel,
        EnvironmentVariables = ProviderPackCatalog.GetRequired(SubscriptionCliProviders.GeminiCli)
            .SubscriptionCli!.EnvironmentVariables.ToList()
    };
}

public sealed class SubscriptionCliExecutableOptions
{
    public bool Enabled { get; set; } = true;
    public string? ExecutablePath { get; set; }
    public string DefaultModel { get; set; } = "default";
    public List<string> AdditionalModels { get; set; } = [];
    public List<string> EnvironmentVariables { get; set; } = [];
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>
    /// Prefer a provider-specific, read-only local OAuth proof before launching
    /// a legacy login-status command. Claude Code 2.1.38 lacks
    /// <c>auth status</c> and otherwise interprets those words as a model
    /// prompt; keeping this enabled avoids spending a turn or leaving a hung
    /// probe child. Tests that exercise the legacy command path disable it.
    /// </summary>
    public bool PreferLocalSubscriptionAuthProof { get; set; } = true;

    /// <summary>
    /// Turn budget passed to the CLI's own agent loop (e.g. Claude Code's
    /// <c>--max-turns</c>). SwarmCore runs the real tool loop and disables the
    /// CLI's native tools, so the CLI only needs to emit a single JSON envelope
    /// per call. A budget of 1 is nonetheless too tight: recent Claude Code
    /// versions report "Reached max turns (1)" in print mode before the answer
    /// completes, so the default leaves headroom.
    /// </summary>
    public int MaxTurns { get; set; } = 12;
}

public static class SubscriptionCliProviders
{
    public const string ClaudeCode = SubscriptionCliCatalog.ClaudeCodeProvider;
    public const string CodexCli = SubscriptionCliCatalog.CodexCliProvider;
    public const string GeminiCli = SubscriptionCliCatalog.GeminiCliProvider;

    public static bool IsSubscriptionCliProvider(string providerName) =>
        SubscriptionCliCatalog.IsSubscriptionCliProvider(providerName);
}

public static class SubscriptionCliModels
{
    public const string ClaudeCodeDefault = SubscriptionCliCatalog.ClaudeCodeModelPrefix + ":default";
    public const string ClaudeCodeSonnet = SubscriptionCliCatalog.ClaudeCodeModelPrefix + ":sonnet";
    public const string ClaudeCodeOpus = SubscriptionCliCatalog.ClaudeCodeModelPrefix + ":opus";
    public const string ClaudeCodeHaiku = SubscriptionCliCatalog.ClaudeCodeModelPrefix + ":haiku";

    public const string CodexCliDefault = SubscriptionCliCatalog.CodexCliModelPrefix + ":default";

    public const string GeminiCliDefault = SubscriptionCliCatalog.GeminiCliModelPrefix + ":default";
    public const string GeminiCliPro = SubscriptionCliCatalog.GeminiCliModelPrefix + ":pro";
    public const string GeminiCliFlash = SubscriptionCliCatalog.GeminiCliModelPrefix + ":flash";
}
