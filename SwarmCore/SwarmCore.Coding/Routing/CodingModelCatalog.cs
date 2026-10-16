using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Coding.Routing;

/// <summary>Model choices come from the same registered providers used for execution.</summary>
public sealed class CodingModelCatalog(ILlmProviderRegistry registry, IEnumerable<INativeLaneRunner> nativeRunners)
{
    public async Task<IReadOnlyList<string>> GetModelsAsync(CodingLane lane, CancellationToken ct = default)
        => (await GetCatalogAsync(lane, ct).ConfigureAwait(false)).Models;

    public async Task<LlmModelCatalog> GetCatalogAsync(CodingLane lane, CancellationToken ct = default)
    {
        var providerName = lane.IsNative
            ? nativeRunners.FirstOrDefault(r => r.Supports(lane))?.ModelCatalogProvider
            : lane.Provider;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var provider = registry.GetProvider(providerName ?? lane.Provider)
            ?? throw new InvalidOperationException("The model provider is not configured.");
        var catalog = provider is ILlmModelCatalogProvider discovery
            ? await discovery.GetModelCatalogAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false)
            : new LlmModelCatalog(await provider.GetAvailableModelsAsync().WaitAsync(timeout.Token).ConfigureAwait(false), "Provider catalog");
        return catalog with { Models = new[] { lane.Model }.Concat(catalog.Models).Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
}
