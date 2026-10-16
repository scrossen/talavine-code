namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>One persisted per-1K-token pricing row keyed by model identifier.</summary>
public sealed record ModelPricingRecord(
    string ModelKey,
    decimal InputCostPer1K,
    decimal OutputCostPer1K,
    decimal? EmbeddingCostPer1K,
    DateTime LastUpdated);

/// <summary>
/// Host seam for the durable model-pricing table consumed by cost estimation
/// and Model Scout candidate assembly. The reusable pricing refresh service
/// (SwarmCore.LlmRouting) reads, diffs, and upserts through this seam so any
/// host keeps pricing current without reimplementing the refresh pipeline.
/// </summary>
public interface IModelPricingStore
{
    Task<bool> HasAnyAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ModelPricingRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert-or-update the given rows exactly as supplied (the refresh service
    /// owns change detection and stamps <see cref="ModelPricingRecord.LastUpdated"/>
    /// only on real change). Implementations must invalidate any cost cache
    /// after committing.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<ModelPricingRecord> rows, CancellationToken ct = default);
}

/// <summary>One provider model surfaced by live model discovery.</summary>
public sealed record DiscoveredProviderModel(
    string Provider,
    string ModelId,
    bool SupportsEmbeddings);

/// <summary>
/// Optional host seam letting the pricing refresh cross-check live provider
/// model lists so newly listed models become priced (via alias resolution)
/// as soon as they appear. Hosts without live discovery simply omit it.
/// </summary>
public interface IModelPricingDiscoverySource
{
    Task<IReadOnlyList<DiscoveredProviderModel>> ListEnabledCloudModelsAsync(
        CancellationToken ct = default);
}
