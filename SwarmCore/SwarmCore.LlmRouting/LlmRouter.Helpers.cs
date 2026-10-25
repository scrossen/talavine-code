using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;
using SwarmCore.LlmRouting.Services;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    private const string SubscriptionUsageRemainingDiagnosticPrefix = "subscription_usage_remaining_fraction=";
    private static readonly TimeSpan SubscriptionCliRuntimeFailureCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AutomaticTransientRuntimeFailureCooldown = TimeSpan.FromMinutes(5);

    // primaryCategory is the failure category the caller resolved at
    // classification time (when it has one); it rides alongside the
    // wire-compatible legacy reason string so the failover exception keeps the
    // exact typed classification instead of re-deriving a lossy one from the
    // reason token.
    private async Task<string> HandleFallbackAsync(string functionType, string prompt, LlmModelSelection selectedModel,
        Stopwatch stopwatch, LlmRouterOptions? options, string reason, string requestId,
        LlmFailureCategory? primaryCategory = null)
    {
        LlmModelSelection? attemptedFallback = null;
        try
        {
        var survivalExecution = await TryExecuteTextSurvivalSetAsync(
            functionType,
            prompt,
            selectedModel,
            options,
            reason,
            requestId,
            primaryCategory).ConfigureAwait(false);
        if (survivalExecution is not null)
        {
            return survivalExecution.Content;
        }

        if (options?.DisableFallback == true)
        {
            var message = $"Fallback disabled for {selectedModel.Provider}/{selectedModel.Model} on {functionType}; selected route failed after same-route recovery (reason={reason}).";
            await ReportOperationalFailureAsync(functionType, selectedModel, requiresVision: false, reason, message);
            throw new FallbackDisabledException(message, reason);
        }

        // Trigger self-healing only when route substitution is permitted. An
        // exact-model request must not mutate its route in the background either.
        if (reason == "model_not_found"
            && _selfHealer != null
            && !selectedModel.BlockWeakerPolicyFallback
            && !HasExactValidatedAdmission(selectedModel, options))
        {
            _ = TriggerSelfHealingAsync(functionType, selectedModel.Provider, selectedModel.Model, requiresVision: false);
        }

        var (fallbackProvider, fallbackModel) = await ResolveFallbackRouteAsync(
            functionType,
            selectedModel,
            prompt,
            requiresVision: false,
            reason,
            options: options);
        attemptedFallback = CarryRoutePolicy(selectedModel, new LlmModelSelection
        {
            Provider = fallbackProvider,
            Model = fallbackModel,
            IsDefault = false,
            Reason = $"Fallback due to {reason}",
            Context = selectedModel.Context,
            AuditCorrelationId = selectedModel.FallbackAuditCorrelationId ?? selectedModel.AuditCorrelationId,
        }, promoteConfiguredFallback: true);

        _logger.LogWarning("Using fallback {FallbackProvider}/{FallbackModel} due to {Reason}",
            fallbackProvider, fallbackModel, reason);

        var provider = _providerRegistry.GetProvider(fallbackProvider);
        if (provider == null)
        {
            throw new InvalidOperationException($"Fallback provider {fallbackProvider} not registered");
        }

        await AppendFallbackSelectionAuditAsync(
            functionType,
            prompt,
            selectedModel,
            attemptedFallback,
            options,
            reason).ConfigureAwait(false);

        // Debug log the fallback request
        var fallbackRequestId = $"{requestId}-fb";
        _debugLogger.LogRequest(new LlmDebugRequest(
            RequestId: fallbackRequestId,
            FunctionType: functionType,
            Provider: fallbackProvider,
            Model: fallbackModel,
            Prompt: prompt,
            Temperature: options?.Temperature,
            MaxTokens: options?.MaxTokens,
            IsFallback: true,
            FallbackReason: reason));

        // Synthesize a selection that carries the original context onto the fallback route,
        // so context-aware defaults (reasoning_effort for gpt-5 etc.) still apply on fallback.
        var fallbackProbe = attemptedFallback;
        var executionSettings = BuildExecutionSettings(options, fallbackProbe);
        using var fallbackSlot = await AcquireLocalSlotAsync(
            fallbackProvider,
            fallbackModel,
            selectedModel.Context,
            executionSettings);
        await AppendExecutionStartedAuditAsync(
            functionType,
            attemptedFallback,
            options,
            fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
        LlmResponse? response;
        stopwatch.Restart();
        try
        {
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            response = await provider.GenerateResponseAsync(prompt, fallbackModel, executionSettings);
            stopwatch.Stop();
        }
        catch (Exception)
            when (IsRequestCancellationRequested(options))
        {
            // The caller's overall request budget expired while the fallback was
            // running. This is not evidence that the provider timed out: close
            // the execution receipt, but do not write provider cooldown or
            // negative known-good evidence.
            stopwatch.Stop();
            await TrackCancellationAsync(
                functionType,
                fallbackProbe,
                stopwatch.ElapsedMilliseconds,
                options,
                fallbackUsed: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            stopwatch.Stop();
            await TrackErrorAsync(
                functionType,
                fallbackProbe,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                ex.Message,
                fallbackUsed: true).ConfigureAwait(false);
            if (IsRateLimitError(ex))
            {
                await RecordCooldownIfRateLimitAsync(fallbackProvider, ex).ConfigureAwait(false);
            }
            else if (IsTimeoutOrTransientError(ex))
            {
                await RecordCooldownIfTimeoutOrTransientAsync(fallbackProvider, ex).ConfigureAwait(false);
            }
            throw new FallbackExecutionException(
                $"Fallback provider {fallbackProvider}/{fallbackModel} threw during execution.",
                ex);
        }

        if (response is null)
        {
            var message = $"Fallback provider {fallbackProvider}/{fallbackModel} returned a null response";
            await TrackErrorAsync(
                functionType,
                fallbackProbe,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                message,
                fallbackUsed: true).ConfigureAwait(false);
            throw new FallbackExecutionException(message);
        }

        if (!response.WasSuccessful)
        {
            await TrackErrorAsync(
                functionType,
                fallbackProbe,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                response.ErrorMessage ?? "fallback_provider_failure",
                fallbackUsed: true).ConfigureAwait(false);

            // If fallback itself is model-not-found, try one in-place self-heal + retry before failing.
            if (_selfHealer is not null
                && !selectedModel.BlockWeakerPolicyFallback
                && !selectedModel.IsUserPinned
                && IsModelNotFoundErrorMessage(response.ErrorMessage, response.HttpStatusCode)
                && await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false))
            {
                try
                {
                    var failedFallback = CarryRoutePolicy(selectedModel, new LlmModelSelection
                    {
                        Provider = fallbackProvider,
                        Model = fallbackModel,
                        IsDefault = false,
                        Context = selectedModel.Context,
                        AuditCorrelationId = selectedModel.FallbackAuditCorrelationId
                                             ?? selectedModel.AuditCorrelationId,
                    }, promoteConfiguredFallback: true);
                    var healedRoute = await TryResolveProvenSelfHealedFallbackAsync(
                        functionType,
                        failedFallback,
                        requiresVision: false,
                        options).ConfigureAwait(false);
                    var healedModel = healedRoute?.Model;
                    if (!string.IsNullOrWhiteSpace(healedModel) &&
                        !IsSameRoute(fallbackProvider, healedModel, fallbackProvider, fallbackModel))
                    {
                        var healedProbe = CarryRoutePolicy(failedFallback, new LlmModelSelection
                        {
                            Provider = fallbackProvider,
                            Model = healedModel,
                            IsDefault = false,
                            Context = selectedModel.Context,
                            AuditCorrelationId = failedFallback.FallbackAuditCorrelationId
                                                 ?? selectedModel.AuditCorrelationId,
                            SupportsToolUse = failedFallback.SupportsToolUse,
                            SupportsStructuredOutput = failedFallback.SupportsStructuredOutput,
                            MaxContextTokens = failedFallback.MaxContextTokens,
                            CapabilityEvidenceSource = failedFallback.CapabilityEvidenceSource,
                        }, promoteConfiguredFallback: true);
                        var capabilityIssue = await GetRouteRequirementIssueAsync(
                            healedProbe,
                            requiresVision: false,
                            options,
                            verifyAvailability: false).ConfigureAwait(false);
                        if (capabilityIssue is not null)
                        {
                            _logger.LogWarning(
                                "Rejected self-healed fallback {Provider}/{Model} for {FunctionType}: {Reason}",
                                fallbackProvider,
                                healedModel,
                                functionType,
                                capabilityIssue);
                            healedModel = null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(healedModel) &&
                        !IsSameRoute(fallbackProvider, healedModel, fallbackProvider, fallbackModel))
                    {
                        _logger.LogWarning(
                            "Fallback model {Provider}/{Model} failed with model-not-found; retrying once with healed model {HealedModel}",
                            fallbackProvider,
                            fallbackModel,
                            healedModel);

                        var healedSelection = CarryRoutePolicy(failedFallback, new LlmModelSelection
                        {
                            Provider = fallbackProvider,
                            Model = healedModel,
                            IsDefault = false,
                            Reason = $"Self-healed fallback due to {reason}",
                            Context = selectedModel.Context,
                            AuditCorrelationId = failedFallback.FallbackAuditCorrelationId
                                                 ?? selectedModel.AuditCorrelationId,
                            SupportsToolUse = failedFallback.SupportsToolUse,
                            SupportsStructuredOutput = failedFallback.SupportsStructuredOutput,
                            MaxContextTokens = failedFallback.MaxContextTokens,
                            CapabilityEvidenceSource = failedFallback.CapabilityEvidenceSource,
                        }, promoteConfiguredFallback: true);
                        attemptedFallback = healedSelection;
                        var healedSettings = BuildExecutionSettings(options, healedSelection);
                        await AppendFallbackSelectionAuditAsync(
                            functionType,
                            prompt,
                            failedFallback,
                            healedSelection,
                            options,
                            "self-healed-fallback").ConfigureAwait(false);
                        await AppendExecutionStartedAuditAsync(
                            functionType,
                            healedSelection,
                            options,
                            fallbackUsed: true, physicalSettings: healedSettings).ConfigureAwait(false);
                        stopwatch.Restart();
                        try
                        {
                            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                            response = await provider.GenerateResponseAsync(prompt, healedModel, healedSettings);
                            stopwatch.Stop();
                        }
                        catch (Exception)
                            when (IsRequestCancellationRequested(options))
                        {
                            stopwatch.Stop();
                            await TrackCancellationAsync(
                                functionType,
                                healedSelection,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                fallbackUsed: true).ConfigureAwait(false);
                            throw;
                        }
                        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                        {
                            stopwatch.Stop();
                            await TrackErrorAsync(
                                functionType,
                                healedSelection,
                                prompt,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                ex.Message,
                                fallbackUsed: true).ConfigureAwait(false);
                            if (IsRateLimitError(ex))
                            {
                                await RecordCooldownIfRateLimitAsync(fallbackProvider, ex).ConfigureAwait(false);
                            }
                            else if (IsTimeoutOrTransientError(ex))
                            {
                                await RecordCooldownIfTimeoutOrTransientAsync(fallbackProvider, ex).ConfigureAwait(false);
                            }
                            throw new FallbackExecutionException(
                                $"Self-healed fallback provider {fallbackProvider}/{healedModel} threw during execution.",
                                ex);
                        }

                        if (response is null)
                        {
                            var message = $"Self-healed fallback provider {fallbackProvider}/{healedModel} returned a null response";
                            await TrackErrorAsync(
                                functionType,
                                healedSelection,
                                prompt,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                message,
                                fallbackUsed: true).ConfigureAwait(false);
                            throw new FallbackExecutionException(message);
                        }

                        if (!response.WasSuccessful)
                        {
                            await TrackErrorAsync(
                                functionType,
                                healedSelection,
                                prompt,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                response.ErrorMessage ?? "self_healed_fallback_provider_failure",
                                fallbackUsed: true).ConfigureAwait(false);
                        }

                        fallbackModel = healedModel;
                    }
                }
                catch (RoutingAuditPersistenceException)
                {
                    throw;
                }
                catch (Exception)
                    when (IsRequestCancellationRequested(options))
                {
                    throw;
                }
                catch (FallbackExecutionException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                {
                    _logger.LogDebug(ex,
                        "Fallback self-heal attempt failed for {FunctionType} on {Provider}/{Model}",
                        functionType,
                        fallbackProvider,
                        fallbackModel);
                }
            }

            if (!response.WasSuccessful)
            {
                await RecordFailedResponseCooldownAsync(
                    fallbackProvider,
                    ClassifyFailureReasonWithCategory(response).Reason,
                    response.ErrorMessage,
                    response.HttpStatusCode, response.FailureCategory);

                throw new FallbackExecutionException(
                    $"Fallback provider {fallbackProvider}/{fallbackModel} returned an unsuccessful response: {response.ErrorMessage ?? "unknown error"}");
            }
        }

        // attemptedFallback is updated to the proof-carrying healed selection when
        // an in-place repair executes. Use that exact object for quality, debug,
        // usage, and execution audit rather than rebuilding the original route.
        var fallbackSelection = attemptedFallback!;

        // Quality gate on fallback response — if fallback also produces garbage, fail hard (no recursive fallback)
        var fallbackQualityIssue = ValidateResponseQuality(response, options, out var normalizedFallbackContent);
        if (fallbackQualityIssue != null)
        {
            await TrackQualityFailureAsync(
                functionType,
                fallbackSelection,
                prompt,
                response,
                stopwatch.ElapsedMilliseconds,
                options,
                fallbackQualityIssue,
                fallbackUsed: true).ConfigureAwait(false);
            if (IsTerminalCallerContractFailure(fallbackQualityIssue, options))
            {
                throw new LlmResponseContractException(
                    functionType,
                    fallbackSelection.Provider,
                    fallbackSelection.Model);
            }
            var repaired = await TryRetryJsonContractAsync(
                functionType,
                prompt,
                fallbackSelection,
                options,
                fallbackRequestId,
                fallbackQualityIssue,
                response,
                isFallback: true);
            if (repaired is not null)
            {
                return repaired;
            }

            throw new FallbackExecutionException(
                $"Fallback provider {fallbackProvider}/{fallbackModel} failed quality gate: {fallbackQualityIssue} (content length: {response.Content?.Length ?? 0})");
        }
        if (normalizedFallbackContent is not null)
        {
            response.Content = normalizedFallbackContent;
        }

        // Debug log the fallback response
        _debugLogger.LogResponse(new LlmDebugResponse(
            RequestId: fallbackRequestId,
            FunctionType: functionType,
            Provider: fallbackProvider,
            Model: fallbackModel,
            Response: response.Content,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            IsFallback: true));

        await TrackUsageAsync(functionType, fallbackSelection, prompt, response, stopwatch.ElapsedMilliseconds, options, fallbackUsed: true);

        return response.Content;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (FallbackDisabledException)
        {
            throw;
        }
        catch (LlmFailoverException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (Exception)
            when (IsRequestCancellationRequested(options))
        {
            throw;
        }
        catch (FallbackExecutionException ex)
        {
            var failover = CreateFailoverException(
                functionType,
                requestId,
                selectedModel,
                attemptedFallback,
                reason,
                ex,
                primaryCategory);
            if (IsLaunchDeclinedRouteExhaustion(reason, ex))
            {
                throw new LlmRouteTemporarilyUnavailableException(
                    $"Route {selectedModel.Provider}/{selectedModel.Model} is temporarily unavailable for {functionType}: the subscription CLI launch was declined by the cross-process co-run guard. The call can be retried shortly.",
                    failover);
            }

            if (IsAutomaticTransientRouteExhaustion(
                    selectedModel,
                    attemptedFallback,
                    options,
                    reason,
                    ex,
                    primaryCategory,
                    failover.FallbackCategory))
            {
                throw new LlmRouteTemporarilyUnavailableException(
                    $"Automatic route {selectedModel.Provider}/{selectedModel.Model} is temporarily exhausted for {functionType}; provider cooldown and Model Scout recovery are searching for the next admitted route.",
                    failover);
            }

            throw failover;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            var wrapped = new FallbackExecutionException(
                $"Fallback setup or execution failed for {functionType} after {reason}.", ex);
            var failover = CreateFailoverException(
                functionType,
                requestId,
                selectedModel,
                attemptedFallback,
                reason,
                wrapped,
                primaryCategory);
            if (IsLaunchDeclinedRouteExhaustion(reason, wrapped))
            {
                throw new LlmRouteTemporarilyUnavailableException(
                    $"Route {selectedModel.Provider}/{selectedModel.Model} is temporarily unavailable for {functionType}: the subscription CLI launch was declined by the cross-process co-run guard. The call can be retried shortly.",
                    failover);
            }

            if (IsAutomaticTransientRouteExhaustion(
                    selectedModel,
                    attemptedFallback,
                    options,
                    reason,
                    wrapped,
                    primaryCategory,
                    failover.FallbackCategory))
            {
                throw new LlmRouteTemporarilyUnavailableException(
                    $"Automatic route {selectedModel.Provider}/{selectedModel.Model} is temporarily exhausted for {functionType}; provider cooldown and Model Scout recovery are searching for the next admitted route.",
                    failover);
            }

            throw failover;
        }
    }

    /// <summary>
    /// A turn where the launch decline explains the exhaustion: the primary
    /// failed as launch_declined, or the attempted fallback's own failure
    /// classifies as <see cref="LlmFailureCategory.LaunchDeclined"/>. A co-run
    /// decline is self-inflicted and clears in seconds, it never enters the
    /// cooldown tracker, so the cooldown-backed transient check above cannot
    /// see it — without this arm an all-declined turn threw a permanent-shaped
    /// <see cref="LlmFailoverException"/> and consumers (agent engine route
    /// recovery, spam classification) stopped retrying.
    /// </summary>
    private static bool IsLaunchDeclinedRouteExhaustion(string reason, Exception fallbackFailure)
    {
        if (string.Equals(reason, "launch_declined", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var current = fallbackFailure; current is not null; current = current.InnerException)
        {
            if (Services.LlmErrorPolicy.Classify(current.Message)
                == LlmFailureCategory.LaunchDeclined)
            {
                return true;
            }
        }

        return false;
    }

    // fallbackCategory is the classification CreateFailoverException already
    // resolved from the attempted fallback's exception chain — the same value
    // the surfaced LlmFailoverException.FallbackCategory carries — so the
    // gate's transiency decision can never disagree with the category the
    // exception's consumers see.
    private bool IsAutomaticTransientRouteExhaustion(
        LlmModelSelection selectedModel,
        LlmModelSelection? attemptedFallback,
        LlmRouterOptions? options,
        string reason,
        Exception fallbackFailure,
        LlmFailureCategory? primaryCategory,
        LlmFailureCategory? fallbackCategory)
    {
        if (selectedModel.IsUserPinned
            || options?.DisableFallback == true
            || options?.OverrideProvenance is LlmOverrideProvenance.User or LlmOverrideProvenance.Profile
            || HasExactValidatedAdmission(selectedModel, options))
        {
            return false;
        }

        // Typed-first transiency contract. When the caller resolved a failure
        // category at classification time, that category decides whether
        // waiting for route recovery can help; the collapsed legacy reason
        // token is consulted only when no category travelled (older lanes).
        //
        //  - Transient (waiting/recovery can help): Timeout, RateLimit, and
        //    QuotaExhausted. The legacy "rate_limit" token always covered
        //    quota exhaustion too, and quota windows do expire, so
        //    QuotaExhausted keeps that parity. (LaunchDeclined has its own
        //    dedicated arm — IsLaunchDeclinedRouteExhaustion, checked before
        //    this method at every call site — and is not re-decided here.)
        //  - NOT transient (waiting cannot fix it): AuthInvalid, ContentPolicy,
        //    InvalidRequest, ModelNotFound, MalformedReply, ProviderError —
        //    these fall through to normal failover surfacing. The deliberate
        //    behavior change versus the token-only gate is exactly the
        //    mislabeled cases (e.g. a thrown 401 riding the "timeout" token
        //    because it travelled as HttpRequestException): an agent must not
        //    park on route recovery for a failure recovery cannot fix.
        //    Genuine timeouts / rate limits / quota keep identical outcomes.
        if (primaryCategory is { } category)
        {
            if (category is not (LlmFailureCategory.Timeout
                or LlmFailureCategory.RateLimit
                or LlmFailureCategory.QuotaExhausted))
            {
                return false;
            }
        }
        else if (reason is not ("rate_limit" or "timeout"))
        {
            return false;
        }

        // Retryable exhaustion is safe only after the last physical provider is
        // actually unavailable to subsequent selection. Cooldown persistence is
        // best-effort at the storage layer; if it failed (or no tracker exists),
        // do not expose a retry signal that could repeat an uncertain paid call.
        var exhaustedProvider = attemptedFallback?.Provider ?? selectedModel.Provider;
        if (_cooldownTracker is null || _cooldownTracker.IsAvailable(exhaustedProvider))
        {
            return false;
        }

        // No admitted fallback after an automatic provider enters cooldown is
        // inherently transient: Scout/provider discovery may expose one shortly.
        if (attemptedFallback is null)
        {
            return true;
        }

        // When a physical fallback was attempted, retry only provider-level
        // transient exhaustion — decided by the SAME typed contract as the
        // primary half above: Timeout/RateLimit/QuotaExhausted are transient;
        // AuthInvalid/ContentPolicy/InvalidRequest/ModelNotFound/
        // MalformedReply surface as normal failover. The deliberate behavior
        // change versus the predicate walk is again the mislabeled cases: a
        // fallback 401 travels as HttpRequestException, matched
        // IsTimeoutOrTransientError by TYPE, and parked agents on route
        // recovery that can never revive a dead credential — even though the
        // fallback's own cooldown row for the same failure was already
        // correctly typed AuthInvalid.
        //
        // ProviderError on the fallback side is deliberately NOT transient:
        // the network-shaped failures the old predicates meant to retry
        // (connection resets/refusals, DNS, "timed out", 502/503/529)
        // classify as Timeout already, so what remains in ProviderError is
        // authored provider failures — null replies, quality-gate text,
        // unknown provider errors — which waiting for route recovery cannot
        // fix. Content/JSON quality failures thus stay permanent, so a
        // malformed response cannot create a paid retry loop.
        if (fallbackCategory is { } typedFallbackCategory
            && HasClassifiableFallbackDiagnostic(fallbackFailure))
        {
            return typedFallbackCategory is LlmFailureCategory.Timeout
                or LlmFailureCategory.RateLimit
                or LlmFailureCategory.QuotaExhausted;
        }

        // No threaded category, or no usable diagnostic anywhere in the
        // fallback chain (classification would only see a blind ProviderError
        // placeholder): only here do the legacy type/string-shape predicates
        // still decide, so a bare typed TimeoutException with an empty
        // message keeps reading as transient.
        for (var current = fallbackFailure; current is not null; current = current.InnerException)
        {
            if (IsRateLimitError(current) || IsTimeoutOrTransientError(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether any exception in the attempted-fallback failure chain carries a
    /// non-empty message for <see cref="Services.LlmErrorPolicy.Classify"/> to
    /// read. When the whole chain is message-less the classified category is a
    /// placeholder ProviderError that saw nothing, so the typed transiency
    /// contract must not decide on it.
    /// </summary>
    private static bool HasClassifiableFallbackDiagnostic(Exception fallbackFailure)
    {
        for (var current = fallbackFailure; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    // primaryCategory: see HandleFallbackAsync — the classification-time
    // category riding alongside the wire-compatible legacy reason.
    private async Task<string> HandleVisionFallbackAsync(string functionType, string prompt, string imageBase64,
        LlmModelSelection selectedModel, Stopwatch stopwatch, LlmRouterOptions? options, string reason, string requestId,
        LlmFailureCategory? primaryCategory = null)
    {
        LlmModelSelection? attemptedFallback = null;
        try
        {
        var survivalExecution = await TryExecuteVisionSurvivalSetAsync(
            functionType,
            prompt,
            imageBase64,
            selectedModel,
            options,
            reason,
            requestId,
            primaryCategory).ConfigureAwait(false);
        if (survivalExecution is not null)
        {
            return survivalExecution.Content;
        }

        if (options?.DisableFallback == true)
        {
            var message = $"Vision fallback disabled for {selectedModel.Provider}/{selectedModel.Model} on {functionType}; selected route failed after same-route recovery (reason={reason}).";
            await ReportOperationalFailureAsync(functionType, selectedModel, requiresVision: true, reason, message);
            throw new FallbackDisabledException(message);
        }

        // Trigger self-healing only when route substitution is permitted.
        if (reason == "model_not_found"
            && _selfHealer != null
            && !selectedModel.BlockWeakerPolicyFallback
            && !HasExactValidatedAdmission(selectedModel, options))
        {
            _ = TriggerSelfHealingAsync(functionType, selectedModel.Provider, selectedModel.Model, requiresVision: true);
        }

        var (fallbackProvider, fallbackModel) = await ResolveFallbackRouteAsync(
            functionType,
            selectedModel,
            prompt,
            requiresVision: true,
            reason,
            options: options);
        attemptedFallback = CarryRoutePolicy(selectedModel, new LlmModelSelection
        {
            Provider = fallbackProvider,
            Model = fallbackModel,
            IsDefault = false,
            Reason = $"Vision fallback due to {reason}",
            Context = selectedModel.Context,
            AuditCorrelationId = selectedModel.FallbackAuditCorrelationId ?? selectedModel.AuditCorrelationId,
        }, promoteConfiguredFallback: true);

        _logger.LogWarning("Using vision fallback {FallbackProvider}/{FallbackModel} due to {Reason}",
            fallbackProvider, fallbackModel, reason);

        var provider = _providerRegistry.GetProvider(fallbackProvider);
        if (provider == null)
        {
            throw new InvalidOperationException($"Vision fallback provider {fallbackProvider} not registered");
        }

        if (!provider.SupportsVision(fallbackModel))
        {
            throw new LlmNoUsableRouteException($"Vision fallback model {fallbackModel} does not support vision");
        }

        await AppendFallbackSelectionAuditAsync(
            functionType,
            prompt,
            selectedModel,
            attemptedFallback,
            options,
            reason,
            requiresVision: true).ConfigureAwait(false);

        // Debug log the fallback request
        var fallbackRequestId = $"{requestId}-vfb";
        var imageSummary = !string.IsNullOrEmpty(imageBase64)
            ? $"Base64 image ({imageBase64.Length} chars)"
            : null;
        _debugLogger.LogRequest(new LlmDebugRequest(
            RequestId: fallbackRequestId,
            FunctionType: functionType,
            Provider: fallbackProvider,
            Model: fallbackModel,
            Prompt: prompt,
            ImageBase64Summary: imageSummary,
            Temperature: options?.Temperature,
            MaxTokens: options?.MaxTokens,
            IsFallback: true,
            FallbackReason: reason));

        var visionFallbackSelection = attemptedFallback;
        var executionSettings = BuildExecutionSettings(options, visionFallbackSelection);
        using var fallbackSlot = await AcquireLocalSlotAsync(
            fallbackProvider,
            fallbackModel,
            selectedModel.Context,
            executionSettings,
            LocalLlmWorkloadKind.Vision);
        await AppendExecutionStartedAuditAsync(
            functionType,
            attemptedFallback,
            options,
            fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
        LlmResponse? response;
        stopwatch.Restart();
        try
        {
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            response = await provider.GenerateVisionResponseAsync(prompt, imageBase64, fallbackModel, executionSettings);
            stopwatch.Stop();
        }
        catch (Exception)
            when (IsRequestCancellationRequested(options))
        {
            // The caller cancelled: close the receipt as a cancellation, never as
            // provider failure evidence or a cooldown (same rule as the text path).
            stopwatch.Stop();
            await TrackCancellationAsync(
                functionType,
                visionFallbackSelection,
                stopwatch.ElapsedMilliseconds,
                options,
                fallbackUsed: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            stopwatch.Stop();
            await TrackErrorAsync(
                functionType,
                visionFallbackSelection,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                ex.Message,
                fallbackUsed: true).ConfigureAwait(false);
            if (IsRateLimitError(ex))
            {
                await RecordCooldownIfRateLimitAsync(fallbackProvider, ex).ConfigureAwait(false);
            }
            else if (IsTimeoutOrTransientError(ex))
            {
                await RecordCooldownIfTimeoutOrTransientAsync(fallbackProvider, ex).ConfigureAwait(false);
            }
            throw new FallbackExecutionException(
                $"Vision fallback provider {fallbackProvider}/{fallbackModel} threw during execution.",
                ex);
        }

        if (response is null)
        {
            var message = $"Vision fallback provider {fallbackProvider}/{fallbackModel} returned a null response";
            await TrackErrorAsync(
                functionType,
                visionFallbackSelection,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                message,
                fallbackUsed: true).ConfigureAwait(false);
            throw new FallbackExecutionException(message);
        }

        if (!response.WasSuccessful)
        {
            await TrackErrorAsync(
                functionType,
                visionFallbackSelection,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                response.ErrorMessage ?? "vision_fallback_provider_failure",
                fallbackUsed: true).ConfigureAwait(false);

            // If fallback itself is model-not-found, try one in-place self-heal + retry before failing.
            if (_selfHealer is not null
                && !selectedModel.BlockWeakerPolicyFallback
                && !selectedModel.IsUserPinned
                && IsModelNotFoundErrorMessage(response.ErrorMessage, response.HttpStatusCode)
                && await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false))
            {
                try
                {
                    var failedFallback = CarryRoutePolicy(selectedModel, new LlmModelSelection
                    {
                        Provider = fallbackProvider,
                        Model = fallbackModel,
                        IsDefault = false,
                        Context = selectedModel.Context,
                        AuditCorrelationId = selectedModel.FallbackAuditCorrelationId
                                             ?? selectedModel.AuditCorrelationId,
                    }, promoteConfiguredFallback: true);
                    var healedRoute = await TryResolveProvenSelfHealedFallbackAsync(
                        functionType,
                        failedFallback,
                        requiresVision: true,
                        options).ConfigureAwait(false);
                    var healedModel = healedRoute?.Model;
                    if (!string.IsNullOrWhiteSpace(healedModel) &&
                        !IsSameRoute(fallbackProvider, healedModel, fallbackProvider, fallbackModel) &&
                        provider.SupportsVision(healedModel))
                    {
                        var healedProbe = CarryRoutePolicy(failedFallback, new LlmModelSelection
                        {
                            Provider = fallbackProvider,
                            Model = healedModel,
                            IsDefault = false,
                            Context = selectedModel.Context,
                            AuditCorrelationId = failedFallback.FallbackAuditCorrelationId
                                                 ?? selectedModel.AuditCorrelationId,
                            SupportsToolUse = failedFallback.SupportsToolUse,
                            SupportsStructuredOutput = failedFallback.SupportsStructuredOutput,
                            MaxContextTokens = failedFallback.MaxContextTokens,
                            CapabilityEvidenceSource = failedFallback.CapabilityEvidenceSource,
                        }, promoteConfiguredFallback: true);
                        var capabilityIssue = await GetRouteRequirementIssueAsync(
                            healedProbe,
                            requiresVision: true,
                            options,
                            verifyAvailability: false).ConfigureAwait(false);
                        if (capabilityIssue is not null)
                        {
                            _logger.LogWarning(
                                "Rejected self-healed vision fallback {Provider}/{Model} for {FunctionType}: {Reason}",
                                fallbackProvider,
                                healedModel,
                                functionType,
                                capabilityIssue);
                            healedModel = null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(healedModel) &&
                        !IsSameRoute(fallbackProvider, healedModel, fallbackProvider, fallbackModel) &&
                        provider.SupportsVision(healedModel))
                    {
                        _logger.LogWarning(
                            "Vision fallback model {Provider}/{Model} failed with model-not-found; retrying once with healed model {HealedModel}",
                            fallbackProvider,
                            fallbackModel,
                            healedModel);

                        var healedSelection = CarryRoutePolicy(failedFallback, new LlmModelSelection
                        {
                            Provider = fallbackProvider,
                            Model = healedModel,
                            IsDefault = false,
                            Reason = $"Self-healed vision fallback due to {reason}",
                            Context = selectedModel.Context,
                            AuditCorrelationId = failedFallback.FallbackAuditCorrelationId
                                                 ?? selectedModel.AuditCorrelationId,
                            SupportsToolUse = failedFallback.SupportsToolUse,
                            SupportsStructuredOutput = failedFallback.SupportsStructuredOutput,
                            MaxContextTokens = failedFallback.MaxContextTokens,
                            CapabilityEvidenceSource = failedFallback.CapabilityEvidenceSource,
                        }, promoteConfiguredFallback: true);
                        attemptedFallback = healedSelection;
                        var healedSettings = BuildExecutionSettings(options, healedSelection);
                        await AppendFallbackSelectionAuditAsync(
                            functionType,
                            prompt,
                            failedFallback,
                            healedSelection,
                            options,
                            "self-healed-vision-fallback",
                            requiresVision: true).ConfigureAwait(false);
                        await AppendExecutionStartedAuditAsync(
                            functionType,
                            healedSelection,
                            options,
                            fallbackUsed: true, physicalSettings: healedSettings).ConfigureAwait(false);
                        stopwatch.Restart();
                        try
                        {
                            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                            response = await provider.GenerateVisionResponseAsync(prompt, imageBase64, healedModel, healedSettings);
                            stopwatch.Stop();
                        }
                        catch (Exception)
                            when (IsRequestCancellationRequested(options))
                        {
                            stopwatch.Stop();
                            await TrackCancellationAsync(
                                functionType,
                                healedSelection,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                fallbackUsed: true).ConfigureAwait(false);
                            throw;
                        }
                        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                        {
                            stopwatch.Stop();
                            await TrackErrorAsync(
                                functionType,
                                healedSelection,
                                prompt,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                ex.Message,
                                fallbackUsed: true).ConfigureAwait(false);
                            throw new FallbackExecutionException(
                                $"Self-healed vision fallback provider {fallbackProvider}/{healedModel} threw during execution.",
                                ex);
                        }

                        if (response is null)
                        {
                            var message = $"Self-healed vision fallback provider {fallbackProvider}/{healedModel} returned a null response";
                            await TrackErrorAsync(
                                functionType,
                                healedSelection,
                                prompt,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                message,
                                fallbackUsed: true).ConfigureAwait(false);
                            throw new FallbackExecutionException(message);
                        }

                        if (!response.WasSuccessful)
                        {
                            await TrackErrorAsync(
                                functionType,
                                healedSelection,
                                prompt,
                                stopwatch.ElapsedMilliseconds,
                                options,
                                response.ErrorMessage ?? "self_healed_vision_fallback_provider_failure",
                                fallbackUsed: true).ConfigureAwait(false);
                        }

                        fallbackModel = healedModel;
                    }
                }
                catch (RoutingAuditPersistenceException)
                {
                    throw;
                }
                catch (FallbackExecutionException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                {
                    _logger.LogDebug(ex,
                        "Vision fallback self-heal attempt failed for {FunctionType} on {Provider}/{Model}",
                        functionType,
                        fallbackProvider,
                        fallbackModel);
                }
            }

            if (!response.WasSuccessful)
            {
                await RecordFailedResponseCooldownAsync(
                    fallbackProvider,
                    ClassifyFailureReasonWithCategory(response).Reason,
                    response.ErrorMessage,
                    response.HttpStatusCode, response.FailureCategory);

                throw new FallbackExecutionException(
                    $"Vision fallback provider {fallbackProvider}/{fallbackModel} returned an unsuccessful response: {response.ErrorMessage ?? "unknown error"}");
            }
        }

        // Quality gate on fallback response — if fallback also produces garbage, fail hard (no recursive fallback)
        visionFallbackSelection = attemptedFallback!;
        var fallbackQualityIssue = ValidateResponseQuality(response, options, out var normalizedFallbackContent);
        if (fallbackQualityIssue != null)
        {
            await TrackQualityFailureAsync(
                functionType,
                visionFallbackSelection,
                prompt,
                response,
                stopwatch.ElapsedMilliseconds,
                options,
                fallbackQualityIssue,
                fallbackUsed: true,
                requiresVision: true).ConfigureAwait(false);
            if (IsTerminalCallerContractFailure(fallbackQualityIssue, options))
            {
                throw new LlmResponseContractException(
                    functionType,
                    visionFallbackSelection.Provider,
                    visionFallbackSelection.Model);
            }
            var repaired = await TryRetryVisionThinkingChannelAsync(
                functionType,
                prompt,
                imageBase64,
                visionFallbackSelection,
                options,
                fallbackRequestId,
                fallbackQualityIssue,
                response,
                isFallback: true);
            if (repaired is not null)
            {
                return repaired;
            }

            throw new FallbackExecutionException(
                $"Vision fallback provider {fallbackProvider}/{fallbackModel} failed quality gate: {fallbackQualityIssue} (content length: {response.Content?.Length ?? 0})");
        }
        if (normalizedFallbackContent is not null)
        {
            response.Content = normalizedFallbackContent;
        }

        // Debug log the fallback response
        _debugLogger.LogResponse(new LlmDebugResponse(
            RequestId: fallbackRequestId,
            FunctionType: functionType,
            Provider: fallbackProvider,
            Model: fallbackModel,
            Response: response.Content,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            IsFallback: true));

        await TrackUsageAsync(
            functionType,
            visionFallbackSelection,
            prompt,
            response,
            stopwatch.ElapsedMilliseconds,
            options,
            fallbackUsed: true,
            requiresVision: true);

        return response.Content;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (FallbackDisabledException)
        {
            throw;
        }
        catch (LlmFailoverException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (Exception)
            when (IsRequestCancellationRequested(options))
        {
            // A cancelled request surfaces as cancellation, never as a failover a
            // caller might retry.
            throw;
        }
        catch (FallbackExecutionException ex)
        {
            throw CreateFailoverException(
                functionType,
                requestId,
                selectedModel,
                attemptedFallback,
                reason,
                ex,
                primaryCategory);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            var wrapped = new FallbackExecutionException(
                $"Vision fallback setup or execution failed for {functionType} after {reason}.", ex);
            throw CreateFailoverException(
                functionType,
                requestId,
                selectedModel,
                attemptedFallback,
                reason,
                wrapped,
                primaryCategory);
        }
    }

    /// <summary>
    /// Warm proof-carrying self-heal discovery after a failed route. This helper
    /// never mutates a preference or substitutes a route: execution paths must
    /// validate and carry the returned proof synchronously before using it.
    /// </summary>
    private async Task TriggerSelfHealingAsync(string functionType, string provider, string failedModel, bool requiresVision)
    {
        if (_selfHealer == null) return;

        try
        {
            _logger.LogWarning("🔧 Proof-carrying self-heal discovery triggered: {FunctionType} - {Provider}/{FailedModel} not found",
                functionType, provider, failedModel);

            var replacement = await _selfHealer.GetFunctionScopedReplacementAsync(
                functionType,
                provider,
                failedModel,
                requiresVision).ConfigureAwait(false);
            var providerImpl = _providerRegistry.GetProvider(provider);
            var accepted = replacement is not null
                           && replacement.HasCurrentFunctionEvidence
                           && !string.IsNullOrWhiteSpace(replacement.AuditCorrelationId)
                           && !string.IsNullOrWhiteSpace(replacement.EvidenceSource)
                           && string.Equals(
                               replacement.EvidenceFunctionType,
                               functionType,
                               StringComparison.OrdinalIgnoreCase)
                           && providerImpl?.SupportsModel(replacement.Model) == true
                           && (!requiresVision || providerImpl.SupportsVision(replacement.Model));

            if (accepted)
            {
                _logger.LogInformation(
                    "Proof-carrying replacement is available for {FunctionType}: {Provider}/{OldModel} -> {NewModel} (receipt {CorrelationId}); no route was mutated by background discovery",
                    functionType,
                    provider,
                    failedModel,
                    replacement!.Model,
                    replacement.AuditCorrelationId);
            }
            else
            {
                _logger.LogWarning("⚠️ Self-heal discovery could not prove a replacement for {Provider}/{FailedModel}",
                    provider, failedModel);
            }
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(ex, "❌ Self-healing failed for {FunctionType}: {Provider}/{FailedModel}",
                functionType, provider, failedModel);
        }
    }

    private async Task TrackUsageAsync(string functionType, LlmModelSelection model, string prompt,
        LlmResponse response, long responseTimeMs, LlmRouterOptions? options,
        bool fallbackUsed = false, bool requiresVision = false,
        bool? successfulOverride = null, string? errorMessageOverride = null,
        bool countSubscriptionCapacity = false)
    {
        try
        {
            var succeeded = successfulOverride ?? response.WasSuccessful;
            RecordTypedJsonPhysicalCall(succeeded, model);
            // Prefer the cache-aware cost calc when the provider surfaced
            // cache-hit tokens (Anthropic does; OpenAI's automatic cache
            // is reflected via cache_read_input_tokens on their newer
            // chat-completions payloads). Fall back to token-estimate cost
            // only when we have nothing better.
            var estimatedInputTokens = EstimateTokenCount(prompt);
            var estimatedOutputTokens = EstimateTokenCount(response.Content);
            var accountedInputTokens = response.InputTokens > 0
                ? response.InputTokens
                : Math.Max(
                    estimatedInputTokens,
                    response.CacheReadInputTokens + response.CacheCreationInputTokens);
            var accountedOutputTokens = response.OutputTokens > 0
                ? response.OutputTokens
                : estimatedOutputTokens;

            decimal cost;
            if (response.EstimatedCost > 0)
            {
                cost = response.EstimatedCost;
            }
            else if (response.CacheReadInputTokens > 0 || response.CacheCreationInputTokens > 0)
            {
                var freshIn = Math.Max(0, accountedInputTokens
                    - response.CacheReadInputTokens - response.CacheCreationInputTokens);
                cost = _costService.CalculateCost(
                    model.Model,
                    freshIn,
                    response.CacheReadInputTokens,
                    response.CacheCreationInputTokens,
                    accountedOutputTokens);
            }
            else
            {
                // Provider token counts include hidden reasoning and other billed
                // tokens that are absent from visible response content. Prefer each
                // reported side independently and estimate only the missing side.
                cost = _costService.CalculateCost(
                    model.Model,
                    accountedInputTokens,
                    accountedOutputTokens);
            }

            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = model.Provider,
                Model = model.Model,
                StrategyId = options?.StrategyId,
                InputTokens = accountedInputTokens,
                OutputTokens = accountedOutputTokens,
                TotalTokens = response.TotalTokens > 0
                    ? response.TotalTokens
                    : accountedInputTokens + accountedOutputTokens,
                EstimatedCost = cost,
                ActualCost = response.ProviderReportedCostUsd,
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                PrefillDurationMs = response.PrefillDurationMs,
                DecodeDurationMs = response.DecodeDurationMs,
                ModelLoadDurationMs = response.ModelLoadDurationMs,
                DeclaredCachePrefixChars = response.DeclaredCachePrefixChars,
                UserId = options?.UserId,
                SessionId = options?.SessionId,
                PromptHash = ComputeHash(prompt),
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = succeeded,
                ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(errorMessageOverride ?? response.ErrorMessage, 2_000),
                FallbackUsed = fallbackUsed,
                InteractivityContext = model.Context?.InteractivityKey,
                PromptText = prompt,
                ResponseText = succeeded ? response.Content : null,
            };

            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed,
                succeeded,
                responseTimeMs,
                record.TotalTokens,
                record.EstimatedCost,
                record.ErrorMessage).ConfigureAwait(false);

            // A selection becomes the durable startup route only after the physical
            // provider has returned a successful, quality-gated completion and its
            // execution receipt is committed. Failed primaries therefore cannot poison
            // a fresh profile, while a successful runtime fallback can become the next
            // launch's immediately usable route.
            if (succeeded)
            {
                await MaterializeFunctionPreferenceIfMissingAsync(
                    functionType,
                    model,
                    options,
                    requiresVision).ConfigureAwait(false);
            }

            // Soft-quota tracker — applies provider-reported remaining usage hints first,
            // then records the successful call so advisors can rotate before a hard limit fires.
            if (_quotaTracker is not null
                && Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(model.Provider))
            {
                ApplySubscriptionQuotaDiagnostics(model.Provider, response);
                if (succeeded || countSubscriptionCapacity)
                {
                    _quotaTracker.RecordCall(
                        model.Provider,
                        capacityUnits: ProviderPackCatalog.ResolveSubscriptionCapacityWeight(
                            model.Provider,
                            model.Model));
                }
            }

            if (succeeded)
            {
                _knownGoodTracker?.RecordSuccess(functionType, model.Provider, model.Model);
            }
            else if (successfulOverride == false && _knownGoodTracker is not null)
            {
                ObserveKnownGoodRevert(functionType, model.Provider, model.Model);
            }

            if (options?.UsageCompletionObserver is not null || HasMissionPhysicalBudget)
            {
                var durablyRecorded = await PersistUsageForCompletionObserverAsync(
                    record,
                    functionType,
                    model.Provider,
                    model.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(
                    options,
                    record,
                    durablyRecorded,
                    response.UsageMeasurementComplete).ConfigureAwait(false);
            }
            else
            {
                _ = PersistUsageTelemetryAsync(record, functionType, model.Provider, model.Model);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track LLM usage for {FunctionType}", functionType);
        }
    }

    private Task TrackQualityFailureAsync(
        string functionType,
        LlmModelSelection model,
        string prompt,
        LlmResponse response,
        long responseTimeMs,
        LlmRouterOptions? options,
        string qualityFailureReason,
        bool fallbackUsed = false,
        bool requiresVision = false)
        => TrackUsageAsync(
            functionType,
            model,
            prompt,
            response,
            responseTimeMs,
            options,
            fallbackUsed,
            requiresVision,
            successfulOverride: false,
            errorMessageOverride: $"quality_gate:{qualityFailureReason}",
            countSubscriptionCapacity: true);

    private void ApplySubscriptionQuotaDiagnostics(string providerName, LlmResponse response)
    {
        foreach (var diagnostic in response.Diagnostics)
        {
            if (!diagnostic.StartsWith(SubscriptionUsageRemainingDiagnosticPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = diagnostic[SubscriptionUsageRemainingDiagnosticPrefix.Length..];
            if (!decimal.TryParse(
                    value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var remainingFraction))
            {
                continue;
            }

            _quotaTracker?.ApplyRemainingFractionHint(providerName, remainingFraction);
            _logger.LogInformation(
                "Applied subscription quota hint for {Provider}: {Remaining:P0} remaining",
                providerName,
                Math.Clamp(remainingFraction, 0m, 1m));
        }
    }

    private async Task TrackErrorAsync(string functionType, LlmModelSelection model, string prompt,
        long responseTimeMs, LlmRouterOptions? options, string errorMessage, bool fallbackUsed = false)
    {
        try
        {
            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = model.Provider,
                Model = model.Model,
                StrategyId = options?.StrategyId,
                InputTokens = EstimateTokenCount(prompt),
                OutputTokens = 0,
                TotalTokens = EstimateTokenCount(prompt),
                EstimatedCost = 0,
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                UserId = options?.UserId,
                SessionId = options?.SessionId,
                PromptHash = ComputeHash(prompt),
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = false,
                // Provider error bodies can echo credentials, bearer tokens,
                // and addresses; redact before the row becomes durable usage.
                ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(errorMessage, 2_000),
                FallbackUsed = fallbackUsed,
                InteractivityContext = model.Context?.InteractivityKey,
                PromptText = prompt
            };
            RecordTypedJsonPhysicalCall(succeeded: false, model);

            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed,
                succeeded: false,
                responseTimeMs,
                record.TotalTokens,
                record.EstimatedCost,
                errorMessage).ConfigureAwait(false);

            if (options?.UsageCompletionObserver is not null || HasMissionPhysicalBudget)
            {
                var durablyRecorded = await PersistUsageForCompletionObserverAsync(
                    record,
                    functionType,
                    model.Provider,
                    model.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(
                    options,
                    record,
                    durablyRecorded,
                    measurementComplete: false).ConfigureAwait(false);
            }
            else
            {
                _ = PersistUsageTelemetryAsync(record, functionType, model.Provider, model.Model);
            }

            if (_knownGoodTracker != null)
            {
                ObserveKnownGoodRevert(functionType, model.Provider, model.Model);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track LLM error for {FunctionType}", functionType);
        }
    }

    /// <summary>
    /// Closes a pending execution receipt after caller cancellation without
    /// creating cooldown, fallback, usage, or negative known-good evidence.
    /// Cancellation after send is an auditable scheduling outcome, not proof
    /// that the route or model is bad.
    /// </summary>
    private Task TrackCancellationAsync(
        string functionType,
        LlmModelSelection model,
        long responseTimeMs,
        LlmRouterOptions? options,
        bool fallbackUsed = false)
        => AppendExecutionAuditAsync(
            functionType,
            model,
            options,
            fallbackUsed,
            succeeded: false,
            responseTimeMs,
            totalTokens: 0,
            estimatedCost: 0m,
            error: "request_cancelled");

    /// <summary>
    /// Starts the known-good revert check without making the caller wait for its
    /// proof call, and logs a failure instead of leaving it unobserved.
    /// </summary>
    private void ObserveKnownGoodRevert(string functionType, string provider, string model)
    {
        Task revert;
        try
        {
            revert = _knownGoodTracker!.RecordFailureAndCheckRevertAsync(functionType, provider, model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Known-good revert check failed for {FunctionType} on {Provider}/{Model}",
                functionType, provider, model);
            return;
        }

        _ = revert.ContinueWith(
            completed => _logger.LogWarning(completed.Exception?.GetBaseException(),
                "Known-good revert check failed for {FunctionType} on {Provider}/{Model}",
                functionType, provider, model),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsRequestCancellationRequested(LlmRouterOptions? options)
    {
        if (options?.RequestCancellationToken.CanBeCanceled == true)
        {
            return options.RequestCancellationToken.IsCancellationRequested;
        }

        return options?.RequestControl?.CancellationToken.IsCancellationRequested == true;
    }

    private async Task NotifyUsageCompletionObserverAsync(
        LlmRouterOptions? options,
        LlmUsageRecord record,
        bool wasDurablyRecorded,
        bool measurementComplete)
    {
        await SettlePhysicalAttemptAsync(record, wasDurablyRecorded, measurementComplete).ConfigureAwait(false);
        var observer = options?.UsageCompletionObserver;
        if (observer is null)
        {
            return;
        }

        try
        {
            await observer(new LlmUsageCompletion(
                record.FunctionType,
                record.Provider,
                record.Model,
                record.InputTokens,
                record.OutputTokens,
                record.EstimatedCost,
                record.WasSuccessful,
                wasDurablyRecorded,
                measurementComplete) { UsageId = record.UsageId, PhysicalCallId = record.PhysicalCallId, RoutingAuditCorrelationId = record.RoutingAuditCorrelationId,
                    ResponseTimeMs = record.ResponseTimeMs }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            // The provider call has already happened. A budget observer fails
            // closed by retaining its durable reservation, while ordinary
            // telemetry and execution audit are still allowed to proceed.
            _logger.LogWarning(
                ex,
                "Usage completion observer failed for {FunctionType} on {Provider}/{Model}",
                record.FunctionType,
                record.Provider,
                record.Model);
        }
    }

    private async Task<bool> PersistUsageForCompletionObserverAsync(
        LlmUsageRecord record,
        string functionType,
        string provider,
        string model)
    {
        try
        {
            if (_usageTracker is not IDurableLlmUsageTracker durableTracker)
            {
                // Preserve telemetry for non-durable host implementations, but do
                // not authorize reservation release without the stronger contract.
                await _usageTracker.TrackUsageAsync(record).ConfigureAwait(false);
                return false;
            }

            await durableTracker.TrackUsageDurablyAsync(record).ConfigureAwait(false);
            _logger.LogInformation(
                "💰 Durably tracked reserved usage: {FunctionType} - {Provider}/{Model} - {TotalTokens} tokens - ${Cost:F4}",
                functionType,
                provider,
                model,
                record.TotalTokens,
                record.EstimatedCost);
            return true;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            // The provider call already happened. Returning false keeps the
            // reservation committed and lets the caller settle conservatively.
            _logger.LogWarning(
                ex,
                "Failed to durably persist reserved LLM usage for {FunctionType}",
                functionType);
            return false;
        }
    }

    private async Task PersistUsageTelemetryAsync(
        LlmUsageRecord record,
        string functionType,
        string provider,
        string model)
    {
        try
        {
            await _usageTracker.TrackUsageAsync(record).ConfigureAwait(false);
            _logger.LogInformation(
                "💰 Tracked usage: {FunctionType} - {Provider}/{Model} - {TotalTokens} tokens - ${Cost:F4}",
                functionType,
                provider,
                model,
                record.TotalTokens,
                record.EstimatedCost);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to persist LLM usage telemetry for {FunctionType}", functionType);
        }
    }

    private async Task AppendExecutionAuditAsync(
        string functionType,
        LlmModelSelection model,
        LlmRouterOptions? options,
        bool fallbackUsed,
        bool succeeded,
        long responseTimeMs,
        int totalTokens,
        decimal estimatedCost,
        string? error)
    {
        try
        {
            var modelScoutDecision = model.ModelScoutDecisionReceipt;
            var functionEvidence = modelScoutDecision?.FunctionEvidence;
            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = LlmRoutingAuditEventKinds.Execution,
                Actor = "llm-router",
                Trigger = fallbackUsed ? "fallback-execution" : "selected-route-execution",
                FunctionType = RoutingDiagnosticSanitizer.Sanitize(functionType, 160) ?? "unknown-function",
                Context = model.Context is null
                    ? null
                    : $"{model.Context.Interactivity}/{model.Context.PrivacyClass}/{model.Context.Urgency}",
                AfterProvider = RoutingDiagnosticSanitizer.Sanitize(model.Provider, 80),
                AfterModel = RoutingDiagnosticSanitizer.Sanitize(model.Model, 240),
                AdvisorOrPolicy = RoutingDiagnosticSanitizer.Sanitize(model.Reason, 1_000),
                Reason = fallbackUsed
                    ? "The selected route failed or was unavailable, so the router executed the correlated fallback."
                    : "Executed the selected route.",
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    fallbackUsed,
                    responseTimeMs,
                    totalTokens,
                    estimatedCost,
                    transportContract = (_providerRegistry.GetProvider(model.Provider) as ILlmProviderExecutionContract)
                        ?.GetExecutionContract(model.Model).ExecutionContractId,
                    overrideProvenance = options?.OverrideProvenance.ToString(),
                    modelScoutDecision,
                    functionEvidence = functionEvidence is null
                        ? null
                        : new
                        {
                            functionType = functionEvidence.FunctionType,
                            provider = functionEvidence.Provider,
                            model = functionEvidence.Model,
                            source = functionEvidence.Source,
                            proofIdentitySha256 = functionEvidence.ProofIdentitySha256,
                            suiteSha256 = functionEvidence.SuiteSha256,
                            evaluatedAtUtc = functionEvidence.EvaluatedAtUtc,
                            expiresAtUtc = functionEvidence.ExpiresAtUtc,
                            weightedScore = functionEvidence.WeightedScore,
                            passRate = functionEvidence.PassRate,
                            criticalFailures = functionEvidence.CriticalFailures,
                            parseFailures = functionEvidence.ParseFailures,
                            caseCount = functionEvidence.CaseCount,
                            hardwareBucket = functionEvidence.HardwareBucket,
                            clearanceTier = functionEvidence.ClearanceTier,
                            bundleVersion = functionEvidence.BundleVersion,
                            executionContractId = functionEvidence.ExecutionContractId,
                            runtimeVersion = functionEvidence.RuntimeVersion,
                            executionRuntimeIdentity = functionEvidence.ExecutionRuntimeIdentity,
                            evidenceScope = functionEvidence.EvidenceScope,
                            sourceFunctionType = functionEvidence.SourceFunctionType,
                            provenance = new
                            {
                                kind = functionEvidence.ProvenanceKind,
                                operatorCleared = functionEvidence.OperatorCleared,
                            },
                        },
                }, RoutingAuditJsonOptions),
                CorrelationId = model.AuditCorrelationId,
                SessionId = options?.SessionId,
                TurnId = options?.TurnId,
                IsUserOverride = options?.OverrideProvenance == LlmOverrideProvenance.User,
                Succeeded = succeeded,
                Error = RoutingDiagnosticSanitizer.Sanitize(error, 2_000),
                BenchmarkSuiteSha256 = functionEvidence?.SuiteSha256,
            }).ConfigureAwait(false);

            if (IsRouteSurvivalAlternate(model)
                && !IsCallerCancellationTerminal(error)
                && !(IsRequestCancellationRequested(options) && !succeeded))
            {
                await AppendRouteSurvivalTerminalAuditAsync(
                        functionType,
                        model,
                        options,
                        succeeded,
                        fallbackUsed,
                        responseTimeMs,
                        totalTokens,
                        estimatedCost,
                        error)
                    .ConfigureAwait(false);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(ex,
                "Failed to append execution audit for {FunctionType} on {Provider}/{Model}",
                functionType,
                model.Provider,
                model.Model);
            throw new RoutingAuditPersistenceException(
                $"The model call completed, but its execution receipt could not be committed for '{functionType}'. The response is withheld to prevent unaudited execution.",
                ex);
        }
    }

    private async Task AppendExecutionStartedAuditAsync(
        string functionType,
        LlmModelSelection model,
        LlmRouterOptions? options,
        bool fallbackUsed,
        Dictionary<string, object>? physicalSettings,
        int inputCount = 1,
        bool embedding = false)
    {
        await ReservePhysicalAttemptAsync(model, physicalSettings, inputCount, embedding).ConfigureAwait(false);
        var scoutAdmission = await AdmitScoutExecutionAsync(functionType, model, options).ConfigureAwait(false);
        if (TypedJsonScope.Value is { } typedScope) typedScope.InitialSelection ??= model;
        try
        {
            var modelScoutDecision = model.ModelScoutDecisionReceipt;
            var functionEvidence = modelScoutDecision?.FunctionEvidence;
            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = LlmRoutingAuditEventKinds.ExecutionStarted,
                Actor = "llm-router",
                Trigger = fallbackUsed ? "fallback-execution-started" : "selected-execution-started",
                FunctionType = RoutingDiagnosticSanitizer.Sanitize(functionType, 160) ?? "unknown-function",
                Context = model.Context is null
                    ? null
                    : $"{model.Context.Interactivity}/{model.Context.PrivacyClass}/{model.Context.Urgency}",
                AfterProvider = RoutingDiagnosticSanitizer.Sanitize(model.Provider, 80),
                AfterModel = RoutingDiagnosticSanitizer.Sanitize(model.Model, 240),
                AdvisorOrPolicy = RoutingDiagnosticSanitizer.Sanitize(model.Reason, 1_000),
                Reason = fallbackUsed
                    ? "A correlated fallback provider call is starting; a terminal execution receipt must follow."
                    : "The selected provider call is starting; a terminal execution receipt must follow.",
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    fallbackUsed,
                    admissionMode = scoutAdmission,
                    state = "pending",
                    transportContract = (_providerRegistry.GetProvider(model.Provider) as ILlmProviderExecutionContract)
                        ?.GetExecutionContract(model.Model).ExecutionContractId,
                    overrideProvenance = options?.OverrideProvenance.ToString(),
                    modelScoutDecision,
                    functionEvidence = functionEvidence is null
                        ? null
                        : new
                        {
                            functionType = functionEvidence.FunctionType,
                            provider = functionEvidence.Provider,
                            model = functionEvidence.Model,
                            source = functionEvidence.Source,
                            proofIdentitySha256 = functionEvidence.ProofIdentitySha256,
                            suiteSha256 = functionEvidence.SuiteSha256,
                            evaluatedAtUtc = functionEvidence.EvaluatedAtUtc,
                            expiresAtUtc = functionEvidence.ExpiresAtUtc,
                            weightedScore = functionEvidence.WeightedScore,
                            passRate = functionEvidence.PassRate,
                            criticalFailures = functionEvidence.CriticalFailures,
                            parseFailures = functionEvidence.ParseFailures,
                            caseCount = functionEvidence.CaseCount,
                            hardwareBucket = functionEvidence.HardwareBucket,
                            clearanceTier = functionEvidence.ClearanceTier,
                            bundleVersion = functionEvidence.BundleVersion,
                            executionContractId = functionEvidence.ExecutionContractId,
                            runtimeVersion = functionEvidence.RuntimeVersion,
                            executionRuntimeIdentity = functionEvidence.ExecutionRuntimeIdentity,
                            evidenceScope = functionEvidence.EvidenceScope,
                            sourceFunctionType = functionEvidence.SourceFunctionType,
                            provenance = new
                            {
                                kind = functionEvidence.ProvenanceKind,
                                operatorCleared = functionEvidence.OperatorCleared,
                            },
                        },
                }, RoutingAuditJsonOptions),
                CorrelationId = model.AuditCorrelationId,
                SessionId = options?.SessionId,
                TurnId = options?.TurnId,
                IsUserOverride = options?.OverrideProvenance == LlmOverrideProvenance.User,
                Succeeded = true,
                BenchmarkSuiteSha256 = functionEvidence?.SuiteSha256,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(
                ex,
                "Failed to append execution-start receipt for {FunctionType} on {Provider}/{Model}",
                functionType,
                model.Provider,
                model.Model);
            throw new RoutingAuditPersistenceException(
                $"The model call was not started because its pending execution receipt could not be committed for '{functionType}'.",
                ex);
        }
    }

    private async Task AppendFallbackSelectionAuditAsync(
        string functionType,
        string prompt,
        LlmModelSelection primary,
        LlmModelSelection fallback,
        LlmRouterOptions? options,
        string reason,
        bool requiresVision = false,
        bool requiresToolUse = false)
    {
        try
        {
            var explicitAuthority = primary.IsUserPinned
                                    || options?.OverrideProvenance is
                                        LlmOverrideProvenance.User or
                                        LlmOverrideProvenance.Profile;
            if (!explicitAuthority && fallback.ModelScoutDecisionReceipt is null)
            {
                throw new InvalidOperationException(
                    "An automatic runtime fallback has no Model Scout decision receipt.");
            }
            if (explicitAuthority)
                ModelScoutExecutionAdmission.AdmitExactConstraint(functionType, fallback, "exact-user");

            var policy = _routingPolicyStore is null
                ? RoutingPolicy.Default
                : RoutingPolicy.NormalizeUntrusted(
                    await _routingPolicyStore.GetAsync().ConfigureAwait(false));
            var budgetPressure = _budgetPressureSource is null
                ? BudgetPressureSnapshot.Unconstrained
                : await _budgetPressureSource.GetCurrentAsync().ConfigureAwait(false);
            var automaticCostLimit = explicitAuthority
                ? (decimal?)null
                : await GetAutomaticFallbackCostLimitAsync().ConfigureAwait(false);
            var modelScoutDecision = fallback.ModelScoutDecisionReceipt;
            var functionEvidence = modelScoutDecision?.FunctionEvidence;
            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = LlmRoutingAuditEventKinds.Selection,
                Actor = explicitAuthority
                    ? options?.UserId ?? "user-or-profile"
                    : "llm-router",
                Trigger = "runtime-fallback-selection",
                FunctionType = RoutingDiagnosticSanitizer.Sanitize(functionType, 160)
                               ?? "unknown-function",
                Context = fallback.Context is null
                    ? null
                    : $"{fallback.Context.Interactivity}/{fallback.Context.PrivacyClass}/{fallback.Context.Urgency}",
                BeforeProvider = RoutingDiagnosticSanitizer.Sanitize(primary.Provider, 80),
                BeforeModel = RoutingDiagnosticSanitizer.Sanitize(primary.Model, 240),
                AfterProvider = RoutingDiagnosticSanitizer.Sanitize(fallback.Provider, 80),
                AfterModel = RoutingDiagnosticSanitizer.Sanitize(fallback.Model, 240),
                AdvisorOrPolicy = "Model Scout runtime fallback admission",
                Reason = "The selected route failed; this exact fallback passed the recorded capability, privacy, and spend envelope before execution.",
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    state = "admitted-before-execution",
                    reason = RoutingDiagnosticSanitizer.Sanitize(reason, 160),
                    explicitAuthority,
                    modelScoutDecision,
                    functionEvidence,
                    requirements = new
                    {
                        requiresVision = requiresVision
                                         || options?.RequiresVision == true
                                         || ModelScoutFunctionRequirements.RequiresVision(functionType),
                        requiresToolUse = requiresToolUse
                                          || options?.RequiresToolUse == true
                                          || ModelScoutFunctionRequirements.RequiresToolUse(functionType),
                        requiresStructuredOutput = options?.RequiresStructuredOutput == true,
                        minimumContextTokens = options?.MinimumContextTokens,
                        minimumCapabilityScore = StrongerCapabilityFloor(
                            fallback.MinimumCapabilityScore,
                            options?.MinimumCapabilityScore),
                        requireMeasuredCapabilityScore = fallback.RequireMeasuredCapabilityScore
                                                           || options?.RequireMeasuredCapabilityScore == true,
                        requiredTier = StrongerRequiredTier(
                            fallback.RequiredTier,
                            options?.RequiredTier)?.ToString(),
                        blockWeakerPolicyFallback = fallback.BlockWeakerPolicyFallback
                                                    || options?.BlockWeakerPolicyFallback == true,
                        requireCurrentFunctionEvidence = fallback.RequireCurrentFunctionEvidence
                                                         || options?.RequireCurrentFunctionEvidence == true,
                        disableFallback = options?.DisableFallback == true,
                        overrideProvenance = options?.OverrideProvenance.ToString(),
                    },
                    spendEvaluation = new
                    {
                        metered = await IsMeteredApiProviderAsync(fallback.Provider),
                        explicitAuthority,
                        automaticCostLimitUsd = automaticCostLimit,
                        selectedEstimatedCostUsd = modelScoutDecision?.Selected.EstimatedCostUsd,
                        policy.MonthlyCloudBudgetUsd,
                        budgetPressure.MonthToDateUsd,
                        budgetPressure.BurnFraction,
                        budgetPressure.ProjectedMonthUsd,
                    },
                }, RoutingAuditJsonOptions),
                PromptSha256 = ComputeHash(prompt),
                BenchmarkSuiteSha256 = functionEvidence?.SuiteSha256,
                CorrelationId = fallback.AuditCorrelationId,
                SessionId = options?.SessionId,
                TurnId = options?.TurnId,
                IsUserOverride = explicitAuthority,
                Succeeded = true,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(
                ex,
                "Failed to append pre-execution fallback selection receipt for {FunctionType} on {Provider}/{Model}",
                functionType,
                fallback.Provider,
                fallback.Model);
            throw new RoutingAuditPersistenceException(
                $"Fallback execution for '{functionType}' was blocked because its selection receipt could not be committed.",
                ex);
        }
    }

    private sealed class RoutingAuditPersistenceException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);

    private static LlmFailoverException CreateFailoverException(
        string functionType,
        string requestId,
        LlmModelSelection primary,
        LlmModelSelection? attemptedFallback,
        string reason,
        Exception fallbackException,
        LlmFailureCategory? primaryCategory = null)
    {
        var fallback = attemptedFallback ?? CarryRoutePolicy(primary, new LlmModelSelection
        {
            Provider = string.IsNullOrWhiteSpace(primary.FallbackProvider)
                ? "unresolved"
                : primary.FallbackProvider,
            Model = string.IsNullOrWhiteSpace(primary.FallbackModel)
                ? "unresolved"
                : primary.FallbackModel,
            IsDefault = false,
            Reason = $"Fallback resolution failed after {reason}",
            Context = primary.Context,
            AuditCorrelationId = primary.AuditCorrelationId,
        }, promoteConfiguredFallback: true);
        // The "(reason)" prose stays for logs and legacy string consumers; the
        // typed categories travel alongside so consumers (mid-turn retry
        // classification and future ones) don't have to re-derive them from
        // the prose. When the caller resolved a category at classification
        // time it wins over the legacy token: the wire-compatible reason
        // collapses distinct categories (a thrown quota exhaustion travels as
        // "rate_limit"), so re-deriving from the token alone would mislabel
        // QuotaExhausted as RateLimit.
        var fallbackError = BuildFailoverError(fallbackException);
        return new LlmFailoverException(
            functionType: functionType,
            promptId: requestId,
            primary: primary,
            fallback: fallback,
            primaryError: $"Primary {primary.Provider}/{primary.Model} failed ({reason}).",
            fallbackError: fallbackError,
            fallbackException: fallbackException,
            primaryCategory: primaryCategory ?? MapExactLegacyReason(reason),
            fallbackCategory: Services.LlmErrorPolicy.Classify(fallbackError));
    }

    /// <summary>
    /// Maps a failover reason token to its typed category ONLY when it is
    /// exactly one of the legacy reasons LlmErrorPolicy emits. Composite or
    /// free-form reasons ("primary_exception", "quality_gate:…",
    /// "thinking_channel_without_content") deliberately return null — encoding
    /// them as ProviderError would let typed-first consumers retry failures
    /// the legacy "(reason)" string matching never retried.
    /// </summary>
    private static LlmFailureCategory? MapExactLegacyReason(string reason)
        => reason?.Trim().ToLowerInvariant() switch
        {
            "provider_error" => LlmFailureCategory.ProviderError,
            "rate_limit" => LlmFailureCategory.RateLimit,
            "timeout" => LlmFailureCategory.Timeout,
            "model_not_found" => LlmFailureCategory.ModelNotFound,
            "launch_declined" => LlmFailureCategory.LaunchDeclined,
            _ => null,
        };

    private static string BuildFailoverError(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return RoutingDiagnosticSanitizer.Sanitize(
            string.Join(" | ", messages),
            maxLength: 1_024) ?? "Fallback failed without a diagnostic.";
    }

    /// <summary>
    /// The selected physical route failed and the caller deliberately disabled
    /// route substitution. Calibration treats this as a terminal candidate
    /// failure instead of repeating the same unavailable provider for every case.
    /// </summary>
    public sealed class FallbackDisabledException(string message, string? failureReason = null)
        : InvalidOperationException(message)
    {
        public string? FailureReason { get; } = failureReason;
    }

    private sealed class FallbackExecutionException(string message, Exception? innerException = null)
        : InvalidOperationException(message, innerException);

    private Dictionary<string, object> BuildExecutionSettings(LlmRouterOptions? options, LlmModelSelection? selection = null)
    {
        var provider = selection is null ? null : _providerRegistry.GetProvider(selection.Provider);
        var experimental = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (selection is not null
            && Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(
                selection.Provider))
        {
            var interactivity = selection.Context?.Interactivity
                ?? options?.Interactivity
                ?? LlmInteractivity.Background;
            experimental[SubscriptionCliExecutionSettings.ProcessPriority] =
                SubscriptionCliExecutionSettings.FromInteractivity(interactivity);
        }
        var settings = ProviderExecutionRequestBuilder.Build(
            provider,
            selection?.Model ?? string.Empty,
            options,
            selection,
            experimental);
        var functionType = selection?.ModelScoutDecisionReceipt?.FunctionType
            ?? selection?.ModelScoutConstraintAdmission?.FunctionType
            ?? selection?.ValidatedAdmissionReceipt?.FunctionType;
        if (!string.IsNullOrWhiteSpace(functionType))
            settings[LlmReservedExecutionSettings.RoutingFunctionType] = functionType;
        return settings;
    }

    private static void ApplyHiddenReasoningRecoverySettings(Dictionary<string, object> settings)
        => ProviderExecutionSettingsMutator.DisableReasoning(settings);

    private static void EnsureRecoveryTokenBudget(Dictionary<string, object> settings)
        => ProviderExecutionSettingsMutator.EnsureOutputBudget(settings, 512);

    private static bool StoppedAtMaxTokens(LlmResponse? response)
        => response?.Diagnostics.Any(diagnostic =>
            ProviderExecutionSettingsMutator.IsOutputLimitDiagnostic(diagnostic)) == true;

    private static void IncreaseJsonRetryTokenBudget(
        Dictionary<string, object> settings,
        LlmResponse? failedResponse)
    {
        var currentBudget = TryGetMaxTokens(settings) ?? 0;
        var observedOutput = failedResponse?.OutputTokens ?? 0;
        var requested = Math.Max(
            LlmJsonRetryLimits.MinimumOutputTokens,
            Math.Max(currentBudget * 2, observedOutput + 1024));
        var retryBudget = Math.Min(requested, LlmJsonRetryLimits.MaximumOutputTokens);
        ProviderExecutionSettingsMutator.SetOutputBudget(settings, retryBudget);
    }

    private static int? TryGetMaxTokens(IReadOnlyDictionary<string, object> settings)
        => ProviderExecutionSettingsMutator.ReadOutputBudget(settings);

    private static string ResolveQualityRetryKind(string qualityFailureReason, LlmRouterOptions? options)
    {
        if (string.Equals(qualityFailureReason, "thinking_channel_without_content", StringComparison.OrdinalIgnoreCase))
        {
            return "thinking_channel_retry";
        }

        if (IsEmptyResponseQualityFailure(qualityFailureReason))
        {
            return "empty_response_retry";
        }

        return options?.ForceJson == true
            ? "json_contract_retry"
            : "quality_retry";
    }

    private static string ResolveQualityRetrySuffix(string qualityFailureReason, LlmRouterOptions? options)
    {
        var kind = ResolveQualityRetryKind(qualityFailureReason, options);
        return kind switch
        {
            "thinking_channel_retry" => "think",
            "empty_response_retry" => "empty",
            "json_contract_retry" => "json",
            _ => "quality"
        };
    }

    private static bool IsEmptyResponseQualityFailure(string qualityFailureReason)
        => string.Equals(qualityFailureReason, "empty_response", StringComparison.OrdinalIgnoreCase)
           || string.Equals(qualityFailureReason, "empty_response_with_output_tokens", StringComparison.OrdinalIgnoreCase);

    private const string CallerResponseContractFailureReason = "caller_response_contract_failed";

    private static bool IsTerminalCallerContractFailure(
        string? qualityFailureReason,
        LlmRouterOptions? options)
        => string.Equals(
               qualityFailureReason,
               CallerResponseContractFailureReason,
               StringComparison.Ordinal)
           && options?.SuppressFallbackOnResponseContentValidationFailure == true;

    /// <summary>
    /// Validates that a "successful" response actually contains usable content.
    /// Returns null if quality is acceptable, or a failure reason string if not.
    /// </summary>
    private static string? ValidateResponseQuality(
        LlmResponse response,
        LlmRouterOptions? options,
        out string? normalizedContent)
    {
        normalizedContent = null;

        if (string.IsNullOrWhiteSpace(response.Content)
            && IsThinkingChannelResponse(response))
        {
            return "thinking_channel_without_content";
        }
        if (string.IsNullOrWhiteSpace(response.Content) && response.OutputTokens > 0)
        {
            return "empty_response_with_output_tokens";
        }

        return ValidateResponseQuality(response.Content, options, out normalizedContent);
    }

    private static string? ValidateResponseQuality(
        string? content,
        LlmRouterOptions? options,
        out string? normalizedContent)
    {
        normalizedContent = null;
        if (string.IsNullOrWhiteSpace(content))
            return "empty_response";

        if (options?.ForceJson == true)
        {
            if (!TryNormalizeJsonResponse(content, options.ExpectedFormat, out var normalized))
            {
                return "invalid_json_response";
            }

            normalizedContent = normalized;
        }

        if (options?.ResponseContentValidator is { } validator)
        {
            try
            {
                if (!validator(content))
                    return CallerResponseContractFailureReason;
            }
            catch
            {
                // A caller validator is part of the response boundary. Its
                // exception may contain model output, so expose only the fixed
                // failure code and fail closed.
                return CallerResponseContractFailureReason;
            }
        }

        return null;
    }

    private static bool IsThinkingChannelResponse(LlmResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
        {
            return true;
        }

        return response.Diagnostics.Any(d =>
            string.Equals(d, "thinking_channel_without_content", StringComparison.OrdinalIgnoreCase)
            || string.Equals(d, "thinking_channel_present", StringComparison.OrdinalIgnoreCase)
            || string.Equals(d, "reasoning_channel_without_content", StringComparison.OrdinalIgnoreCase)
            || string.Equals(d, "reasoning_channel_present", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsThinkingChannelErrorMessage(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("thinking_channel_without_content", StringComparison.OrdinalIgnoreCase)
               || errorMessage.Contains("reasoning_channel_without_content", StringComparison.OrdinalIgnoreCase)
               || (errorMessage.Contains("thinking", StringComparison.OrdinalIgnoreCase)
                   && errorMessage.Contains("empty content", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTimeoutOrTransientError(Exception ex) =>
        ex is TaskCanceledException ||
        ex is TimeoutException ||
        ex is HttpRequestException ||
        ex.InnerException is TimeoutException ||
        ex.InnerException is System.IO.IOException;

    // Thrown-failure predicates share the single typed classifier so the catch
    // chains, the cooldown writer, and the failover exception all agree on one
    // category. The classifier prefers the transport's structured HTTP status
    // (HttpRequestException.StatusCode) and never treats a bare digit run in
    // the message ("130429 tokens", "req_0142901") as a status code.
    private static bool IsRateLimitError(Exception ex) =>
        Services.LlmErrorPolicy.Classify(ex)
            is LlmFailureCategory.RateLimit or LlmFailureCategory.QuotaExhausted;

    /// <summary>
    /// Durably record a rate-limit or quota hit on <paramref name="providerName"/>
    /// before routing reports temporary exhaustion. Subsequent decisions skip
    /// this provider until the cooldown elapses. Safe when no tracker is wired up.
    /// </summary>
    private Task RecordCooldownIfRateLimitAsync(string? providerName, Exception ex)
    {
        if (_cooldownTracker is null) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(providerName)) return Task.CompletedTask;

        // The typed policy decides the cooldown, not this method: a thrown
        // quota/credit-exhaustion message (which usually also carries 429
        // markers) must land the long QuotaExhausted cooldown and operator
        // warning, exactly like the same message on the failed-response path.
        var category = Services.LlmErrorPolicy.Classify(ex);
        if (category is not LlmFailureCategory.RateLimit
            and not LlmFailureCategory.QuotaExhausted)
        {
            return Task.CompletedTask;
        }

        return RecordFailedResponseCooldownAsync(
            providerName,
            "rate_limit",
            ex.Message,
            Services.LlmErrorPolicy.TryGetHttpStatusCode(ex), category);
    }

    /// <summary>
    /// Records a short provider cooldown after an uncertain timeout/network failure
    /// before the caller reports retryable automatic-route exhaustion. A timed-out
    /// request may already have reached a metered provider, so an immediate retry of
    /// the same physical route could create duplicate paid work.
    /// </summary>
    private Task RecordCooldownIfTimeoutOrTransientAsync(string? providerName, Exception ex)
    {
        if (_cooldownTracker is null) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(providerName)) return Task.CompletedTask;
        if (!IsTimeoutOrTransientError(ex)) return Task.CompletedTask;

        // "Timeout-or-transient" is a claim about the exception TYPE, not the
        // failure: HttpRequestException also carries adverse HTTP statuses
        // (401 Unauthorized, billing/credit failures, 400s...) whose message
        // classifies to a category with its own policy. Persisting those as
        // Timeout handed typed-first consumers a wrong category with no way to
        // recover the truth — a thrown 401 got a 5-minute Timeout cooldown
        // instead of AuthInvalid's 2-hour + operator-attention treatment.
        // Route any specifically-classified failure through the same typed
        // policy path the failed-response lane uses (it resolves duration,
        // penalization, and surfacing per category and records the typed row).
        var category = Services.LlmErrorPolicy.Classify(ex);
        if (category is not LlmFailureCategory.Timeout
            and not LlmFailureCategory.ProviderError)
        {
            return RecordFailedResponseCooldownAsync(
                providerName,
                Services.LlmErrorPolicy
                    .Resolve(category, isSubscriptionCli: false, ex.Message)
                    .LegacyReason,
                ex.Message,
                Services.LlmErrorPolicy.TryGetHttpStatusCode(ex), category);
        }

        var retryAfter = Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(providerName)
            ? SubscriptionCliRuntimeFailureCooldown
            : AutomaticTransientRuntimeFailureCooldown;
        // Genuine timeouts and unclassifiable network-shaped transients
        // (connection refused, DNS, socket resets — ProviderError under
        // Classify because no keyword matches) keep the historic uncertain-
        // post-send treatment and exact durations above: a timed-out request
        // may already have reached a metered provider, so the route is parked
        // briefly either way. The row is typed Timeout because that IS the
        // taxonomy's bucket for network trouble, and Resolve(Timeout,
        // isSubscriptionCli) yields exactly these cooldown durations —
        // ProviderError would mislabel the row (its resolved policy never
        // cools down a non-CLI provider at all).
        return MarkProviderCooldownAsync(
            providerName,
            retryAfter,
            ex.Message,
            LlmFailureCategory.Timeout);
    }

    // Internal (not private) so architecture tests can prove the typed
    // category actually reaches the cooldown tracker for every penalizing
    // failure category, without driving the full routing pipeline.
    internal async Task RecordFailedResponseCooldownAsync(
        string? providerName,
        string reason,
        string? errorMessage,
        int? httpStatusCode = null,
        LlmFailureCategory? failureCategory = null)
    {
        if (_cooldownTracker is null) return;
        if (string.IsNullOrWhiteSpace(providerName)) return;

        var isSubscriptionCli = Configuration.SubscriptionCliProviders
            .IsSubscriptionCliProvider(providerName);
        var category = failureCategory ?? (string.IsNullOrWhiteSpace(errorMessage) && httpStatusCode is null
            ? Services.LlmErrorPolicy.ClassifyLegacyReason(reason)
            : Services.LlmErrorPolicy.Classify(errorMessage, httpStatusCode));
        var policy = Services.LlmErrorPolicy.Resolve(category, isSubscriptionCli, errorMessage);

        if (policy.SurfaceToUser)
        {
            // Dead credential / exhausted paid quota: silent per-call retries
            // cannot fix this. Log loudly; the provider-health UI shows the
            // persisted reason once the cooldown lands.
            _logger.LogWarning(
                "Provider {Provider} failed with {Category}; this needs operator attention: {Error}",
                providerName,
                policy.Category,
                errorMessage ?? reason);
        }

        // Content-policy and invalid-request failures are the request's fault:
        // never sideline a healthy provider for them. Model-not-found is the
        // model id's fault and is handled by default-model repair, not cooldown.
        if (!policy.PenalizeProvider || policy.ProviderCooldown is not { } cooldown)
        {
            return;
        }

        await MarkProviderCooldownAsync(
                providerName,
                cooldown,
                category == LlmFailureCategory.RateLimit
                    ? errorMessage
                    : $"{policy.LegacyReason}: {errorMessage ?? $"{policy.Category} failure"}",
                // The typed category rides along with the string reason so
                // downstream consumers (health latch, probe clearing, UI chips)
                // never have to re-derive it by keyword-sniffing the reason.
                policy.Category)
            .ConfigureAwait(false);
    }

    // Internal (not private) so the router test assembly calls it directly
    // instead of binding to it by reflection.
    internal async Task MarkProviderCooldownAsync(
        string providerName,
        TimeSpan retryAfter,
        string? reason,
        LlmFailureCategory? category = null)
    {
        if (_cooldownTracker is null) return;

        // The reason is persisted (SQLite) and rendered in the provider-health
        // UI. Provider error bodies routinely echo the rejected API key, bearer
        // tokens, or addresses, so redact before the row becomes durable — the
        // usage row and audit receipt for the same failure are already
        // sanitized; the cooldown row must not be the one place the secret lands.
        var trimmedReason = RoutingDiagnosticSanitizer.Sanitize(reason, 200);
        try
        {
            await _cooldownTracker
                .MarkExhaustedAsync(providerName, retryAfter, trimmedReason, category)
                .ConfigureAwait(false);
            // A subscription usage limit ends when the provider refills the
            // window. Tell the quota estimate when that is, so the refilled
            // subscription is not still ranked as drained after the cooldown.
            if (category is LlmFailureCategory.RateLimit or LlmFailureCategory.QuotaExhausted
                && _quotaTracker is not null
                && Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(providerName))
            {
                var resetsAtUtc = _cooldownTracker.GetCooldownUntil(providerName) ?? DateTime.UtcNow + retryAfter;
                _quotaTracker.RecordWindowReset(
                    providerName,
                    new DateTimeOffset(DateTime.SpecifyKind(resetsAtUtc, DateTimeKind.Utc)));
            }
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogDebug(ex, "Failed to record cooldown for provider {Provider}", providerName);
        }
    }

    private static bool IsModelNotFoundError(Exception ex) =>
        Services.LlmErrorPolicy.Classify(ex) is LlmFailureCategory.ModelNotFound;

    private static bool IsModelNotFoundErrorMessage(string? errorMessage, int? httpStatusCode = null)
        => !string.IsNullOrWhiteSpace(errorMessage)
           && Services.LlmErrorPolicy.Classify(errorMessage, httpStatusCode)
               is LlmFailureCategory.ModelNotFound;

    private static bool IsSameRoute(string leftProvider, string leftModel, string rightProvider, string rightModel) =>
        IsExactRouteIdentity(leftProvider, leftModel, rightProvider, rightModel);

    /// <summary>
    /// Classification-time pairing of the wire-compatible legacy reason token
    /// with the typed category it was derived from. Failure sites that thread
    /// a category into the fallback flow (and ultimately
    /// <see cref="LlmFailoverException.PrimaryCategory"/>) must use this
    /// overload so the typed value is the SAME classification that chose the
    /// reason token — re-deriving it later from the collapsed token mislabels
    /// auth/content-policy/invalid-request/malformed-reply failures as
    /// ProviderError and quota exhaustion as RateLimit.
    ///
    /// The thinking-channel special case deliberately returns a null category:
    /// its reason token ("thinking_channel_without_content") is a quality-gate
    /// signal, not an LlmErrorPolicy classification, and stamping ProviderError
    /// on it would make it mid-turn-retryable where it never was.
    ///
    /// Failed-response overload: carries the provider's structured HTTP status
    /// into classification so the message text never has to be sniffed for one.
    /// </summary>
    private (string Reason, LlmFailureCategory? Category) ClassifyFailureReasonWithCategory(LlmResponse response)
        => response.FailureCategory is { } category
            ? (Services.LlmErrorPolicy.Resolve(category, false, response.ErrorMessage).LegacyReason, category)
            : ClassifyFailureReasonWithCategory(response.ErrorMessage, response.HttpStatusCode);

    /// <summary>
    /// Thrown-failure overload: reads <see cref="HttpRequestException.StatusCode"/>
    /// from the exception chain when the transport supplied one.
    /// </summary>
    private (string Reason, LlmFailureCategory? Category) ClassifyFailureReasonWithCategory(Exception exception)
        => exception is LlmProviderHttpException typed
            ? (Services.LlmErrorPolicy.Resolve(typed.Category, false, typed.Message).LegacyReason, typed.Category)
            : ClassifyFailureReasonWithCategory(
            exception.Message,
            Services.LlmErrorPolicy.TryGetHttpStatusCode(exception));

    private (string Reason, LlmFailureCategory? Category) ClassifyFailureReasonWithCategory(
        string? errorMessage,
        int? httpStatusCode)
    {
        if (IsThinkingChannelErrorMessage(errorMessage))
        {
            return ("thinking_channel_without_content", null);
        }

        // Central error taxonomy; the returned string stays wire-compatible
        // with historic audit/reason consumers, while cooldown/penalty
        // decisions read the full typed policy in RecordFailedResponseCooldownAsync.
        var category = Services.LlmErrorPolicy.Classify(errorMessage, httpStatusCode);
        var reason = Services.LlmErrorPolicy
            .Resolve(category, isSubscriptionCli: false, errorMessage)
            .LegacyReason;
        return (reason, category);
    }

    private async Task<(string Provider, string Model)> ResolveFallbackRouteAsync(
        string functionType,
        LlmModelSelection selectedModel,
        string prompt,
        bool requiresVision,
        string reason,
        LlmRouterOptions? options = null)
    {
        if (HasExactValidatedAdmission(selectedModel, options))
        {
            var exactAdmissionMessage =
                $"Exact host-validated route {selectedModel.Provider}/{selectedModel.Model} failed for {functionType} (reason={reason}). "
                + "Its proof and spend receipt authorizes only that physical route; Model Scout must re-admit any replacement before a later call.";
            await ReportOperationalFailureAsync(
                functionType,
                selectedModel,
                requiresVision,
                reason,
                exactAdmissionMessage).ConfigureAwait(false);
            throw new InvalidOperationException(exactAdmissionMessage);
        }

        var hasConfiguredFallback = !string.IsNullOrWhiteSpace(selectedModel.FallbackProvider)
                                    && !string.IsNullOrWhiteSpace(selectedModel.FallbackModel);
        var hasProviderConstraint = options?.OverrideProvenance is
                                        LlmOverrideProvenance.User or
                                        LlmOverrideProvenance.Profile
                                    && !string.IsNullOrWhiteSpace(options.OverrideProvider);
        var hasExplicitDirectedFallback = hasConfiguredFallback
                                          && (selectedModel.IsUserPinned || hasProviderConstraint);
        if (!hasConfiguredFallback
            && (selectedModel.IsUserPinned || hasProviderConstraint))
        {
            var noConfiguredFallbackMessage = $"No fallback route configured for {selectedModel.Provider}/{selectedModel.Model} on {functionType} (reason={reason}).";
            await ReportOperationalFailureAsync(functionType, selectedModel, requiresVision, reason, noConfiguredFallbackMessage);
            throw new LlmNoUsableRouteException(noConfiguredFallbackMessage);
        }

        string? unusableAutomaticFallback = null;
        if (hasConfiguredFallback
            && await ViolatesLocalOnlyPrivacyAsync(
                selectedModel.FallbackProvider!,
                selectedModel.FallbackModel,
                selectedModel.Context,
                options))
        {
            var privacyMessage =
                $"Configured fallback route {selectedModel.FallbackProvider}/{selectedModel.FallbackModel} is forbidden for {functionType}: "
                + "LocalOnly privacy does not permit a cloud fallback.";
            if (hasExplicitDirectedFallback)
            {
                await ReportOperationalFailureAsync(
                    functionType,
                    selectedModel,
                    requiresVision,
                    reason,
                    privacyMessage).ConfigureAwait(false);
                throw new InvalidOperationException(privacyMessage);
            }

            unusableAutomaticFallback = privacyMessage;
        }

        // Preserve a precise hard-requirement diagnosis for an automatic
        // configured alternate, but never execute it on catalog evidence alone.
        // A route that passes this check still has to be re-admitted by current
        // function proof below.
        if (hasConfiguredFallback && !hasExplicitDirectedFallback)
        {
            if (unusableAutomaticFallback is null)
            {
                var configuredIssue = await GetFallbackRouteIssueAsync(
                    selectedModel,
                    selectedModel.FallbackProvider!,
                    selectedModel.FallbackModel!,
                    requiresVision,
                    options,
                    carriesConfiguredEvidence: false).ConfigureAwait(false);
                if (configuredIssue is not null)
                {
                    unusableAutomaticFallback =
                        $"Configured automatic fallback route {selectedModel.FallbackProvider}/{selectedModel.FallbackModel} is not usable for {functionType}: "
                        + $"{configuredIssue} (reason={reason}).";
                }
            }

            if (unusableAutomaticFallback is not null)
            {
                // This is a stale automatic suggestion, not user authority.
                // Keep the diagnosis, but let the fresh Scout decision below
                // find another independently admitted route.
                _logger.LogWarning(
                    "Skipping unusable automatic fallback before fresh Model Scout recovery: {Reason}",
                    unusableAutomaticFallback);
            }
        }

        // Runtime substitution is another automatic model choice, not an
        // availability-only escape hatch. Unless the user explicitly declared
        // the exact fallback, resolve a fresh current function-scoped Scout
        // candidate (or a proof-carrying self-healer result) and fail closed.
        if (!hasExplicitDirectedFallback)
        {
            if (await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false))
            {
                var provenFallback = await TryResolveProvenModelScoutFallbackAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    requiresVision,
                    options).ConfigureAwait(false);
                if (provenFallback is not null)
                {
                    return provenFallback.Value;
                }

                if (!selectedModel.BlockWeakerPolicyFallback)
                {
                    var healedFallback = await TryResolveProvenSelfHealedFallbackAsync(
                        functionType,
                        selectedModel,
                        requiresVision,
                        options).ConfigureAwait(false);
                    if (healedFallback is not null)
                    {
                        return healedFallback.Value;
                    }
                }
            }

            var noProofMessage =
                $"No current function-scoped fallback proof is available for {functionType} after "
                + $"{selectedModel.Provider}/{selectedModel.Model} failed (reason={reason}); bounded recovery could not prove an alternate route."
                + (string.IsNullOrWhiteSpace(unusableAutomaticFallback)
                    ? string.Empty
                    : $" Configured fallback diagnosis: {RoutingDiagnosticSanitizer.Sanitize(unusableAutomaticFallback, maxLength: 768)}");
            await ReportOperationalFailureAsync(
                functionType,
                selectedModel,
                requiresVision,
                reason,
                noProofMessage).ConfigureAwait(false);
            throw new LlmNoUsableRouteException(noProofMessage);
        }

        if (!hasConfiguredFallback)
        {
            var noUserFallbackMessage =
                $"User/profile-directed route {selectedModel.Provider}/{selectedModel.Model} has no exact configured fallback for {functionType} (reason={reason}).";
            await ReportOperationalFailureAsync(
                functionType,
                selectedModel,
                requiresVision,
                reason,
                noUserFallbackMessage).ConfigureAwait(false);
            throw new LlmNoUsableRouteException(noUserFallbackMessage);
        }

        var fallbackProvider = selectedModel.FallbackProvider!;
        var fallbackModel = selectedModel.FallbackModel!;

        if (hasProviderConstraint
            && !string.Equals(
                ResolveRegisteredProviderName(fallbackProvider),
                ResolveRegisteredProviderName(options!.OverrideProvider!),
                StringComparison.OrdinalIgnoreCase))
        {
            var constraintMessage =
                $"Fallback route {fallbackProvider}/{selectedModel.FallbackModel ?? "default"} is outside the explicit provider constraint {options.OverrideProvider} for {functionType}.";
            await ReportOperationalFailureAsync(
                functionType,
                selectedModel,
                requiresVision,
                reason,
                constraintMessage).ConfigureAwait(false);
            throw new InvalidOperationException(constraintMessage);
        }

        if (_providerRegistry.GetProvider(fallbackProvider)?.SupportsModel(fallbackModel) != true)
        {
            var unavailablePinnedFallbackMessage =
                $"User/profile-configured fallback route {fallbackProvider}/{fallbackModel} is not available for {functionType}.";
            await ReportOperationalFailureAsync(
                functionType,
                selectedModel,
                requiresVision,
                reason,
                unavailablePinnedFallbackMessage).ConfigureAwait(false);
            throw new InvalidOperationException(unavailablePinnedFallbackMessage);
        }

        var fallbackIssue = await GetFallbackRouteIssueAsync(
            selectedModel,
            fallbackProvider,
            fallbackModel,
            requiresVision,
            options,
            carriesConfiguredEvidence: true).ConfigureAwait(false);
        if (!IsSameRoute(selectedModel.Provider, selectedModel.Model, fallbackProvider, fallbackModel)
            && fallbackIssue is null)
        {
            return (fallbackProvider, fallbackModel);
        }

        var unavailableConfiguredFallbackMessage =
            $"User/profile-configured fallback route {fallbackProvider}/{fallbackModel} is not usable for {functionType}: "
            + $"{fallbackIssue ?? "it resolves to the same route as the primary"} (reason={reason}).";
        await ReportOperationalFailureAsync(
            functionType,
            selectedModel,
            requiresVision,
            reason,
            unavailableConfiguredFallbackMessage).ConfigureAwait(false);
        throw new InvalidOperationException(unavailableConfiguredFallbackMessage);
    }

    /// <summary>
    /// Determines whether a thrown primary failure may enter runtime failover.
    /// Automatic routes can ask Scout for a fresh, independently admitted route
    /// even when an advisor's optional fallback was missing or discarded. Exact
    /// user/profile routes remain sovereign and require their explicit fallback.
    /// </summary>
    private static bool CanAttemptRuntimeFallback(
        LlmModelSelection selectedModel,
        LlmRouterOptions? options)
    {
        if (options?.DisableFallback == true
            || HasExactValidatedAdmission(selectedModel, options))
        {
            return false;
        }

        var hasConfiguredFallback = !string.IsNullOrWhiteSpace(selectedModel.FallbackProvider)
                                    && !string.IsNullOrWhiteSpace(selectedModel.FallbackModel);
        if (hasConfiguredFallback)
        {
            return true;
        }

        var exactUserOrProfileRoute = selectedModel.IsUserPinned
                                      || options?.OverrideProvenance is
                                          LlmOverrideProvenance.User or
                                          LlmOverrideProvenance.Profile;
        return !exactUserOrProfileRoute;
    }

    private static bool HasExactValidatedAdmission(
        LlmModelSelection selectedModel,
        LlmRouterOptions? options)
        => selectedModel.ValidatedAdmissionReceipt is not null
           || options?.OverrideProvenance == LlmOverrideProvenance.ValidatedAdmission;

    private async Task<(string Provider, string Model)?> TryResolveProvenModelScoutFallbackAsync(
        string functionType,
        LlmModelSelection selectedModel,
        string prompt,
        bool requiresVision,
        LlmRouterOptions? options)
    {
        if (_modelScout is null)
        {
            return null;
        }

        try
        {
            var strictCurrentFunctionEvidence = selectedModel.RequireCurrentFunctionEvidence
                                                || RequiresStrictCurrentFunctionEvidence(functionType);
            var context = selectedModel.Context ?? FunctionTypeProfiles.Resolve(
                functionType,
                options?.Interactivity ?? LlmInteractivity.Unspecified,
                options?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
                options?.Urgency ?? LlmUrgency.Unspecified,
                options?.Workload ?? LlmWorkloadKind.Unspecified);
            var constrainedProvider = options?.OverrideProvenance is
                                          LlmOverrideProvenance.User or
                                          LlmOverrideProvenance.Profile
                                      && !string.IsNullOrWhiteSpace(options.OverrideProvider)
                ? ResolveRegisteredProviderName(options.OverrideProvider)
                : null;
            var tier = selectedModel.RequiredTier
                       ?? ModelScoutTierPolicy.DeriveTier(
                           context.Urgency,
                           context.Interactivity,
                           zeroMarginalCostLane: false);
            var automaticFallbackCostLimit = await GetAutomaticFallbackCostLimitAsync()
                .ConfigureAwait(false);
            var connectionPolicy = _routingPolicyStore is null ? RoutingPolicy.Default
                : await _routingPolicyStore.GetAsync().ConfigureAwait(false);
            var decision = await _modelScout.RecommendAsync(new ModelScoutRecommendationRequest
            {
                ModelRequest = options?.ModelRequest?.Snapshot(),
                PreferredModelConnection = connectionPolicy.PreferredModelConnection,
                FunctionType = functionType,
                Provider = constrainedProvider,
                Tier = tier,
                Interactivity = context.Interactivity,
                Workload = context.Workload,
                // The substitute serves the same demand as the failed primary,
                // including a frontier safety floor carried on the selection.
                Demand = selectedModel.RequiredTier == ModelScoutTier.Frontier
                    ? LlmTaskDemand.Merge(options?.TaskDemand, new LlmTaskDemand
                    {
                        Complexity = LlmTaskComplexity.HighStakes,
                        Signals = ["frontier-safety-floor"],
                    })
                    : options?.TaskDemand,
                SubscriptionQuotaThreshold = connectionPolicy.SubscriptionQuotaThreshold,
                PrivacyClass = context.PrivacyClass,
                RequiresVision = requiresVision,
                RequiresToolUse = options?.RequiresToolUse == true,
                RequiresStructuredOutput = options?.RequiresStructuredOutput == true,
                StructuredOutputRepairAvailable = options?.AllowTextJsonContractRepair == true,
                MinimumContextTokens = options?.MinimumContextTokens,
                EstimatedInputTokens = EstimatePromptTokens(prompt),
                EstimatedOutputTokens = ResolveEstimatedOutputTokens(options),
                MaximumEstimatedCostUsd = automaticFallbackCostLimit,
                LatencyTargetMs = context.LatencyBudgetMs,
                MinimumCapabilityScore = selectedModel.MinimumCapabilityScore,
                RequireMeasuredCapabilityScore = selectedModel.RequireMeasuredCapabilityScore,
                AuditSessionId = options?.SessionId,
                AuditTurnId = options?.TurnId,
                PersistAudit = false,
                RequireCurrentFunctionEvidence = strictCurrentFunctionEvidence,
                MaintainCurrentFunctionEvidenceRecovery = !strictCurrentFunctionEvidence,
                MaintainServingInventory = true,
            }).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(decision.AuditCorrelationId))
            {
                _logger.LogWarning(
                    "Model Scout returned runtime fallback candidates for {FunctionType} without an auditable correlation receipt; rejecting them",
                    functionType);
                return null;
            }

            var rankedCandidates = decision.Candidates
                .Select((ranked, rank) => new { Ranked = ranked, Rank = rank })
                .Where(item => item.Ranked.IsEligible
                               && MeetsCapabilityEnvelope(
                                   item.Ranked.Candidate.CapabilityScore,
                                   item.Ranked.Candidate.CapabilityEvidenceKind,
                                   selectedModel.MinimumCapabilityScore,
                                   tier,
                                   selectedModel.RequireMeasuredCapabilityScore)
                               && (!strictCurrentFunctionEvidence
                                   || HasCurrentFunctionEvidence(
                                       item.Ranked.Candidate,
                                       functionType)))
                // Runtime failover is a fresh automatic choice. For ordinary
                // functions Scout may return both exact function proof and
                // audited provisional priors in one decision; exact proof must
                // remain authoritative even when a general prior has a higher
                // blended score. Within the same admission class, preserve the
                // fresh Scout rank: the fallback captured before the primary
                // failed is stale and must not override current cooldown and
                // provider-health evidence.
                .OrderBy(item => ModelScoutModelRequests.PreferenceIndex(options?.ModelRequest, item.Ranked.Candidate.Provider, item.Ranked.Candidate.Model))
                .ThenBy(item => ModelScoutModelRequests.ConnectionOrder(decision.Request, item.Ranked.Candidate))
                .ThenByDescending(item => HasCurrentFunctionEvidence(
                    item.Ranked.Candidate,
                    functionType))
                .ThenBy(item => item.Rank);

            foreach (var item in rankedCandidates)
            {
                var candidate = item.Ranked.Candidate;
                var hasExactFunctionProof = HasCurrentFunctionEvidence(
                    candidate,
                    functionType);
                var provider = ResolveRegisteredProviderName(candidate.Provider);
                var issue = await GetAutomaticFallbackIssueAsync(
                    selectedModel,
                    provider,
                    candidate.Model,
                    requiresVision,
                    options,
                    candidate.SupportsToolUse,
                    candidate.SupportsStructuredOutput,
                    candidate.MaxContextTokens,
                    candidate.CapabilityEvidenceSource ?? candidate.EvidenceSource,
                    candidate.CapabilityScore,
                    candidate.CapabilityEvidenceKind,
                    candidate.EstimatedCostUsd,
                    automaticFallbackCostLimit).ConfigureAwait(false);
                if (issue is not null)
                {
                    _logger.LogDebug(
                        "Skipped {AdmissionKind} runtime fallback candidate {Provider}/{Model} for {FunctionType}: {Reason}",
                        hasExactFunctionProof ? "current function-proven" : "hard-gated provisional",
                        provider,
                        candidate.Model,
                        functionType,
                        issue);
                    continue;
                }

                ApplyProvenFallbackEvidence(
                    selectedModel,
                    provider,
                    candidate.Model,
                    decision.AuditCorrelationId,
                    candidate.SupportsToolUse,
                    candidate.SupportsStructuredOutput,
                    candidate.MaxContextTokens,
                    candidate.CapabilityEvidenceSource ?? candidate.EvidenceSource,
                    candidate.CapabilityScore,
                    candidate.CapabilityEvidenceKind,
                    BuildRuntimeDecisionReceipt(decision, item.Ranked));
                _logger.LogWarning(
                    "Model Scout selected {AdmissionKind} runtime fallback {Provider}/{Model} for {FunctionType} (receipt {CorrelationId})",
                    hasExactFunctionProof
                        ? "current function-proven"
                        : "hard-gated provisional while proof recovery continues",
                    provider,
                    candidate.Model,
                    functionType,
                    decision.AuditCorrelationId);
                return (provider, candidate.Model);
            }
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(
                ex,
                "Model Scout could not admit an auditable runtime fallback for {FunctionType}",
                functionType);
        }

        return null;
    }

    private async Task<(string Provider, string Model)?> TryResolveProvenSelfHealedFallbackAsync(
        string functionType,
        LlmModelSelection selectedModel,
        bool requiresVision,
        LlmRouterOptions? options)
    {
        if (_selfHealer is null
            || selectedModel.BlockWeakerPolicyFallback
            || HasExactValidatedAdmission(selectedModel, options))
        {
            return null;
        }

        try
        {
            var replacement = await _selfHealer.GetFunctionScopedReplacementAsync(
                functionType,
                selectedModel.Provider,
                selectedModel.Model,
                requiresVision).ConfigureAwait(false);
            if (replacement is null
                || !replacement.HasCurrentFunctionEvidence
                || string.IsNullOrWhiteSpace(replacement.AuditCorrelationId)
                || string.IsNullOrWhiteSpace(replacement.EvidenceSource)
                || !string.Equals(
                    replacement.EvidenceFunctionType,
                    functionType,
                    StringComparison.OrdinalIgnoreCase)
                || replacement.ModelScoutDecisionReceipt is not { } decisionReceipt
                || GetDecisionReceiptConstraintIssue(
                    decisionReceipt,
                    selectedModel.Provider,
                    replacement.Model,
                    selectedModel.MinimumCapabilityScore,
                    selectedModel.RequiredTier,
                    selectedModel.RequireMeasuredCapabilityScore) is not null
                || !ModelScoutFunctionEvidence.IsCoherent(
                    decisionReceipt.FunctionEvidence,
                    functionType,
                    selectedModel.Provider,
                    replacement.Model,
                    replacement.EvidenceSource))
            {
                return null;
            }

            var provider = ResolveRegisteredProviderName(selectedModel.Provider);
            var automaticFallbackCostLimit = await GetAutomaticFallbackCostLimitAsync()
                .ConfigureAwait(false);
            var issue = await GetAutomaticFallbackIssueAsync(
                selectedModel,
                provider,
                replacement.Model,
                requiresVision,
                options,
                replacement.SupportsToolUse,
                replacement.SupportsStructuredOutput,
                replacement.MaxContextTokens,
                replacement.CapabilityEvidenceSource ?? replacement.EvidenceSource,
                decisionReceipt.Selected.CapabilityScore,
                decisionReceipt.Selected.CapabilityProvenance,
                decisionReceipt.Selected.EstimatedCostUsd,
                automaticFallbackCostLimit).ConfigureAwait(false);
            if (issue is not null)
            {
                _logger.LogWarning(
                    "Rejected proof-carrying self-healed fallback {Provider}/{Model} for {FunctionType}: {Reason}",
                    provider,
                    replacement.Model,
                    functionType,
                    issue);
                return null;
            }

            ApplyProvenFallbackEvidence(
                selectedModel,
                provider,
                replacement.Model,
                replacement.AuditCorrelationId,
                replacement.SupportsToolUse,
                replacement.SupportsStructuredOutput,
                replacement.MaxContextTokens,
                replacement.CapabilityEvidenceSource ?? replacement.EvidenceSource,
                decisionReceipt.Selected.CapabilityScore,
                decisionReceipt.Selected.CapabilityProvenance,
                replacement.ModelScoutDecisionReceipt);
            _logger.LogWarning(
                "Self-healer selected current function-proven runtime fallback {Provider}/{Model} for {FunctionType} (receipt {CorrelationId})",
                provider,
                replacement.Model,
                functionType,
                replacement.AuditCorrelationId);
            return (provider, replacement.Model);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(
                ex,
                "Self-healer could not prove a runtime fallback for {FunctionType}",
                functionType);
            return null;
        }
    }

    private async Task<string?> GetAutomaticFallbackIssueAsync(
        LlmModelSelection selectedModel,
        string provider,
        string model,
        bool requiresVision,
        LlmRouterOptions? options,
        bool? supportsToolUse,
        bool? supportsStructuredOutput,
        int? maxContextTokens,
        string? capabilityEvidenceSource,
        decimal? capabilityScore,
        ModelScoutCapabilityEvidenceKind capabilityEvidenceKind,
        decimal? estimatedCostUsd,
        decimal automaticFallbackCostLimit)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "the proven fallback model is empty";
        }
        if (IsSameRoute(selectedModel.Provider, selectedModel.Model, provider, model))
        {
            return "it resolves to the failed primary route";
        }
        if (options?.OverrideProvenance is LlmOverrideProvenance.User or LlmOverrideProvenance.Profile
            && !string.IsNullOrWhiteSpace(options.OverrideProvider)
            && !string.Equals(
                provider,
                ResolveRegisteredProviderName(options.OverrideProvider),
                StringComparison.OrdinalIgnoreCase))
        {
            return $"it is outside the explicit provider constraint {options.OverrideProvider}";
        }
        var capabilityConstraintIssue = GetExplicitCapabilityConstraintIssue(
            capabilityScore,
            capabilityEvidenceKind,
            selectedModel.MinimumCapabilityScore,
            selectedModel.RequiredTier,
            selectedModel.RequireMeasuredCapabilityScore);
        if (capabilityConstraintIssue is not null)
        {
            return capabilityConstraintIssue;
        }
        if (await IsMeteredApiProviderAsync(provider)
            && automaticFallbackCostLimit <= 0m)
        {
            return "no explicit automatic cloud-spend budget authorizes this metered fallback";
        }
        if (await IsMeteredApiProviderAsync(provider)
            && estimatedCostUsd is null)
        {
            return "the metered fallback has no verified per-call cost estimate";
        }
        if (await IsMeteredApiProviderAsync(provider)
            && estimatedCostUsd > automaticFallbackCostLimit)
        {
            return $"estimated fallback cost {estimatedCostUsd:C4} exceeds the remaining automatic budget admission {automaticFallbackCostLimit:C4}";
        }

        var providerImpl = _providerRegistry.GetProvider(provider);
        if (providerImpl is null || !providerImpl.SupportsModel(model))
        {
            return "the provider does not advertise support for the exact model";
        }

        var probe = new LlmModelSelection
        {
            Provider = provider,
            Model = model,
            IsDefault = false,
            Context = selectedModel.Context,
            AuditCorrelationId = selectedModel.FallbackAuditCorrelationId
                                 ?? selectedModel.AuditCorrelationId,
            SupportsToolUse = supportsToolUse,
            SupportsStructuredOutput = supportsStructuredOutput,
            MaxContextTokens = maxContextTokens,
            CapabilityEvidenceSource = capabilityEvidenceSource,
            CapabilityScore = capabilityScore,
            CapabilityEvidenceKind = capabilityEvidenceKind,
        };
        var hardRequirementIssue = await GetRouteRequirementIssueAsync(
            probe,
            requiresVision,
            options,
            verifyAvailability: true).ConfigureAwait(false);
        if (hardRequirementIssue is not null)
        {
            return hardRequirementIssue;
        }

        return options?.RequiresToolUse == true
            ? GetToolUseSelectionIssue(probe)
            : null;
    }

    private async Task<decimal> GetAutomaticFallbackCostLimitAsync()
    {
        // A connected API key makes a provider reachable, but is not authority
        // for a surprise runtime charge. Automatic metered fallback requires a
        // user-configured monthly cloud budget and a current spend snapshot.
        // Exact User/Profile fallbacks bypass this helper and remain the user's
        // explicit route choice (subject to privacy and hard capabilities).
        if (_routingPolicyStore is null || _budgetPressureSource is null)
        {
            return 0m;
        }

        try
        {
            var policy = RoutingPolicy.NormalizeUntrusted(
                await _routingPolicyStore.GetAsync().ConfigureAwait(false));
            if (policy.MonthlyCloudBudgetUsd is not { } monthlyBudget
                || monthlyBudget <= 0m)
            {
                return 0m;
            }

            var pressure = await _budgetPressureSource.GetCurrentAsync().ConfigureAwait(false);
            return Math.Max(0m, monthlyBudget - Math.Max(0m, pressure.MonthToDateUsd));
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(
                ex,
                "Automatic metered fallback was denied because cloud-spend admission could not be verified");
            return 0m;
        }
    }

    private static void ApplyProvenFallbackEvidence(
        LlmModelSelection selectedModel,
        string provider,
        string model,
        string? auditCorrelationId,
        bool? supportsToolUse,
        bool? supportsStructuredOutput,
        int? maxContextTokens,
        string? capabilityEvidenceSource,
        decimal? capabilityScore,
        ModelScoutCapabilityEvidenceKind capabilityEvidenceKind,
        ModelScoutRuntimeDecisionReceipt? modelScoutDecisionReceipt)
    {
        selectedModel.FallbackProvider = provider;
        selectedModel.FallbackModel = model;
        selectedModel.FallbackAuditCorrelationId = auditCorrelationId;
        selectedModel.FallbackSupportsToolUse = supportsToolUse;
        selectedModel.FallbackSupportsStructuredOutput = supportsStructuredOutput;
        selectedModel.FallbackMaxContextTokens = maxContextTokens;
        selectedModel.FallbackCapabilityEvidenceSource = capabilityEvidenceSource;
        selectedModel.FallbackCapabilityScore = capabilityScore;
        selectedModel.FallbackCapabilityEvidenceKind = capabilityEvidenceKind;
        selectedModel.FallbackModelScoutDecisionReceipt = modelScoutDecisionReceipt;
    }

    private async Task<string?> GetFallbackRouteIssueAsync(
        LlmModelSelection selectedModel,
        string providerName,
        string modelName,
        bool requiresVision,
        LlmRouterOptions? options,
        bool carriesConfiguredEvidence)
    {
        var probe = new LlmModelSelection
        {
            Provider = ResolveRegisteredProviderName(providerName),
            Model = modelName,
            IsDefault = false,
            Context = selectedModel.Context,
            AuditCorrelationId = carriesConfiguredEvidence
                ? selectedModel.FallbackAuditCorrelationId ?? selectedModel.AuditCorrelationId
                : selectedModel.AuditCorrelationId,
            SupportsToolUse = carriesConfiguredEvidence ? selectedModel.FallbackSupportsToolUse : null,
            SupportsStructuredOutput = carriesConfiguredEvidence ? selectedModel.FallbackSupportsStructuredOutput : null,
            MaxContextTokens = carriesConfiguredEvidence ? selectedModel.FallbackMaxContextTokens : null,
            CapabilityEvidenceSource = carriesConfiguredEvidence ? selectedModel.FallbackCapabilityEvidenceSource : null,
        };
        var hardRequirementIssue = await GetRouteRequirementIssueAsync(
            probe,
            requiresVision,
            options,
            verifyAvailability: true).ConfigureAwait(false);
        if (hardRequirementIssue is not null)
        {
            return hardRequirementIssue;
        }

        // A translator alone is not sufficient: the exact user-configured
        // alternate must still carry affirmative model-level tool-use evidence
        // before it can execute. Automatic alternates use the stricter
        // proof-bound validator above.
        return options?.RequiresToolUse == true
            ? GetToolUseSelectionIssue(probe)
            : null;
    }

    private async Task ReportOperationalFailureAsync(
        string functionType,
        LlmModelSelection selectedModel,
        bool requiresVision,
        string reason,
        string summary)
    {
        if (_operationalAlerts is null) return;

        try
        {
            await _operationalAlerts.ReportAsync(new LlmOperationalAlert
            {
                FunctionType = functionType,
                Provider = selectedModel.Provider,
                Model = selectedModel.Model,
                Reason = reason,
                Summary = summary,
                RequiresVision = requiresVision,
                Interactivity = selectedModel.Context?.Interactivity ?? LlmInteractivity.Background,
                Severity = selectedModel.Context?.Interactivity == LlmInteractivity.UserWaiting
                    ? LlmOperationalAlertSeverity.Urgent
                    : LlmOperationalAlertSeverity.Error
            });
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogDebug(ex, "LLM operational alert sink failed for {FunctionType}", functionType);
        }
    }

    private static int EstimateTokenCount(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    private static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task TrackEmbeddingUsageAsync(string functionType, LlmModelSelection model, string text,
        LlmEmbeddingResponse response, long responseTimeMs, LlmRouterOptions? options)
    {
        try
        {
            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = model.Provider,
                Model = model.Model,
                InputTokens = response.TokenCount > 0 ? response.TokenCount : EstimateTokenCount(text),
                OutputTokens = 0, // Embeddings don't have output tokens
                TotalTokens = response.TokenCount > 0 ? response.TokenCount : EstimateTokenCount(text),
                EstimatedCost = response.EstimatedCost > 0 ? response.EstimatedCost : CalculateEmbeddingCost(model.Model, text),
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                UserId = options?.UserId,
                SessionId = options?.SessionId,
                PromptHash = ComputeHash(text),
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = response.Success,
                // Provider error bodies can echo credentials, bearer tokens and addresses.
                ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(response.ErrorMessage, 2_000),
                FallbackUsed = false
            };

            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed: false,
                succeeded: response.Success,
                responseTimeMs,
                record.TotalTokens,
                record.EstimatedCost,
                response.ErrorMessage).ConfigureAwait(false);
            if (HasMissionPhysicalBudget || options?.UsageCompletionObserver is not null)
            {
                var durable = await PersistUsageForCompletionObserverAsync(record, functionType, model.Provider, model.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(options, record, durable, false).ConfigureAwait(false);
            }
            else await _usageTracker.TrackUsageAsync(record);

            if (response.Success)
            {
                _logger.LogInformation("💰 Tracked embedding usage: {FunctionType} - {Provider}/{Model} - {TotalTokens} tokens - ${Cost:F6}",
                    functionType, model.Provider, model.Model, record.TotalTokens, record.EstimatedCost);
            }
            else
            {
                _logger.LogWarning("💰 Tracked failed embedding usage: {FunctionType} - {Provider}/{Model} - {TotalTokens} tokens - {Error}",
                    functionType, model.Provider, model.Model, record.TotalTokens, response.ErrorMessage);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track embedding usage for {FunctionType}", functionType);
        }
    }

    private async Task TrackBatchEmbeddingUsageAsync(string functionType, LlmModelSelection model, int textCount,
        int totalTokens, decimal totalCost, long responseTimeMs, bool wasSuccessful, string? errorMessage, LlmRouterOptions? options)
    {
        try
        {
            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = model.Provider,
                Model = model.Model,
                InputTokens = totalTokens,
                OutputTokens = 0,
                TotalTokens = totalTokens,
                EstimatedCost = totalCost,
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                UserId = options?.UserId,
                SessionId = options?.SessionId,
                PromptHash = $"batch_{textCount}_texts",
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = wasSuccessful,
                ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(errorMessage, 2_000),
                FallbackUsed = false
            };

            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed: false,
                succeeded: wasSuccessful,
                responseTimeMs,
                totalTokens,
                totalCost,
                errorMessage).ConfigureAwait(false);
            if (HasMissionPhysicalBudget || options?.UsageCompletionObserver is not null)
            {
                var durable = await PersistUsageForCompletionObserverAsync(record, functionType, model.Provider, model.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(options, record, durable, false).ConfigureAwait(false);
            }
            else await _usageTracker.TrackUsageAsync(record);

            if (wasSuccessful)
            {
                _logger.LogInformation("💰 Tracked batch embedding usage: {FunctionType} - {Provider}/{Model} - {TextCount} texts - {TotalTokens} tokens - ${Cost:F6}",
                    functionType, model.Provider, model.Model, textCount, totalTokens, totalCost);
            }
            else
            {
                _logger.LogWarning("💰 Tracked failed batch embedding usage: {FunctionType} - {Provider}/{Model} - {TextCount} texts - {TotalTokens} tokens - {Error}",
                    functionType, model.Provider, model.Model, textCount, totalTokens, errorMessage);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track batch embedding usage for {FunctionType}", functionType);
        }
    }

    private async Task TrackEmbeddingErrorAsync(
        string functionType,
        LlmModelSelection model,
        string promptHash,
        int estimatedTokens,
        long responseTimeMs,
        string errorMessage,
        LlmRouterOptions? options)
    {
        try
        {
            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = model.Provider,
                Model = model.Model,
                InputTokens = estimatedTokens,
                OutputTokens = 0,
                TotalTokens = estimatedTokens,
                EstimatedCost = 0m,
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                UserId = options?.UserId,
                SessionId = options?.SessionId,
                PromptHash = promptHash,
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = false,
                ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(errorMessage, 2_000),
                FallbackUsed = false,
            };

            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed: false,
                succeeded: false,
                responseTimeMs,
                estimatedTokens,
                estimatedCost: 0m,
                errorMessage).ConfigureAwait(false);
            if (HasMissionPhysicalBudget || options?.UsageCompletionObserver is not null)
            {
                var durable = await PersistUsageForCompletionObserverAsync(record, functionType, model.Provider, model.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(options, record, durable, false).ConfigureAwait(false);
            }
            else await _usageTracker.TrackUsageAsync(record).ConfigureAwait(false);
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track embedding error for {FunctionType}", functionType);
        }
    }

    private decimal CalculateEmbeddingCost(string model, string text)
    {
        var tokens = EstimateTokenCount(text);
        return _costService.CalculateEmbeddingCost(model, tokens);
    }
}
