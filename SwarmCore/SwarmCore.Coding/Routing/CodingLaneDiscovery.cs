using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Native;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.Coding.Routing;

/// <summary>
/// Host-neutral discovery of the subscription coding CLIs installed on this machine and the lanes
/// they offer: each CLI as a native agent lane (its own tools and loop in the repository) and, as
/// late fallbacks, the same CLI driven through the SwarmCore tool loop. Model ids come from the
/// provider packs, never hard-coded per host. SwarmCode's lane seeder and Talavine's Model Scout
/// lane source both build on it.
/// </summary>
public static class CodingLaneDiscovery
{
    /// <summary>The CLIs that can run as native agents, in default preference order.</summary>
    public static IReadOnlyList<string> NativeCliProviders { get; } =
    [
        SubscriptionCliCatalog.ClaudeCodeProvider,
        SubscriptionCliCatalog.CodexCliProvider,
        SubscriptionCliCatalog.GeminiCliProvider,
    ];

    /// <summary>A runnable executable, not just a path: the Windows Store Codex package is visible but not launchable.</summary>
    public static bool IsCliInstalled(string provider)
        => SubscriptionCliDiscovery.ResolveExecutableCandidates(provider)
            .Any(path => File.Exists(path) && !SubscriptionCliDiscovery.IsProtectedDesktopPackageCli(path));

    /// <summary>The CLI's generic default model (for example <c>claude-code:default</c>) from its provider pack.</summary>
    public static string SubscriptionModel(string provider)
    {
        var cli = ProviderPackCatalog.GetRequired(provider).SubscriptionCli!;
        var prefix = provider == SubscriptionCliCatalog.ClaudeCodeProvider ? SubscriptionCliCatalog.ClaudeCodeModelPrefix
            : provider == SubscriptionCliCatalog.GeminiCliProvider ? SubscriptionCliCatalog.GeminiCliModelPrefix
            : SubscriptionCliCatalog.CodexCliModelPrefix;
        var generic = $"{prefix}:default";
        return cli.BuiltInModels.Contains(generic, StringComparer.OrdinalIgnoreCase) ? generic : cli.DefaultModel;
    }

    /// <summary>The native-agent provider id for a subscription CLI provider, or null when it has no native runner.</summary>
    public static string? NativeProviderFor(string cliProvider) => cliProvider switch
    {
        SubscriptionCliCatalog.ClaudeCodeProvider => ClaudeCodeNativeLaneRunner.ProviderId,
        SubscriptionCliCatalog.CodexCliProvider => CodexNativeLaneRunner.ProviderId,
        SubscriptionCliCatalog.GeminiCliProvider => GeminiCliNativeLaneRunner.ProviderId,
        _ => null,
    };

    /// <summary>
    /// Native agent lanes for the installed CLIs: <c>claude</c>, <c>codex</c>, <c>gemini-cli</c> with
    /// priorities 0, 1, 2. <paramref name="cliInstalled"/> is a test seam (defaults to <see cref="IsCliInstalled"/>).
    /// </summary>
    public static IReadOnlyList<CodingLane> NativeLanes(Func<string, bool>? cliInstalled = null)
    {
        var lanes = new List<CodingLane>();
        if (Installed(SubscriptionCliCatalog.ClaudeCodeProvider, cliInstalled))
            lanes.Add(new("claude", ClaudeCodeNativeLaneRunner.ProviderId, SubscriptionModel(SubscriptionCliCatalog.ClaudeCodeProvider))
            {
                Description = "Claude Code (native agent in the repository)",
                Priority = 0,
            });
        if (Installed(SubscriptionCliCatalog.CodexCliProvider, cliInstalled))
            lanes.Add(new("codex", CodexNativeLaneRunner.ProviderId, SubscriptionModel(SubscriptionCliCatalog.CodexCliProvider))
            {
                Description = "Codex (native agent in the repository)",
                Priority = 1,
            });
        if (Installed(SubscriptionCliCatalog.GeminiCliProvider, cliInstalled))
            lanes.Add(new("gemini-cli", GeminiCliNativeLaneRunner.ProviderId, SubscriptionModel(SubscriptionCliCatalog.GeminiCliProvider))
            {
                Description = "Gemini CLI (native agent in the repository)",
                Priority = 2,
            });
        return lanes;
    }

    /// <summary>
    /// The installed Claude Code and Codex CLIs driven through the SwarmCore tool loop
    /// (<c>claude-tools</c> priority 90, <c>codex-tools</c> priority 91): late fallbacks after native and API lanes.
    /// </summary>
    public static IReadOnlyList<CodingLane> ToolLoopFallbackLanes(Func<string, bool>? cliInstalled = null)
    {
        var lanes = new List<CodingLane>();
        if (Installed(SubscriptionCliCatalog.ClaudeCodeProvider, cliInstalled))
            lanes.Add(new("claude-tools", SubscriptionCliCatalog.ClaudeCodeProvider, SubscriptionModel(SubscriptionCliCatalog.ClaudeCodeProvider))
            {
                Description = "Claude Code through Talavine Code's own tools (fallback)",
                Priority = 90,
            });
        if (Installed(SubscriptionCliCatalog.CodexCliProvider, cliInstalled))
            lanes.Add(new("codex-tools", SubscriptionCliCatalog.CodexCliProvider, SubscriptionModel(SubscriptionCliCatalog.CodexCliProvider))
            {
                Description = "Codex through Talavine Code's own tools (fallback)",
                Priority = 91,
            });
        return lanes;
    }

    private static bool Installed(string provider, Func<string, bool>? cliInstalled)
    {
        try { return (cliInstalled ?? IsCliInstalled)(provider); }
        catch { return false; }
    }
}
