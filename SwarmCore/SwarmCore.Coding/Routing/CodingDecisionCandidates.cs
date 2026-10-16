using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.Coding.Routing;

/// <summary>Builds editable suggestions from the host's lanes and current catalogs; no customer or model IDs.</summary>
public static class CodingDecisionCandidates
{
    public static async Task<IReadOnlyList<ModelDecisionCandidate>> SuggestAsync(IReadOnlyList<CodingLane> lanes,
        CodingModelCatalog catalog, IEnumerable<INativeLaneRunner> runners, CancellationToken ct = default)
    {
        var choices = new List<ModelDecisionCandidate>();
        // Include every configured route, including custom/local model IDs absent from provider packs.
        // Catalog metadata can add alternatives, but must not hide the host's explicit choices.
        foreach (var lane in lanes.Where(l => !string.IsNullOrWhiteSpace(l.Model)))
            choices.Add(new("option_" + choices.Count, lane.Name, lane.Model,
                lane.Description ?? "Configured model. Describe the tasks this model is suited for."));
        foreach (var lane in lanes)
        {
            var provider = lane.IsNative ? runners.FirstOrDefault(r => r.Supports(lane))?.ModelCatalogProvider : lane.Provider;
            if (provider is null || !ProviderPackCatalog.TryGet(provider, out var pack)) continue;
            IReadOnlyList<string> models;
            try { models = ExpandThinkingVariants(provider, await catalog.GetModelsAsync(lane, ct).ConfigureAwait(false)); }
            catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            Add(pack!.ModelDefaults.Economy, "low", "Mechanical edits, formatting, and straightforward explanations; favor speed and low cost.");
            Add(pack.ModelDefaults.Balanced, "medium", "Bounded features, ordinary debugging, and tests; moderate complexity and uncertainty.");
            Add(pack.ModelDefaults.Frontier, "high", "Ambiguous failures, concurrency, security, and changes across components; favor reliability.");
            Add(pack.ModelDefaults.Frontier, "max", "Exceptionally difficult systemic investigations or architecture with interacting correctness constraints; highest effort.");

            void Add(string? model, string effort, string description)
            {
                if (model is null) return;
                // Variants come from discovery or the adapter's exact-model capability metadata.
                var variant = (effort == "max" ? new[] { "max", "xhigh" } : new[] { effort })
                    .Select(e => models.FirstOrDefault(m => m.Equals(model + "@" + e, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(m => m is not null);
                var selected = variant ?? (effort == "max" ? null : models.FirstOrDefault(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)));
                if (selected is null || choices.Any(c => c.Route == lane.Name && c.Model == selected)) return;
                choices.Add(new("option_" + choices.Count, lane.Name, selected, description));
            }
        }
        return choices.Take(26).ToArray();
    }

    public static IReadOnlyList<string> ExpandThinkingVariants(string provider, IReadOnlyList<string> models)
    {
        // The Codex execution dialect defines @effort for both native and engine adapters.
        // Other adapters retain their discovered model IDs until they expose an equivalent contract.
        if (!ProviderPackCatalog.TryGet(provider, out var pack) || pack!.ExecutionDialect != "codex-cli-exec-v1") return models;
        return models.Concat(models.Where(m => !m.Contains('@')).SelectMany(m =>
            ModelRouteCapabilities.ReasoningEfforts(provider, m).Select(e => m + "@" + e))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
