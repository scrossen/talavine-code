using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCode.Hosting;

/// <summary>
/// Seeds <see cref="CodingLaneOptions.Lanes"/> from what is actually available when the
/// configuration defines none: Claude Code, Codex and Gemini CLI as native agent lanes when their CLIs are
/// installed (each builds the way its own CLI does, in the repository, with its own tools), one
/// lane per BYOK provider that has a key, and finally the two CLIs through SwarmCore's own
/// tool loop as the last fallbacks. Model ids come from the provider packs /
/// <see cref="LlmProvidersOptions"/> defaults, never hard-coded here.
/// </summary>
public sealed class DefaultLaneSeeder : IPostConfigureOptions<CodingLaneOptions>
{
    // lane name, provider id, selector for the provider's default model
    private static readonly (string Lane, string Provider)[] ByokProviders =
    [
        ("openai", "OpenAI"),
        ("anthropic", "Claude"),
        ("gemini", "Gemini"),
        ("openrouter", "OpenRouter"),
        ("deepseek", "DeepSeek"),
        ("grok", "Grok"),
        ("venice", "Venice"),
    ];

    private readonly ILlmApiKeyProvider _keys;
    private readonly LlmProvidersOptions _providers;
    private readonly Func<string, bool> _cliInstalled;

    public DefaultLaneSeeder(ILlmApiKeyProvider keys, LlmProvidersOptions providers)
        : this(keys, providers, IsCliInstalled)
    {
    }

    /// <summary>Test seam: <paramref name="cliInstalled"/> answers whether a subscription CLI's executable exists.</summary>
    internal DefaultLaneSeeder(ILlmApiKeyProvider keys, LlmProvidersOptions providers, Func<string, bool> cliInstalled)
    {
        _keys = keys;
        _providers = providers;
        _cliInstalled = cliInstalled;
    }

    /// <summary>A runnable executable, not just a path: the Windows Store Codex package is visible but not launchable.</summary>
    private static bool IsCliInstalled(string provider) => CodingLaneDiscovery.IsCliInstalled(provider);

    public void PostConfigure(string? name, CodingLaneOptions options)
    {
        if (options.Lanes.Count > 0) return;

        // Native agent lanes first, then BYOK API lanes, then the CLIs through the tool loop
        // (CodingLaneDiscovery is the host-neutral part Talavine's lane source shares).
        var lanes = new List<CodingLane>(CodingLaneDiscovery.NativeLanes(_cliInstalled));

        var priority = 10;
        foreach (var (lane, provider) in ByokProviders)
        {
            bool hasKey;
            try { hasKey = _keys.HasApiKeyAsync(provider).GetAwaiter().GetResult(); }
            catch { hasKey = false; }
            if (!hasKey) continue;

            var model = DefaultModel(provider);
            if (string.IsNullOrWhiteSpace(model)) continue;
            lanes.Add(new CodingLane(lane, provider, model)
            {
                Description = $"{provider} API (BYOK)",
                Priority = priority++,
            });
        }

        // Venice's decline-fallback model (its catalog's most_uncensored trait) is the first stop after another model
        // declines, never the first lane for a new request. One lane, only when Venice has a key and a model is configured.
        if (lanes.Any(l => l.Provider == "Venice") && _providers.Venice.DeclineFallbackModel is { } fallback && !string.IsNullOrWhiteSpace(fallback)
            && !lanes.Any(l => l.Provider == "Venice" && string.Equals(l.Model, fallback.Trim(), StringComparison.Ordinal)))
        {
            lanes.Add(new CodingLane(DeclineFallbackLaneName, "Venice", fallback.Trim())
            {
                Description = "Venice API (BYOK), tried first when another model declines a request",
                Priority = priority++,
                PreferOnDecline = true,
            });
        }

        lanes.AddRange(CodingLaneDiscovery.ToolLoopFallbackLanes(_cliInstalled));
        options.Lanes = lanes;
    }

    /// <summary>Name of the seeded Venice lane that takes over after another model declines.</summary>
    public const string DeclineFallbackLaneName = "venice-uncensored";

    private string? DefaultModel(string provider) => provider switch
    {
        "OpenAI" => _providers.OpenAI.DefaultModel,
        "Claude" => _providers.Claude.DefaultModel,
        "Gemini" => _providers.Gemini.DefaultModel,
        "OpenRouter" => _providers.OpenRouter.DefaultModel,
        "DeepSeek" => _providers.DeepSeek.DefaultModel,
        "Grok" => _providers.Grok.DefaultModel,
        "Venice" => _providers.Venice.DefaultModel,
        _ => null,
    };
}
