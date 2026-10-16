using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;
using SwarmCore.LlmRouting.ToolUse;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Native tool-use surface of the router. Separate partial file to keep the
/// large core router tidy. The router resolves a provider/model pair via the
/// same function-preference machinery used by other function types, then
/// delegates to the matching <see cref="IToolUseProvider"/>.
/// </summary>
public partial class LlmRouter
{
    private static void ValidateNativeToolRoute(ChatToolUseRequest request, string provider, string model)
    {
        if (HasMissionPhysicalBudget && request.SteeringSession is not null && OpenAIResponsesContract.IsSelected(provider, model))
            throw new SwarmCore.Abstractions.Budgets.MissionBudgetExceededException("Native Responses steering requires reservation for each internal response before dispatch; use a bounded HTTP/SSE invocation.");
        if (request.SteeringSession?.Transport is not null && !OpenAIResponsesContract.IsSelected(provider, model))
            throw new InvalidOperationException("A steering continuation cannot cross provider/model routes; reconcile its durable inputs first.");
        if (request.IsContextCompaction && !OpenAIResponsesContract.IsSelected(provider, model))
            throw new NotSupportedException("This selected route has no reviewed native compaction adapter.");
        foreach (var compact in request.Messages.SelectMany(m => m.Parts).OfType<ResponsesCompactionPart>())
            if (!compact.Binding.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) || compact.Binding.Model != model
                || compact.Binding.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new ProviderContextReconstructionRequiredException("Opaque context route or lifetime changed.");
        foreach (var output in request.Messages.SelectMany(m => m.Parts).OfType<ResponsesOutputPart>())
            if (!output.Binding.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) || output.Binding.Model != model
                || output.Binding.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new ProviderContextReconstructionRequiredException("Retained response history route or lifetime changed.");
        foreach (var update in request.Messages.SelectMany(m => m.Parts).OfType<ResponsesConfigurationUpdatePart>())
            if (!update.Binding.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) || update.Binding.Model != model
                || update.Binding.ExpiresAtUtc <= DateTimeOffset.UtcNow || !OpenAIResponsesContract.IsSelected(provider, model))
                throw new ProviderContextReconstructionRequiredException("Reasoning configuration route or lifetime changed.");
        if (request.ToolExecutionProtocol == ToolExecutionProtocol.Conventional) return;
        if (request.ToolExecutionProtocol != ToolExecutionProtocol.ResponsesNativeAsyncSingleAgent
            || request.Options?.DisableFallback != true
            || !OpenAIResponsesContract.IsSelected(provider, model)
            || !provider.Equals(request.Options.OverrideProvider, StringComparison.OrdinalIgnoreCase)
            || !model.Equals(request.Options.OverrideModel, StringComparison.Ordinal))
            throw new InvalidOperationException("Native async continuation requires the exact reviewed Responses route with fallback disabled.");
    }
    private IToolUseProviderRegistry? _toolUseRegistry;

    /// <summary>
    /// Sets the tool-use provider registry. Wired up in a separate setter
    /// rather than the constructor so the existing LlmRouter constructor
    /// signature stays stable. DI registration is responsible for calling
    /// this during application startup.
    /// </summary>
    internal void AttachToolUseRegistry(IToolUseProviderRegistry registry)
    {
        _toolUseRegistry = registry;
    }

    private IToolUseCapabilityProver? _toolUseProver;

    /// <summary>
    /// Supplies the on-demand tool-use prover. Optional and attached the same
    /// way as the registry above: when absent the router behaves exactly as
    /// before (unknown capability is refused), so hosts and tests that do not
    /// register one are unaffected.
    /// </summary>
    internal void AttachToolUseProver(IToolUseCapabilityProver prover)
    {
        _toolUseProver = prover;
    }

    public async Task<ChatToolUseResponse> ChatWithToolsAsync(
        string functionType,
        ChatToolUseRequest request,
        CancellationToken ct = default)
    {
        await using var physicalInvocation = new PhysicalInvocation();

        var suppliedCancellation = request.Options?.RequestCancellationToken ?? CancellationToken.None;
        using var routingCancellation = ct.CanBeCanceled && suppliedCancellation.CanBeCanceled && ct != suppliedCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, suppliedCancellation) : null;
        ct = routingCancellation?.Token ?? (ct.CanBeCanceled ? ct : suppliedCancellation);
        var routingOptions = CloneOrCreate(request.Options);
        routingOptions.RequestCancellationToken = ct;
        request = request with { Options = routingOptions };
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var promptSummary = BuildToolUsePromptSummary(request);
        ct.ThrowIfCancellationRequested();
        var selectedModel = await ResolveModelSelectionAsync(functionType, request, promptSummary)
            .WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        selectedModel = await EnsureToolUseSelectionOrFallbackAsync(functionType, selectedModel, request.Options)
            .WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ValidateNativeToolRoute(request, selectedModel.Provider, selectedModel.Model);
        var translator = ResolveTranslator(selectedModel.Provider);

        // Inject the resolved provider/model into options so translators
        // don't have to re-resolve.
        var mutatedOptions = CloneOrCreate(request.Options);
        mutatedOptions.OverrideProvider = selectedModel.Provider;
        mutatedOptions.OverrideModel = selectedModel.Model;
        mutatedOptions.OverrideProvenance = LlmOverrideProvenance.Policy;
        mutatedOptions.RequiresToolUse = true;
        mutatedOptions.ExecutionSettings = MergeToolUseExecutionSettings(
            selectedModel,
            request.Options?.ExecutionSettings,
            ct);

        var effective = request with { Options = mutatedOptions };
        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];
        ChatToolUseResponse response;
        IDisposable? localSlot = null;
        var primaryExecutionStarted = false;
        try
        {
            localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                mutatedOptions.ExecutionSettings,
                ct: ct);
            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                mutatedOptions,
                fallbackUsed: false, physicalSettings: mutatedOptions.ExecutionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(mutatedOptions).ConfigureAwait(false);
            stopwatch.Restart();
            primaryExecutionStarted = true;
            response = await translator.ChatAsync(effective, ct);
            if (GetToolUseReplyIssue(request, response) is { } replyIssue)
            {
                throw new MalformedToolUseReplyException(replyIssue);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            localSlot?.Dispose();
            localSlot = null;
            if (primaryExecutionStarted)
            {
                await TrackToolUseErrorAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    mutatedOptions,
                    "Tool-use provider call was cancelled.").ConfigureAwait(false);
            }
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            stopwatch.Stop();
            localSlot?.Dispose();
            localSlot = null;
            if (primaryExecutionStarted)
            {
                await TrackToolUseErrorAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    mutatedOptions,
                    ex.Message);
            }
            // If the provider just said "you're rate-limited / usage cap
            // hit," record a cooldown before rethrowing so the agent's next
            // turn (or the route advisor's next selection pass) skips this
            // provider until the cooldown clears. Subscription CLIs like
            // Claude Code surface "usage limit" here; cloud APIs surface 429.
            if (IsRateLimitError(ex))
            {
                await RecordCooldownIfRateLimitAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            }
            else if (IsTimeoutOrTransientError(ex))
            {
                await RecordCooldownIfTimeoutOrTransientAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            }
            var fallbackResponse = await TryToolUseFallbackAsync(
                functionType,
                request,
                selectedModel,
                ex,
                promptSummary,
                requestId,
                ct);
            if (fallbackResponse is not null)
            {
                return fallbackResponse;
            }
            throw;
        }
        finally
        {
            localSlot?.Dispose();
        }
        stopwatch.Stop();

        RecordObservedToolUseCapability(selectedModel, response);
        await TrackToolUseUsageAsync(functionType, selectedModel, response, stopwatch.ElapsedMilliseconds, mutatedOptions, promptSummary);
        if (IsSuccessfulToolUseCompletion(response))
        {
            await MaterializeFunctionPreferenceIfMissingAsync(
                    functionType,
                    selectedModel,
                    request.Options,
                    requiresVision: request.Options?.RequiresVision == true)
                .ConfigureAwait(false);
        }
        return response;
    }

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamChatWithToolsAsync(
        string functionType,
        ChatToolUseRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var physicalInvocation = new PhysicalInvocation();

        var suppliedCancellation = request.Options?.RequestCancellationToken ?? CancellationToken.None;
        using var routingCancellation = ct.CanBeCanceled && suppliedCancellation.CanBeCanceled && ct != suppliedCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, suppliedCancellation) : null;
        ct = routingCancellation?.Token ?? (ct.CanBeCanceled ? ct : suppliedCancellation);
        var routingOptions = CloneOrCreate(request.Options);
        routingOptions.RequestCancellationToken = ct;
        request = request with { Options = routingOptions };
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var promptSummary = BuildToolUsePromptSummary(request);
        ct.ThrowIfCancellationRequested();
        var selectedModel = await ResolveModelSelectionAsync(functionType, request, promptSummary)
            .WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        selectedModel = await EnsureToolUseSelectionOrFallbackAsync(functionType, selectedModel, request.Options)
            .WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ValidateNativeToolRoute(request, selectedModel.Provider, selectedModel.Model);
        var translator = ResolveTranslator(selectedModel.Provider);

        var mutatedOptions = CloneOrCreate(request.Options);
        mutatedOptions.OverrideProvider = selectedModel.Provider;
        mutatedOptions.OverrideModel = selectedModel.Model;
        mutatedOptions.OverrideProvenance = LlmOverrideProvenance.Policy;
        mutatedOptions.RequiresToolUse = true;
        mutatedOptions.ExecutionSettings = MergeToolUseExecutionSettings(
            selectedModel,
            request.Options?.ExecutionSettings,
            ct);

        var effective = request with { Options = mutatedOptions };
        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];
        Exception? primaryException = null;
        // Treat the primary stream as transactional until its route is committed.
        // Reasoning and tool-call framing are not independently useful, and
        // forwarding them before a successful terminal event would let a failed
        // primary's events become attached to a fallback response. Keep them in
        // order here and either commit them with the primary or discard them
        // before fallback. A visible text delta commits the primary immediately,
        // because switching routes after visible answer text would splice two
        // answers together. With fallback disabled, the primary is already the
        // only possible route and can retain normal low-latency streaming.
        var bufferedPrimaryEvents = new List<ToolUseStreamEvent>();
        var primaryStreamCommitted = request.Options?.DisableFallback == true;
        var primaryExecutionStarted = false;
        var primaryTerminalReceiptWritten = false;
        IDisposable? localSlot = null;

        try
        {
            localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                mutatedOptions.ExecutionSettings,
                ct: ct);

            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                mutatedOptions,
                fallbackUsed: false, physicalSettings: mutatedOptions.ExecutionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(mutatedOptions).ConfigureAwait(false);
            stopwatch.Restart();
            primaryExecutionStarted = true;

            // Manual enumeration so we can wrap MoveNextAsync in a try/catch and
            // record a cooldown when the provider rate-limits. The yield-return
            // form (`await foreach { yield return ev; }`) doesn't allow try/catch
            // around the yield, so we drive the enumerator by hand.
            await using var enumerator = translator.StreamAsync(effective, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                ToolUseStreamEvent ev;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    ev = enumerator.Current;
                }
                catch (RoutingAuditPersistenceException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                {
                    stopwatch.Stop();
                    await TrackToolUseErrorAsync(functionType, selectedModel, stopwatch.ElapsedMilliseconds, mutatedOptions, ex.Message);
                    primaryTerminalReceiptWritten = true;
                    if (IsRateLimitError(ex))
                    {
                        await RecordCooldownIfRateLimitAsync(selectedModel.Provider, ex).ConfigureAwait(false);
                    }
                    else if (IsTimeoutOrTransientError(ex))
                    {
                        await RecordCooldownIfTimeoutOrTransientAsync(selectedModel.Provider, ex).ConfigureAwait(false);
                    }
                    primaryException = ex;
                    break;
                }

                // The final MessageStoppedEvent carries the complete response
                // (including token usage, when the provider reports it). Capture
                // it for usage tracking before committing buffered events.
                if (ev is MessageStoppedEvent stopped)
                {
                    stopwatch.Stop();
                    if (GetToolUseReplyIssue(request, stopped.Response) is { } replyIssue)
                    {
                        await TrackToolUseErrorAsync(
                            functionType,
                            selectedModel,
                            stopwatch.ElapsedMilliseconds,
                            mutatedOptions,
                            replyIssue).ConfigureAwait(false);
                        primaryTerminalReceiptWritten = true;
                        primaryException = new MalformedToolUseReplyException(replyIssue);
                        break;
                    }

                    await TrackToolUseUsageAsync(functionType, selectedModel, stopped.Response, stopwatch.ElapsedMilliseconds, mutatedOptions, promptSummary);
                    primaryTerminalReceiptWritten = true;
                    if (IsSuccessfulToolUseCompletion(stopped.Response))
                    {
                        await MaterializeFunctionPreferenceIfMissingAsync(
                                functionType,
                                selectedModel,
                                request.Options,
                                requiresVision: request.Options?.RequiresVision == true)
                            .ConfigureAwait(false);
                    }

                    if (!primaryStreamCommitted)
                    {
                        primaryStreamCommitted = true;
                        foreach (var bufferedEvent in bufferedPrimaryEvents)
                        {
                            yield return bufferedEvent;
                        }
                        bufferedPrimaryEvents.Clear();
                    }

                    yield return ev;
                    continue;
                }

                if (!primaryStreamCommitted
                    && (request.RequireToolCall
                        || ev is not TextDeltaEvent textDelta
                        || string.IsNullOrWhiteSpace(textDelta.Delta)))
                {
                    bufferedPrimaryEvents.Add(ev);
                    continue;
                }

                if (!primaryStreamCommitted)
                {
                    primaryStreamCommitted = true;
                    foreach (var bufferedEvent in bufferedPrimaryEvents)
                    {
                        yield return bufferedEvent;
                    }
                    bufferedPrimaryEvents.Clear();
                }

                yield return ev;
            }

        if (primaryException is null && !primaryTerminalReceiptWritten)
        {
            stopwatch.Stop();
            var protocolError = new InvalidOperationException(
                $"Tool-use provider {selectedModel.Provider}/{selectedModel.Model} completed its stream without a MessageStoppedEvent.");
            if (primaryExecutionStarted)
            {
                await TrackToolUseErrorAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    mutatedOptions,
                    protocolError.Message);
                primaryTerminalReceiptWritten = true;
            }
            primaryException = protocolError;
        }

        if (primaryException is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (primaryStreamCommitted || request.Options?.DisableFallback == true
                || IsContextOverflowFailure(primaryException))
            {
                throw primaryException;
            }

            // The primary never committed. Drop its buffered reasoning/protocol
            // before starting another route so neither the consumer nor this
            // iterator retains failed-route content alongside fallback output.
            bufferedPrimaryEvents.Clear();

            // The streaming fallback owns a fresh local-runtime admission. Release
            // the primary lease before entering it or a local -> local fallback
            // waits forever on the one slot held by this same async iterator.
            localSlot.Dispose();
            localSlot = null;
            await foreach (var fallbackEvent in StreamToolUseFallbackAsync(
                               functionType,
                               request,
                               selectedModel,
                               primaryException,
                               promptSummary,
                               requestId,
                               ct))
            {
                yield return fallbackEvent;
            }
        }
        }
        finally
        {
            localSlot?.Dispose();
            if (primaryExecutionStarted && !primaryTerminalReceiptWritten)
            {
                stopwatch.Stop();
                await TrackToolUseErrorAsync(
                    functionType,
                    selectedModel,
                    stopwatch.ElapsedMilliseconds,
                    mutatedOptions,
                    "Tool-use stream was abandoned by its consumer before a terminal MessageStoppedEvent was received.")
                    .ConfigureAwait(false);
                primaryTerminalReceiptWritten = true;
            }
        }
    }

    private async Task<ChatToolUseResponse?> TryToolUseFallbackAsync(
        string functionType,
        ChatToolUseRequest request,
        LlmModelSelection selectedModel,
        Exception primaryException,
        string promptSummary,
        string requestId,
        CancellationToken ct)
    {
        if (request.Options?.DisableFallback == true || IsContextOverflowFailure(primaryException))
        {
            return null;
        }

        var survivalExecution = await TryExecuteToolUseSurvivalSetAsync(
                functionType,
                request,
                selectedModel,
                primaryException,
                promptSummary,
                requestId,
                ct)
            .ConfigureAwait(false);
        if (survivalExecution is not null)
        {
            return survivalExecution.Response;
        }

        ct.ThrowIfCancellationRequested();
        // Classify once and carry BOTH the wire-compatible reason token and the
        // typed category into every failover thrown from this flow: the token
        // collapses distinct categories (auth/content/invalid/malformed all
        // travel as "provider_error", quota as "rate_limit"), and re-deriving
        // from the token alone made auth-shaped tool-use failures eligible for
        // mid-turn retry as generic provider errors.
        var (reason, primaryCategory) = ClassifyFailureReasonWithCategory(primaryException);
        LlmModelSelection fallbackSelection;
        try
        {
            fallbackSelection = await BuildToolUseFallbackSelectionAsync(
                functionType,
                selectedModel,
                promptSummary,
                reason,
                request.Options?.AllowAnyProviderFallback == true,
                request.Options)
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception setupException) when (setupException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                attemptedFallback: null,
                request.Options,
                reason,
                setupException,
                primaryCategory);
        }

        var fallbackOptions = CloneOrCreate(request.Options);
        fallbackOptions.OverrideProvider = fallbackSelection.Provider;
        fallbackOptions.OverrideModel = fallbackSelection.Model;
        fallbackOptions.OverrideProvenance = LlmOverrideProvenance.Fallback;
        fallbackOptions.RequiresToolUse = true;
        fallbackOptions.ExecutionSettings = MergeToolUseExecutionSettings(
            fallbackSelection,
            request.Options?.ExecutionSettings,
            ct);

        var fallbackRequest = request with { Options = fallbackOptions };
        IToolUseProvider fallbackTranslator;
        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureToolUseSelectionSupported(fallbackSelection);
            fallbackTranslator = ResolveTranslator(fallbackSelection.Provider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception setupException) when (setupException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                fallbackSelection,
                request.Options,
                reason,
                setupException,
                primaryCategory);
        }
        var fallbackStopwatch = Stopwatch.StartNew();
        var fallbackExecutionStarted = false;

        try
        {
            ct.ThrowIfCancellationRequested();
            await AppendFallbackSelectionAuditAsync(
                functionType,
                promptSummary,
                selectedModel,
                fallbackSelection,
                request.Options,
                reason,
                requiresToolUse: true).ConfigureAwait(false);
            _logger.LogWarning(
                primaryException,
                "Tool-use primary {Provider}/{Model} failed for {FunctionType}; trying fallback {FallbackProvider}/{FallbackModel}",
                selectedModel.Provider,
                selectedModel.Model,
                functionType,
                fallbackSelection.Provider,
                fallbackSelection.Model);

            using var fallbackSlot = await AcquireLocalSlotAsync(
                fallbackSelection.Provider,
                fallbackSelection.Model,
                fallbackSelection.Context,
                fallbackOptions.ExecutionSettings,
                ct: ct);

            await AppendExecutionStartedAuditAsync(
                functionType,
                fallbackSelection,
                fallbackOptions,
                fallbackUsed: true, physicalSettings: fallbackOptions.ExecutionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(fallbackOptions).ConfigureAwait(false);
            fallbackStopwatch.Restart();
            fallbackExecutionStarted = true;
            var response = await fallbackTranslator.ChatAsync(fallbackRequest, ct).ConfigureAwait(false);
            fallbackStopwatch.Stop();
            if (GetToolUseReplyIssue(request, response) is { } replyIssue)
            {
                throw new MalformedToolUseReplyException(replyIssue);
            }
            RecordObservedToolUseCapability(fallbackSelection, response);
            await TrackToolUseUsageAsync(
                functionType,
                fallbackSelection,
                response,
                fallbackStopwatch.ElapsedMilliseconds,
                fallbackOptions,
                promptSummary,
                fallbackUsed: true);
            if (IsSuccessfulToolUseCompletion(response))
            {
                await MaterializeFunctionPreferenceIfMissingAsync(
                        functionType,
                        fallbackSelection,
                        request.Options,
                        requiresVision: request.Options?.RequiresVision == true)
                    .ConfigureAwait(false);
            }
            return response;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            fallbackStopwatch.Stop();
            if (fallbackExecutionStarted)
            {
                await TrackToolUseErrorAsync(
                    functionType,
                    fallbackSelection,
                    fallbackStopwatch.ElapsedMilliseconds,
                    fallbackOptions,
                    "Fallback tool-use provider call was cancelled.",
                    fallbackUsed: true).ConfigureAwait(false);
            }
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception fallbackException) when (fallbackException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            fallbackStopwatch.Stop();
            if (fallbackExecutionStarted)
            {
                await TrackToolUseErrorAsync(
                    functionType,
                    fallbackSelection,
                    fallbackStopwatch.ElapsedMilliseconds,
                    fallbackOptions,
                    fallbackException.Message,
                    fallbackUsed: true);
            }
            if (IsRateLimitError(fallbackException))
            {
                await RecordCooldownIfRateLimitAsync(fallbackSelection.Provider, fallbackException).ConfigureAwait(false);
            }
            else if (IsTimeoutOrTransientError(fallbackException))
            {
                await RecordCooldownIfTimeoutOrTransientAsync(fallbackSelection.Provider, fallbackException).ConfigureAwait(false);
            }

            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                fallbackSelection,
                request.Options,
                reason,
                fallbackException,
                primaryCategory);
        }
    }

    private async IAsyncEnumerable<ToolUseStreamEvent> StreamToolUseFallbackAsync(
        string functionType,
        ChatToolUseRequest request,
        LlmModelSelection selectedModel,
        Exception primaryException,
        string promptSummary,
        string requestId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var survivalExecution = await TryExecuteToolUseStreamSurvivalSetAsync(
                functionType,
                request,
                selectedModel,
                primaryException,
                promptSummary,
                requestId,
                ct)
            .ConfigureAwait(false);
        if (survivalExecution is not null)
        {
            foreach (var streamEvent in survivalExecution.Events)
            {
                yield return streamEvent;
            }
            yield break;
        }

        // Classify once; see TryToolUseFallbackAsync — the typed category rides
        // alongside the collapsed legacy reason token into every failover
        // thrown from this flow.
        var (reason, primaryCategory) = ClassifyFailureReasonWithCategory(primaryException);
        LlmModelSelection fallbackSelection;
        try
        {
            fallbackSelection = await BuildToolUseFallbackSelectionAsync(
                functionType,
                selectedModel,
                promptSummary,
                reason,
                request.Options?.AllowAnyProviderFallback == true,
                request.Options)
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception setupException) when (setupException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                attemptedFallback: null,
                request.Options,
                reason,
                setupException,
                primaryCategory);
        }

        var fallbackOptions = CloneOrCreate(request.Options);
        fallbackOptions.OverrideProvider = fallbackSelection.Provider;
        fallbackOptions.OverrideModel = fallbackSelection.Model;
        fallbackOptions.OverrideProvenance = LlmOverrideProvenance.Fallback;
        fallbackOptions.RequiresToolUse = true;
        fallbackOptions.ExecutionSettings = MergeToolUseExecutionSettings(
            fallbackSelection,
            request.Options?.ExecutionSettings,
            ct);

        var fallbackRequest = request with { Options = fallbackOptions };
        IToolUseProvider fallbackTranslator;
        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureToolUseSelectionSupported(fallbackSelection);
            fallbackTranslator = ResolveTranslator(fallbackSelection.Provider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception setupException) when (setupException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                fallbackSelection,
                request.Options,
                reason,
                setupException,
                primaryCategory);
        }
        var fallbackStopwatch = Stopwatch.StartNew();
        Exception? fallbackException = null;
        var fallbackExecutionStarted = false;
        var fallbackTerminalReceiptWritten = false;
        var bufferedRequiredToolEvents = new List<ToolUseStreamEvent>();

        ct.ThrowIfCancellationRequested();
        _logger.LogWarning(
            primaryException,
            "Tool-use stream primary {Provider}/{Model} failed before output for {FunctionType}; trying fallback {FallbackProvider}/{FallbackModel}",
            selectedModel.Provider,
            selectedModel.Model,
            functionType,
            fallbackSelection.Provider,
            fallbackSelection.Model);

        await AppendFallbackSelectionAuditAsync(
            functionType,
            promptSummary,
            selectedModel,
            fallbackSelection,
            request.Options,
            reason,
            requiresToolUse: true).ConfigureAwait(false);

        IDisposable fallbackSlot;
        try
        {
            ct.ThrowIfCancellationRequested();
            fallbackSlot = await AcquireLocalSlotAsync(
                fallbackSelection.Provider,
                fallbackSelection.Model,
                fallbackSelection.Context,
                fallbackOptions.ExecutionSettings,
                ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception setupException) when (setupException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                fallbackSelection,
                request.Options,
                reason,
                setupException,
                primaryCategory);
        }
        using var fallbackSlotLease = fallbackSlot;

        IAsyncEnumerator<ToolUseStreamEvent> enumerator;
        try
        {
            ct.ThrowIfCancellationRequested();
            await AppendExecutionStartedAuditAsync(
                functionType,
                fallbackSelection,
                fallbackOptions,
                fallbackUsed: true, physicalSettings: fallbackOptions.ExecutionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(fallbackOptions).ConfigureAwait(false);
            fallbackStopwatch.Restart();
            fallbackExecutionStarted = true;
            enumerator = fallbackTranslator.StreamAsync(fallbackRequest, ct).GetAsyncEnumerator(ct);
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception setupException) when (setupException is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            fallbackStopwatch.Stop();
            if (fallbackExecutionStarted)
            {
                await TrackToolUseErrorAsync(
                    functionType,
                    fallbackSelection,
                    fallbackStopwatch.ElapsedMilliseconds,
                    fallbackOptions,
                    setupException.Message,
                    fallbackUsed: true).ConfigureAwait(false);
                fallbackTerminalReceiptWritten = true;
            }
            throw CreateToolUseFailoverOrTransientException(
                functionType,
                requestId,
                selectedModel,
                fallbackSelection,
                request.Options,
                reason,
                setupException,
                primaryCategory);
        }
        await using var enumeratorLease = enumerator;
        try
        {
            while (true)
            {
                ToolUseStreamEvent ev;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    ev = enumerator.Current;
                }
                catch (RoutingAuditPersistenceException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                {
                    fallbackException = ex;
                    fallbackStopwatch.Stop();
                    await TrackToolUseErrorAsync(
                        functionType,
                        fallbackSelection,
                        fallbackStopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        ex.Message,
                        fallbackUsed: true);
                    fallbackTerminalReceiptWritten = true;
                    if (IsRateLimitError(ex))
                    {
                        await RecordCooldownIfRateLimitAsync(fallbackSelection.Provider, ex).ConfigureAwait(false);
                    }
                    else if (IsTimeoutOrTransientError(ex))
                    {
                        await RecordCooldownIfTimeoutOrTransientAsync(fallbackSelection.Provider, ex).ConfigureAwait(false);
                    }
                    break;
                }

                if (ev is MessageStoppedEvent stopped)
                {
                    fallbackStopwatch.Stop();
                    if (GetToolUseReplyIssue(request, stopped.Response) is { } replyIssue)
                    {
                        await TrackToolUseErrorAsync(
                            functionType,
                            fallbackSelection,
                            fallbackStopwatch.ElapsedMilliseconds,
                            fallbackOptions,
                            replyIssue,
                            fallbackUsed: true).ConfigureAwait(false);
                        fallbackTerminalReceiptWritten = true;
                        fallbackException = new MalformedToolUseReplyException(replyIssue);
                        bufferedRequiredToolEvents.Clear();
                        break;
                    }

                    await TrackToolUseUsageAsync(
                        functionType,
                        fallbackSelection,
                        stopped.Response,
                        fallbackStopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        promptSummary,
                        fallbackUsed: true);
                    fallbackTerminalReceiptWritten = true;
                    if (IsSuccessfulToolUseCompletion(stopped.Response))
                    {
                        await MaterializeFunctionPreferenceIfMissingAsync(
                                functionType,
                                fallbackSelection,
                                request.Options,
                                requiresVision: request.Options?.RequiresVision == true)
                            .ConfigureAwait(false);
                    }

                    if (request.RequireToolCall)
                    {
                        foreach (var bufferedEvent in bufferedRequiredToolEvents)
                        {
                            yield return bufferedEvent;
                        }
                        bufferedRequiredToolEvents.Clear();
                        yield return ev;
                        continue;
                    }
                }

                if (request.RequireToolCall)
                {
                    bufferedRequiredToolEvents.Add(ev);
                    continue;
                }

                yield return ev;
            }

            if (fallbackException is null && !fallbackTerminalReceiptWritten)
            {
                fallbackStopwatch.Stop();
                fallbackException = new InvalidOperationException(
                    $"Tool-use fallback provider {fallbackSelection.Provider}/{fallbackSelection.Model} completed its stream without a MessageStoppedEvent.");
                if (fallbackExecutionStarted)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        fallbackSelection,
                        fallbackStopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        fallbackException.Message,
                        fallbackUsed: true);
                    fallbackTerminalReceiptWritten = true;
                }
            }

            if (fallbackException is not null)
            {
                throw CreateToolUseFailoverOrTransientException(
                    functionType,
                    requestId,
                    selectedModel,
                    fallbackSelection,
                    request.Options,
                    reason,
                    fallbackException,
                    primaryCategory);
            }
        }
        finally
        {
            if (fallbackExecutionStarted && !fallbackTerminalReceiptWritten)
            {
                fallbackStopwatch.Stop();
                await TrackToolUseErrorAsync(
                    functionType,
                    fallbackSelection,
                    fallbackStopwatch.ElapsedMilliseconds,
                    fallbackOptions,
                    "Tool-use fallback stream was abandoned by its consumer before a terminal MessageStoppedEvent was received.",
                    fallbackUsed: true).ConfigureAwait(false);
                fallbackTerminalReceiptWritten = true;
            }
        }
    }

    private async Task<LlmModelSelection> BuildToolUseFallbackSelectionAsync(
        string functionType,
        LlmModelSelection selectedModel,
        string promptSummary,
        string reason,
        bool allowAnyProviderFallback = false,
        LlmRouterOptions? options = null)
    {
        var fallbackRequirements = CloneOrCreate(options);
        fallbackRequirements.RequiresToolUse = true;
        var (fallbackProvider, fallbackModel) = await ResolveFallbackRouteAsync(
            functionType,
            selectedModel,
            promptSummary,
            requiresVision: fallbackRequirements.RequiresVision == true,
            reason: reason,
            allowAnyProviderFallback: allowAnyProviderFallback,
            options: fallbackRequirements).ConfigureAwait(false);

        var usesConfiguredEvidence = !string.IsNullOrWhiteSpace(selectedModel.FallbackProvider)
                                     && !string.IsNullOrWhiteSpace(selectedModel.FallbackModel)
                                     && IsSameRoute(
                                         fallbackProvider,
                                         fallbackModel,
                                         ResolveRegisteredProviderName(selectedModel.FallbackProvider),
                                         selectedModel.FallbackModel);
        var capability = usesConfiguredEvidence
            ? null
            : _capabilitiesService.GetModelCapability(fallbackProvider, fallbackModel);

        return CarryRoutePolicy(selectedModel, new LlmModelSelection
        {
            Provider = fallbackProvider,
            Model = fallbackModel,
            IsDefault = false,
            Reason = $"Tool-use fallback due to {reason}",
            Context = selectedModel.Context,
            AuditCorrelationId = selectedModel.AuditCorrelationId,
            SupportsToolUse = usesConfiguredEvidence
                ? selectedModel.FallbackSupportsToolUse
                : capability?.SupportsFunctionCalling,
            SupportsStructuredOutput = usesConfiguredEvidence
                ? selectedModel.FallbackSupportsStructuredOutput
                : capability?.SupportsStructuredOutput,
            MaxContextTokens = usesConfiguredEvidence
                ? selectedModel.FallbackMaxContextTokens
                : capability?.MaxContextTokens,
            CapabilityEvidenceSource = usesConfiguredEvidence
                ? selectedModel.FallbackCapabilityEvidenceSource
                : capability is null ? null : "capability-catalog",
        }, promoteConfiguredFallback: usesConfiguredEvidence);
    }

    /// <summary>
    /// Tool-use failover must expose the same temporary-exhaustion contract as
    /// ordinary generation. Otherwise a rate-limited automatic primary whose
    /// fallback proof is still refreshing becomes a terminal chat error even
    /// though the next route-advisor pass can immediately choose another
    /// connected provider.
    /// </summary>
    private Exception CreateToolUseFailoverOrTransientException(
        string functionType,
        string requestId,
        LlmModelSelection selectedModel,
        LlmModelSelection? attemptedFallback,
        LlmRouterOptions? options,
        string reason,
        Exception fallbackFailure,
        LlmFailureCategory? primaryCategory)
    {
        var failover = CreateFailoverException(
            functionType,
            requestId,
            selectedModel,
            attemptedFallback,
            reason,
            fallbackFailure,
            primaryCategory);

        if (IsLaunchDeclinedRouteExhaustion(reason, fallbackFailure))
        {
            return new LlmRouteTemporarilyUnavailableException(
                $"Route {selectedModel.Provider}/{selectedModel.Model} is temporarily unavailable for {functionType}: the subscription CLI launch was declined by the cross-process co-run guard. The call can be retried shortly.",
                failover);
        }

        if (IsAutomaticTransientRouteExhaustion(
                selectedModel,
                attemptedFallback,
                options,
                reason,
                fallbackFailure,
                primaryCategory,
                failover.FallbackCategory))
        {
            return new LlmRouteTemporarilyUnavailableException(
                $"Automatic route {selectedModel.Provider}/{selectedModel.Model} is temporarily exhausted for {functionType}; provider cooldown and Model Scout recovery are searching for the next admitted route.",
                failover);
        }

        return failover;
    }

    private async Task TrackToolUseUsageAsync(
        string functionType,
        LlmModelSelection model,
        ChatToolUseResponse response,
        long responseTimeMs,
        LlmRouterOptions options,
        string promptSummary,
        bool fallbackUsed = false)
    {
        try
        {
            // Aggregate a rough prompt-length estimate from any assistant
            // text. We don't have the original messages here — input tokens
            // come from the provider's usage report when available, and are
            // zero otherwise. That's fine: tool-use cost tracking is
            // best-effort, and every production provider reports usage.
            var inputTokens = response.Usage?.InputTokens ?? 0;
            var outputTokens = response.Usage?.OutputTokens ?? EstimateTokenCount(response.Text ?? "");
            var totalTokens = inputTokens + outputTokens;

            var estimatedCost = totalTokens > 0
                ? _costService.CalculateCost(response.Model, inputTokens, outputTokens)
                : 0m;

            // A completion with no tool calls and no text is dead air, not a
            // success. Recording it as successful poisons the usage truth that
            // Model Scout's tier ranking and the known-good tracker feed on —
            // a route that keeps emitting nothing would look healthy. The
            // response still flows back to the caller (the agent engine owns
            // retry policy); only the quality bookkeeping changes.
            var isDeadAirCompletion = !IsSuccessfulToolUseCompletion(response);
            var qualityError = response.ErrorMessage ?? (isDeadAirCompletion
                ? response.StopReason == ChatStopReason.MaxTokens
                    ? "output_token_limit: provider reached its output limit before completing the reply"
                    : "empty_response: completion had no text and no tool calls"
                : null);

            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = response.Provider,
                Model = response.Model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                CachedInputTokens = response.Usage?.CachedInputTokens ?? 0,
                EstimatedCost = estimatedCost,
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                UserId = options.UserId,
                SessionId = options.SessionId,
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = !isDeadAirCompletion,
                ErrorMessage = qualityError,
                FallbackUsed = fallbackUsed,
                InteractivityContext = model.Context?.InteractivityKey,
                PromptText = promptSummary,
                ResponseText = response.Text
            };

            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed,
                succeeded: !isDeadAirCompletion,
                responseTimeMs,
                totalTokens,
                estimatedCost,
                error: qualityError).ConfigureAwait(false);
            if (isDeadAirCompletion)
            {
                _logger.LogWarning(
                    "Tool-use completion from {Provider}/{Model} for {FunctionType} was incomplete ({Reason}); recorded as a quality failure.",
                    response.Provider, response.Model, functionType, qualityError);
                if (_knownGoodTracker is not null)
                {
                    await _knownGoodTracker
                        .RecordFailureAndCheckRevertAsync(functionType, response.Provider, response.Model)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                _knownGoodTracker?.RecordSuccess(functionType, response.Provider, response.Model);
            }
            if (options.UsageCompletionObserver is not null || HasMissionPhysicalBudget)
            {
                var durable = await PersistUsageForCompletionObserverAsync(record, functionType, record.Provider, record.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(options, record, durable, response.Usage is { InputTokens: > 0, OutputTokens: > 0 }).ConfigureAwait(false);
            }
            else
            {
                _ = PersistUsageTelemetryAsync(record, functionType, response.Provider, response.Model);
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track tool-use usage for {FunctionType}", functionType);
        }
    }

    private static bool IsSuccessfulToolUseCompletion(ChatToolUseResponse response)
        => response.FailureCategory is null
           && response.StopReason is not (ChatStopReason.Error or ChatStopReason.MaxTokens)
           && (response.ToolCalls.Count > 0 || !string.IsNullOrWhiteSpace(response.Text));

    private void RecordObservedToolUseCapability(
        LlmModelSelection selectedModel,
        ChatToolUseResponse response)
    {
        if (IsSuccessfulToolUseCompletion(response) && response.ToolCalls.Count > 0)
        {
            _toolUseProver?.RecordSuccessfulToolCall(selectedModel.Provider, selectedModel.Model);
        }
    }

    private async Task TrackToolUseErrorAsync(
        string functionType,
        LlmModelSelection model,
        long responseTimeMs,
        LlmRouterOptions options,
        string errorMessage,
        bool fallbackUsed = false)
    {
        try
        {
            var record = new LlmUsageRecord
            {
                PhysicalCallId = CurrentPhysicalCallId,
                FunctionType = functionType,
                Provider = model.Provider,
                Model = model.Model,
                InputTokens = 0,
                OutputTokens = 0,
                TotalTokens = 0,
                EstimatedCost = 0m,
                RequestTimestamp = DateTime.UtcNow,
                ResponseTimeMs = (int)responseTimeMs,
                UserId = options.UserId,
                SessionId = options.SessionId,
                RoutingAuditCorrelationId = model.AuditCorrelationId,
                WasSuccessful = false,
                // Provider error bodies can echo credentials, bearer tokens,
                // and addresses; redact before the row becomes durable usage.
                ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(errorMessage, 2_000),
                FallbackUsed = fallbackUsed,
                InteractivityContext = model.Context?.InteractivityKey
            };
            await AppendExecutionAuditAsync(
                functionType,
                model,
                options,
                fallbackUsed,
                succeeded: false,
                responseTimeMs,
                totalTokens: 0,
                estimatedCost: 0m,
                errorMessage).ConfigureAwait(false);
            if (options.UsageCompletionObserver is not null || HasMissionPhysicalBudget)
            {
                var durable = await PersistUsageForCompletionObserverAsync(record, functionType, record.Provider, record.Model).ConfigureAwait(false);
                await NotifyUsageCompletionObserverAsync(options, record, durable, false).ConfigureAwait(false);
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
        catch (ProviderContextReconstructionRequiredException) { throw; }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to track tool-use error for {FunctionType}", functionType);
        }
    }

    private async Task<LlmModelSelection> ResolveModelSelectionAsync(
        string functionType,
        ChatToolUseRequest request,
        string promptSummary)
    {
        // Use the same selection path as text/vision calls so explicit
        // overrides, LocalOnly, urgency, advisor recommendations, and fallback
        // metadata behave consistently across the router surface.
        var toolOptions = CloneOrCreate(request.Options);
        toolOptions.RequiresToolUse = true;
        var userTexts = request.Messages
            .LastOrDefault(message => message.Role == ChatRole.User)
            ?.Parts
            .OfType<TextPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        // Hosts can supply authentic intent separately from generated
        // continuation text. Preserve even an explicitly empty value.
        toolOptions.RoutingUserText ??= userTexts is { Count: > 0 }
            ? string.Join("\n", userTexts)
            : null;
        toolOptions.RoutingAvailableToolIds = request.Tools
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        toolOptions.RoutingCompletedToolIds = GetSuccessfullyCompletedToolIds(request.Messages);
        return await SelectOptimalModelAsync(functionType, promptSummary, toolOptions);
    }

    private static IReadOnlyCollection<string> GetSuccessfullyCompletedToolIds(
        IReadOnlyList<ChatMessage> messages)
    {
        // A successful control from an older turn must never suppress safety
        // policy for a later unrelated request. Only inspect the transcript
        // suffix that belongs to the latest user-authored turn.
        var latestUserIndex = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User)
            {
                latestUserIndex = index;
                break;
            }
        }

        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        var successfulCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = Math.Max(0, latestUserIndex + 1); index < messages.Count; index++)
        {
            foreach (var part in messages[index].Parts)
            {
                switch (part)
                {
                    case ToolUsePart use when !string.IsNullOrWhiteSpace(use.Id)
                                               && !string.IsNullOrWhiteSpace(use.Name):
                        toolNamesByCallId[use.Id] = use.Name;
                        break;
                    case ToolResultPart result when !result.IsError
                                                    && !string.IsNullOrWhiteSpace(result.ToolUseId):
                        successfulCallIds.Add(result.ToolUseId);
                        break;
                }
            }
        }

        return successfulCallIds
            .Select(callId => toolNamesByCallId.GetValueOrDefault(callId))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildToolUsePromptSummary(ChatToolUseRequest request)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            lines.Add($"system: {request.SystemPrompt}");
        }

        foreach (var message in request.Messages)
        {
            var parts = message.Parts.Select(part => part switch
            {
                TextPart text => text.Text,
                ToolUsePart tool => $"[tool_use {tool.Name}] {tool.Input}",
                ToolResultPart result => $"[tool_result {(result.IsError ? "error" : "ok")}] {result.Content}",
                // Provider chain-of-thought is round-tripped on the wire but
                // kept out of summaries (and therefore the usage log) so it
                // doesn't blow up the prompt-text column or leak into AB-test
                // judgements.
                ReasoningPart => "",
                // Never copy image bytes into prompt summaries or usage/audit
                // telemetry. The capability gate only needs to know an image is
                // present and the provider needs its media type.
                ImagePart image => $"[image {image.MediaType}]",
                _ => part.ToString() ?? ""
            });
            lines.Add($"{message.Role}: {string.Join(" ", parts)}");
        }

        if (request.Tools.Count > 0)
        {
            lines.Add("tools: " + string.Join(", ", request.Tools.Select(t => t.Name)));
        }

        return string.Join("\n", lines);
    }


    private IToolUseProvider ResolveTranslator(string providerName)
    {
        if (_toolUseRegistry is null)
        {
            throw new InvalidOperationException(
                "Tool-use registry is not attached. Ensure AddToolUseProviders() is called during DI registration.");
        }
        return _toolUseRegistry.Resolve(providerName);
    }

    /// <summary>
    /// A context-window overflow is the request's size against this route, not
    /// route trouble. Tool-use callers own their transcript and recover by
    /// compacting it and retrying; failing over here instead loads another
    /// model (on a LAN node, evicting the one that just served the turn) for a
    /// prompt a same-sized window drops the same way. The overflow therefore
    /// surfaces to the caller unchanged, like a fallback-disabled failure.
    /// </summary>
    private static bool IsContextOverflowFailure(Exception exception)
        => Services.LlmErrorPolicy.Classify(exception) == LlmFailureCategory.ContextOverflow;

    public async Task<int?> GetRouteContextWindowAsync(string provider, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        int? reviewed = ProviderPacks.ProviderPackCatalog.TryGetModelFacts(provider, model, out var facts)
                        && facts?.MaxContextTokens is > 0
            ? facts.MaxContextTokens
            : null;
        var window = reviewed;
        if (_toolUseRegistry is { } registry
            && registry.Supports(provider)
            && registry.Resolve(provider) is IToolUseContextWindowSource runtime)
        {
            try
            {
                // The window a request that sets none of its own is served at.
                int? current = null;
                if (await runtime.GetContextWindowTokensAsync(model, ct).ConfigureAwait(false) is int reported and > 0)
                {
                    current = reviewed is int known ? Math.Min(known, reported) : reported;
                    // Model Scout ranks routes by whether a request fits; it
                    // reads this observation rather than probing on the hot path.
                    _contextWindows?.Record(provider, model, current.Value);
                    // A runner the native adapter loaded larger for an oversized
                    // request reports that window, but a request that sets no
                    // window of its own is served at the model's default.
                    if (_contextWindows?.TryGet(provider, model, out var served) == true)
                    {
                        current = Math.Min(current.Value, served);
                    }
                }
                else if (_contextWindows?.TryGetRuntimeDefault(provider, out var runtimeDefault) == true)
                {
                    // Not loaded and no Modelfile window: the runtime will load it
                    // at its default, which its other text runners show. Sizing the
                    // first request to it avoids an overflow-then-compact round trip.
                    // An estimate, so it is not recorded as this model's observation.
                    current = reviewed is int known ? Math.Min(known, runtimeDefault) : runtimeDefault;
                }

                window = current ?? reviewed;

                // A runtime that sets the window on each request grows it for a
                // request that does not fit, so the route serves whole anything up
                // to its largest window, and sizing against the current window
                // would compact a turn the runtime serves whole. That largest
                // window is a capability, not a runner's report: the provider
                // records it apart from runner windows for Model Scout, so it
                // never stands in for the window a request without one gets.
                if (runtime is IToolUseGrowableContextWindowSource growable
                    && await growable.GetMaximumContextWindowTokensAsync(model, ct).ConfigureAwait(false) is int maximum and > 0)
                {
                    var largest = reviewed is int known ? Math.Min(known, maximum) : maximum;
                    window = current is int now ? Math.Max(now, largest) : largest;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Runtime context window for {Provider}/{Model} is unavailable.", provider, model);
            }
        }

        return window;
    }

    private async Task<LlmModelSelection> EnsureToolUseSelectionOrFallbackAsync(
        string functionType,
        LlmModelSelection selection,
        LlmRouterOptions? options)
    {
        var primaryIssue = GetToolUseSelectionIssue(selection);
        if (primaryIssue is null)
            return selection;

        if (!string.IsNullOrWhiteSpace(selection.FallbackProvider)
            && !string.IsNullOrWhiteSpace(selection.FallbackModel))
        {
            var fallback = CarryRoutePolicy(selection, new LlmModelSelection
            {
                Provider = ResolveRegisteredProviderName(selection.FallbackProvider),
                Model = selection.FallbackModel,
                FallbackProvider = selection.Provider,
                FallbackModel = selection.Model,
                IsDefault = false,
                Reason = $"Tool-use fallback: primary {selection.Provider}/{selection.Model} is incompatible ({primaryIssue})",
                Context = selection.Context,
                AuditCorrelationId = selection.AuditCorrelationId,
                SupportsToolUse = selection.FallbackSupportsToolUse,
                SupportsStructuredOutput = selection.FallbackSupportsStructuredOutput,
                MaxContextTokens = selection.FallbackMaxContextTokens,
                CapabilityEvidenceSource = selection.FallbackCapabilityEvidenceSource,
            }, promoteConfiguredFallback: true);
            var hardRequirementIssue = await GetRouteRequirementIssueAsync(
                fallback,
                options?.RequiresVision == true,
                options,
                verifyAvailability: true).ConfigureAwait(false);
            if (GetToolUseSelectionIssue(fallback) is null
                && hardRequirementIssue is null)
            {
                _logger.LogWarning(
                    "Tool-use route {Provider}/{Model} is incompatible for {FunctionType}; using configured fallback {FallbackProvider}/{FallbackModel}",
                    selection.Provider,
                    selection.Model,
                    functionType,
                    fallback.Provider,
                    fallback.Model);
                return fallback;
            }
        }

        throw new NotSupportedException(
            $"Selected route {selection.Provider}/{selection.Model} cannot execute native tool use for {functionType}: {primaryIssue}. "
            + "Configure a tool-capable fallback or ask Model Scout to find one.");
    }

    private void EnsureToolUseSelectionSupported(LlmModelSelection selection)
    {
        var issue = GetToolUseSelectionIssue(selection);
        if (issue is not null)
        {
            throw new NotSupportedException(
                $"Route {selection.Provider}/{selection.Model} cannot execute native tool use: {issue}.");
        }
    }

    private string? GetToolUseSelectionIssue(LlmModelSelection selection)
    {
        if (_toolUseRegistry is null || !_toolUseRegistry.Supports(selection.Provider))
        {
            return "no native tool-use translator is registered";
        }

        if (ModelWorkloadFamilies.IsEmbeddingOnly(selection.Model))
        {
            return "the model is a dedicated embedding model, not a chat/tool-use model";
        }

        if (ModelWorkloadFamilies.IsVisionOnly(selection.Provider, selection.Model))
        {
            return "the model is a dedicated vision-lane model, not a text tool-use assistant";
        }

        // Explicit choices remain authoritative route intent, but cannot waive a
        // hard execution capability. Any negative evidence wins over a positive
        // claim from another source.
        var capability = _capabilitiesService.GetModelCapability(selection.Provider, selection.Model);
        if (selection.SupportsToolUse == false || capability?.SupportsFunctionCalling == false)
        {
            return "the model capability catalog explicitly says function calling is unsupported";
        }

        bool? supportsToolUse = selection.SupportsToolUse == true
                                || capability?.SupportsFunctionCalling == true
            ? true
            : null;

        if (supportsToolUse is null
            && SubscriptionCliProviders.IsSubscriptionCliProvider(selection.Provider))
        {
            supportsToolUse = true;
        }

        if (supportsToolUse is null && _toolUseProver?.GetCachedVerdict(selection.Provider, selection.Model) is { } probed)
        {
            // A probe already answered this question for this route.
            supportsToolUse = probed;
        }

        if (supportsToolUse != true)
        {
            // Unknown is a question, not a verdict. The async caller
            // (GetRouteRequirementIssueAsync) probes on this exact marker and
            // re-asks; if the model really cannot call tools the probe records
            // that and we land here again with a definite answer.
            return supportsToolUse is null
                ? ToolUseCapabilityUnknownIssue
                : "the model was probed and did not produce a tool call";
        }

        return null;
    }

    /// <summary>
    /// Marker meaning "nobody has established whether this route can call
    /// tools" — as opposed to knowing that it cannot. Only this one is worth
    /// probing.
    /// </summary>
    internal const string ToolUseCapabilityUnknownIssue = "model-level tool-use capability is not proven";

    /// <summary>
    /// The provider itself reported that the exact model is not installed. This
    /// deliberately does not mention capability proof: it is a route-availability
    /// fact, so <c>IsAutomaticCapabilityDiscoveryPending</c> must not classify it
    /// as a transient "evidence pending" state that background callers poll on.
    /// </summary>
    internal const string ModelNotInstalledIssue = "the provider reports the model is not installed (model not found)";

    private static LlmRouterOptions CloneOrCreate(LlmRouterOptions? source)
        => CloneRouterOptions(source);

    private static Dictionary<string, object> MergeToolUseExecutionSettings(
        LlmModelSelection selection,
        Dictionary<string, object>? requestSettings,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (selection.ExecutionSettings is not null)
        {
            foreach (var kvp in selection.ExecutionSettings)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        if (requestSettings is not null)
        {
            foreach (var kvp in requestSettings)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        if (SubscriptionCliProviders.IsSubscriptionCliProvider(selection.Provider))
        {
            merged[SubscriptionCliExecutionSettings.ProcessPriority] =
                SubscriptionCliExecutionSettings.FromInteractivity(
                    selection.Context?.Interactivity
                    ?? LlmInteractivity.Background);
        }
        if (selection.Context?.PrivacyClass is { } privacy
            && privacy != LlmPrivacyClass.Unspecified)
        {
            merged[LlmReservedExecutionSettings.RequestPrivacyClass] = privacy;
        }
        if (cancellationToken.CanBeCanceled)
        {
            merged[LlmReservedExecutionSettings.RequestCancellationToken] =
                cancellationToken;
        }

        return merged;
    }
}
