using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.Compute;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Reuses receiver-local Scout admission, credentials, containment and quota accounting.
/// The coordinator supplies text, never commands, tools, provider settings or credentials.
/// This first transport contract cannot authorize metered API execution.
/// </summary>
public sealed class ComputeInferenceExecutor(
    IModelScout scout, ILlmRouter router, ILlmSettingsProvider settings,
    ILogger<ComputeInferenceExecutor> logger) : IComputeInferenceExecutor
{
    public async Task<ComputeInferenceResult> ExecuteAsync(ComputeInferenceRequest request,
        bool subscriptionSharingEnabled, CancellationToken cancellationToken = default)
    {
        try { return await ExecuteCoreAsync(request, subscriptionSharingEnabled, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure(request, "route_unavailable", ex);
            return Failure(request, "route_unavailable");
        }
    }

    // Records why a coded failure happened on the receiver so a defect is not
    // indistinguishable from an unavailable route. Only the exception type and a
    // redacted, bounded diagnostic are logged: never the prompt, and nothing is
    // transported back to the coordinator.
    private void LogFailure(ComputeInferenceRequest request, string errorCode, Exception ex)
        => logger.LogWarning(
            "Compute inference request {RequestId} for job {JobId} failed with {ErrorCode}: {ExceptionType}: {Diagnostic}",
            request.Id,
            request.JobId,
            errorCode,
            ex.GetType().FullName,
            RoutingDiagnosticSanitizer.Sanitize(ex.Message, 500));

    private async Task<ComputeInferenceResult> ExecuteCoreAsync(ComputeInferenceRequest request,
        bool subscriptionSharingEnabled, CancellationToken cancellationToken)
    {
        try { ComputeInferenceRequestValidation.Validate(request, DateTime.UtcNow); }
        catch (ArgumentException) { return Failure(request, "invalid_request"); }
        if (request.FunctionType != FunctionTypes.ContentGeneration) return Failure(request, "invalid_request");

        var allowSubscription = subscriptionSharingEnabled && request.AllowSubscription && !request.LocalOnly;
        var allowed = (await settings.GetAllProviderSettingsAsync().ConfigureAwait(false))
            .Where(p => p.IsEnabled && (IsAllowedLocal(p)
                || (allowSubscription
                    && IsContainedSubscription(p.ProviderName))))
            .ToArray();
        if (allowed.Length == 0) return Failure(request, "route_unavailable");

        var preference = await settings.GetFunctionPreferenceAsync(request.FunctionType).ConfigureAwait(false);
        if (preference is { IsEnabled: false }) return Failure(request, "route_not_authorized");
        var provider = request.ProviderName;
        var model = request.Model;
        if (LlmPreferenceAuthors.IsUserPinned(preference?.PinnedByUserAt))
        {
            if (provider is not null && (!Same(provider, preference!.PreferredProvider)
                || !Same(model, preference.PreferredModel))) return Failure(request, "pin_conflict");
            provider = preference!.PreferredProvider;
            model = preference.PreferredModel;
        }
        if (provider is not null && !allowed.Any(p => Same(p.ProviderName, provider)))
            return Failure(request, "route_not_authorized");

        var decision = await scout.RecommendAsync(new ModelScoutRecommendationRequest
        {
            FunctionType = request.FunctionType,
            Provider = provider,
            ExactModel = model,
            AllowedProviders = allowed.Select(p => p.ProviderName).ToArray(),
            Workload = LlmWorkloadKind.DeferredEnrichment,
            Interactivity = LlmInteractivity.Background,
            PrivacyClass = allowSubscription ? LlmPrivacyClass.CloudOk : LlmPrivacyClass.LocalOnly,
            MaximumEstimatedCostUsd = 0,
            EstimatedInputTokens = (request.Prompt.Length + (request.SystemPrompt?.Length ?? 0)) / 4 + 1,
            EstimatedOutputTokens = request.MaxTokens,
            RequireCurrentFunctionEvidence = true,
            // This leased background job owns bounded readiness work. A passive
            // cache-only check can indefinitely defer a healthy local receiver
            // whenever its short-lived identity snapshot has expired.
            AllowLiveInventoryRefresh = true,
            MaintainServingInventory = true,
            AuditSessionId = "crm-compute",
            AuditTurnId = request.Id.ToString("N"),
        }, cancellationToken).ConfigureAwait(false);
        var candidate = decision.Selected?.Candidate;
        if (candidate is null || decision.Selected!.IsEligible == false)
            return Failure(request, "route_unavailable");
        var selectedSettings = allowed.FirstOrDefault(p => Same(p.ProviderName, candidate.Provider));
        if (selectedSettings is null || (provider is not null && (!Same(provider, candidate.Provider)
            || !Same(model, candidate.Model)))
            || (!IsContainedSubscription(candidate.Provider)
                && !LlmProviderLocalityPolicy.IsTrustedLocalRoute(candidate.Provider, candidate.Model, selectedSettings)))
            return Failure(request, "route_not_authorized");

        var usages = new List<LlmUsageCompletion>();
        var options = new LlmRouterOptions
        {
            OverrideProvider = candidate.Provider,
            OverrideModel = candidate.Model,
            OverrideProvenance = LlmOverrideProvenance.Policy,
            DisableFallback = true,
            RequireCurrentFunctionEvidence = true,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature is { } temperature ? (float)temperature : null,
            Workload = LlmWorkloadKind.DeferredEnrichment,
            Interactivity = LlmInteractivity.Background,
            PrivacyClass = IsContainedSubscription(candidate.Provider) ? LlmPrivacyClass.CloudOk : LlmPrivacyClass.LocalOnly,
            Urgency = LlmUrgency.Batch,
            RequestCancellationToken = cancellationToken,
            SessionId = "crm-compute",
            TurnId = request.Id.ToString("N"),
            UsageCompletionObserver = usage => { usages.Add(usage); return Task.CompletedTask; },
        };
        try
        {
            var prompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? request.Prompt
                : request.SystemPrompt + "\n\n" + request.Prompt;
            var content = await router.GenerateResponseAsync(request.FunctionType, prompt, options).ConfigureAwait(false);
            var successfulUsage = usages.LastOrDefault(u => u.WasSuccessful);
            if (successfulUsage is null || !Same(successfulUsage.Provider, candidate.Provider)
                || !Same(successfulUsage.Model, candidate.Model)) return Failure(request, "execution_receipt_missing") with
                {
                    ProviderName = candidate.Provider, Model = candidate.Model,
                    InputTokens = usages.Sum(u => u.InputTokens), OutputTokens = usages.Sum(u => u.OutputTokens),
                    EstimatedCostUsd = usages.Sum(u => u.EstimatedCost), UsageMeasurementComplete = false,
                };
            return new ComputeInferenceResult
            {
                Success = !string.IsNullOrWhiteSpace(content) && content.Length <= 100_000,
                Content = content.Length <= 100_000 ? content : "",
                ProviderName = successfulUsage.Provider,
                Model = successfulUsage.Model,
                InputTokens = usages.Sum(u => u.InputTokens),
                OutputTokens = usages.Sum(u => u.OutputTokens),
                EstimatedCostUsd = usages.Sum(u => u.EstimatedCost),
                UsageMeasurementComplete = usages.All(u => u.MeasurementComplete && u.WasDurablyRecorded),
                ErrorCode = string.IsNullOrWhiteSpace(content) || content.Length > 100_000 ? "invalid_output" : null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Provider exception text may contain prompt text or credentials; never transport it.
            LogFailure(request, "inference_failed", ex);
            return Failure(request, "inference_failed") with
            {
                ProviderName = candidate.Provider, Model = candidate.Model,
                InputTokens = usages.Sum(u => u.InputTokens), OutputTokens = usages.Sum(u => u.OutputTokens),
                EstimatedCostUsd = usages.Sum(u => u.EstimatedCost),
                UsageMeasurementComplete = usages.Count > 0 && usages.All(u => u.MeasurementComplete && u.WasDurablyRecorded),
            };
        }
    }

    private static bool IsAllowedLocal(LlmProviderSettings settings)
        => LlmProviderLocalityPolicy.IsLocalProviderName(settings.ProviderName)
            && LlmProviderLocalityPolicy.IsTrustedLocalProvider(settings.ProviderName, settings);

    // Gemini's reviewed adapter does not yet guarantee tool-free containment.
    private static bool IsContainedSubscription(string provider)
        => ComputeInferenceExecutionPolicy.IsContainedSubscription(provider);
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static ComputeInferenceResult Failure(ComputeInferenceRequest request, string code) => new()
    { ProviderName = request.ProviderName ?? "", Model = request.Model ?? "", ErrorCode = code };
}
