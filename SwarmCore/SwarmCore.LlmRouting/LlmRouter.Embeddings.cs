using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Services;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Embedding-related methods for the LLM router
/// </summary>
public partial class LlmRouter
{
    public async Task<(bool IsAvailable, string Message)> IsEmbeddingAvailableAsync()
    {
        try
        {
            var pin = await ResolveEmbeddingPinAsync();
            var provider = _providerRegistry.GetProvider(pin.Provider);
            if (provider is not { SupportsEmbeddings: true })
            {
                return (false, $"{pin.Provider} is not registered as an embedding provider");
            }

            return (true, $"Embedding pinned to {pin.Provider}/{pin.Model}");
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(ex, "Error checking embedding availability");
            return (false, $"Error checking embedding availability: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve the embedding pin to use for the next ordinary query or write. During an active
    /// rebuild this remains the active/source pin; only the rebuild worker may request the exact
    /// target generation. This keeps live content searchable and makes cancel lossless. Falls back to
    /// <see cref="EmbeddingModelDefaults"/> when no pin service is registered — keeps tests
    /// with hand-built routers compiling without losing the consistency guarantee in production.
    /// </summary>
    private async Task<EmbeddingModelIdentity> ResolveEmbeddingPinAsync()
    {
        EmbeddingModelIdentity? activePin = null;
        if (_embeddingPin is not null)
        {
            // Load persisted provenance before consulting the rebuild's synchronous
            // hot-path view. This prevents the first request after startup from using
            // a constructor default in place of an upgraded profile's legacy pin.
            activePin = await _embeddingPin.GetAsync().ConfigureAwait(false);
        }

        if (_embeddingRebuild is not null)
        {
            // Hydrate a persisted in-flight rebuild before the synchronous hot-path
            // read; after the first call both operations are cache hits.
            await _embeddingRebuild.GetAsync().ConfigureAwait(false);
            var writePin = _embeddingRebuild.GetWritePin();
            if (writePin is not null) return writePin;
        }
        if (activePin is not null) return activePin;
        return new EmbeddingModelIdentity(
            EmbeddingModelDefaults.Provider,
            EmbeddingModelDefaults.Model,
            EmbeddingModelDefaults.Dimension,
            EmbeddingModelDefaults.ArtifactDigest);
    }

    public async Task<LlmEmbeddingResult> GenerateEmbeddingAsync(string functionType, string text, LlmRouterOptions? options = null)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];
        LlmModelSelection? selectedModel = null;
        var executionStarted = false;

        try
        {
            selectedModel = await SelectEmbeddingModelAsync(functionType, options);
            await AuditEmbeddingSelectionAsync(functionType, text, options, selectedModel);

            // Debug log the embedding request
            var textPreview = text.Length > 200 ? text[..200] + "..." : text;
            _debugLogger.LogEmbeddingRequest(new LlmDebugEmbeddingRequest(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                TextLength: text.Length,
                TextPreview: textPreview));

            _logger.LogDebug("🔢 LlmRouter: Using {Provider}/{Model} for embedding {FunctionType}",
                selectedModel.Provider, selectedModel.Model, functionType);

            var provider = _providerRegistry.GetProvider(selectedModel.Provider);
            if (provider == null)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} not registered in provider registry");
            }

            if (!provider.SupportsEmbeddings)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} does not support embeddings");
            }

            var executionSettings = BuildExecutionSettings(options, selectedModel);
            var requestCancellation =
                LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
            if (provider is ILlmProviderRuntimeReadiness runtimeReadiness)
            {
                var readiness = await runtimeReadiness.CheckRuntimeReadinessAsync(
                    selectedModel.Model,
                    requestCancellation).ConfigureAwait(false);
                if (!readiness.IsReady)
                {
                    _logger.LogDebug(
                        "Skipping embedding execution for {Provider}/{Model}: {Reason}",
                        selectedModel.Provider,
                        selectedModel.Model,
                        readiness.Message);
                    return new LlmEmbeddingResult
                    {
                        Success = false,
                        Provider = selectedModel.Provider,
                        Model = selectedModel.Model,
                        FunctionType = functionType,
                        Duration = stopwatch.Elapsed,
                        ErrorMessage = readiness.Message
                    };
                }
            }
            using var localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                executionSettings,
                workloadKind: LocalLlmWorkloadKind.Embedding);
            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                options,
                fallbackUsed: false, physicalSettings: executionSettings, embedding: true).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            stopwatch.Restart();
            executionStarted = true;
            var response = await provider.GenerateEmbeddingAsync(text, selectedModel.Model, executionSettings);
            stopwatch.Stop();

            // Debug log the embedding response
            _debugLogger.LogEmbeddingResponse(new LlmDebugEmbeddingResponse(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                Success: response.Success,
                VectorDimension: response.Vector?.Length,
                TokenCount: response.TokenCount,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: response.ErrorMessage));

            var result = new LlmEmbeddingResult
            {
                Success = response.Success,
                Vector = response.Vector,
                Provider = response.Provider,
                Model = response.Model,
                FunctionType = functionType,
                TokenCount = response.TokenCount,
                EstimatedCost = response.EstimatedCost,
                Duration = stopwatch.Elapsed,
                ErrorMessage = response.ErrorMessage
            };

            // The correlated execution receipt is part of the durable routing
            // contract, so do not return before it has been appended.
            await TrackEmbeddingUsageAsync(
                functionType,
                selectedModel,
                text,
                response,
                stopwatch.ElapsedMilliseconds,
                options).ConfigureAwait(false);

            return result;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (IsRequestCancellationRequested(options))
        {
            // Caller cancellation and local-runtime preemption are scheduling
            // outcomes, not model failures. Close a physical execution receipt
            // if one started, but do not emit error logs or usage evidence.
            stopwatch.Stop();
            if (executionStarted && selectedModel is not null)
            {
                await TrackCancellationAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    options).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error generating embedding for {FunctionType}", functionType);

            if (executionStarted && selectedModel is not null)
            {
                await TrackEmbeddingErrorAsync(
                    functionType,
                    selectedModel,
                    ComputeHash(text),
                    EstimateTokenCount(text),
                    stopwatch.ElapsedMilliseconds,
                    ex.Message,
                    options).ConfigureAwait(false);
            }

            // Debug log the error
            _debugLogger.LogEmbeddingResponse(new LlmDebugEmbeddingResponse(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel?.Provider ?? "unknown",
                Model: selectedModel?.Model ?? "unknown",
                Success: false,
                VectorDimension: null,
                TokenCount: null,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: ex.Message));

            return new LlmEmbeddingResult
            {
                Success = false,
                Provider = selectedModel?.Provider ?? "unknown",
                Model = selectedModel?.Model ?? "unknown",
                FunctionType = functionType,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<List<LlmEmbeddingResult>> GenerateEmbeddingsAsync(string functionType, IList<string> texts, LlmRouterOptions? options = null)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];
        LlmModelSelection? selectedModel = null;
        var executionStarted = false;

        try
        {
            selectedModel = await SelectEmbeddingModelAsync(functionType, options);
            var batchAuditMaterial = string.Join("|", texts.Select(ComputeHash));
            await AuditEmbeddingSelectionAsync(functionType, batchAuditMaterial, options, selectedModel);

            // Debug log batch embedding request with summary
            var totalChars = texts.Sum(t => t.Length);
            var firstTextPreview = texts.Count > 0 && texts[0].Length > 100
                ? texts[0][..100] + "..."
                : texts.FirstOrDefault() ?? "";
            _debugLogger.LogEmbeddingRequest(new LlmDebugEmbeddingRequest(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                TextLength: totalChars,
                TextPreview: $"Batch of {texts.Count} texts. First: {firstTextPreview}",
                BatchSize: texts.Count));

            _logger.LogDebug("🔢 LlmRouter: Using {Provider}/{Model} for batch embedding {FunctionType} ({Count} texts)",
                selectedModel.Provider, selectedModel.Model, functionType, texts.Count);

            var provider = _providerRegistry.GetProvider(selectedModel.Provider);
            if (provider == null)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} not registered in provider registry");
            }

            if (!provider.SupportsEmbeddings)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} does not support embeddings");
            }

            var executionSettings = BuildExecutionSettings(options, selectedModel);
            var requestCancellation =
                LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
            if (provider is ILlmProviderRuntimeReadiness runtimeReadiness)
            {
                var readiness = await runtimeReadiness.CheckRuntimeReadinessAsync(
                    selectedModel.Model,
                    requestCancellation).ConfigureAwait(false);
                if (!readiness.IsReady)
                {
                    _logger.LogDebug(
                        "Skipping batch embedding execution for {Provider}/{Model}: {Reason}",
                        selectedModel.Provider,
                        selectedModel.Model,
                        readiness.Message);
                    return texts.Select(_ => new LlmEmbeddingResult
                    {
                        Success = false,
                        Provider = selectedModel.Provider,
                        Model = selectedModel.Model,
                        FunctionType = functionType,
                        Duration = stopwatch.Elapsed,
                        ErrorMessage = readiness.Message
                    }).ToList();
                }
            }
            using var localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                executionSettings,
                workloadKind: LocalLlmWorkloadKind.Embedding);
            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                options,
                fallbackUsed: false, physicalSettings: executionSettings, inputCount: texts.Count, embedding: true).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            stopwatch.Restart();
            executionStarted = true;
            var responses = await provider.GenerateEmbeddingsAsync(texts, selectedModel.Model, executionSettings);
            stopwatch.Stop();

            // Debug log batch embedding response
            var successCount = responses.Count(r => r.Success);
            var totalTokens = responses.Sum(r => r.TokenCount);
            _debugLogger.LogEmbeddingResponse(new LlmDebugEmbeddingResponse(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                Success: successCount == responses.Count,
                VectorDimension: responses.FirstOrDefault()?.Vector?.Length,
                TokenCount: totalTokens,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: successCount < responses.Count ? $"{responses.Count - successCount} failed" : null));

            var results = responses.Select(response => new LlmEmbeddingResult
            {
                Success = response.Success,
                Vector = response.Vector,
                Provider = response.Provider,
                Model = response.Model,
                FunctionType = functionType,
                TokenCount = response.TokenCount,
                EstimatedCost = response.EstimatedCost,
                Duration = stopwatch.Elapsed,
                ErrorMessage = response.ErrorMessage
            }).ToList();

            // Persist the correlated execution receipt before returning.
            var totalCost = responses.Sum(r => r.EstimatedCost);
            await TrackBatchEmbeddingUsageAsync(
                functionType,
                selectedModel,
                texts.Count,
                totalTokens,
                totalCost,
                stopwatch.ElapsedMilliseconds,
                successCount == responses.Count,
                successCount < responses.Count ? $"{responses.Count - successCount} embedding response(s) failed" : null,
                options).ConfigureAwait(false);

            return results;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (IsRequestCancellationRequested(options))
        {
            // Match the single-embedding contract: caller cancellation and
            // local-runtime preemption propagate without blaming the route.
            stopwatch.Stop();
            if (executionStarted && selectedModel is not null)
            {
                await TrackCancellationAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    options).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error generating batch embeddings for {FunctionType}", functionType);

            if (executionStarted && selectedModel is not null)
            {
                await TrackEmbeddingErrorAsync(
                    functionType,
                    selectedModel,
                    $"batch_{texts.Count}_texts",
                    texts.Sum(EstimateTokenCount),
                    stopwatch.ElapsedMilliseconds,
                    ex.Message,
                    options).ConfigureAwait(false);
            }

            // Debug log the error
            _debugLogger.LogEmbeddingResponse(new LlmDebugEmbeddingResponse(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel?.Provider ?? "unknown",
                Model: selectedModel?.Model ?? "unknown",
                Success: false,
                VectorDimension: null,
                TokenCount: null,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: ex.Message));

            return texts.Select(_ => new LlmEmbeddingResult
            {
                Success = false,
                Provider = selectedModel?.Provider ?? "unknown",
                Model = selectedModel?.Model ?? "unknown",
                FunctionType = functionType,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            }).ToList();
        }
    }

    private async Task<LlmModelSelection> SelectEmbeddingModelAsync(string functionType, LlmRouterOptions? options)
    {
        // Embeddings deliberately bypass the dynamic advisor chain — switching embedding
        // models invalidates the existing semantic index, so the pin is the only thing
        // that decides routing. Ordinary calls must match the active pin. The dedicated
        // EmbeddingRebuild provenance may select an exact source/target generation from
        // persisted rebuild state; generic Policy provenance cannot forge that authority.
        // Urgency / budget /
        // time-of-day advisors don't get a vote.
        var pin = await ResolveEmbeddingPinAsync().ConfigureAwait(false);
        var context = FunctionTypeProfiles.Resolve(
            functionType,
            options?.Interactivity ?? LlmInteractivity.Unspecified,
            options?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
            options?.Urgency ?? LlmUrgency.Unspecified,
            options?.Workload ?? LlmWorkloadKind.Unspecified);
        var hasOverrideProvider = !string.IsNullOrWhiteSpace(options?.OverrideProvider);
        var hasOverrideModel = !string.IsNullOrWhiteSpace(options?.OverrideModel);
        if (hasOverrideProvider || hasOverrideModel)
        {
            if (!hasOverrideProvider || !hasOverrideModel)
            {
                throw new InvalidOperationException(
                    "Embedding overrides must include both provider and model. To change embeddings, start an embedding rebuild so the vector table and HNSW index migrate together.");
            }

            var selectedPin = pin;
            if (options!.OverrideProvenance == LlmOverrideProvenance.EmbeddingRebuild)
            {
                var rebuildState = _embeddingRebuild is null
                    ? null
                    : await _embeddingRebuild.GetAsync().ConfigureAwait(false);
                var requestedGenerationId = options.EmbeddingGenerationId.GetValueOrDefault();
                var isControlledTargetExecution = options.OverrideProvenance == LlmOverrideProvenance.EmbeddingRebuild
                    && requestedGenerationId != Guid.Empty
                    && rebuildState is { IsActive: true }
                    && requestedGenerationId == rebuildState.EffectiveTargetGenerationId
                    && rebuildState.TargetPin.MatchesFor(options.OverrideProvider, options.OverrideModel);
                var isControlledSourceExecution = options.OverrideProvenance == LlmOverrideProvenance.EmbeddingRebuild
                    && requestedGenerationId != Guid.Empty
                    && rebuildState is { Phase: EmbeddingRebuildPhase.Flipped }
                    && requestedGenerationId == rebuildState.EffectiveSourceGenerationId
                    && rebuildState.SourcePin.MatchesFor(options.OverrideProvider, options.OverrideModel);
                if (!isControlledTargetExecution && !isControlledSourceExecution)
                {
                    throw new InvalidOperationException(
                        $"Embedding override {options.OverrideProvider}/{options.OverrideModel} does not match the current write pin {pin.Provider}/{pin.Model}. " +
                        "Embedding model changes must go through the embedding rebuild flow so semantic search is rebuilt under one vector space.");
                }

                selectedPin = isControlledTargetExecution
                    ? rebuildState!.TargetPin
                    : rebuildState!.SourcePin;
            }
            else if (!pin.MatchesFor(options.OverrideProvider, options.OverrideModel))
            {
                throw new InvalidOperationException(
                    $"Embedding override {options.OverrideProvider}/{options.OverrideModel} does not match the current write pin {pin.Provider}/{pin.Model}. " +
                    "Embedding model changes must go through the embedding rebuild flow so semantic search is rebuilt under one vector space.");
            }

            return new LlmModelSelection
            {
                Provider = selectedPin.Provider,
                Model = selectedPin.Model,
                Context = context,
                ExecutionSettings = BuildEmbeddingIdentitySettings(selectedPin),
                EmbeddingIdentity = selectedPin,
                IsDefault = false,
                Reason = selectedPin == pin
                    ? $"Pinned embedding write model ({pin.Provider}/{pin.Model}, {pin.Dimension}d)"
                    : $"Pinned embedding search model ({selectedPin.Provider}/{selectedPin.Model}, {selectedPin.Dimension}d)"
            };
        }

        return new LlmModelSelection
        {
            Provider = pin.Provider,
            Model = pin.Model,
            Context = context,
            ExecutionSettings = BuildEmbeddingIdentitySettings(pin),
            EmbeddingIdentity = pin,
            IsDefault = true,
            Reason = $"Pinned embedding model ({pin.Provider}/{pin.Model}, {pin.Dimension}d)"
        };
    }

    private static Dictionary<string, object>? BuildEmbeddingIdentitySettings(EmbeddingModelIdentity identity)
        => string.IsNullOrWhiteSpace(identity.ArtifactDigest)
           && string.Equals(identity.Provider, EmbeddingModelDefaults.Provider, StringComparison.OrdinalIgnoreCase)
           && EmbeddingModelDefaults.IsSupportedMigrationModel(identity.Model)
            ? throw new InvalidOperationException(
                $"Embedding identity {identity.Provider}/{identity.Model} has no verified artifact digest. " +
                "It is retained for read-only migration compatibility and cannot generate query, write, or repair vectors. " +
                "Start a fresh rebuild with an approved installed artifact digest.")
            : string.IsNullOrWhiteSpace(identity.ArtifactDigest)
                ? null
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [LlmReservedExecutionSettings.ExpectedModelArtifactDigest] =
                    EmbeddingModelIdentity.NormalizeDigest(identity.ArtifactDigest)!,
            };

    private async Task AuditEmbeddingSelectionAsync(
        string functionType,
        string hashMaterial,
        LlmRouterOptions? options,
        LlmModelSelection selection)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        selection.AuditCorrelationId = correlationId;
        await AppendSelectionAuditAsync(
            functionType,
            hashMaterial,
            options,
            before: null,
            selection,
            correlationId,
            requiresVision: false,
            error: null,
            failure: null,
            new RoutingSelectionTrace()).ConfigureAwait(false);
    }
}
