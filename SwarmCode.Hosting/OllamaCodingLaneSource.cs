using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting;

/// <summary>Fresh installed-artifact evidence per request. Settings remain revisioned host-owned data.</summary>
public sealed class OllamaCodingLaneSource(IOptions<CodingLaneOptions> options, IOptions<OllamaProviderOptions> ollama,
    ModelSourcesStore store, OllamaSetupClient client, ILogger<OllamaCodingLaneSource> logger) : ICodingLaneSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CodingLaneSet? snapshot;
    public CodingLaneSet GetLanes() => Volatile.Read(ref snapshot) ?? CodingLaneSet.From(options.Value);
    public ValueTask<CodingLaneSet> GetAdmissionLanesAsync(string conversationId, CancellationToken ct = default)
        => ValueTask.FromResult(GetLanes());

    public async ValueTask<CodingLaneSet> GetLanesAsync(string conversationId, CancellationToken ct = default)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        return GetLanes();
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var baseline = CodingLaneSet.From(options.Value);
            var document = store.Read();
            var configured = document.Sources.FirstOrDefault(s => s.Kind == "Ollama");
            if (configured is { Enabled: false })
            {
                Volatile.Write(ref snapshot, baseline with { Lanes = baseline.Lanes.Where(l => l.Provider != "Ollama").ToArray() });
                return;
            }
            var endpoint = ollama.Value.Endpoint;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var inventory = await client.InventoryAsync(endpoint, timeout.Token).ConfigureAwait(false);
                var lanes = OllamaCodingLanes.Merge(baseline.Lanes, endpoint, inventory.Models, document.LocalModels, document.PreferLocalModelsOnDecline);
                Volatile.Write(ref snapshot, baseline with { Lanes = lanes.OrderBy(l => l.Priority).ToArray() });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Remove previously discovered routes on failure; never route on a stale installed inventory.
                logger.LogDebug(ex, "Ollama inventory unavailable; automatic local fallback is not offered.");
                Volatile.Write(ref snapshot, baseline with { Lanes = baseline.Lanes.Select(l => l.Provider == "Ollama" ? l with { PreferOnDecline = false } : l).ToArray() });
            }
        }
        finally { gate.Release(); }
    }
}
