using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Coding.Routing;

public sealed class CodingFallbackDiscoveryOptions
{
    public bool Enabled { get; set; }
    public bool RememberResponses { get; set; } = true;
    public List<string> Providers { get; set; } = [];
    public List<string> PreferredModelNamespaces { get; set; } = [];
    public int MaxCandidates { get; set; } = 3;
    public decimal MaxInputPricePerMillionUsd { get; set; } = 0.25m;
    public decimal MaxOutputPricePerMillionUsd { get; set; } = 1m;
}

public interface ICodingFallbackDiscovery
{
    Task<CodingFallbackDiscoveryResult> DiscoverAsync(IReadOnlyList<CodingLane> configured,
        IReadOnlyList<CodingLane> tried, CodingFallbackContext context, CancellationToken ct);
}

/// <summary>Offers bounded catalog candidates to the existing turn/router admission path.
/// Catalog metadata never grants tools, quality clearance, or willingness to execute a request.</summary>
public sealed class CatalogCodingFallbackDiscovery(ILlmProviderRegistry registry, IOptions<CodingOptions> options,
    IModelResponseMemory? memory = null, ILogger<CatalogCodingFallbackDiscovery>? logger = null) : ICodingFallbackDiscovery
{
    public async Task<CodingFallbackDiscoveryResult> DiscoverAsync(IReadOnlyList<CodingLane> configured,
        IReadOnlyList<CodingLane> tried, CodingFallbackContext context, CancellationToken ct)
    {
        var policy = options.Value.FallbackDiscovery;
        if (!policy.Enabled) return new([]);
        IReadOnlyList<ModelResponseObservation> history = [];
        string? historyWarning = null;
        if (context.UseMemory && policy.RememberResponses && memory is not null)
        {
            try { history = await memory.ReadAsync(context.ScopeId, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Could not load model response history; catalog candidates retain unknown history");
                historyWarning = "Model response history could not be read. Searching with unknown history; existing evidence has not been replaced.";
            }
        }
        history = history.Where(row => row.ScopeId == context.ScopeId).ToArray();
        var result = new List<CodingLane>();
        foreach (var providerName in policy.Providers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A catalog is considered only for a provider the host already offered.
            if (!configured.Any(l => l.Provider.Equals(providerName, StringComparison.OrdinalIgnoreCase))) continue;
            if (registry.GetProvider(providerName) is not ILlmModelCatalogProvider discovery) continue;
            // One provider's catalog being down is a miss for that provider only, not for every other provider's candidates.
            LlmModelCatalog catalog;
            try { catalog = await discovery.GetModelCatalogAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Model catalog of {Provider} is unavailable; its fallback candidates are skipped", providerName);
                continue;
            }
            result.AddRange(SelectCandidates(providerName, catalog.Details, tried, policy, history, context.TaskFingerprint));
        }
        return new(result.Take(Math.Clamp(policy.MaxCandidates, 1, 10)).ToArray(), historyWarning);
    }

    internal static IReadOnlyList<CodingLane> SelectCandidates(string provider, IReadOnlyList<LlmCatalogModel> models,
        IReadOnlyList<CodingLane> tried, CodingFallbackDiscoveryOptions policy,
        IReadOnlyList<ModelResponseObservation>? history = null, string? taskFingerprint = null, DateTimeOffset? now = null)
    {
        var seen = tried.Where(l => l.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).Select(l => l.Model)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CodingLane>();
        var candidates = models.Where(m => m.SupportsTools && m.ContextTokens >= 32768
            && m.InputPricePerMillionUsd is >= 0 && m.InputPricePerMillionUsd <= policy.MaxInputPricePerMillionUsd
            && m.OutputPricePerMillionUsd is >= 0 && m.OutputPricePerMillionUsd <= policy.MaxOutputPricePerMillionUsd
            && !m.Id.StartsWith('~') && !m.Id.Contains(":batch", StringComparison.OrdinalIgnoreCase)
            && !seen.Contains(m.Id))
            .OrderBy(m => policy.PreferredModelNamespaces.Contains(Namespace(m.Id), StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(m => ModelResponseLearning.Score(provider, m.Id, taskFingerprint, history ?? [], now ?? DateTimeOffset.UtcNow))
            .ThenBy(m => m.EndpointModerated == false ? 0 : 1)
            .ThenBy(m => m.InputPricePerMillionUsd + m.OutputPricePerMillionUsd)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        // First cover distinct model families; do not spend all attempts on variants of one family.
        foreach (var model in candidates.Where(m => selectedNamespaces.Add(Namespace(m.Id)))) Add(model);
        foreach (var model in candidates.Where(m => !result.Any(l => l.Model == m.Id))) Add(model);
        return result.Take(Math.Clamp(policy.MaxCandidates, 1, 10)).ToArray();

        static string Namespace(string id) => id.Split('/')[0];
        void Add(LlmCatalogModel model)
        {
            var key = provider + "/" + model.Id;
            result.Add(new CodingLane(provider.ToLowerInvariant() + ":" + model.Id, provider, model.Id)
            { Description = "Catalog candidate; willingness and task completion must be observed", HealthKey = "route:" + key });
        }
    }
}
