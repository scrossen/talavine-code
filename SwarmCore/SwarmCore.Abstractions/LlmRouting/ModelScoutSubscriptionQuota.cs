namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Live subscription window state handed to the Model Scout ranker so sibling
/// tiers inside one subscription provider stop ranking as a flat cost bucket.
/// A subscription route has no marginal API bill, but an opus-class call
/// consumes far more of the shared usage window than a haiku-class call; the
/// ranker scales that relative tier weight by the provider's current burn
/// fraction so capability can win a fresh window while a depleting window
/// steers admissible work to the cheapest passing tier.
/// </summary>
public sealed class ModelScoutSubscriptionQuotaContext
{
    public static readonly ModelScoutSubscriptionQuotaContext Empty =
        new(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, decimal> _burnByProvider;

    public ModelScoutSubscriptionQuotaContext(IReadOnlyDictionary<string, decimal> burnByProvider)
    {
        ArgumentNullException.ThrowIfNull(burnByProvider);
        _burnByProvider = burnByProvider is Dictionary<string, decimal> dictionary
                          && ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase)
            ? burnByProvider
            : new Dictionary<string, decimal>(burnByProvider, StringComparer.OrdinalIgnoreCase);
    }

    public bool HasAnyBurnSignal => _burnByProvider.Any(entry => entry.Value > 0m);

    public bool TryGetBurnFraction(string providerName, out decimal burnFraction)
    {
        if (!string.IsNullOrWhiteSpace(providerName)
            && _burnByProvider.TryGetValue(providerName.Trim(), out var burn))
        {
            burnFraction = Math.Clamp(burn, 0m, 1m);
            return true;
        }

        burnFraction = 0m;
        return false;
    }

    /// <summary>
    /// Snapshot of every configured subscription window the tracker knows.
    /// A null tracker (worker/tests without quota accounting) yields the empty
    /// context, which leaves ranking behavior exactly as before.
    /// </summary>
    public static ModelScoutSubscriptionQuotaContext FromTracker(ISubscriptionQuotaTracker? tracker)
    {
        if (tracker is null) return Empty;
        var burnByProvider = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in tracker.GetAllSnapshots())
        {
            if (string.IsNullOrWhiteSpace(snapshot.ProviderName)) continue;
            burnByProvider[snapshot.ProviderName.Trim()] = snapshot.BurnFraction;
        }

        return burnByProvider.Count == 0 ? Empty : new ModelScoutSubscriptionQuotaContext(burnByProvider);
    }
}

/// <summary>
/// Relative window-burn weight of a subscription model tier. Values are
/// deliberately coarse categorical ratios — only the ordering and rough
/// magnitude inside one provider matter, because the ranker normalizes the
/// weights among that provider's eligible sibling tiers. Unknown ids land on
/// the standard weight so a newly discovered tier is never accidentally
/// treated as frontier-expensive or economy-cheap.
/// </summary>
public static class SubscriptionModelTierWeights
{
    public const decimal Light = 1m;
    public const decimal Standard = 3m;
    public const decimal Heavy = 10m;

    private static readonly string[] LightTokens = ["haiku", "flash", "mini", "nano", "lite", "light"];
    private static readonly string[] HeavyTokens = ["opus", "pro", "ultra", "max"];

    public static decimal Resolve(string? model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return Standard;

        // Codex effort variants ("codex-cli:gpt-x@high") are execution
        // parameters of one base tier: they scale that tier's burn instead of
        // forming an independent tier of their own.
        var effortMultiplier = ResolveReasoningEffortMultiplier(normalized);
        var effortSeparator = normalized.LastIndexOf('@');
        if (effortSeparator > 0)
        {
            normalized = normalized[..effortSeparator];
        }

        var tokens = normalized.Split(
            ['-', ':', '.', '/', '_', ' '],
            StringSplitOptions.RemoveEmptyEntries);
        var baseWeight = Standard;
        if (tokens.Any(token => HeavyTokens.Contains(token, StringComparer.Ordinal)))
            baseWeight = Heavy;
        else if (tokens.Any(token => LightTokens.Contains(token, StringComparer.Ordinal)))
            baseWeight = Light;

        return baseWeight * effortMultiplier;
    }

    public static decimal ResolveReasoningEffortMultiplier(string? model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        var effortSeparator = normalized.LastIndexOf('@');
        if (effortSeparator <= 0 || effortSeparator == normalized.Length - 1)
        {
            return 1m;
        }

        return normalized[(effortSeparator + 1)..] switch
        {
            "minimal" or "min" => 0.35m,
            "low" => 0.50m,
            "medium" => 1m,
            "high" => 2.5m,
            "xhigh" or "max" => 4m,
            _ => 1m,
        };
    }
}
