using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.Chat;

/// <summary>Provider-advertised choices take precedence over shipped adapter capability metadata.</summary>
public static class ChatReasoningOptions
{
    public static async Task<IReadOnlyList<string>> GetAsync(ILlmProviderRegistry? registry, string provider, string model, CancellationToken ct = default)
    {
        model = ModelReasoningSelection.Split(model).Model;
        if (registry?.GetProvider(provider) is ILlmModelCatalogProvider discovery)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var catalog = await discovery.GetModelCatalogAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (catalog.ReasoningEfforts.TryGetValue(model, out var choices)) return choices;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        return ModelRouteCapabilities.ReasoningEfforts(provider, model);
    }
}
