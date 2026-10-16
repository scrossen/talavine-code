using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics;
using SwarmCore.Abstractions.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Providers;
using SwarmCore.LlmRouting.Services;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    public async Task<Dictionary<string, (bool IsHealthy, string Message)>> TestProvidersAsync()
    {
        var results = new Dictionary<string, (bool IsHealthy, string Message)>();

        foreach (var provider in _providerRegistry.GetAllProviders())
        {
            try
            {
                var configurationIdentity = await GetProviderDiscoveryConfigurationIdentityAsync(provider.ProviderName)
                    .ConfigureAwait(false);
                var result = await _providerDiscovery.ProbeConnectivityAsync(
                        provider,
                        ProviderDiscoveryTimeout,
                        highPriority: true,
                        configurationIdentity: configurationIdentity)
                    .ConfigureAwait(false);
                results[provider.ProviderName] = (result.Completed && result.IsHealthy, result.Message);
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                results[provider.ProviderName] = (false, ex.Message);
            }
        }

        return results;
    }

    public async Task<(bool IsValid, string Message)> ValidateModelAsync(string provider, string model)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        var result = await ValidateModelAsync(
                provider,
                model,
                requireFresh: false,
                CancellationToken.None)
            .ConfigureAwait(false);
        return (result.IsValid, result.Message);
    }

    public async Task<LlmModelValidationResult> ValidateModelAsync(
        string provider,
        string model,
        bool requireFresh,
        CancellationToken cancellationToken)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        provider = ResolveRegisteredProviderName(provider);
        model = model?.Trim() ?? string.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        // Configuration identity must be known before either cache or flight
        // lookup. It is a one-way hash over endpoint, credential, defaults, and
        // provider settings, so evidence from a rotated key/endpoint can never
        // satisfy the new configuration.
        var configurationIdentity = await GetProviderDiscoveryConfigurationIdentityAsync(provider)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = BuildModelValidationKey(provider, model, configurationIdentity);

        return await _modelValidationCoordinator.RunAsync(
                cacheKey,
                requireFresh,
                () => ValidateModelPhysicalAsync(provider, model, configurationIdentity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal const string ModelValidationTimeoutSettingKey = "subscription_cli_timeout_seconds";

    /// <summary>Upper bound for one physical model-validation probe process.</summary>
    internal const int ModelValidationProbeTimeoutSeconds = 60;

    private async Task<LlmModelValidationResult> ValidateModelPhysicalAsync(
        string provider,
        string model,
        string configurationIdentity)
    {
        try
        {
            var llmProvider = _providerRegistry.GetProvider(provider);
            if (llmProvider == null)
            {
                return new LlmModelValidationResult(
                    LlmModelValidationStatus.Unavailable,
                    $"Provider {provider} not registered",
                    configurationIdentity);
            }

            var catalog = await _providerDiscovery.GetModelCatalogAsync(
                    llmProvider,
                    ProviderDiscoveryTimeout,
                    highPriority: true,
                    configurationIdentity: configurationIdentity)
                .ConfigureAwait(false);
            if (!catalog.Completed)
            {
                return new LlmModelValidationResult(
                    catalog.Deferred
                        ? LlmModelValidationStatus.Deferred
                        : LlmModelValidationStatus.Unavailable,
                    $"Could not verify the model catalog for {provider}: {catalog.Message}",
                    configurationIdentity);
            }

            if (!catalog.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                return new LlmModelValidationResult(
                    LlmModelValidationStatus.Invalid,
                    $"Model {model} not supported by {provider}",
                    configurationIdentity);
            }

            // Quick probe. Budget needs to accommodate reasoning models that
            // burn tokens on hidden chain-of-thought before producing visible
            // content — DeepSeek's v4 family, OpenAI's o-series, Gemini 2.5,
            // etc. The provider contract owns reasoning suppression. The
            // process-owned flight guarantees at most one billable invocation
            // per exact provider/model/configuration identity at a time.
            const string functionType = "model-validation";
            const string prompt = "Say 'OK' and nothing else.";
            var options = new LlmRouterOptions
            {
                OverrideProvider = provider,
                OverrideModel = model,
                OverrideProvenance = LlmOverrideProvenance.Calibration,
                DisableFallback = true,
                MaxTokens = 256,
                GenerationParameters = new LlmGenerationParameters
                {
                    MaxOutputTokens = 256,
                    Reasoning = OpenAICompatibleExecutionContract.ResolveBackgroundReasoning(provider, model),
                },
            };
            var selection = new LlmModelSelection
            {
                Provider = provider,
                Model = model,
                IsDefault = false,
                Reason = "Exact diagnostic model-validation probe.",
                AuditCorrelationId = Guid.NewGuid().ToString("N"),
                BlockWeakerPolicyFallback = true,
            };
            var executionSettings = BuildExecutionSettings(options, selection);
            executionSettings[SubscriptionCliExecutionSettings.ProcessPriority] =
                SubscriptionCliProcessPriority.Background;
            // Callers only stop waiting for this shared flight; the physical
            // call itself must be bounded. Without this a "Say OK" probe on a
            // quota-limited subscription CLI ran to the provider's full
            // generation timeout after every waiter had given up.
            executionSettings[ModelValidationTimeoutSettingKey] = ModelValidationProbeTimeoutSeconds;

            // A validation probe is still a physical, potentially billable model
            // call. Give it the same selection -> pending -> terminal ledger as
            // production calls, and fail before invocation if any required receipt
            // cannot be committed.
            await AppendSelectionAuditAsync(
                    functionType,
                    prompt,
                    options,
                    before: null,
                    selection,
                    selection.AuditCorrelationId,
                    requiresVision: false,
                    error: null,
                    failure: null,
                    new RoutingSelectionTrace())
                .ConfigureAwait(false);
            using var probeSlot = await AcquireLocalSlotAsync(
                    provider,
                    model,
                    context: null,
                    executionSettings)
                .ConfigureAwait(false);
            await AppendExecutionStartedAuditAsync(
                    functionType,
                    selection,
                    options,
                    fallbackUsed: false, physicalSettings: executionSettings)
                .ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            LlmResponse response;
            try
            {
                await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                response = await llmProvider.GenerateResponseAsync(
                        prompt,
                        model,
                        executionSettings)
                    .ConfigureAwait(false);
                stopwatch.Stop();
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                stopwatch.Stop();
                await TrackErrorAsync(
                        functionType,
                        selection,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        ex.Message)
                    .ConfigureAwait(false);
                throw;
            }

            await TrackUsageAsync(
                    functionType,
                    selection,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options)
                .ConfigureAwait(false);

            var qualityError = ValidateResponseQuality(response, new LlmRouterOptions(), out _);
            var isValid = response.WasSuccessful && qualityError is null;
            var failureMessage = response.ErrorMessage ?? qualityError ?? "Validation failed";
            // Quota exhaustion, rate limiting, and transient transport trouble are
            // credential/capacity states, not evidence against the model itself.
            // They must surface as Unavailable so the coordinator never caches
            // them as a definitive hour-long Invalid verdict.
            var failureCategory = LlmErrorPolicy.Classify(failureMessage);
            var failureStatus = failureCategory is LlmFailureCategory.RateLimit
                    or LlmFailureCategory.QuotaExhausted
                    or LlmFailureCategory.Timeout
                    or LlmFailureCategory.LaunchDeclined
                ? LlmModelValidationStatus.Unavailable
                : LlmModelValidationStatus.Invalid;
            return new LlmModelValidationResult(
                isValid ? LlmModelValidationStatus.Valid : failureStatus,
                isValid ? "OK" : failureMessage,
                configurationIdentity);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            return new LlmModelValidationResult(
                LlmModelValidationStatus.Unavailable,
                ex.Message,
                configurationIdentity);
        }
    }

    private static string BuildModelValidationKey(
        string provider,
        string model,
        string configurationIdentity)
        => $"{provider.Length}:{provider}{model.Length}:{model}{configurationIdentity}";

    internal static string CleanHtmlFromPrompt(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        // Remove common HTML tags
        var cleaned = Regex.Replace(prompt, @"<[^>]+>", " ");
        // Preserve prompt structure. Flattening every newline into a space
        // destroys evidence boundaries and field instructions used by structured
        // local-model prompts, which can turn a bounded JSON response into an
        // unparseable max-token completion.
        cleaned = cleaned.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        cleaned = Regex.Replace(cleaned, @"[^\S\n]+", " ");
        cleaned = Regex.Replace(cleaned, @" *\n *", "\n");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    // Structural extraction is shared with the rest of the system via
    // SwarmCore.Abstractions.Json.JsonRepair so the router, the message-pipeline
    // scorer, and the AI services all salvage identically.
    private static T DeserializeJsonResponse<T>(string response)
    {
        if (JsonRepair.TryDeserializeComplete<T>(response, LlmJsonOptions, out var value, out var issue)
            && value is not null)
        {
            return value;
        }

        throw new JsonException(string.IsNullOrWhiteSpace(issue)
            ? "Failed to deserialize response."
            : issue);
    }

    private static bool TryNormalizeJsonResponse(string? response, string? expectedFormat, out string normalized)
        => JsonRepair.TryNormalize(response, IsJsonArrayExpected(expectedFormat), out normalized);

    private static bool IsJsonArrayExpected(string? expectedFormat)
    {
        if (string.IsNullOrWhiteSpace(expectedFormat))
        {
            return false;
        }

        var normalized = expectedFormat.Trim().ToLowerInvariant();
        return normalized is "json_array" or "json-array" or "array"
               || normalized.Contains("json array", StringComparison.Ordinal);
    }
}
