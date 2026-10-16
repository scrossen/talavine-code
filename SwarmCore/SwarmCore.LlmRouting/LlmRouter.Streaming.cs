using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    public async Task<string> GenerateStreamingAsync(string functionType, string prompt, Func<string, Task> onChunk, LlmRouterOptions? options = null)
    {
        var response = await GenerateStreamingWithMetadataAsync(functionType, prompt, onChunk, options);
        return response.Content;
    }

    public async Task<LlmRouterResponse> GenerateStreamingWithMetadataAsync(string functionType, string prompt, Func<string, Task> onChunk, LlmRouterOptions? options = null)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var stopwatch = Stopwatch.StartNew();
        var cleanedPrompt = CleanHtmlFromPrompt(prompt);

        var selectedModel = await SelectOptimalModelInternalAsync(functionType, cleanedPrompt, options);
        var primaryExecutionStarted = false;
        var primaryChunkCallbackInvoked = false;
        var primaryTerminalReceiptWritten = false;

        async Task OnPrimaryChunkAsync(string chunk)
        {
            // Once the caller has observed any primary-stream callback, appending a
            // fallback response would corrupt the logical message. This also treats
            // a caller callback failure as terminal for the primary request instead
            // of retrying a different provider behind the caller's back.
            primaryChunkCallbackInvoked = true;
            try
            {
                await onChunk(chunk).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                throw new PartialStreamingOutputException(
                    $"The caller's streaming callback failed after output from {selectedModel.Provider}/{selectedModel.Model} was delivered; fallback was suppressed to avoid mixing responses.",
                    ex);
            }
        }

        try
        {
            _logger.LogInformation("🤖 LlmRouter: Streaming from {Provider}/{Model} for {FunctionType}",
                selectedModel.Provider, selectedModel.Model, functionType);

            var provider = _providerRegistry.GetProvider(selectedModel.Provider);
            if (provider == null)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} not registered in provider registry");
            }

            if (!provider.SupportsStreaming)
            {
                _logger.LogWarning("Provider {Provider} does not support streaming, falling back to non-streaming", selectedModel.Provider);
                var nonStreamingSettings = BuildExecutionSettings(options, selectedModel);
                using var nonStreamSlot = await AcquireLocalSlotAsync(
                    selectedModel.Provider,
                    selectedModel.Model,
                    selectedModel.Context,
                    nonStreamingSettings);
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selectedModel,
                    options,
                    fallbackUsed: false, physicalSettings: nonStreamingSettings).ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                stopwatch.Restart();
                primaryExecutionStarted = true;
                var nonStreamingResponse = await provider.GenerateResponseAsync(cleanedPrompt, selectedModel.Model, nonStreamingSettings);
                if (nonStreamingResponse is null)
                {
                    throw new InvalidOperationException($"Provider {selectedModel.Provider}/{selectedModel.Model} returned a null response");
                }
                stopwatch.Stop();

                if (!nonStreamingResponse.WasSuccessful)
                {
                    // Classify once; see the text path — the typed category
                    // travels alongside the collapsed legacy reason token.
                    var (reason, failureCategory) = ClassifyFailureReasonWithCategory(nonStreamingResponse);
                    await TrackErrorAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        nonStreamingResponse.ErrorMessage ?? $"streaming_provider_failure:{reason}")
                        .ConfigureAwait(false);
                    primaryTerminalReceiptWritten = true;
                    _logger.LogWarning("Non-streaming response from {Provider}/{Model} was unsuccessful: {Error}. Trying fallback streaming/non-streaming.",
                        selectedModel.Provider, selectedModel.Model, nonStreamingResponse.ErrorMessage);
                    stopwatch = Stopwatch.StartNew();
                    return await HandleStreamingFallbackAsync(
                        functionType, cleanedPrompt, selectedModel, onChunk, stopwatch, options, reason,
                        failureCategory);
                }

                // Same quality gate as the streamed branch: dead air is not success.
                var nonStreamingQualityIssue = ValidateResponseQuality(nonStreamingResponse, options, out var normalizedNonStreamingContent);
                if (nonStreamingQualityIssue is not null)
                {
                    await TrackQualityFailureAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        nonStreamingResponse,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        nonStreamingQualityIssue).ConfigureAwait(false);
                    primaryTerminalReceiptWritten = true;
                    if (IsTerminalCallerContractFailure(nonStreamingQualityIssue, options))
                    {
                        throw new LlmResponseContractException(functionType, selectedModel.Provider, selectedModel.Model);
                    }
                    stopwatch = Stopwatch.StartNew();
                    return await HandleStreamingFallbackAsync(
                        functionType, cleanedPrompt, selectedModel, onChunk, stopwatch, options, nonStreamingQualityIssue);
                }
                if (normalizedNonStreamingContent is not null)
                {
                    nonStreamingResponse.Content = normalizedNonStreamingContent;
                }

                await TrackUsageAsync(functionType, selectedModel, prompt, nonStreamingResponse, stopwatch.ElapsedMilliseconds, options);
                primaryTerminalReceiptWritten = true;
                await OnPrimaryChunkAsync(nonStreamingResponse.Content).ConfigureAwait(false);
                
                return new LlmRouterResponse
                {
                    Content = nonStreamingResponse.Content,
                    Provider = selectedModel.Provider,
                    Model = selectedModel.Model,
                    FunctionType = functionType,
                    InputTokens = nonStreamingResponse.InputTokens,
                    OutputTokens = nonStreamingResponse.OutputTokens,
                    EstimatedCost = nonStreamingResponse.EstimatedCost,
                    Duration = stopwatch.Elapsed,
                    UsedFallback = false
                };
            }

            var executionSettings = BuildExecutionSettings(options, selectedModel);
            using var localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                executionSettings);
            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                options,
                fallbackUsed: false, physicalSettings: executionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            stopwatch.Restart();
            primaryExecutionStarted = true;
            var response = await provider.GenerateStreamingResponseAsync(
                cleanedPrompt,
                selectedModel.Model,
                OnPrimaryChunkAsync,
                executionSettings);

            stopwatch.Stop();

            if (response is null)
            {
                throw new InvalidOperationException($"Streaming provider {selectedModel.Provider}/{selectedModel.Model} returned a null response");
            }

            if (!response.WasSuccessful)
            {
                // Classify once; see the text path — the typed category
                // travels alongside the collapsed legacy reason token.
                var (reason, failureCategory) = ClassifyFailureReasonWithCategory(response);
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    response.ErrorMessage ?? $"streaming_{reason}").ConfigureAwait(false);
                primaryTerminalReceiptWritten = true;
                if (primaryChunkCallbackInvoked)
                {
                    throw new PartialStreamingOutputException(
                        $"Primary streaming route {selectedModel.Provider}/{selectedModel.Model} failed after output was delivered; fallback was suppressed to avoid mixing responses.");
                }
                stopwatch = Stopwatch.StartNew();
                return await HandleStreamingFallbackAsync(
                    functionType,
                    cleanedPrompt,
                    selectedModel,
                    onChunk,
                    stopwatch,
                    options,
                    reason,
                    failureCategory).ConfigureAwait(false);
            }

            // Quality gate: a stream that "succeeded" with no usable content
            // (WasSuccessful=true, Content="") must not be tracked as success —
            // that materializes the preference and feeds the known-good
            // tracker. Fall back only when nothing reached the caller; once a
            // chunk has been observed, appending another route's answer would
            // corrupt the logical message, so the primary's output stands and
            // only the quality bookkeeping changes.
            var streamQualityIssue = ValidateResponseQuality(response, options, out var normalizedStreamContent);
            if (streamQualityIssue is not null)
            {
                _logger.LogWarning(
                    "Quality gate failed for streaming {Provider}/{Model} on {FunctionType}: {Reason}. Content length: {Length}. Chunks delivered: {Delivered}.",
                    selectedModel.Provider, selectedModel.Model, functionType, streamQualityIssue,
                    response.Content?.Length ?? 0, primaryChunkCallbackInvoked);
                await TrackQualityFailureAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    streamQualityIssue).ConfigureAwait(false);
                primaryTerminalReceiptWritten = true;

                if (IsTerminalCallerContractFailure(streamQualityIssue, options))
                {
                    throw new LlmResponseContractException(functionType, selectedModel.Provider, selectedModel.Model);
                }

                if (!primaryChunkCallbackInvoked)
                {
                    stopwatch = Stopwatch.StartNew();
                    return await HandleStreamingFallbackAsync(
                        functionType,
                        cleanedPrompt,
                        selectedModel,
                        onChunk,
                        stopwatch,
                        options,
                        streamQualityIssue).ConfigureAwait(false);
                }

                return new LlmRouterResponse
                {
                    // The caller has already observed at least one chunk, so a
                    // provider that omits aggregate content must still satisfy
                    // the router's non-null response contract without mixing in
                    // a fallback answer.
                    Content = response.Content ?? string.Empty,
                    Provider = selectedModel.Provider,
                    Model = selectedModel.Model,
                    FunctionType = functionType,
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens,
                    EstimatedCost = response.EstimatedCost,
                    Duration = stopwatch.Elapsed,
                    UsedFallback = false
                };
            }
            if (normalizedStreamContent is not null)
            {
                response.Content = normalizedStreamContent;
            }

            // Commit execution evidence before returning the streamed response.
            await TrackUsageAsync(functionType, selectedModel, prompt, response, stopwatch.ElapsedMilliseconds, options);
            primaryTerminalReceiptWritten = true;

            return new LlmRouterResponse
            {
                Content = response.Content,
                Provider = selectedModel.Provider,
                Model = selectedModel.Model,
                FunctionType = functionType,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                EstimatedCost = response.EstimatedCost,
                Duration = stopwatch.Elapsed,
                UsedFallback = false
            };
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (FallbackDisabledException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (LlmFailoverException)
        {
            throw;
        }
        catch (PartialStreamingOutputException)
        {
            throw;
        }
        catch (Exception)
            when (IsRequestCancellationRequested(options))
        {
            // Caller cancellation is a scheduling outcome, not route evidence:
            // no fallback, no cooldown, no negative known-good evidence. A
            // provider call that already started still needs a terminal receipt.
            stopwatch.Stop();
            if (primaryExecutionStarted && !primaryTerminalReceiptWritten)
            {
                await TrackCancellationAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    options).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException && (IsRateLimitError(ex) && CanAttemptRuntimeFallback(selectedModel, options)))
        {
            // Lock the rate-limited provider out of rotation for the next few
            // hours so subsequent calls don't waste a network roundtrip
            // re-discovering the same wall. The current call still falls
            // back via the configured fallback chain below.
            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    ex.Message).ConfigureAwait(false);
            }
            await RecordCooldownIfRateLimitAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            if (primaryChunkCallbackInvoked)
            {
                throw new PartialStreamingOutputException(
                    $"Primary streaming route {selectedModel.Provider}/{selectedModel.Model} was rate-limited after output was delivered; fallback was suppressed to avoid mixing responses.",
                    ex);
            }
            // The wire-compatible reason stays "rate_limit"; the category
            // resolved from the actual message travels alongside so a thrown
            // quota exhaustion cannot surface mislabeled as RateLimit.
            return await HandleStreamingFallbackAsync(
                functionType, cleanedPrompt, selectedModel, onChunk, stopwatch, options, "rate_limit",
                Services.LlmErrorPolicy.Classify(ex));
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException && (IsModelNotFoundError(ex)))
        {
            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    ex.Message).ConfigureAwait(false);
            }
            if (primaryChunkCallbackInvoked)
            {
                throw new PartialStreamingOutputException(
                    $"Primary streaming route {selectedModel.Provider}/{selectedModel.Model} failed after output was delivered; fallback was suppressed to avoid mixing responses.",
                    ex);
            }
            return await HandleStreamingFallbackAsync(functionType, cleanedPrompt, selectedModel, onChunk, stopwatch, options, "model_not_found");
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException && (IsTimeoutOrTransientError(ex) && CanAttemptRuntimeFallback(selectedModel, options)))
        {
            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    ex.Message).ConfigureAwait(false);
            }
            if (primaryChunkCallbackInvoked)
            {
                throw new PartialStreamingOutputException(
                    $"Primary streaming route {selectedModel.Provider}/{selectedModel.Model} timed out after output was delivered; fallback was suppressed to avoid mixing responses.",
                    ex);
            }
            // See the text path: a thrown adverse HTTP status surfaces its real
            // category; unclassifiable transients stay typed Timeout.
            var thrownTransientCategory = Services.LlmErrorPolicy.Classify(ex);
            return await HandleStreamingFallbackAsync(
                functionType, cleanedPrompt, selectedModel, onChunk, stopwatch, options, "timeout",
                thrownTransientCategory is LlmFailureCategory.ProviderError
                    ? LlmFailureCategory.Timeout
                    : thrownTransientCategory);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            if (CanAttemptRuntimeFallback(selectedModel, options))
            {
                _logger.LogWarning(ex, "Primary streaming {Provider}/{Model} failed for {FunctionType}; trying fallback",
                    selectedModel.Provider, selectedModel.Model, functionType);
                if (primaryExecutionStarted)
                {
                    await TrackErrorAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        ex.Message).ConfigureAwait(false);
                }

                if (primaryChunkCallbackInvoked)
                {
                    throw new PartialStreamingOutputException(
                        $"Primary streaming route {selectedModel.Provider}/{selectedModel.Model} failed after output was delivered; fallback was suppressed to avoid mixing responses.",
                        ex);
                }

                try
                {
                    return await HandleStreamingFallbackAsync(functionType, cleanedPrompt, selectedModel, onChunk, stopwatch, options, "primary_exception");
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
                catch (Exception fbEx) when (fbEx is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                {
                    stopwatch.Stop();
                    var fallbackSelection = await BuildFallbackFailureSelectionAsync(selectedModel, requiresVision: false);

                    // Both raw messages are in scope: classify them so this
                    // failover carries typed categories instead of forcing
                    // consumers back onto prose parsing.
                    throw new LlmFailoverException(
                        functionType: functionType,
                        promptId: "streaming",
                        primary: selectedModel,
                        fallback: fallbackSelection,
                        primaryError: ex.Message,
                        fallbackError: fbEx.Message,
                        primaryException: ex,
                        fallbackException: fbEx,
                        primaryCategory: Services.LlmErrorPolicy.Classify(ex),
                        fallbackCategory: Services.LlmErrorPolicy.Classify(fbEx));
                }
            }

            stopwatch.Stop();
            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(functionType, selectedModel, prompt, stopwatch.ElapsedMilliseconds, options, ex.Message);
            }
            // See the text path: a pinned/no-fallback route that was rate-limited
            // or timed out still records its provider cooldown.
            if (IsRateLimitError(ex))
            {
                await RecordCooldownIfRateLimitAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            }
            else if (IsTimeoutOrTransientError(ex))
            {
                await RecordCooldownIfTimeoutOrTransientAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            }
            throw;
        }
    }

    private sealed class PartialStreamingOutputException : InvalidOperationException
    {
        public PartialStreamingOutputException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    // primaryCategory: see HandleFallbackAsync — the classification-time
    // category riding alongside the wire-compatible legacy reason.
    private async Task<LlmRouterResponse> HandleStreamingFallbackAsync(string functionType, string prompt, LlmModelSelection selectedModel,
        Func<string, Task> onChunk, Stopwatch stopwatch, LlmRouterOptions? options, string reason,
        LlmFailureCategory? primaryCategory = null)
    {
        LlmModelSelection? attemptedFallback = null;
        try
        {
        var survivalExecution = await TryExecuteStreamingSurvivalSetAsync(
            functionType,
            prompt,
            selectedModel,
            onChunk,
            options,
            reason,
            primaryCategory).ConfigureAwait(false);
        if (survivalExecution is not null)
        {
            return survivalExecution.Response;
        }

        if (options?.DisableFallback == true)
        {
            var message = $"Streaming fallback disabled for {selectedModel.Provider}/{selectedModel.Model} on {functionType}; selected route failed after same-route recovery (reason={reason}).";
            await ReportOperationalFailureAsync(functionType, selectedModel, requiresVision: false, reason, message);
            throw new FallbackDisabledException(message);
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
            Reason = $"Streaming fallback due to {reason}",
            Context = selectedModel.Context,
            AuditCorrelationId = selectedModel.AuditCorrelationId,
        }, promoteConfiguredFallback: true);

        _logger.LogWarning("Using fallback {FallbackProvider}/{FallbackModel} for streaming due to {Reason}",
            fallbackProvider, fallbackModel, reason);

        var provider = _providerRegistry.GetProvider(fallbackProvider);
        if (provider == null)
        {
            throw new InvalidOperationException($"Fallback provider {fallbackProvider} not registered");
        }

        var streamFallbackSelection = attemptedFallback;
        var executionSettings = BuildExecutionSettings(options, streamFallbackSelection);
        using var fallbackSlot = await AcquireLocalSlotAsync(
            fallbackProvider,
            fallbackModel,
            selectedModel.Context,
            executionSettings);
        LlmResponse? response;
        var fallbackUsesStreaming = provider.SupportsStreaming;
        var fallbackChunkDelivered = false;
        async Task OnFallbackChunkAsync(string chunk)
        {
            fallbackChunkDelivered = true;
            await onChunk(chunk).ConfigureAwait(false);
        }
        try
        {
            await AppendFallbackSelectionAuditAsync(
                functionType,
                prompt,
                selectedModel,
                streamFallbackSelection,
                options,
                reason).ConfigureAwait(false);
            await AppendExecutionStartedAuditAsync(
                functionType,
                streamFallbackSelection,
                options,
                fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            stopwatch.Restart();
            if (fallbackUsesStreaming)
            {
                response = await provider.GenerateStreamingResponseAsync(prompt, fallbackModel, OnFallbackChunkAsync, executionSettings);
                if (response is null)
                {
                    throw new InvalidOperationException($"Streaming fallback provider {fallbackProvider}/{fallbackModel} returned a null response");
                }
            }
            else
            {
                response = await provider.GenerateResponseAsync(prompt, fallbackModel, executionSettings);
                if (response is null)
                {
                    throw new InvalidOperationException($"Streaming fallback provider {fallbackProvider}/{fallbackModel} returned a null response");
                }
            }

            stopwatch.Stop();
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            stopwatch.Stop();
            await TrackErrorAsync(
                functionType,
                streamFallbackSelection,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                ex.Message,
                fallbackUsed: true).ConfigureAwait(false);
            throw new FallbackExecutionException(
                $"Streaming fallback provider {fallbackProvider}/{fallbackModel} threw during execution.",
                ex);
        }

        if (!response.WasSuccessful)
        {
            await TrackErrorAsync(
                functionType,
                streamFallbackSelection,
                prompt,
                stopwatch.ElapsedMilliseconds,
                options,
                response.ErrorMessage ?? "streaming_fallback_provider_failure",
                fallbackUsed: true).ConfigureAwait(false);
            await RecordFailedResponseCooldownAsync(
                fallbackProvider,
                ClassifyFailureReasonWithCategory(response).Reason,
                response.ErrorMessage,
                response.HttpStatusCode, response.FailureCategory).ConfigureAwait(false);
            throw new FallbackExecutionException(
                $"Streaming fallback provider {fallbackProvider}/{fallbackModel} returned an unsuccessful response: {response.ErrorMessage ?? "unknown error"}");
        }

        var fallbackSelection = CarryRoutePolicy(selectedModel, new LlmModelSelection
        {
            Provider = fallbackProvider,
            Model = fallbackModel,
            IsDefault = false,
            Reason = $"Fallback due to {reason}",
            Context = selectedModel.Context,
            AuditCorrelationId = selectedModel.AuditCorrelationId
        }, promoteConfiguredFallback: true);

        // Quality gate on the fallback too: an empty "successful" stream is a
        // quality failure, never success evidence. If the fallback already
        // delivered chunks the caller keeps them; otherwise the request fails.
        var fallbackQualityIssue = ValidateResponseQuality(response, options, out var normalizedFallbackContent);
        if (fallbackQualityIssue is not null)
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
            if (!fallbackChunkDelivered)
            {
                throw new FallbackExecutionException(
                    $"Streaming fallback provider {fallbackProvider}/{fallbackModel} returned an unusable response ({fallbackQualityIssue}).");
            }
        }
        else
        {
            if (normalizedFallbackContent is not null)
            {
                response.Content = normalizedFallbackContent;
            }
            await TrackUsageAsync(functionType, fallbackSelection, prompt, response, stopwatch.ElapsedMilliseconds, options, fallbackUsed: true);
        }

        if (!fallbackUsesStreaming)
        {
            await onChunk(response.Content);
        }

        return new LlmRouterResponse
        {
            Content = response.Content,
            Provider = fallbackProvider,
            Model = fallbackModel,
            FunctionType = functionType,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            EstimatedCost = response.EstimatedCost,
            Duration = stopwatch.Elapsed,
            UsedFallback = true,
            FallbackReason = reason
        };
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
        catch (PartialStreamingOutputException)
        {
            // A delivery callback may already have exposed committed output.
            // Preserve the stop signal instead of relabeling it as route failure.
            throw;
        }
        catch (FallbackExecutionException ex)
        {
            throw CreateFailoverException(
                functionType,
                "streaming",
                selectedModel,
                attemptedFallback,
                reason,
                ex,
                primaryCategory);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            var wrapped = new FallbackExecutionException(
                $"Streaming fallback setup or execution failed for {functionType} after {reason}.", ex);
            throw CreateFailoverException(
                functionType,
                "streaming",
                selectedModel,
                attemptedFallback,
                reason,
                wrapped,
                primaryCategory);
        }
    }
}
