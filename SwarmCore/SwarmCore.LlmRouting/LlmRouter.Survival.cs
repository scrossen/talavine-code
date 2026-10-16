using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Runtime execution of proof-bound Model Scout survival sets. The legacy primary /
/// fallback columns remain the compatibility lane; this partial is entered only after
/// an automatic primary has failed and a current durable set is still bound to that
/// exact unpinned preference.
/// </summary>
public partial class LlmRouter
{
    private const int MaximumSurvivalPhysicalRoutes = 3;
    private const string SurvivalAlternateSelectionReason = "Proof-bound route survival alternate";

    private sealed class RouteSurvivalAttemptSession
    {
        public required string FunctionType { get; init; }
        public required LlmRouteSurvivalSet SurvivalSet { get; init; }
        public required LlmModelSelection Primary { get; init; }
        public required LlmRouterOptions? Options { get; init; }
        public required bool RequiresVision { get; init; }
        public required List<LlmFailoverAttempt> Attempts { get; init; }
        public required HashSet<string> PhysicalIdentities { get; init; }
        public required HashSet<string> ExecutionChannels { get; init; }
        public int NextRouteIndex { get; set; } = 1;
        public LlmModelSelection? LastSelection { get; set; }
    }

    private sealed record SurvivalResponseExecution(string Content, LlmResponse Response, LlmModelSelection Selection);

    private sealed record SurvivalStreamingExecution(LlmRouterResponse Response);

    private sealed record SurvivalToolExecution(ChatToolUseResponse Response);

    private sealed record SurvivalToolStreamExecution(IReadOnlyList<ToolUseStreamEvent> Events);

    /// <summary>
    /// A provider returned a syntactically valid tool-use envelope that did not meet
    /// the router-owned response contract. In particular, a required-tool turn cannot
    /// be satisfied by refusal/prose with zero tool calls.
    /// </summary>
    private sealed class MalformedToolUseReplyException : InvalidOperationException
    {
        public MalformedToolUseReplyException(string message) : base(message)
        {
            // Preserve the retry contract when visible text has committed the
            // stream and only the agent can safely restart the iteration.
            Data["swarmcore.malformed_tool_use_envelope"] = true;
        }
    }

    /// <summary>
    /// The durable preference or its proof-bound survival set changed after an
    /// alternate was admitted. This is a control-plane stop, never evidence that
    /// the provider failed and never permission to continue to another route.
    /// </summary>
    private sealed class SurvivalBindingChangedException : InvalidOperationException
    {
        public SurvivalBindingChangedException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    private static bool IsSurvivalFailoverCategory(LlmFailureCategory category)
        => category is LlmFailureCategory.AuthInvalid
            or LlmFailureCategory.RateLimit
            or LlmFailureCategory.QuotaExhausted
            or LlmFailureCategory.Timeout
            or LlmFailureCategory.ModelNotFound
            or LlmFailureCategory.LaunchDeclined
            or LlmFailureCategory.ProviderError
            or LlmFailureCategory.MalformedReply;

    private static LlmFailureCategory ResolveSurvivalFailureCategory(
        LlmFailureCategory? category,
        string? diagnostic,
        string? reason = null)
    {
        if (category is { } typed)
        {
            return typed;
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            if (diagnostic.Contains("quality_gate", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("malformed", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("required tool call", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("without a MessageStoppedEvent", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("null response", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("empty response", StringComparison.OrdinalIgnoreCase))
            {
                return LlmFailureCategory.MalformedReply;
            }

            return LlmErrorPolicy.Classify(diagnostic);
        }

        return reason switch
        {
            "rate_limit" => LlmFailureCategory.RateLimit,
            "timeout" => LlmFailureCategory.Timeout,
            "model_not_found" => LlmFailureCategory.ModelNotFound,
            "launch_declined" => LlmFailureCategory.LaunchDeclined,
            _ when reason?.Contains("quality", StringComparison.OrdinalIgnoreCase) == true
                => LlmFailureCategory.MalformedReply,
            _ => LlmFailureCategory.ProviderError,
        };
    }

    private static bool IsAutomaticSurvivalSetEligible(
        string functionType,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options)
    {
        if (selectedModel.IsUserPinned
            || options?.DisableFallback == true
            || HasExactValidatedAdmission(selectedModel, options)
            || options?.OverrideProvenance is LlmOverrideProvenance.User
                or LlmOverrideProvenance.Profile
                or LlmOverrideProvenance.ValidatedAdmission
            || string.Equals(functionType, FunctionTypes.MessagePipeline, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A survival set intentionally spans independent execution channels. Any
        // exact provider/model override is a narrower route authority and therefore
        // cannot be widened by this automatic recovery lane, regardless of which
        // subsystem supplied the override.
        return string.IsNullOrWhiteSpace(options?.OverrideProvider)
               && string.IsNullOrWhiteSpace(options?.OverrideModel);
    }

    /// <summary>
    /// A model-not-found self-heal is itself another physical model identity. When a
    /// valid survival set is already bound to the failed automatic primary, let the
    /// bounded three-channel session own recovery instead of spending a fourth route
    /// identity before that session starts.
    /// </summary>
    private async Task<bool> HasBoundSurvivalSetForPrimaryAsync(
        string functionType,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options)
    {
        if (!IsAutomaticSurvivalSetEligible(functionType, selectedModel, options))
        {
            return false;
        }

        var cancellationToken = options?.RequestCancellationToken ?? CancellationToken.None;
        try
        {
            var preference = await _settingsProvider
                .GetFunctionPreferenceAsync(functionType)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var survivalSet = await _settingsProvider
                .GetRouteSurvivalSetAsync(functionType, cancellationToken)
                .ConfigureAwait(false);
            if (preference is not { IsEnabled: true }
                || LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt)
                || survivalSet is null
                || !LlmRouteSurvivalSetValidation.TryValidate(
                    survivalSet,
                    FunctionPreferenceVersion.Capture(functionType, preference),
                    out _))
            {
                return false;
            }

            return await TryFindEligibleSurvivalPrimaryIndexAsync(
                    survivalSet,
                    selectedModel,
                    cancellationToken)
                .ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogDebug(
                ex,
                "Could not inspect the route survival set before same-provider self-heal for {FunctionType}",
                functionType);
            return false;
        }
    }

    private async Task<RouteSurvivalAttemptSession?> TryCreateRouteSurvivalSessionAsync(
        string functionType,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options,
        bool requiresVision,
        string primaryDiagnostic,
        LlmFailureCategory? primaryCategory,
        CancellationToken cancellationToken)
    {
        if (!IsAutomaticSurvivalSetEligible(functionType, selectedModel, options))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        LlmFunctionPreference? preference;
        LlmRouteSurvivalSet? survivalSet;
        try
        {
            preference = await _settingsProvider
                .GetFunctionPreferenceAsync(functionType)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            survivalSet = await _settingsProvider
                .GetRouteSurvivalSetAsync(functionType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(
                ex,
                "Could not read the proof-bound route survival set for {FunctionType}",
                functionType);
            return null;
        }

        if (preference is null
            || LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt)
            || !preference.IsEnabled
            || survivalSet is null)
        {
            return null;
        }

        var preferenceVersion = FunctionPreferenceVersion.Capture(functionType, preference);
        if (!LlmRouteSurvivalSetValidation.TryValidate(
                survivalSet,
                preferenceVersion,
                out var validationError)
            || survivalSet.Routes.Count < LlmRouteSurvivalSetValidation.MinimumRouteCount)
        {
            _logger.LogWarning(
                "Rejected route survival set for {FunctionType}: {Reason}",
                functionType,
                validationError ?? "the bound route set is too small");
            return null;
        }

        var selectedRouteIndex = await TryFindEligibleSurvivalPrimaryIndexAsync(
                survivalSet,
                selectedModel,
                cancellationToken)
            .ConfigureAwait(false);
        if (selectedRouteIndex is null)
        {
            _logger.LogWarning(
                "Rejected route survival set for {FunctionType}: selected automatic route {Provider}/{Model} was not an eligible current suffix of the bound route order",
                functionType,
                selectedModel.Provider,
                selectedModel.Model);
            return null;
        }

        if (selectedRouteIndex.Value > 0)
        {
            _logger.LogInformation(
                "Accepted route survival suffix for {FunctionType} at bound route {RouteIndex}: all prior proof-bound routes are currently unavailable and {Provider}/{Model} remains the exact selected physical route",
                functionType,
                selectedRouteIndex.Value + 1,
                selectedModel.Provider,
                selectedModel.Model);
        }

        var category = ResolveSurvivalFailureCategory(primaryCategory, primaryDiagnostic);
        var attempts = new List<LlmFailoverAttempt>(MaximumSurvivalPhysicalRoutes)
        {
            CreateSurvivalAttempt(selectedModel, category, primaryDiagnostic, wasExecuted: true),
        };
        var session = new RouteSurvivalAttemptSession
        {
            FunctionType = functionType,
            SurvivalSet = survivalSet,
            Primary = selectedModel,
            Options = options,
            RequiresVision = requiresVision,
            Attempts = attempts,
            PhysicalIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PhysicalRouteIdentity(selectedModel.Provider, selectedModel.Model),
            },
            ExecutionChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                LlmRouteSurvivalSetValidation.CanonicalExecutionChannel(selectedModel.Provider),
            },
            NextRouteIndex = selectedRouteIndex.Value + 1,
        };

        // The primary terminal receipt was already written through the legacy
        // execution audit path before survival recovery was entered. Mirror it into
        // the route-health vocabulary only after the current bound survival set has
        // been proven. That keeps request/policy errors from poisoning route health
        // while making every physically attempted survival route visible to Scout.
        if (IsSurvivalFailoverCategory(category))
        {
            await AppendRouteSurvivalTerminalAuditAsync(
                    functionType,
                    selectedModel,
                    options,
                    succeeded: false,
                    fallbackUsed: false,
                    responseTimeMs: 0,
                    totalTokens: 0,
                    estimatedCost: 0m,
                    primaryDiagnostic)
                .ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>
    /// A hard-safety advisor may already have skipped a cooled-down first route
    /// before the router call starts. The selected primary can therefore begin at
    /// a later member of the same immutable survival order, but only when every
    /// preceding exact physical route is provably unavailable now. API and CLI
    /// channels remain distinct because matching uses canonical execution-channel
    /// identity rather than vendor family names.
    /// </summary>
    private async Task<int?> TryFindEligibleSurvivalPrimaryIndexAsync(
        LlmRouteSurvivalSet survivalSet,
        LlmModelSelection selectedModel,
        CancellationToken cancellationToken)
    {
        var selectedRouteIndex = -1;
        for (var index = 0; index < survivalSet.Routes.Count; index++)
        {
            var candidate = survivalSet.Routes[index];
            if (SamePhysicalRoute(
                    selectedModel.Provider,
                    selectedModel.Model,
                    candidate.Provider,
                    candidate.Model))
            {
                selectedRouteIndex = index;
                break;
            }
        }

        if (selectedRouteIndex < 0)
        {
            return null;
        }

        for (var index = 0; index < selectedRouteIndex; index++)
        {
            if (!await IsSurvivalPrefixRouteCurrentlyUnavailableAsync(
                    survivalSet.Routes[index],
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return null;
            }
        }

        return selectedRouteIndex;
    }

    private async Task<bool> IsSurvivalPrefixRouteCurrentlyUnavailableAsync(
        LlmRouteSurvivalCandidate candidate,
        CancellationToken cancellationToken)
    {
        var providerName = ResolveRegisteredProviderName(candidate.Provider);

        // Cooldowns are channel-specific. In particular, an exhausted direct API
        // route does not make its subscription CLI sibling unavailable, or vice versa.
        if (_cooldownTracker is not null && !_cooldownTracker.IsAvailable(providerName))
        {
            return true;
        }

        var provider = _providerRegistry.GetProvider(providerName);
        if (provider is null || !provider.SupportsModel(candidate.Model))
        {
            return true;
        }

        try
        {
            return !await IsProviderAvailableAsync(
                    providerName,
                    cancellationToken,
                    waitForDeferredCapacity: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            // Failure to prove that a preceding route is unavailable must not
            // authorize skipping it. Reject the suffix and leave routing closed.
            _logger.LogWarning(
                ex,
                "Could not prove that preceding survival route {Provider}/{Model} is unavailable",
                candidate.Provider,
                candidate.Model);
            return false;
        }
    }

    /// <summary>
    /// Re-admits an exact automatic route from the durable proof-bound survival
    /// set when the live discovered catalog is temporarily empty or stale. This
    /// is not catalog bypass: the set carries current function proof for the exact
    /// provider/model, remains cryptographically bound to the current unpinned
    /// preference, and the route is independently checked against every live
    /// request, capability, availability, and privacy gate. Surprise-cost checks
    /// still apply to alternates; this admission is the already-active exact
    /// preferred route, not permission to widen into a new metered fallback.
    /// </summary>
    private async Task<LlmModelSelection?> TryReadmitBoundSurvivalCandidateAsync(
        string functionType,
        LlmCallContext context,
        string providerName,
        string model,
        bool requiresVision,
        LlmRouterOptions? options,
        string reason,
        decimal? minimumCapabilityScore,
        ModelScoutTier tier,
        bool requireMeasuredCapabilityScore,
        bool blockWeakerPolicyFallback,
        bool requireCurrentFunctionEvidence)
    {
        if (options?.RequireCurrentWorkloadSizing == true
            && options.OverrideProvenance is not (
                LlmOverrideProvenance.User or LlmOverrideProvenance.Profile))
        {
            // A survival receipt proves that an automatic route was usable when
            // the set was built; it does not prove that route is still the best
            // latency choice for this request's output size. Exact user/profile
            // choices are not automatic and retain their normal authority.
            return null;
        }

        var provider = ResolveRegisteredProviderName(providerName);
        var probe = new LlmModelSelection
        {
            Provider = provider,
            Model = model,
            IsDefault = false,
            Context = context,
        };
        if (!IsAutomaticSurvivalSetEligible(functionType, probe, options))
        {
            return null;
        }

        var cancellationToken = options?.RequestCancellationToken ?? CancellationToken.None;
        try
        {
            var preference = await _settingsProvider
                .GetFunctionPreferenceAsync(functionType)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var survivalSet = await _settingsProvider
                .GetRouteSurvivalSetAsync(functionType, cancellationToken)
                .ConfigureAwait(false);
            if (preference is not { IsEnabled: true }
                || LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt)
                || survivalSet is null
                || !LlmRouteSurvivalSetValidation.TryValidate(
                    survivalSet,
                    FunctionPreferenceVersion.Capture(functionType, preference),
                    out _))
            {
                return null;
            }

            var selectedRouteIndex = await TryFindEligibleSurvivalPrimaryIndexAsync(
                    survivalSet,
                    probe,
                    cancellationToken)
                .ConfigureAwait(false);
            if (selectedRouteIndex is null)
            {
                return null;
            }

            var candidate = survivalSet.Routes[selectedRouteIndex.Value];
            if (!MeetsCapabilityEnvelope(
                    candidate.DecisionEvidence.CapabilityScore,
                    candidate.DecisionEvidence.CapabilityProvenance,
                    minimumCapabilityScore,
                    tier,
                    requireMeasuredCapabilityScore)
                || (requireCurrentFunctionEvidence
                    && !ModelScoutFunctionEvidence.IsCoherent(
                        candidate.FunctionEvidence,
                        functionType,
                        candidate.Provider,
                        candidate.Model))
                || (requiresVision && candidate.SupportsVision != true)
                || (options?.RequiresToolUse == true && candidate.SupportsToolUse != true)
                || (RequiresNativeStructuredOutput(options)
                    && candidate.SupportsStructuredOutput != true)
                || (options?.MinimumContextTokens is > 0 and var requiredContext
                    && (candidate.MaxContextTokens is null
                        || candidate.MaxContextTokens < requiredContext)))
            {
                return null;
            }

            var receipt = new ModelScoutRuntimeDecisionReceipt(
                new ModelScoutScoreWeights(0.5m, 0.25m, 0.25m),
                candidate.DecisionEvidence,
                [],
                candidate.FunctionEvidence) { FunctionType = functionType };
            var selection = new LlmModelSelection
            {
                Provider = provider,
                Model = model,
                IsDefault = false,
                Reason = $"{reason}; exact route re-admitted by the current proof-bound survival set",
                Context = context,
                AuditCorrelationId = survivalSet.CorrelationId,
                SupportsToolUse = candidate.SupportsToolUse,
                SupportsStructuredOutput = candidate.SupportsStructuredOutput,
                MaxContextTokens = candidate.MaxContextTokens,
                CapabilityEvidenceSource = candidate.DecisionEvidence.EvidenceSource
                                           ?? candidate.FunctionEvidence.Source,
                CapabilityScore = candidate.DecisionEvidence.CapabilityScore,
                CapabilityEvidenceKind = candidate.DecisionEvidence.CapabilityProvenance,
                ModelScoutDecisionReceipt = receipt,
                MinimumCapabilityScore = minimumCapabilityScore,
                RequireMeasuredCapabilityScore = requireMeasuredCapabilityScore,
                RequiredTier = tier,
                BlockWeakerPolicyFallback = blockWeakerPolicyFallback,
                RequireCurrentFunctionEvidence = requireCurrentFunctionEvidence,
            };
            var requirementIssue = await GetRouteRequirementIssueAsync(
                    selection,
                    requiresVision,
                    options,
                    verifyAvailability: true)
                .ConfigureAwait(false);
            if (requirementIssue is not null
                || (options?.RequiresToolUse == true
                    && GetToolUseSelectionIssue(selection) is not null))
            {
                return null;
            }

            // Availability/capability checks can await discovery. Re-read the
            // exact rows after those awaits before returning route authority.
            var currentPreference = await _settingsProvider
                .GetFunctionPreferenceAsync(functionType)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var currentSet = await _settingsProvider
                .GetRouteSurvivalSetAsync(functionType, cancellationToken)
                .ConfigureAwait(false);
            if (currentPreference is not { IsEnabled: true }
                || LlmPreferenceAuthors.IsUserPinned(currentPreference.PinnedByUserAt)
                || currentSet is null
                || !string.Equals(
                    survivalSet.MutationRevision,
                    currentSet.MutationRevision,
                    StringComparison.Ordinal)
                || !string.Equals(
                    survivalSet.ProofBundleSha256,
                    currentSet.ProofBundleSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !LlmRouteSurvivalSetValidation.TryValidate(
                    currentSet,
                    FunctionPreferenceVersion.Capture(functionType, currentPreference),
                    out _))
            {
                return null;
            }

            _logger.LogInformation(
                "Re-admitted exact automatic route {Provider}/{Model} for {FunctionType} from current proof-bound survival set {CorrelationId}",
                provider,
                model,
                functionType,
                survivalSet.CorrelationId);
            return selection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(
                ex,
                "Could not re-admit exact automatic route {Provider}/{Model} from the proof-bound survival set for {FunctionType}",
                provider,
                model,
                functionType);
            return null;
        }
    }

    private async Task<LlmModelSelection?> TryGetNextSurvivalSelectionAsync(
        RouteSurvivalAttemptSession session,
        CancellationToken cancellationToken)
    {
        while (session.NextRouteIndex < session.SurvivalSet.Routes.Count
               && session.PhysicalIdentities.Count < MaximumSurvivalPhysicalRoutes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read both rows before every alternate. The validation hash binds all
            // route, revision and pin state; requiring the same survival-set mutation
            // revision prevents a set replaced between attempts from being spliced into
            // the in-flight request.
            var currentSet = await EnsureSurvivalBindingStillCurrentAsync(
                    session,
                    cancellationToken)
                .ConfigureAwait(false);

            var candidate = currentSet.Routes[session.NextRouteIndex++];
            var physicalIdentity = PhysicalRouteIdentity(candidate.Provider, candidate.Model);
            var executionChannel = LlmRouteSurvivalSetValidation.CanonicalExecutionChannel(
                candidate.Provider);
            if (session.PhysicalIdentities.Contains(physicalIdentity)
                || session.ExecutionChannels.Contains(executionChannel))
            {
                continue;
            }

            var selection = BuildSurvivalSelection(session, candidate);
            var automaticFallbackCostLimit = await GetAutomaticFallbackCostLimitAsync()
                .ConfigureAwait(false);
            var issue = await GetAutomaticFallbackIssueAsync(
                    session.Primary,
                    selection.Provider,
                    selection.Model,
                    session.RequiresVision,
                    session.Options,
                    candidate.SupportsToolUse,
                    candidate.SupportsStructuredOutput,
                    candidate.MaxContextTokens,
                    candidate.DecisionEvidence.EvidenceSource ?? candidate.FunctionEvidence.Source,
                    candidate.DecisionEvidence.CapabilityScore,
                    candidate.DecisionEvidence.CapabilityProvenance,
                    candidate.DecisionEvidence.EstimatedCostUsd,
                    automaticFallbackCostLimit)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (issue is not null)
            {
                _logger.LogWarning(
                    "Skipped route survival candidate {Provider}/{Model} for {FunctionType}: {Reason}",
                    selection.Provider,
                    selection.Model,
                    session.FunctionType,
                    issue);
                continue;
            }

            // Availability/capability admission can involve bounded discovery. Re-read
            // the exact preference and survival revision after that await as the final
            // authority check immediately before this physical identity is committed.
            await EnsureSurvivalBindingStillCurrentAsync(session, cancellationToken)
                .ConfigureAwait(false);

            session.PhysicalIdentities.Add(physicalIdentity);
            session.ExecutionChannels.Add(executionChannel);
            session.LastSelection = selection;
            return selection;
        }

        return null;
    }

    private async Task<LlmRouteSurvivalSet> EnsureSurvivalBindingStillCurrentAsync(
        RouteSurvivalAttemptSession session,
        CancellationToken cancellationToken)
    {
        var preference = await _settingsProvider
            .GetFunctionPreferenceAsync(session.FunctionType)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentSet = await _settingsProvider
            .GetRouteSurvivalSetAsync(session.FunctionType, cancellationToken)
            .ConfigureAwait(false);
        if (preference is null
            || LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt)
            || !preference.IsEnabled
            || currentSet is null
            || !string.Equals(
                currentSet.MutationRevision,
                session.SurvivalSet.MutationRevision,
                StringComparison.Ordinal)
            || !string.Equals(
                currentSet.ProofBundleSha256,
                session.SurvivalSet.ProofBundleSha256,
                StringComparison.OrdinalIgnoreCase)
            || !LlmRouteSurvivalSetValidation.TryValidate(
                currentSet,
                FunctionPreferenceVersion.Capture(session.FunctionType, preference),
                out _))
        {
            throw new SurvivalBindingChangedException(
                $"The proof-bound route survival set for {session.FunctionType} changed before the next alternate could execute.");
        }

        return currentSet;
    }

    /// <summary>
    /// Re-check the durable route authority after every setup wait (including the
    /// execution-start receipt) and immediately before invoking the provider. If
    /// authority changed, close the pending receipt without penalizing the provider
    /// and stop the survival session.
    /// </summary>
    private async Task RevalidateSurvivalImmediatelyBeforeDispatchAsync(
        RouteSurvivalAttemptSession session,
        LlmModelSelection selection,
        LlmRouterOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureSurvivalBindingStillCurrentAsync(session, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TrackCancellationAsync(
                    session.FunctionType,
                    selection,
                    responseTimeMs: 0,
                    options,
                    fallbackUsed: true)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException && (ex is not RoutingAuditPersistenceException))
        {
            await AppendSurvivalDispatchAbortedAuditAsync(
                    session.FunctionType,
                    selection,
                    options)
                .ConfigureAwait(false);

            if (ex is SurvivalBindingChangedException)
            {
                throw;
            }

            throw new SurvivalBindingChangedException(
                $"The proof-bound route survival authority for {session.FunctionType} could not be revalidated immediately before provider dispatch.",
                ex);
        }
    }

    private async Task AppendSurvivalDispatchAbortedAuditAsync(
        string functionType,
        LlmModelSelection selection,
        LlmRouterOptions? options)
    {
        try
        {
            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = LlmRoutingAuditEventKinds.Execution,
                Actor = "llm-router",
                Trigger = "fallback-execution-aborted",
                FunctionType = RoutingDiagnosticSanitizer.Sanitize(functionType, 160)
                               ?? "unknown-function",
                AfterProvider = RoutingDiagnosticSanitizer.Sanitize(selection.Provider, 80),
                AfterModel = RoutingDiagnosticSanitizer.Sanitize(selection.Model, 240),
                AdvisorOrPolicy = RoutingDiagnosticSanitizer.Sanitize(selection.Reason, 1_000),
                Reason = "The proof-bound survival route was not dispatched because its current preference or survival-set authority changed after admission.",
                CorrelationId = selection.AuditCorrelationId,
                SessionId = options?.SessionId,
                TurnId = options?.TurnId,
                Succeeded = false,
                Error = "survival_authority_changed_before_dispatch",
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(
                ex,
                "Failed to close the aborted survival execution receipt for {FunctionType} on {Provider}/{Model}",
                functionType,
                selection.Provider,
                selection.Model);
            throw new RoutingAuditPersistenceException(
                $"The survival route was withheld before execution, but its terminal audit receipt could not be committed for '{functionType}'.",
                ex);
        }
    }

    private LlmModelSelection BuildSurvivalSelection(
        RouteSurvivalAttemptSession session,
        LlmRouteSurvivalCandidate candidate)
    {
        var runtimeReceipt = new ModelScoutRuntimeDecisionReceipt(
            session.Primary.ModelScoutDecisionReceipt?.AppliedWeights
            ?? new ModelScoutScoreWeights(0.5m, 0.25m, 0.25m),
            candidate.DecisionEvidence,
            [],
            candidate.FunctionEvidence) { FunctionType = session.FunctionType };
        return CarryRoutePolicy(session.Primary, new LlmModelSelection
        {
            Provider = ResolveRegisteredProviderName(candidate.Provider),
            Model = candidate.Model,
            IsDefault = false,
            IsUserPinned = false,
            Reason = SurvivalAlternateSelectionReason,
            Context = session.Primary.Context,
            AuditCorrelationId = session.SurvivalSet.CorrelationId,
            SupportsToolUse = candidate.SupportsToolUse,
            SupportsStructuredOutput = candidate.SupportsStructuredOutput,
            MaxContextTokens = candidate.MaxContextTokens,
            CapabilityEvidenceSource = candidate.DecisionEvidence.EvidenceSource
                                       ?? candidate.FunctionEvidence.Source,
            CapabilityScore = candidate.DecisionEvidence.CapabilityScore,
            CapabilityEvidenceKind = candidate.DecisionEvidence.CapabilityProvenance,
            ModelScoutDecisionReceipt = runtimeReceipt,
        });
    }

    private static LlmFailoverAttempt CreateSurvivalAttempt(
        LlmModelSelection selection,
        LlmFailureCategory category,
        string diagnostic,
        bool wasExecuted)
        => new(
            selection.Provider,
            selection.Model,
            LlmRouteSurvivalSetValidation.CanonicalExecutionChannel(selection.Provider),
            category,
            RoutingDiagnosticSanitizer.Sanitize(diagnostic, maxLength: 768) ?? "route failure",
            wasExecuted);

    private static string PhysicalRouteIdentity(string provider, string model)
    {
        var channel = LlmRouteSurvivalSetValidation.CanonicalExecutionChannel(provider);
        var normalizedModel = channel == "ollama"
            ? NormalizeOllamaModelTag(model)
            : model.Trim();
        return $"{channel}\u001f{normalizedModel}";
    }

    private static bool SamePhysicalRoute(
        string leftProvider,
        string leftModel,
        string rightProvider,
        string rightModel)
        => string.Equals(
               PhysicalRouteIdentity(leftProvider, leftModel),
               PhysicalRouteIdentity(rightProvider, rightModel),
               StringComparison.OrdinalIgnoreCase);

    private void RecordSurvivalFailure(
        RouteSurvivalAttemptSession session,
        LlmModelSelection selection,
        LlmFailureCategory category,
        string diagnostic,
        bool wasExecuted = true)
    {
        session.Attempts.Add(CreateSurvivalAttempt(selection, category, diagnostic, wasExecuted));
    }

    private static bool IsRouteSurvivalAlternate(LlmModelSelection selection)
        => string.Equals(
            selection.Reason,
            SurvivalAlternateSelectionReason,
            StringComparison.Ordinal);

    private static bool IsCallerCancellationTerminal(string? error)
        => string.Equals(error, "request_cancelled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               error,
               "Survival tool-use provider call was cancelled.",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               error,
               "Survival tool-use stream was cancelled.",
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Emits the route-health terminal event consumed by Model Scout in addition to
    /// the router's backward-compatible generic execution receipt. These rows are
    /// intentionally limited to a currently validated survival session so a random
    /// direct call cannot manufacture route-health evidence.
    /// </summary>
    private async Task AppendRouteSurvivalTerminalAuditAsync(
        string functionType,
        LlmModelSelection selection,
        LlmRouterOptions? options,
        bool succeeded,
        bool fallbackUsed,
        long responseTimeMs,
        int totalTokens,
        decimal estimatedCost,
        string? error)
    {
        try
        {
            var modelScoutDecision = selection.ModelScoutDecisionReceipt;
            var functionEvidence = modelScoutDecision?.FunctionEvidence;
            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = succeeded
                    ? LlmRoutingAuditEventKinds.RouteExecuted
                    : LlmRoutingAuditEventKinds.RouteExecutionFailed,
                Actor = "llm-router",
                Trigger = succeeded
                    ? "survival-route-executed"
                    : "survival-route-execution-failed",
                FunctionType = RoutingDiagnosticSanitizer.Sanitize(functionType, 160)
                               ?? "unknown-function",
                Context = selection.Context is null
                    ? null
                    : $"{selection.Context.Interactivity}/{selection.Context.PrivacyClass}/{selection.Context.Urgency}",
                AfterProvider = RoutingDiagnosticSanitizer.Sanitize(selection.Provider, 80),
                AfterModel = RoutingDiagnosticSanitizer.Sanitize(selection.Model, 240),
                AdvisorOrPolicy = LlmRouteSurvivalSet.Schema,
                Reason = succeeded
                    ? "The proof-bound survival route completed successfully."
                    : "The proof-bound survival route failed with a failover-eligible provider or reply category.",
                EvidenceJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    schema = LlmRouteSurvivalSet.Schema,
                    state = succeeded ? "executed" : "execution-failed",
                    fallbackUsed,
                    executionChannel = LlmRouteSurvivalSetValidation.CanonicalExecutionChannel(selection.Provider),
                    responseTimeMs,
                    totalTokens,
                    estimatedCost,
                    modelScoutDecision,
                }, RoutingAuditJsonOptions),
                CorrelationId = selection.AuditCorrelationId,
                SessionId = options?.SessionId,
                TurnId = options?.TurnId,
                IsUserOverride = false,
                Succeeded = succeeded,
                Error = RoutingDiagnosticSanitizer.Sanitize(error, 2_000),
                BenchmarkSuiteSha256 = functionEvidence?.SuiteSha256,
            }).ConfigureAwait(false);
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogError(
                ex,
                "Failed to append survival route terminal audit for {FunctionType} on {Provider}/{Model}",
                functionType,
                selection.Provider,
                selection.Model);
            throw new RoutingAuditPersistenceException(
                $"The survival route completed, but its route-health receipt could not be committed for '{functionType}'. The response is withheld to prevent unaudited execution.",
                ex);
        }
    }

    private static LlmFailoverException CreateSurvivalFailoverException(
        RouteSurvivalAttemptSession session,
        string requestId,
        string fallbackDiagnostic,
        Exception? innerException = null)
    {
        var fallback = session.LastSelection ?? CarryRoutePolicy(session.Primary, new LlmModelSelection
        {
            Provider = "unresolved",
            Model = "unresolved",
            IsDefault = false,
            Context = session.Primary.Context,
            AuditCorrelationId = session.Primary.AuditCorrelationId,
            Reason = "The proof-bound survival set had no executable alternate.",
        });
        var primaryAttempt = session.Attempts[0];
        var fallbackAttempt = session.Attempts.LastOrDefault() ?? primaryAttempt;
        return new LlmFailoverException(
            session.FunctionType,
            requestId,
            session.Primary,
            fallback,
            primaryAttempt.Error,
            RoutingDiagnosticSanitizer.Sanitize(fallbackDiagnostic, maxLength: 768) ?? "route failure",
            primaryException: innerException,
            fallbackException: innerException,
            primaryCategory: primaryAttempt.Category,
            fallbackCategory: fallbackAttempt.Category,
            attempts: session.Attempts.ToArray());
    }

    private async Task RecordSurvivalCooldownAsync(
        LlmModelSelection selection,
        LlmFailureCategory category,
        string diagnostic)
    {
        if (category is LlmFailureCategory.RateLimit or LlmFailureCategory.QuotaExhausted)
        {
            await RecordFailedResponseCooldownAsync(selection.Provider, "rate_limit", diagnostic)
                .ConfigureAwait(false);
            return;
        }

        if (category == LlmFailureCategory.Timeout)
        {
            await RecordFailedResponseCooldownAsync(selection.Provider, "timeout", diagnostic)
                .ConfigureAwait(false);
            return;
        }

        var reason = LlmErrorPolicy.Resolve(
                category,
                Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(selection.Provider),
                diagnostic)
            .LegacyReason;
        await RecordFailedResponseCooldownAsync(selection.Provider, reason, diagnostic)
            .ConfigureAwait(false);
    }

    private async Task<SurvivalResponseExecution?> TryExecuteResponseSurvivalSetAsync(
        string functionType,
        string prompt,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options,
        bool requiresVision,
        string reason,
        string requestId,
        LlmFailureCategory? primaryCategory,
        Func<ILlmProvider, LlmModelSelection, Dictionary<string, object>, Task<LlmResponse>> executeAsync)
    {
        var category = ResolveSurvivalFailureCategory(primaryCategory, reason, reason);
        var cancellationToken = options?.RequestCancellationToken ?? CancellationToken.None;
        var session = await TryCreateRouteSurvivalSessionAsync(
                functionType,
                selectedModel,
                options,
                requiresVision,
                $"Primary {selectedModel.Provider}/{selectedModel.Model} failed ({reason}).",
                category,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        if (!IsSurvivalFailoverCategory(category))
        {
            throw CreateSurvivalFailoverException(
                session,
                requestId,
                $"Automatic failover stopped on non-retryable {category}.");
        }

        while (await TryGetNextSurvivalSelectionAsync(session, cancellationToken).ConfigureAwait(false)
               is { } selection)
        {
            var provider = _providerRegistry.GetProvider(selection.Provider)!;
            var executionSettings = BuildExecutionSettings(options, selection);
            var stopwatch = Stopwatch.StartNew();
            var executionStarted = false;
            try
            {
                await AppendFallbackSelectionAuditAsync(
                    functionType,
                    prompt,
                    selectedModel,
                    selection,
                    options,
                    $"survival-set:{reason}",
                    requiresVision: requiresVision).ConfigureAwait(false);
                using var localSlot = await AcquireLocalSlotAsync(
                    selection.Provider,
                    selection.Model,
                    selection.Context,
                    executionSettings,
                    requiresVision ? LocalLlmWorkloadKind.Vision : LocalLlmWorkloadKind.Text,
                    ct: cancellationToken).ConfigureAwait(false);
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selection,
                    options,
                    fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
                await RevalidateSurvivalImmediatelyBeforeDispatchAsync(
                        session,
                        selection,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                stopwatch.Restart();
                executionStarted = true;
                var response = await executeAsync(provider, selection, executionSettings)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                if (response is null)
                {
                    throw new InvalidOperationException(
                        $"Survival route {selection.Provider}/{selection.Model} returned a null response.");
                }

                if (!response.WasSuccessful)
                {
                    var diagnostic = response.ErrorMessage ?? "survival_provider_failure";
                    var failedCategory = ResolveSurvivalFailureCategory(null, diagnostic);
                    await TrackErrorAsync(
                        functionType,
                        selection,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        diagnostic,
                        fallbackUsed: true).ConfigureAwait(false);
                    await RecordSurvivalCooldownAsync(selection, failedCategory, diagnostic)
                        .ConfigureAwait(false);
                    RecordSurvivalFailure(session, selection, failedCategory, diagnostic);
                    if (!IsSurvivalFailoverCategory(failedCategory))
                    {
                        throw CreateSurvivalFailoverException(session, requestId, diagnostic);
                    }
                    continue;
                }

                var qualityIssue = ValidateResponseQuality(response, options, out var normalizedContent);
                if (qualityIssue is not null)
                {
                    var diagnostic = $"quality_gate:{qualityIssue}";
                    await TrackQualityFailureAsync(
                        functionType,
                        selection,
                        prompt,
                        response,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        qualityIssue,
                        fallbackUsed: true).ConfigureAwait(false);
                    RecordSurvivalFailure(
                        session,
                        selection,
                        LlmFailureCategory.MalformedReply,
                        diagnostic);
                    if (IsTerminalCallerContractFailure(qualityIssue, options))
                    {
                        throw new LlmResponseContractException(
                            functionType,
                            selection.Provider,
                            selection.Model);
                    }
                    continue;
                }

                if (normalizedContent is not null)
                {
                    response.Content = normalizedContent;
                }
                await TrackUsageAsync(
                    functionType,
                    selection,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    fallbackUsed: true,
                    requiresVision: requiresVision).ConfigureAwait(false);
                return new SurvivalResponseExecution(response.Content, response, selection);
            }
            catch (RoutingAuditPersistenceException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (executionStarted)
                {
                    await TrackCancellationAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        fallbackUsed: true).ConfigureAwait(false);
                }
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
            catch (SurvivalBindingChangedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                stopwatch.Stop();
                var failedCategory = ResolveSurvivalFailureCategory(null, ex.Message);
                if (executionStarted)
                {
                    await TrackErrorAsync(
                        functionType,
                        selection,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        ex.Message,
                        fallbackUsed: true).ConfigureAwait(false);
                }
                await RecordSurvivalCooldownAsync(selection, failedCategory, ex.Message)
                    .ConfigureAwait(false);
                RecordSurvivalFailure(session, selection, failedCategory, ex.Message, executionStarted);
                if (!IsSurvivalFailoverCategory(failedCategory))
                {
                    throw CreateSurvivalFailoverException(session, requestId, ex.Message, ex);
                }
            }
        }

        throw CreateSurvivalFailoverException(
            session,
            requestId,
            "All admitted proof-bound survival routes were exhausted.");
    }

    private Task<SurvivalResponseExecution?> TryExecuteTextSurvivalSetAsync(
        string functionType,
        string prompt,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options,
        string reason,
        string requestId,
        LlmFailureCategory? primaryCategory)
        => TryExecuteResponseSurvivalSetAsync(
            functionType,
            prompt,
            selectedModel,
            options,
            requiresVision: false,
            reason,
            requestId,
            primaryCategory,
            (provider, selection, executionSettings) => provider.GenerateResponseAsync(
                prompt,
                selection.Model,
                executionSettings));

    private Task<SurvivalResponseExecution?> TryExecuteVisionSurvivalSetAsync(
        string functionType,
        string prompt,
        string imageBase64,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options,
        string reason,
        string requestId,
        LlmFailureCategory? primaryCategory)
        => TryExecuteResponseSurvivalSetAsync(
            functionType,
            prompt,
            selectedModel,
            options,
            requiresVision: true,
            reason,
            requestId,
            primaryCategory,
            (provider, selection, executionSettings) => provider.GenerateVisionResponseAsync(
                prompt,
                imageBase64,
                selection.Model,
                executionSettings));

    private async Task<SurvivalStreamingExecution?> TryExecuteStreamingSurvivalSetAsync(
        string functionType,
        string prompt,
        LlmModelSelection selectedModel,
        Func<string, Task> onChunk,
        LlmRouterOptions? options,
        string reason,
        LlmFailureCategory? primaryCategory)
    {
        var category = ResolveSurvivalFailureCategory(primaryCategory, reason, reason);
        var cancellationToken = options?.RequestCancellationToken ?? CancellationToken.None;
        var session = await TryCreateRouteSurvivalSessionAsync(
                functionType,
                selectedModel,
                options,
                requiresVision: false,
                $"Primary {selectedModel.Provider}/{selectedModel.Model} failed ({reason}).",
                category,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        if (!IsSurvivalFailoverCategory(category))
        {
            throw CreateSurvivalFailoverException(
                session,
                "streaming",
                $"Automatic failover stopped on non-retryable {category}.");
        }

        while (await TryGetNextSurvivalSelectionAsync(session, cancellationToken).ConfigureAwait(false)
               is { } selection)
        {
            var provider = _providerRegistry.GetProvider(selection.Provider)!;
            var executionSettings = BuildExecutionSettings(options, selection);
            var bufferedChunks = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            var executionStarted = false;
            try
            {
                await AppendFallbackSelectionAuditAsync(
                    functionType,
                    prompt,
                    selectedModel,
                    selection,
                    options,
                    $"survival-set:{reason}").ConfigureAwait(false);
                using var localSlot = await AcquireLocalSlotAsync(
                    selection.Provider,
                    selection.Model,
                    selection.Context,
                    executionSettings,
                    ct: cancellationToken).ConfigureAwait(false);
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selection,
                    options,
                    fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
                await RevalidateSurvivalImmediatelyBeforeDispatchAsync(
                        session,
                        selection,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                stopwatch.Restart();
                executionStarted = true;
                LlmResponse response;
                if (provider.SupportsStreaming)
                {
                    response = await provider.GenerateStreamingResponseAsync(
                            prompt,
                            selection.Model,
                            chunk =>
                            {
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    bufferedChunks.Add(chunk);
                                }
                                return Task.CompletedTask;
                            },
                            executionSettings)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    response = await provider.GenerateResponseAsync(
                            prompt,
                            selection.Model,
                            executionSettings)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (response is not null && response.WasSuccessful)
                    {
                        bufferedChunks.Add(response.Content);
                    }
                }
                stopwatch.Stop();

                if (response is null)
                {
                    throw new InvalidOperationException(
                        $"Streaming survival route {selection.Provider}/{selection.Model} returned a null response.");
                }
                if (!response.WasSuccessful)
                {
                    var diagnostic = response.ErrorMessage ?? "streaming_survival_provider_failure";
                    var failedCategory = ResolveSurvivalFailureCategory(null, diagnostic);
                    await TrackErrorAsync(
                        functionType,
                        selection,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        diagnostic,
                        fallbackUsed: true).ConfigureAwait(false);
                    await RecordSurvivalCooldownAsync(selection, failedCategory, diagnostic)
                        .ConfigureAwait(false);
                    RecordSurvivalFailure(session, selection, failedCategory, diagnostic);
                    if (!IsSurvivalFailoverCategory(failedCategory))
                    {
                        throw CreateSurvivalFailoverException(session, "streaming", diagnostic);
                    }
                    continue;
                }

                var qualityIssue = ValidateResponseQuality(response, options, out var normalizedContent);
                if (qualityIssue is not null)
                {
                    var diagnostic = $"quality_gate:{qualityIssue}";
                    await TrackQualityFailureAsync(
                        functionType,
                        selection,
                        prompt,
                        response,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        qualityIssue,
                        fallbackUsed: true).ConfigureAwait(false);
                    RecordSurvivalFailure(session, selection, LlmFailureCategory.MalformedReply, diagnostic);
                    if (IsTerminalCallerContractFailure(qualityIssue, options))
                    {
                        throw new LlmResponseContractException(
                            functionType,
                            selection.Provider,
                            selection.Model);
                    }
                    continue;
                }
                if (normalizedContent is not null)
                {
                    response.Content = normalizedContent;
                }

                await TrackUsageAsync(
                    functionType,
                    selection,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    fallbackUsed: true).ConfigureAwait(false);

                // A recovery stream is transactional until the physical route has
                // produced a successful terminal response. Only then may its buffered
                // chunks become visible; a failed alternate can never leak into the
                // next route's answer.
                if (bufferedChunks.Count == 0 && !string.IsNullOrEmpty(response.Content))
                {
                    bufferedChunks.Add(response.Content);
                }
                try
                {
                    foreach (var chunk in bufferedChunks)
                    {
                        await onChunk(chunk).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                {
                    // The provider route has already succeeded and a callback may
                    // have made one or more chunks visible. A consumer failure is
                    // never provider evidence and can never authorize another route.
                    throw new PartialStreamingOutputException(
                        $"The successful survival stream from {selection.Provider}/{selection.Model} could not be delivered completely; fallback is suppressed because output may already be visible.",
                        ex);
                }

                return new SurvivalStreamingExecution(new LlmRouterResponse
                {
                    Content = response.Content,
                    Provider = selection.Provider,
                    Model = selection.Model,
                    FunctionType = functionType,
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens,
                    EstimatedCost = response.EstimatedCost,
                    Duration = stopwatch.Elapsed,
                    UsedFallback = true,
                    FallbackReason = reason,
                });
            }
            catch (PartialStreamingOutputException)
            {
                throw;
            }
            catch (RoutingAuditPersistenceException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (executionStarted)
                {
                    await TrackCancellationAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        fallbackUsed: true).ConfigureAwait(false);
                }
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
            catch (SurvivalBindingChangedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                stopwatch.Stop();
                var failedCategory = ResolveSurvivalFailureCategory(null, ex.Message);
                if (executionStarted)
                {
                    await TrackErrorAsync(
                        functionType,
                        selection,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        ex.Message,
                        fallbackUsed: true).ConfigureAwait(false);
                }
                await RecordSurvivalCooldownAsync(selection, failedCategory, ex.Message)
                    .ConfigureAwait(false);
                RecordSurvivalFailure(session, selection, failedCategory, ex.Message, executionStarted);
                if (!IsSurvivalFailoverCategory(failedCategory))
                {
                    throw CreateSurvivalFailoverException(session, "streaming", ex.Message, ex);
                }
            }
        }

        throw CreateSurvivalFailoverException(
            session,
            "streaming",
            "All admitted proof-bound streaming survival routes were exhausted.");
    }

    private static string? GetToolUseReplyIssue(
        ChatToolUseRequest request,
        ChatToolUseResponse? response)
    {
        if (response is null)
        {
            return "The tool-use provider returned a null response.";
        }
        // A typed terminal failure retains usage and reaches the engine as an error.
        // Do not relabel refusals/incomplete outcomes as malformed replies or retry them.
        if (response.FailureCategory is not null) return null;
        // A token-limited completion is an incomplete, but valid, envelope.
        // Preserve its usage and stop reason for the agent's bounded recovery;
        // a required tool call may itself have been cut off by this limit.
        if (response.StopReason == ChatStopReason.MaxTokens) return null;
        if (request.RequireToolCall && response.ToolCalls.Count == 0)
        {
            return "The tool-use provider returned no required tool call.";
        }
        if (!IsSuccessfulToolUseCompletion(response))
        {
            return "The tool-use provider returned an empty response with no tool calls.";
        }
        return null;
    }

    private async Task<SurvivalToolExecution?> TryExecuteToolUseSurvivalSetAsync(
        string functionType,
        ChatToolUseRequest request,
        LlmModelSelection selectedModel,
        Exception primaryException,
        string promptSummary,
        string requestId,
        CancellationToken cancellationToken)
    {
        var category = ResolveSurvivalFailureCategory(null, primaryException.Message);
        var session = await TryCreateRouteSurvivalSessionAsync(
                functionType,
                selectedModel,
                request.Options,
                request.Options?.RequiresVision == true,
                primaryException.Message,
                category,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }
        if (!IsSurvivalFailoverCategory(category))
        {
            throw CreateSurvivalFailoverException(
                session,
                requestId,
                $"Automatic failover stopped on non-retryable {category}.",
                primaryException);
        }

        while (await TryGetNextSurvivalSelectionAsync(session, cancellationToken).ConfigureAwait(false)
               is { } selection)
        {
            var fallbackOptions = CloneOrCreate(request.Options);
            fallbackOptions.OverrideProvider = selection.Provider;
            fallbackOptions.OverrideModel = selection.Model;
            fallbackOptions.OverrideProvenance = LlmOverrideProvenance.Fallback;
            fallbackOptions.RequiresToolUse = true;
            fallbackOptions.ExecutionSettings = MergeToolUseExecutionSettings(
                selection,
                request.Options?.ExecutionSettings,
                cancellationToken);
            var fallbackRequest = request with { Options = fallbackOptions };
            var stopwatch = Stopwatch.StartNew();
            var executionStarted = false;
            try
            {
                EnsureToolUseSelectionSupported(selection);
                var translator = ResolveTranslator(selection.Provider);
                await AppendFallbackSelectionAuditAsync(
                    functionType,
                    promptSummary,
                    selectedModel,
                    selection,
                    request.Options,
                    "survival-set:tool-use",
                    requiresToolUse: true).ConfigureAwait(false);
                using var localSlot = await AcquireLocalSlotAsync(
                    selection.Provider,
                    selection.Model,
                    selection.Context,
                    fallbackOptions.ExecutionSettings,
                    ct: cancellationToken).ConfigureAwait(false);
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selection,
                    fallbackOptions,
                    fallbackUsed: true, physicalSettings: fallbackOptions.ExecutionSettings).ConfigureAwait(false);
                await RevalidateSurvivalImmediatelyBeforeDispatchAsync(
                        session,
                        selection,
                        fallbackOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(fallbackOptions).ConfigureAwait(false);
                stopwatch.Restart();
                executionStarted = true;
                var response = await translator.ChatAsync(fallbackRequest, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                if (GetToolUseReplyIssue(request, response) is { } replyIssue)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        replyIssue,
                        fallbackUsed: true).ConfigureAwait(false);
                    RecordSurvivalFailure(
                        session,
                        selection,
                        LlmFailureCategory.MalformedReply,
                        replyIssue);
                    continue;
                }

                RecordObservedToolUseCapability(selection, response);
                await TrackToolUseUsageAsync(
                    functionType,
                    selection,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    fallbackOptions,
                    promptSummary,
                    fallbackUsed: true).ConfigureAwait(false);
                if (IsSuccessfulToolUseCompletion(response))
                {
                    await MaterializeFunctionPreferenceIfMissingAsync(
                            functionType,
                            selection,
                            request.Options,
                            requiresVision: request.Options?.RequiresVision == true)
                        .ConfigureAwait(false);
                }
                return new SurvivalToolExecution(response);
            }
            catch (RoutingAuditPersistenceException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (executionStarted)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        "Survival tool-use provider call was cancelled.",
                        fallbackUsed: true).ConfigureAwait(false);
                }
                throw;
            }
            catch (LlmFailoverException)
            {
                throw;
            }
            catch (SurvivalBindingChangedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                stopwatch.Stop();
                var failedCategory = ResolveSurvivalFailureCategory(null, ex.Message);
                if (executionStarted)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        ex.Message,
                        fallbackUsed: true).ConfigureAwait(false);
                }
                await RecordSurvivalCooldownAsync(selection, failedCategory, ex.Message)
                    .ConfigureAwait(false);
                RecordSurvivalFailure(session, selection, failedCategory, ex.Message, executionStarted);
                if (!IsSurvivalFailoverCategory(failedCategory))
                {
                    throw CreateSurvivalFailoverException(session, requestId, ex.Message, ex);
                }
            }
        }

        throw CreateSurvivalFailoverException(
            session,
            requestId,
            "All admitted proof-bound tool-use survival routes were exhausted.",
            primaryException);
    }

    private async Task<SurvivalToolStreamExecution?> TryExecuteToolUseStreamSurvivalSetAsync(
        string functionType,
        ChatToolUseRequest request,
        LlmModelSelection selectedModel,
        Exception primaryException,
        string promptSummary,
        string requestId,
        CancellationToken cancellationToken)
    {
        var category = ResolveSurvivalFailureCategory(null, primaryException.Message);
        var session = await TryCreateRouteSurvivalSessionAsync(
                functionType,
                selectedModel,
                request.Options,
                request.Options?.RequiresVision == true,
                primaryException.Message,
                category,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }
        if (!IsSurvivalFailoverCategory(category))
        {
            throw CreateSurvivalFailoverException(
                session,
                requestId,
                $"Automatic failover stopped on non-retryable {category}.",
                primaryException);
        }

        while (await TryGetNextSurvivalSelectionAsync(session, cancellationToken).ConfigureAwait(false)
               is { } selection)
        {
            var fallbackOptions = CloneOrCreate(request.Options);
            fallbackOptions.OverrideProvider = selection.Provider;
            fallbackOptions.OverrideModel = selection.Model;
            fallbackOptions.OverrideProvenance = LlmOverrideProvenance.Fallback;
            fallbackOptions.RequiresToolUse = true;
            fallbackOptions.ExecutionSettings = MergeToolUseExecutionSettings(
                selection,
                request.Options?.ExecutionSettings,
                cancellationToken);
            var fallbackRequest = request with { Options = fallbackOptions };
            var stopwatch = Stopwatch.StartNew();
            var executionStarted = false;
            try
            {
                EnsureToolUseSelectionSupported(selection);
                var translator = ResolveTranslator(selection.Provider);
                await AppendFallbackSelectionAuditAsync(
                    functionType,
                    promptSummary,
                    selectedModel,
                    selection,
                    request.Options,
                    "survival-set:tool-use-stream",
                    requiresToolUse: true).ConfigureAwait(false);
                using var localSlot = await AcquireLocalSlotAsync(
                    selection.Provider,
                    selection.Model,
                    selection.Context,
                    fallbackOptions.ExecutionSettings,
                    ct: cancellationToken).ConfigureAwait(false);
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selection,
                    fallbackOptions,
                    fallbackUsed: true, physicalSettings: fallbackOptions.ExecutionSettings).ConfigureAwait(false);
                await RevalidateSurvivalImmediatelyBeforeDispatchAsync(
                        session,
                        selection,
                        fallbackOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(fallbackOptions).ConfigureAwait(false);
                stopwatch.Restart();
                executionStarted = true;

                // Recovery streams are transactionally buffered. No reasoning,
                // protocol or text event from a failed alternate is committed to the
                // caller, so the next physical route starts with a clean transcript.
                var events = new List<ToolUseStreamEvent>();
                MessageStoppedEvent? terminal = null;
                await foreach (var streamEvent in translator
                                   .StreamAsync(fallbackRequest, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    events.Add(streamEvent);
                    if (streamEvent is MessageStoppedEvent stopped)
                    {
                        terminal = stopped;
                    }
                }
                stopwatch.Stop();

                if (terminal is null)
                {
                    throw new MalformedToolUseReplyException(
                        $"Tool-use survival route {selection.Provider}/{selection.Model} completed without a MessageStoppedEvent.");
                }
                if (GetToolUseReplyIssue(request, terminal.Response) is { } replyIssue)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        replyIssue,
                        fallbackUsed: true).ConfigureAwait(false);
                    RecordSurvivalFailure(
                        session,
                        selection,
                        LlmFailureCategory.MalformedReply,
                        replyIssue);
                    continue;
                }

                RecordObservedToolUseCapability(selection, terminal.Response);
                await TrackToolUseUsageAsync(
                    functionType,
                    selection,
                    terminal.Response,
                    stopwatch.ElapsedMilliseconds,
                    fallbackOptions,
                    promptSummary,
                    fallbackUsed: true).ConfigureAwait(false);
                if (IsSuccessfulToolUseCompletion(terminal.Response))
                {
                    await MaterializeFunctionPreferenceIfMissingAsync(
                            functionType,
                            selection,
                            request.Options,
                            requiresVision: request.Options?.RequiresVision == true)
                        .ConfigureAwait(false);
                }
                return new SurvivalToolStreamExecution(events);
            }
            catch (RoutingAuditPersistenceException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (executionStarted)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        "Survival tool-use stream was cancelled.",
                        fallbackUsed: true).ConfigureAwait(false);
                }
                throw;
            }
            catch (LlmFailoverException)
            {
                throw;
            }
            catch (SurvivalBindingChangedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                stopwatch.Stop();
                var failedCategory = ResolveSurvivalFailureCategory(null, ex.Message);
                if (executionStarted)
                {
                    await TrackToolUseErrorAsync(
                        functionType,
                        selection,
                        stopwatch.ElapsedMilliseconds,
                        fallbackOptions,
                        ex.Message,
                        fallbackUsed: true).ConfigureAwait(false);
                }
                await RecordSurvivalCooldownAsync(selection, failedCategory, ex.Message)
                    .ConfigureAwait(false);
                RecordSurvivalFailure(session, selection, failedCategory, ex.Message, executionStarted);
                if (!IsSurvivalFailoverCategory(failedCategory))
                {
                    throw CreateSurvivalFailoverException(session, requestId, ex.Message, ex);
                }
            }
        }

        throw CreateSurvivalFailoverException(
            session,
            requestId,
            "All admitted proof-bound streaming tool-use survival routes were exhausted.",
            primaryException);
    }
}
