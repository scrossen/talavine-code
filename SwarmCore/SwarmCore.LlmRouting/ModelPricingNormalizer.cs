namespace SwarmCore.LlmRouting;

using SwarmCore.LlmRouting.ProviderPacks;

/// <summary>
/// Utility class for normalizing model pricing from provider configurations.
/// Handles provider-specific pricing formats and provides prefix-based matching
/// for dynamic model names (e.g., Gemini models with date/preview suffixes).
/// </summary>
public static class ModelPricingNormalizer
{
    /// <summary>
    /// Represents normalized pricing for a model (per 1K tokens).
    /// </summary>
    public record NormalizedCost(decimal Input, decimal Output);

    /// <summary>
    /// Provider-specific pricing configuration with input/output suffixes.
    /// </summary>
    public record ProviderPricing(string ProviderId, Dictionary<string, decimal> RawPricing);

    /// <summary>
    /// Normalizes pricing from provider-specific formats into a unified cost table.
    /// Handles Gemini's _input/_output suffix format and other provider formats.
    /// </summary>
    public static Dictionary<string, NormalizedCost> NormalizePricing(
        IEnumerable<ProviderPricing> providerPricings)
    {
        var result = new Dictionary<string, NormalizedCost>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providerPricings)
        {
            NormalizeProviderPricing(provider.ProviderId, provider.RawPricing, result);
        }

        return result;
    }

    /// <summary>
    /// Gets cost for a model, with prefix-based fallback matching for providers like Gemini.
    /// </summary>
    /// <param name="modelKey">The model identifier to look up</param>
    /// <param name="costs">The normalized cost table</param>
    /// <returns>The cost if found, null if not</returns>
    public static NormalizedCost? GetCostWithFallback(
        string modelKey,
        Dictionary<string, NormalizedCost> costs)
    {
        // Direct match first
        if (costs.TryGetValue(modelKey, out var directCost))
            return directCost;

        return TryMatchReviewedPackPrefix(modelKey, costs);
    }

    /// <summary>
    /// Attempts to match a Gemini model name to a base pricing tier using prefix matching.
    /// Handles models like "gemini-2.5-flash-lite-preview-09-2025" matching to "gemini-2.5-flash" pricing.
    /// </summary>
    private static NormalizedCost? TryMatchReviewedPackPrefix(
        string modelKey,
        Dictionary<string, NormalizedCost> costs)
    {
        var reviewedPrefixes = ProviderPackCatalog.GetDirectApiPacks()
            .SelectMany(pack => pack.DirectApi!.ModelFacts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(prefix => prefix.Length);
        foreach (var prefix in reviewedPrefixes)
        {
            if (modelKey.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                modelKey.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                if (costs.TryGetValue(prefix, out var cost))
                    return cost;
            }
        }
        return null;
    }

    /// <summary>
    /// Normalizes pricing for a single provider into the result dictionary.
    /// </summary>
    private static void NormalizeProviderPricing(
        string providerId,
        Dictionary<string, decimal> rawPricing,
        Dictionary<string, NormalizedCost> result)
    {
        if (string.Equals(providerId, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            NormalizeGeminiPricing(rawPricing, result);
            return;
        }

        // Default: treat each entry as input-only cost, use same for output
        foreach (var (model, price) in rawPricing)
        {
            if (!result.ContainsKey(model))
                result[model] = new NormalizedCost(price, price);
        }
    }

    /// <summary>
    /// Normalizes Gemini pricing which uses _input and _output suffixes.
    /// </summary>
    private static void NormalizeGeminiPricing(
        Dictionary<string, decimal> rawPricing,
        Dictionary<string, NormalizedCost> result)
    {
        // Group by base model name
        var groups = rawPricing
            .GroupBy(kvp => ExtractBaseName(kvp.Key))
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (model, dict) in groups)
        {
            dict.TryGetValue(model + "_input", out var inputCost);
            dict.TryGetValue(model + "_output", out var outputCost);

            if (inputCost == 0 && outputCost == 0)
                continue;

            result[model] = new NormalizedCost(inputCost, outputCost);
        }
    }

    /// <summary>
    /// Extracts the base model name from a pricing key by removing _input/_output suffixes.
    /// </summary>
    private static string ExtractBaseName(string key)
    {
        if (key.EndsWith("_input", StringComparison.OrdinalIgnoreCase))
            return key[..^6];
        if (key.EndsWith("_output", StringComparison.OrdinalIgnoreCase))
            return key[..^7];
        return key;
    }

    /// <summary>
    /// Adds common known Gemini model pricing. Call this to populate default pricing
    /// that can be used for prefix matching.
    /// </summary>
    public static void AddProviderPackPricing(Dictionary<string, NormalizedCost> costs)
    {
        foreach (var entry in Pricing.ProviderPackPricingProvider.GetAllPricing())
        {
            if (entry.Value.EmbeddingCostPer1K is null)
            {
                TrySet(
                    costs,
                    entry.Key,
                    entry.Value.InputCostPer1K,
                    entry.Value.OutputCostPer1K);
            }
        }
    }

    private static void TrySet(
        Dictionary<string, NormalizedCost> costs,
        string model,
        decimal input,
        decimal output)
    {
        if (!costs.ContainsKey(model))
            costs[model] = new NormalizedCost(input, output);
    }
}
