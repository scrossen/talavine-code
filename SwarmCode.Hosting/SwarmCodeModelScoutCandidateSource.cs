using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting;

/// <summary>
/// Host inventory adapter for the shared Model Scout ranker. Advertised capabilities
/// and installed artifacts are provisional catalog evidence, never quality clearance.
/// </summary>
public sealed class SwarmCodeModelScoutCandidateSource(
    ILlmSettingsProvider settings,
    ILlmProviderRegistry registry,
    OllamaSetupClient ollama,
    IOptions<OllamaProviderOptions> ollamaOptions,
    ILlmApiKeyProvider? keys = null,
    ILogger<SwarmCodeModelScoutCandidateSource>? logger = null,
    ModelSourcesStore? modelSources = null) : IModelScoutCandidateSource
{
    private const int MaxModelsPerProvider = 8;
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<ModelScoutCandidate>> LoadCandidatesAsync(
        ModelScoutRecommendationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidates = new List<ModelScoutCandidate>();
        var rows = await settings.GetAllProviderSettingsAsync().ConfigureAwait(false);
        var configured = modelSources?.Read().Sources ?? [];
        foreach (var source in configured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Validate();
            if (rows.Any(row => row.ProviderName.Equals(source.Provider, StringComparison.OrdinalIgnoreCase))) continue;
            // The settings listing omits disabled rows. An explicit disable must win
            // over a connected source; never replace it with an enabled host row.
            var explicitRow = await settings.GetProviderSettingsAsync(source.Provider).ConfigureAwait(false);
            if (explicitRow is not null) rows.Add(explicitRow);
            else if (source.Enabled && registry.GetProvider(source.Provider) is not null)
                rows.Add(new LlmProviderSettings
                {
                    ProviderName = source.Provider, Endpoint = source.Endpoint,
                    DefaultModel = source.Model, IsEnabled = true,
                }); // In-memory inventory adapter only; no settings or qualification write.
        }
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(request.Provider)
                && !row.ProviderName.Equals(request.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            var provider = registry.GetProvider(row.ProviderName);
            if (provider is null || !row.IsEnabled || row.DisabledByHealthCheck) continue;
            var source = configured.FirstOrDefault(item => item.Provider.Equals(row.ProviderName, StringComparison.OrdinalIgnoreCase));
            if (source is { Enabled: false }) continue;
            if (source is not null && OllamaProviderFamily.IsLocalDaemon(row.ProviderName)
                && !SameLoopbackEndpoint(source.Endpoint, ollamaOptions.Value.Endpoint)) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DiscoveryBudget);
                await AddProviderAsync(candidates, row, provider, request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A stale configured model is not a substitute for current discovery.
                logger?.LogDebug("Model Scout discovery timed out for {Provider}", row.ProviderName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex, "Model Scout discovery unavailable for {Provider}", row.ProviderName);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return candidates;
    }

    private async Task AddProviderAsync(List<ModelScoutCandidate> result, LlmProviderSettings row,
        ILlmProvider provider, ModelScoutRecommendationRequest request, CancellationToken ct)
    {
        var localDaemon = OllamaProviderFamily.IsLocalDaemon(row.ProviderName);
        var actualEndpoint = localDaemon ? ollamaOptions.Value.Endpoint : row.Endpoint;
        // The adapter's endpoint, not an older settings row, owns this inventory.
        // An Ollama daemon on a public host cannot acquire this device's local proof.
        if (localDaemon && (!TryNormalizeLoopbackEndpoint(actualEndpoint, out _)
            || !string.IsNullOrWhiteSpace(row.Endpoint) && !TryNormalizeLoopbackEndpoint(row.Endpoint, out _))) return;
        var trustedLocal = localDaemon || LlmProviderLocalityPolicy.IsTrustedLocalProvider(row);
        var subscription = SubscriptionCliProviders.IsSubscriptionCliProvider(row.ProviderName);
        if (!trustedLocal && !subscription && provider is not ICredentialFreeLlmProvider
            && !(keys is not null ? await keys.HasApiKeyAsync(row.ProviderName).ConfigureAwait(false)
                : !string.IsNullOrWhiteSpace(row.ApiKey))) return;

        IReadOnlyList<OllamaInstalledModel>? installed = null;
        LlmModelCatalog catalog;
        if (localDaemon)
        {
            // /show's authoritative locality check excludes aliases of hosted models.
            var inventory = await ollama.InventoryAsync(actualEndpoint!, ct).ConfigureAwait(false);
            installed = inventory.Models.Where(model => model.IsOnDevice).ToArray();
            catalog = new(installed.Select(model => model.Name).ToArray(), "ollama-installed-artifacts");
        }
        else if (provider is ILlmModelCatalogProvider catalogProvider)
            catalog = await catalogProvider.GetModelCatalogAsync(ct).ConfigureAwait(false);
        else
        {
            // The legacy listing interface has no cancellation argument. Bound our wait
            // and observe late failures without inventing a configured-default fallback.
            var listing = provider.GetAvailableModelsAsync();
            try { catalog = new(await listing.WaitAsync(ct).ConfigureAwait(false), "provider-advertised-models"); }
            catch
            {
                _ = listing.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw;
            }
        }

        var models = catalog.Models.Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(model => string.IsNullOrWhiteSpace(request.ExactModel)
                || model.Equals(request.ExactModel, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(model => model.Equals(row.DefaultModel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model, StringComparer.OrdinalIgnoreCase).Take(MaxModelsPerProvider);
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            var detail = catalog.Details.FirstOrDefault(item => item.Id.Equals(model, StringComparison.OrdinalIgnoreCase));
            var artifact = installed?.SingleOrDefault(item => item.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
            var capability = provider is ILlmModelCapabilityEvidenceProvider metadata
                ? await metadata.GetModelCapabilityEvidenceAsync(model, ct).ConfigureAwait(false) : null;
            if (capability is not null && (!capability.Provider.Equals(row.ProviderName, StringComparison.OrdinalIgnoreCase)
                || !capability.Model.Equals(model, StringComparison.OrdinalIgnoreCase)
                || (artifact is not null && (!capability.ArtifactDigest.Equals(artifact.Digest, StringComparison.Ordinal)
                    || !SameLoopbackEndpoint(actualEndpoint, capability.Endpoint)))))
                continue; // Never join metadata from another artifact or route.

            // Local daemon metadata is only admitted alongside fresh on-device inventory.
            var isLocal = trustedLocal && (!OllamaProviderFamily.IsOllamaFamily(row.ProviderName) || artifact is not null);
            var free = isLocal || subscription;
            var input = free ? 0m : detail?.InputPricePerMillionUsd / 1000m;
            var output = free ? 0m : detail?.OutputPricePerMillionUsd / 1000m;
            result.Add(new ModelScoutCandidate
            {
                Provider = row.ProviderName,
                Model = model,
                CapabilityScore = 75m,
                CapabilityEvidenceKind = ModelScoutCapabilityEvidenceKind.CatalogPrior,
                IsAvailable = true,
                IsLocal = isLocal,
                IsSubscription = subscription,
                SupportsToolUse = capability?.SupportsToolUse ?? artifact?.SupportsTools ?? detail?.SupportsTools,
                SupportsVision = capability?.SupportsVision,
                SupportsStructuredOutput = capability?.SupportsStructuredOutput,
                MaxContextTokens = capability?.MaxContextTokens ?? detail?.ContextTokens,
                InputCostPer1K = input,
                OutputCostPer1K = output,
                EstimatedCostUsd = input is not null && output is not null
                    ? request.EstimatedInputTokens / 1000m * input + request.EstimatedOutputTokens / 1000m * output : null,
                CapabilityEvidenceSource = "swarmcode-provider-catalog:provisional; " + catalog.Source,
                EvidenceSource = capability?.Source ?? catalog.Source,
                EvidenceVersion = artifact?.Digest,
                EvidenceTimestampUtc = capability?.ObservedAtUtc.UtcDateTime ?? DateTime.UtcNow,
                IsEvidenceCurrent = true,
                // No FunctionEvidence or measured score: inventory is not qualification.
            });
        }
    }

    private static bool SameLoopbackEndpoint(string? expected, string? observed)
        => TryNormalizeLoopbackEndpoint(expected, out var expectedEndpoint)
           && TryNormalizeLoopbackEndpoint(observed, out var observedEndpoint)
           && expectedEndpoint == observedEndpoint;

    private static bool TryNormalizeLoopbackEndpoint(string? endpoint, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        normalized = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            + uri.AbsolutePath.TrimEnd('/');
        return true;
    }
}
