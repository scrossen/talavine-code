using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Pricing;

/// <summary>
/// Reusable pricing seed/refresh pipeline: reviewed provider-pack baseline, live fetched
/// overlays, and optional model-discovery cross-checks, all persisted through
/// the host's <see cref="IModelPricingStore"/>. Hosts schedule it (maintenance
/// pass, admin UI) and own the underlying table; this service owns change
/// detection so <c>LastUpdated</c> reflects true last change.
/// </summary>
public sealed class ModelPricingRefreshService(
    IModelPricingFetcher fetcher,
    IModelPricingStore store,
    ILogger<ModelPricingRefreshService> logger,
    IModelPricingDiscoverySource? discoverySource = null)
{
    public sealed class RefreshResult
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Discovered { get; set; }
        public int Total => Inserted + Updated + Unchanged;
        public DateTime FinishedAt { get; set; }
        public List<string> Sources { get; set; } = new();
    }

    /// <summary>
    /// Seeds the pricing store from reviewed provider-pack pricing if it is empty.
    /// Non-destructive: existing rows are left alone.
    /// </summary>
    public async Task<int> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        if (await store.HasAnyAsync(ct).ConfigureAwait(false))
        {
            return 0;
        }

        var seed = ProviderPackPricingProvider.GetAllPricing();
        var now = DateTime.UtcNow;
        await store.UpsertAsync(
            seed.Select(pair => new ModelPricingRecord(
                pair.Key,
                pair.Value.InputCostPer1K,
                pair.Value.OutputCostPer1K,
                pair.Value.EmbeddingCostPer1K,
                now)).ToList(),
            ct).ConfigureAwait(false);
        logger.LogInformation(
            "Seeded {Count} model pricing rows from reviewed provider packs",
            seed.Count);
        return seed.Count;
    }

    /// <summary>
    /// Fetches fresh pricing from providers (scraped + provider-pack fallback) and
    /// upserts into the pricing store. Existing rows whose price has changed are
    /// updated; unchanged rows are left untouched.
    /// </summary>
    public async Task<RefreshResult> RefreshFromProvidersAsync(CancellationToken ct = default)
    {
        var result = new RefreshResult();
        var fetched = await fetcher.FetchAllPricingAsync(ct).ConfigureAwait(false);
        result.Discovered = await AddDiscoveredModelsWithKnownPricingAsync(fetched, ct)
            .ConfigureAwait(false);

        if (fetched.Count == 0)
        {
            logger.LogWarning("Pricing fetcher returned zero entries; nothing to upsert");
            result.FinishedAt = DateTime.UtcNow;
            return result;
        }

        var existing = (await store.GetAllAsync(ct).ConfigureAwait(false))
            .ToDictionary(row => row.ModelKey, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upserts = new List<ModelPricingRecord>();

        foreach (var (key, entry) in fetched)
        {
            sources.Add(entry.Source);

            if (existing.TryGetValue(key, out var row))
            {
                if (row.InputCostPer1K == entry.InputCostPer1K
                    && row.OutputCostPer1K == entry.OutputCostPer1K
                    && row.EmbeddingCostPer1K == entry.EmbeddingCostPer1K)
                {
                    result.Unchanged++;
                    continue;
                }

                upserts.Add(row with
                {
                    InputCostPer1K = entry.InputCostPer1K,
                    OutputCostPer1K = entry.OutputCostPer1K,
                    EmbeddingCostPer1K = entry.EmbeddingCostPer1K,
                    LastUpdated = now,
                });
                result.Updated++;
            }
            else
            {
                upserts.Add(new ModelPricingRecord(
                    key,
                    entry.InputCostPer1K,
                    entry.OutputCostPer1K,
                    entry.EmbeddingCostPer1K,
                    now));
                result.Inserted++;
            }
        }

        if (upserts.Count > 0)
        {
            await store.UpsertAsync(upserts, ct).ConfigureAwait(false);
        }

        result.FinishedAt = now;
        result.Sources = sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        logger.LogInformation(
            "Refreshed model pricing: {Inserted} inserted, {Updated} updated, {Unchanged} unchanged, {Discovered} discovered model rows added (sources: {Sources})",
            result.Inserted, result.Updated, result.Unchanged, result.Discovered,
            string.Join(", ", result.Sources));

        return result;
    }

    /// <summary>
    /// Cross-check live provider model lists against pricing that is already
    /// trusted (scraped/LLM-extracted/provider-pack/catalog). Newly listed models
    /// become available as soon as their price can be resolved by alias, while
    /// avoiding unsafe "unknown model = some default price" assumptions.
    /// </summary>
    private async Task<int> AddDiscoveredModelsWithKnownPricingAsync(
        Dictionary<string, ModelPricingEntry> fetched,
        CancellationToken ct)
    {
        if (discoverySource is null)
            return 0;

        IReadOnlyList<DiscoveredProviderModel> models;
        try
        {
            models = await discoverySource.ListEnabledCloudModelsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Model discovery failed during pricing refresh");
            return 0;
        }

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var model in models)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(model.ModelId)) continue;
            if (model.SupportsEmbeddings || !LooksLikeChatModel(model.ModelId)) continue;
            if (fetched.ContainsKey(model.ModelId)) continue;

            var price = ResolveKnownPricing(model.ModelId, fetched, now);
            if (price is null) continue;

            fetched[model.ModelId] = price;
            added++;
        }

        return added;
    }

    private static ModelPricingEntry? ResolveKnownPricing(
        string modelId,
        Dictionary<string, ModelPricingEntry> fetched,
        DateTime now)
    {
        if (ModelPricingAliasResolver.TryResolve(modelId, fetched, out var pricedModelKey, out var fromFetched))
        {
            return fromFetched with
            {
                Source = string.Equals(pricedModelKey, modelId, StringComparison.OrdinalIgnoreCase)
                    ? $"{fromFetched.Source}+model-discovery"
                    : $"{fromFetched.Source}+model-discovery:{pricedModelKey}",
                FetchedAt = now,
            };
        }

        return null;
    }

    private static bool LooksLikeChatModel(string modelKey)
    {
        if (modelKey.Contains("embedding", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelKey.Contains("embed", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelKey.Contains("tts", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelKey.Contains("whisper", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelKey.Contains("dall-e", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelKey.Contains("image", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelKey.Contains("audio", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
