namespace SwarmCore.LlmRouting.Pricing;

/// <summary>
/// Fetches model pricing from external provider sources (APIs, pricing pages, etc.).
/// Implementations must NOT depend on ILlmRouter or ILlmCostService to avoid circular dependencies.
/// </summary>
public interface IModelPricingFetcher
{
    /// <summary>
    /// Fetches current pricing for all known models from all providers.
    /// Starts with reviewed provider-pack fallback pricing and overlays dynamically fetched data.
    /// Returns per-1K token costs keyed by model identifier.
    /// </summary>
    Task<Dictionary<string, ModelPricingEntry>> FetchAllPricingAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches pricing for a specific provider only.
    /// </summary>
    Task<Dictionary<string, ModelPricingEntry>> FetchProviderPricingAsync(string providerName, CancellationToken ct = default);
}

/// <summary>
/// Per-model pricing entry with source metadata and per-1K token costs.
/// </summary>
public record ModelPricingEntry(
    decimal InputCostPer1K,
    decimal OutputCostPer1K,
    decimal? EmbeddingCostPer1K,
    string Source,
    DateTime FetchedAt);
