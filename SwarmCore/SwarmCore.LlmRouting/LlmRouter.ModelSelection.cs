using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    private static readonly JsonSerializerOptions RoutingAuditJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RoutingSelectionTrace
    {
        public List<RoutingGovernorTrace> GovernorCalls { get; } = [];
    }

    private sealed record RoutingGovernorTrace(
        bool HardSafetyOnly,
        string? AdvisorName,
        LlmRoutePolicyKind PolicyKind,
        int Priority,
        RoutingPolicy Policy,
        BudgetPressureSnapshot BudgetPressure,
        IReadOnlyList<LlmRouteAdvisorEvaluation> Evaluations,
        IReadOnlyList<RoutingGovernorCandidateTrace> Candidates);

    private sealed record RoutingGovernorCandidateTrace(
        string AdvisorName,
        LlmRoutePolicyKind PolicyKind,
        int Priority,
        string Provider,
        string Model,
        string? FallbackProvider,
        string? FallbackModel,
        decimal? MinimumCapabilityScore,
        bool RequireMeasuredCapabilityScore,
        ModelScoutTier? RequiredTier,
        bool BlockWeakerPolicyFallback,
        bool RequireCurrentFunctionEvidence,
        bool RequiresVision,
        bool? SupportsToolUse,
        bool? SupportsStructuredOutput,
        int? MaxContextTokens,
        string? CapabilityEvidenceSource,
        bool PersistPreference,
        string? Reason);

    // Interface implementation (3 parameters)
    public Task<LlmModelSelection> SelectOptimalModelAsync(string functionType, string prompt, LlmRouterOptions? options = null)
        => SelectOptimalModelInternalAsync(functionType, prompt, options, requiresVision: options?.RequiresVision ?? false);

    // Audit wrapper shared by text, vision, streaming and tool-use routes. Keeping it
    // around the private selector means every production selection is correlated with
    // its eventual usage row, not only calls to the public inspection API.
    private async Task<LlmModelSelection> SelectOptimalModelInternalAsync(string functionType, string prompt,
        LlmRouterOptions? options = null, bool requiresVision = false)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        if (options?.ModelRequest is not null) options = CloneRouterOptions(options);
        var intrinsicVision = ModelScoutFunctionRequirements.RequiresVision(functionType);
        var intrinsicToolUse = ModelScoutFunctionRequirements.RequiresToolUse(functionType);
        var effectiveRequiresVision = requiresVision
                                      || options?.RequiresVision == true
                                      || intrinsicVision;
        if ((intrinsicVision && options?.RequiresVision != true)
            || (intrinsicToolUse && options?.RequiresToolUse != true))
        {
            // Function identity is a hard capability contract. Clone instead of
            // mutating caller-owned options because the same instance may be
            // reused concurrently for a different request.
            options = CloneRouterOptions(options);
            options.RequiresVision = effectiveRequiresVision;
            options.RequiresToolUse = options.RequiresToolUse || intrinsicToolUse;
        }
        var correlationId = Guid.NewGuid().ToString("N");
        var before = await TryReadPreferenceForAuditAsync(functionType).ConfigureAwait(false);
        var trace = new RoutingSelectionTrace();

        try
        {
            var selection = await SelectOptimalModelCoreAsync(
                functionType,
                prompt,
                options,
                effectiveRequiresVision,
                trace).ConfigureAwait(false);
            ValidateModelRequestRoute(functionType, options, selection.Provider, selection.Model);
            if (!IsScoutResearchRequest(options))
                selection = await EnsureHardRequirementsOrFallbackAsync(
                functionType,
                prompt,
                selection,
                effectiveRequiresVision,
                options).ConfigureAwait(false);
            selection = await AdmitScoutSelectionAsync(
                functionType, prompt, selection, options, effectiveRequiresVision).ConfigureAwait(false);
            ValidateModelRequestRoute(functionType, options, selection.Provider, selection.Model);
            // A Model Scout decision already has a durable recommendation receipt.
            // Reuse its correlation so recommendation → selection → execution/usage
            // is one joinable audit chain.
            correlationId = string.IsNullOrWhiteSpace(selection.AuditCorrelationId)
                ? correlationId
                : selection.AuditCorrelationId;
            selection.AuditCorrelationId = correlationId;

            await AppendSelectionAuditAsync(
                functionType,
                prompt,
                options,
                before,
                selection,
                correlationId,
                effectiveRequiresVision,
                error: null,
                failure: null,
                trace).ConfigureAwait(false);
            return selection;
        }
        catch (Exception ex)
        {
            await AppendSelectionAuditAsync(
                functionType,
                prompt,
                options,
                before,
                selection: null,
                correlationId,
                effectiveRequiresVision,
                error: ex.Message,
                failure: ex,
                trace).ConfigureAwait(false);
            throw;
        }
    }

    // Internal selection implementation with vision parameter.
    private async Task<LlmModelSelection> SelectOptimalModelCoreAsync(string functionType, string prompt,
        LlmRouterOptions? options,
        bool requiresVision,
        RoutingSelectionTrace trace)
    {
        // Resolve the effective call context (caller options + function-type profile defaults).
        // Every selection below will stamp this onto LlmModelSelection.Context for downstream
        // consumers (A/B selector, telemetry).
        var context = FunctionTypeProfiles.Resolve(
            functionType,
            options?.Interactivity ?? LlmInteractivity.Unspecified,
            options?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
            options?.Urgency ?? LlmUrgency.Unspecified,
            options?.Workload ?? LlmWorkloadKind.Unspecified);

        // Check for explicit overrides first
        if (!string.IsNullOrEmpty(options?.OverrideProvider) && !string.IsNullOrEmpty(options?.OverrideModel))
        {
            var overrideProvider = ResolveRegisteredProviderName(options.OverrideProvider);
            var isExactUserOrProfileRoute = options.OverrideProvenance is
                LlmOverrideProvenance.User or
                LlmOverrideProvenance.Profile;
            if (context.PrivacyClass == LlmPrivacyClass.LocalOnly
                && !await IsLocalRouteAsync(overrideProvider, options.OverrideModel))
            {
                throw new InvalidOperationException(
                    $"Override provider {overrideProvider} cannot be used for LocalOnly routing on {functionType}.");
            }

            if (IsScoutResearchRequest(options))
            {
                if (!await IsProviderAvailableAsync(overrideProvider, options.RequestCancellationToken,
                        waitForDeferredCapacity: true).ConfigureAwait(false)
                    || _providerRegistry.GetProvider(overrideProvider)?.SupportsModel(options.OverrideModel) != true)
                    throw new ModelScoutAdmissionException("Scout research candidate is unavailable.");
                return new LlmModelSelection
                {
                    Provider = overrideProvider, Model = options.OverrideModel, Context = context,
                    IsDefault = false, AllowPreferenceMaterialization = false,
                    Reason = "Model Scout exact research admission; production qualification is not granted",
                };
            }

            if (isExactUserOrProfileRoute)
            {
                ValidateModelRequestRoute(functionType, options, overrideProvider, options.OverrideModel);
                var exactPreference = new LlmFunctionPreference
                {
                    FunctionType = functionType,
                    PreferredProvider = overrideProvider,
                    PreferredModel = options.OverrideModel,
                    IsEnabled = true,
                    UpdatedBy = options.OverrideProvenance == LlmOverrideProvenance.User
                        ? LlmPreferenceAuthors.User
                        : "profile",
                    // Transient route for this call only, but downstream advisors read pin
                    // authority off PinnedByUserAt, so a user override must carry it.
                    PinnedByUserAt = options.OverrideProvenance == LlmOverrideProvenance.User
                        ? DateTime.UtcNow
                        : null,
                };
                var safetySelection = await TryGetAdvisedSelectionAsync(
                    functionType,
                    prompt,
                    context,
                    exactPreference,
                    requiresVision,
                    options,
                    trace,
                    hardSafetyOnly: true,
                    exactProviderConstraint: overrideProvider,
                    exactModelConstraint: options.OverrideModel).ConfigureAwait(false);
                if (safetySelection is not null)
                {
                    safetySelection.IsUserPinned = options.OverrideProvenance == LlmOverrideProvenance.User;
                    safetySelection.Reason = $"Exact {options.OverrideProvenance} route admitted by hard safety policy: {safetySelection.Reason}";
                    return safetySelection;
                }
            }

            // Message-pipeline sovereignty chooses the exact route; it does not
            // bypass the host's production-proof and spend authority. Require the
            // same exact, current receipt for User/Profile overrides that the
            // enrichment path supplies for automatic routes. Failing this check is
            // terminal for the requested route and must happen before Scout or a
            // provider is invoked.
            if (isExactUserOrProfileRoute && RequiresStrictCurrentFunctionEvidence(functionType))
            {
                var admitted = await TrySelectValidatedAdmissionAsync(
                        functionType,
                        context,
                        overrideProvider,
                        options.OverrideModel,
                        requiresVision,
                        options)
                    .ConfigureAwait(false);
                if (admitted is not null)
                {
                    admitted.IsUserPinned = options.OverrideProvenance == LlmOverrideProvenance.User;
                    admitted.Reason = $"Exact {options.OverrideProvenance} route admitted by host proof authority: {admitted.Reason}";
                    return admitted;
                }

                throw new LlmRouteAdmissionRejectedException(
                    functionType,
                    overrideProvider,
                    options.OverrideModel,
                    $"Exact {options.OverrideProvenance} route {overrideProvider}/{options.OverrideModel} requires a current validated production admission for {functionType}.");
            }

            if (options.OverrideProvenance == LlmOverrideProvenance.ValidatedAdmission)
            {
                var admitted = await TrySelectValidatedAdmissionAsync(
                        functionType,
                        context,
                        overrideProvider,
                        options.OverrideModel,
                        requiresVision,
                        options)
                    .ConfigureAwait(false);
                if (admitted is not null)
                {
                    return admitted;
                }

                // A dedicated proof receipt is an exact authority decision. It
                // must never silently degrade into generic Scout admission or a
                // different model when validation fails.
                throw new LlmRouteAdmissionRejectedException(
                    functionType,
                    overrideProvider,
                    options.OverrideModel,
                    $"Validated route admission for {overrideProvider}/{options.OverrideModel} is missing, stale, or no longer satisfies the hard requirements for {functionType}.");
            }

            // User/profile routes are sovereign exact constraints. Every other exact
            // override is an internal control-plane instruction (policy, fallback,
            // self-heal, benchmark, or unspecified) and must carry fresh
            // function-scoped Scout proof before it can reach a provider.
            var exactProvider = _providerRegistry.GetProvider(overrideProvider);
            if (isExactUserOrProfileRoute
                && HasExplicitModelScoutConstraint(options))
            {
                var constrainedSelection = await TryReadmitExactAutomaticRouteAsync(
                    functionType,
                    prompt,
                    context,
                    overrideProvider,
                    options.OverrideModel,
                    requiresVision,
                    options,
                    $"Exact {options.OverrideProvenance} route with caller constraint envelope",
                    options.MinimumCapabilityScore,
                    options.RequiredTier,
                    options.RequireMeasuredCapabilityScore,
                    options.BlockWeakerPolicyFallback,
                    options.RequireCurrentFunctionEvidence).ConfigureAwait(false);
                if (constrainedSelection is not null)
                {
                    constrainedSelection.IsUserPinned = options.OverrideProvenance == LlmOverrideProvenance.User;
                    return constrainedSelection;
                }

                throw MissingExplicitModelScoutConstraintRoute(functionType);
            }

            if (isExactUserOrProfileRoute
                && await IsProviderAvailableAsync(
                    overrideProvider,
                    options.RequestCancellationToken,
                    waitForDeferredCapacity: true,
                    // A cooldown protects AUTOMATIC rotation from a flaky
                    // provider; it must not override an explicit pin. Observed
                    // live: one transient CLI failure mid-turn recorded a
                    // 15-minute cooldown, and the very next iteration of the
                    // SAME turn died with "Exact User route … is unavailable".
                    // If the pinned provider is genuinely down, the call fails
                    // visibly and the engine's retry/wrap-up handles it.
                    bypassCooldownGate: true,
                    honorPinWithoutDiscovery: true)
                && exactProvider?.SupportsModel(options.OverrideModel) == true)
            {
                return new LlmModelSelection
                {
                    Provider = overrideProvider,
                    Model = options.OverrideModel,
                    IsDefault = false,
                    Reason = "Explicit override specified",
                    IsUserPinned = options.OverrideProvenance == LlmOverrideProvenance.User,
                    Context = context
                };
            }

            // A Fallback-provenance override that names the user's own explicitly
            // configured fallback carries the user's authority. The string path
            // executes that route directly (ResolveFallbackRouteAsync treats it
            // as an explicit directed fallback), so the typed-JSON retry must not
            // be held to Scout proof the user never required — without this the
            // retry threw "Exact Fallback route … is unavailable" on every pinned
            // route whose primary answered well-formed JSON of the wrong shape.
            // Any other Fallback-provenance override is an internal control-plane
            // route and still needs fresh function-scoped proof below.
            if (options.OverrideProvenance == LlmOverrideProvenance.Fallback
                && await IsUserDirectedConfiguredFallbackAsync(functionType, overrideProvider, options.OverrideModel)
                    .ConfigureAwait(false)
                && exactProvider?.SupportsModel(options.OverrideModel) == true
                && await IsProviderAvailableAsync(
                    overrideProvider,
                    options.RequestCancellationToken,
                    waitForDeferredCapacity: true,
                    bypassCooldownGate: true,
                    honorPinWithoutDiscovery: true).ConfigureAwait(false))
            {
                return new LlmModelSelection
                {
                    Provider = overrideProvider,
                    Model = options.OverrideModel,
                    IsDefault = false,
                    Reason = "Explicit user-configured fallback admitted as the exact retry route",
                    IsUserPinned = true,
                    Context = context,
                };
            }

            if (!isExactUserOrProfileRoute)
            {
                var provenOverride = await TryReadmitExactAutomaticRouteAsync(
                    functionType,
                    prompt,
                    context,
                    overrideProvider,
                    options.OverrideModel,
                    requiresVision,
                    options,
                    $"{options.OverrideProvenance} exact override",
                    options.MinimumCapabilityScore,
                    options.RequiredTier,
                    options.RequireMeasuredCapabilityScore,
                    options.BlockWeakerPolicyFallback).ConfigureAwait(false);
                if (provenOverride is not null)
                {
                    provenOverride.IsDefault = false;
                    return provenOverride;
                }
            }

            if (isExactUserOrProfileRoute || options.DisableFallback)
            {
                throw new InvalidOperationException(
                    $"Exact {options.OverrideProvenance} route {overrideProvider}/{options.OverrideModel} is unavailable or unsupported for {functionType}.");
            }
            _logger.LogWarning(
                "Exact {Provenance} route {Provider}/{Model} was not re-admitted by current proof for {FunctionType}; continuing through ordinary routing",
                options.OverrideProvenance,
                overrideProvider,
                options.OverrideModel,
                functionType);
        }

        // Profiles may constrain only the provider and deliberately leave the
        // model blank ("let the router choose within this provider"). This is a
        // real route constraint, not an incomplete exact override. Ask Model
        // Scout to rank inside that provider and never silently escape to a
        // different provider.
        if (!string.IsNullOrWhiteSpace(options?.OverrideProvider)
            && string.IsNullOrWhiteSpace(options.OverrideModel))
        {
            var constrainedProvider = ResolveRegisteredProviderName(options.OverrideProvider);
            if (context.PrivacyClass == LlmPrivacyClass.LocalOnly && !await IsLocalProviderAsync(constrainedProvider))
            {
                throw new InvalidOperationException(
                    $"Provider constraint {constrainedProvider} cannot be used for LocalOnly routing on {functionType}.");
            }
            if (!await IsProviderAvailableAsync(
                    constrainedProvider,
                    options?.RequestCancellationToken ?? CancellationToken.None,
                    waitForDeferredCapacity: true).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Provider constraint {constrainedProvider} is not available for {functionType}.");
            }

            var scoutSelection = await TryGetModelScoutDefaultAsync(
                functionType, prompt, context, requiresVision, options).ConfigureAwait(false);
            if (scoutSelection is null)
                throw new LlmRouteSelectionBlockedException(
                    $"Model Scout has no qualified executable model on {constrainedProvider} for {functionType}; recovery remains active.");
            scoutSelection.IsDefault = false;
            scoutSelection.Reason = $"Provider-constrained {scoutSelection.Reason}";
            return scoutSelection;
        }

        if (string.Equals(functionType, FunctionTypes.MessagePipeline, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Message-pipeline routing requires an exact route and current admission receipt from its dedicated routing plan/guard. Generic function preferences and default advisors are not authoritative for this lane.");
        }

        // A/B testing intentionally does NOT influence live routing. Live calls always
        // go to the configured/preferred model for the function type (the "champion").
        // Sampling challengers is offline: AbTestReplayService periodically picks
        // captured prompts from LlmUsageTracking and feeds them to challenger
        // models for the judge to compare. This avoids the failure mode where
        // two iterations of the same tool-use loop got served by different
        // providers — e.g. iteration 0 by OpenAI/gpt-5 and iteration 1 by
        // DeepSeek thinking-mode, which 400s with "reasoning_content must be
        // passed back to the API" because the prior assistant turn was produced
        // by a provider that doesn't emit it.

        // Look up function-specific preference. The settings provider walks the
        // function type fallback chain internally (e.g. "email-search-analysis" →
        // "data-analysis" → "default") so callers don't need to know the hierarchy.
        var preference = await _settingsProvider.GetFunctionPreferenceAsync(functionType);
        var isUserPinnedPreference = LlmPreferenceAuthors.IsUserPinned(preference?.PinnedByUserAt);
        var automaticModelScoutSelectionEnabled =
            await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false);
        var configuredPreferenceIsExact =
            isUserPinnedPreference || !automaticModelScoutSelectionEnabled;
        var requireCurrentWorkloadSizing = options?.RequireCurrentWorkloadSizing == true
                                           && automaticModelScoutSelectionEnabled
                                           && !isUserPinnedPreference;

        if (configuredPreferenceIsExact && preference is { IsEnabled: true })
        {
            var safetySelection = await TryGetAdvisedSelectionAsync(
                functionType,
                prompt,
                context,
                preference,
                requiresVision,
                options,
                trace,
                hardSafetyOnly: true,
                includeNonBlockingHardSafetyRecommendations: !configuredPreferenceIsExact,
                exactProviderConstraint: configuredPreferenceIsExact
                    ? preference.PreferredProvider
                    : null,
                exactModelConstraint: configuredPreferenceIsExact
                    ? preference.PreferredModel
                    : null,
                exactFallbackProviderConstraint: configuredPreferenceIsExact
                    ? preference.FallbackProvider
                    : null,
                exactFallbackModelConstraint: configuredPreferenceIsExact
                    ? preference.FallbackModel
                    : null).ConfigureAwait(false);
            if (safetySelection is not null)
            {
                safetySelection.IsUserPinned = isUserPinnedPreference;
                safetySelection.Reason = isUserPinnedPreference
                    ? $"User-pinned route admitted by hard safety policy: {safetySelection.Reason}"
                    : configuredPreferenceIsExact
                        ? $"Configured route admitted by hard safety policy while automatic optimization is off: {safetySelection.Reason}"
                        : $"Hard safety policy superseded the automatic cached route: {safetySelection.Reason}";
                return safetySelection;
            }
        }

        if (isUserPinnedPreference
            && preference is { IsEnabled: true }
            && HasExplicitModelScoutConstraint(options))
        {
            // A user pin remains the exact target, while an explicit caller
            // quality/proof envelope still has to be demonstrated. Re-admit the
            // pin itself instead of silently replacing it or rejecting it without
            // first checking the available Scout evidence.
            var provenPinned = await TryReadmitExactAutomaticRouteAsync(
                functionType,
                prompt,
                context,
                preference.PreferredProvider,
                preference.PreferredModel,
                requiresVision,
                options,
                "User-pinned route with caller constraint envelope",
                options!.MinimumCapabilityScore,
                options.RequiredTier,
                options.RequireMeasuredCapabilityScore,
                options.BlockWeakerPolicyFallback,
                options.RequireCurrentFunctionEvidence).ConfigureAwait(false);
            if (provenPinned is null)
            {
                throw MissingExplicitModelScoutConstraintRoute(functionType);
            }

            provenPinned.IsUserPinned = true;
            provenPinned.Reason = $"User-pinned route re-admitted under the caller constraint envelope: {provenPinned.Reason}";
            return provenPinned;
        }

        // Every automatic ordinary request is decided by Scout. Its host-owned
        // evidence cache is the serving cache; a saved preference is not an
        // independent model selector or permission to bypass current policy.
        if (!isUserPinnedPreference && automaticModelScoutSelectionEnabled)
        {
            var decision = await TryGetScoutPolicySelectionAsync(
                functionType, prompt, context, preference, requiresVision, options, trace).ConfigureAwait(false);
            if (decision is not null) return decision;
            if (HasExplicitModelScoutConstraint(options))
                throw MissingExplicitModelScoutConstraintRoute(functionType);
            if (requireCurrentWorkloadSizing)
                throw MissingCurrentWorkloadSizedRoute(functionType);
            throw new LlmRouteTemporarilyUnavailableException(
                    $"Model Scout has no executable route for {functionType}; recovery remains active.");
        }

        if (requireCurrentWorkloadSizing)
        {
            // The caller explicitly said a cached/default route is not a valid
            // proxy for this request's output envelope. This includes hosts that
            // have automatic selection enabled but failed to register Scout.
            throw MissingCurrentWorkloadSizedRoute(functionType);
        }


        if (HasExplicitModelScoutConstraint(options))
        {
            // A caller-provided quality/proof envelope is a hard admission
            // contract. Configured and provider-default routes do not carry
            // sufficient evidence to prove it, especially when automatic Scout
            // selection is disabled or unavailable.
            throw MissingExplicitModelScoutConstraintRoute(functionType);
        }

        // Privacy constraint: if the caller (or function profile) says LocalOnly, we must not
        // route to a cloud provider no matter what preferences say. A user pin is still an
        // exact route constraint: prefer its exact trusted-local model (and preserve its
        // explicit fallback) before considering an automatic local default. If the pin cannot
        // be honored locally, only its explicit compatible local fallback may replace it.
        if (context.PrivacyClass == LlmPrivacyClass.LocalOnly)
        {
            if (isUserPinnedPreference && preference is { IsEnabled: true })
            {
                var pinnedLocalSelection = await PickLocalOnlyUserPreferenceAsync(
                    functionType,
                    preference,
                    requiresVision).ConfigureAwait(false);
                pinnedLocalSelection.Context = context;
                return pinnedLocalSelection;
            }

            var localSelection = await TryPickLocalSelectionAsync(functionType, requiresVision);
            if (localSelection is not null)
            {
                localSelection.Context = context;
                return localSelection;
            }
            _logger.LogWarning(
                "LocalOnly privacy class requested for {FunctionType} but no local provider is available",
                functionType);
            throw new InvalidOperationException(
                $"Local-only LLM routing was requested for {functionType}, but no local provider is available.");
        }

        if (preference is { IsEnabled: true })
        {
            var configuredSelection = await TrySelectConfiguredPreferenceAsync(
                functionType,
                preference,
                isUserPinnedPreference,
                context,
                requiresVision,
                options,
                allowDirectConfiguredFallback: configuredPreferenceIsExact).ConfigureAwait(false);
            if (configuredSelection is not null)
            {
                return configuredSelection;
            }
        }

        // Compatibility fallback for hosts that have not registered IModelScout.
        // Analyze complexity for automatic model selection
        var complexity = CalculatePromptComplexity(prompt);

        // Default provider and model based on complexity
        var defaultProvider = await GetDefaultAvailableProviderAsync();
        var defaultModel = await SelectModelByComplexityAsync(defaultProvider, complexity, requiresVision);

        var defaultFallback = await GetFallbackProviderAsync(defaultProvider);
        if (!automaticModelScoutSelectionEnabled)
        {
            var defaultPreference = new LlmFunctionPreference
            {
                FunctionType = functionType,
                PreferredProvider = defaultProvider,
                PreferredModel = defaultModel,
                IsEnabled = true,
                UpdatedBy = "configured-default",
            };
            var safetySelection = await TryGetAdvisedSelectionAsync(
                functionType,
                prompt,
                context,
                defaultPreference,
                requiresVision,
                options,
                trace,
                hardSafetyOnly: true,
                exactProviderConstraint: defaultProvider,
                exactModelConstraint: defaultModel).ConfigureAwait(false);
            if (safetySelection is not null)
            {
                safetySelection.Reason = $"Configured default admitted by hard safety policy while automatic optimization is off: {safetySelection.Reason}";
                return safetySelection;
            }
        }
        return new LlmModelSelection
        {
            Provider = defaultProvider,
            Model = defaultModel,
            FallbackProvider = defaultFallback,
            FallbackModel = await GetFallbackModelAsync(defaultFallback),
            IsDefault = true,
            Reason = $"Default selection - complexity: {complexity:P0}",
            Context = context
        };
    }

    /// <summary>
    /// True when <paramref name="provider"/>/<paramref name="model"/> is exactly
    /// the fallback the user configured on their pinned preference for
    /// <paramref name="functionType"/>. Such a route is user authority, not an
    /// automatic suggestion, and may be admitted without Scout proof.
    /// </summary>
    private async Task<bool> IsUserDirectedConfiguredFallbackAsync(
        string functionType,
        string provider,
        string model)
    {
        var preference = await _settingsProvider.GetFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        return preference is { IsEnabled: true }
               && LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt)
               && !string.IsNullOrWhiteSpace(preference.FallbackProvider)
               && !string.IsNullOrWhiteSpace(preference.FallbackModel)
               && IsExactRouteIdentity(
                   ResolveRegisteredProviderName(preference.FallbackProvider),
                   preference.FallbackModel,
                   provider,
                   model);
    }

    private async Task<LlmModelSelection?> TrySelectConfiguredPreferenceAsync(
        string functionType,
        LlmFunctionPreference preference,
        bool isUserPinnedPreference,
        LlmCallContext context,
        bool requiresVision,
        LlmRouterOptions? options,
        bool allowDirectConfiguredFallback)
    {
        if (string.IsNullOrWhiteSpace(preference.PreferredProvider)
            || string.IsNullOrWhiteSpace(preference.PreferredModel))
        {
            return null;
        }

        // Lazy materialization is committed only after the router's quality gate
        // accepts a physical completion. For stable typed-JSON workloads, its exact
        // route provenance therefore carries the structured-output proof that the
        // otherwise schema-minimal FunctionPreferences row cannot represent. Older
        // builds replaced that provenance after five successful completions with a
        // known-good metadata author. Accept that legacy shape only when the author
        // and the persisted known-good route both match the current route exactly.
        // Never infer proof for manual/bootstrap rows or for a user-pinned route.
        var stableStructuredOutput = ModelScoutFunctionRequirements
            .UsesStableStructuredOutput(functionType);
        var hasRouterMaterializationAttestation = string.Equals(
            preference.UpdatedBy,
            LlmPreferenceAuthors.RouterAutoMaterialization,
            StringComparison.Ordinal);
        var hasLegacyKnownGoodAttestation = preference.KnownGoodConfirmedAt.HasValue
                                            && !string.IsNullOrWhiteSpace(preference.KnownGoodProvider)
                                            && !string.IsNullOrWhiteSpace(preference.KnownGoodModel)
                                            && string.Equals(
                                                preference.UpdatedBy,
                                                $"known-good-confirmation:{preference.KnownGoodProvider}",
                                                StringComparison.OrdinalIgnoreCase)
                                            && IsExactRouteIdentity(
                                                preference.PreferredProvider,
                                                preference.PreferredModel,
                                                preference.KnownGoodProvider,
                                                preference.KnownGoodModel);
        var hasDurableStructuredOutputAttestation = !isUserPinnedPreference
                                                     && stableStructuredOutput
                                                     && (hasRouterMaterializationAttestation
                                                         || hasLegacyKnownGoodAttestation);

        var preferredProvider = ResolveRegisteredProviderName(preference.PreferredProvider);
        if (context.PrivacyClass == LlmPrivacyClass.LocalOnly
            && !await IsLocalRouteAsync(preferredProvider, preference.PreferredModel))
        {
            // Let the dedicated LocalOnly branch below honor a pinned local fallback or
            // choose a reachable automatic local route. Never return a cached cloud route.
            return null;
        }

        var prefFallbackProvider = string.IsNullOrWhiteSpace(preference.FallbackProvider)
            ? null
            : ResolveRegisteredProviderName(preference.FallbackProvider!);
        var preferredProviderInstance = _providerRegistry.GetProvider(preferredProvider);
        // An automatic preference is the durable output of a prior Scout admission.
        // Do not reject it merely because a cloud provider's compile-time model list
        // predates the live catalog Scout used; that recreates foreground discovery on
        // every cold start. Exact user pins retain the stricter provider-model check.
        var preferredModelAccepted = preferredProviderInstance is not null
                                     && (!isUserPinnedPreference
                                         || preferredProviderInstance.SupportsModel(preference.PreferredModel));
        var preferredRouteAvailable = preferredModelAccepted
                                      && await IsProviderAvailableAsync(
                                          preferredProvider,
                                          options?.RequestCancellationToken ?? CancellationToken.None,
                                          // Persisted user pins have the same authority as
                                          // exact per-call user overrides. A cooldown protects
                                          // automatic rotation; it must not silently discard a
                                          // user's exact CLI/API/local route and fall into a
                                          // different provider lane.
                                          bypassCooldownGate: isUserPinnedPreference,
                                          honorPinWithoutDiscovery: isUserPinnedPreference,
                                          honorConfiguredRouteWithoutDiscovery: !isUserPinnedPreference);

        // Local provider preferences are explicit routing decisions. Do not replace a
        // function-specific model with the provider default just because the provider is local;
        // that silently defeats benchmarked/known-good function preferences.
        if (await IsLocalProviderAsync(preferredProvider))
        {
            if (!preferredRouteAvailable)
            {
                _logger.LogWarning(
                    "Local provider {Provider} for {FunctionType} is configured but unavailable, trying fallback/default routing",
                    preferredProvider, functionType);
            }
            else
            {
                var localModel = preference.PreferredModel;

                if (!isUserPinnedPreference
                    && (requiresVision || preference.RequiresVision)
                    && !_capabilitiesService.SupportsVision(preferredProvider, localModel))
                {
                    var visionModel = await GetVisionCapableModelAsync(preferredProvider).ConfigureAwait(false);
                    if (visionModel is not null)
                    {
                        localModel = visionModel;
                    }
                }

                return new LlmModelSelection
                {
                    Provider = preferredProvider,
                    Model = localModel,
                    FallbackProvider = prefFallbackProvider,
                    FallbackModel = preference.FallbackModel,
                    IsDefault = false,
                    AllowPreferenceMaterialization = false,
                    Reason = isUserPinnedPreference
                        ? $"User-pinned function preference for local provider {functionType} (via fallback chain)"
                        : $"Function preference for local provider {functionType} (via fallback chain)",
                    IsUserPinned = isUserPinnedPreference,
                    SupportsStructuredOutput = hasDurableStructuredOutputAttestation
                                               && string.Equals(
                                                   localModel,
                                                   preference.PreferredModel,
                                                   StringComparison.Ordinal)
                        ? true
                        : null,
                    Context = context
                };
            }
        }

        // Cloud provider: use the exact configured model.
        if (preferredRouteAvailable)
        {
            if (!isUserPinnedPreference && (requiresVision || preference.RequiresVision))
            {
                if (!_capabilitiesService.SupportsVision(preferredProvider, preference.PreferredModel))
                {
                    var visionModel = await GetVisionCapableModelAsync(preferredProvider).ConfigureAwait(false);
                    if (visionModel is not null)
                    {
                        return new LlmModelSelection
                        {
                            Provider = preferredProvider,
                            Model = visionModel,
                            FallbackProvider = prefFallbackProvider,
                            FallbackModel = preference.FallbackModel,
                            IsDefault = false,
                            AllowPreferenceMaterialization = false,
                            Reason = $"Vision-capable model selected for {functionType}",
                            Context = context
                        };
                    }
                }
            }

            return new LlmModelSelection
            {
                Provider = preferredProvider,
                Model = preference.PreferredModel,
                FallbackProvider = prefFallbackProvider,
                FallbackModel = preference.FallbackModel,
                IsDefault = false,
                AllowPreferenceMaterialization = false,
                Reason = isUserPinnedPreference
                    ? $"User-pinned function preference for {functionType} (via fallback chain)"
                    : $"Function preference for {functionType} (via fallback chain)",
                IsUserPinned = isUserPinnedPreference,
                SupportsStructuredOutput = hasDurableStructuredOutputAttestation ? true : null,
                Context = context
            };
        }

        // A fallback attached to an automatic/system route is not spend authority. Only
        // an exact user/manual route may promote it directly; otherwise return null so
        // Scout can re-admit that fallback (or choose a safer alternative) with a fresh
        // decision receipt. Preserve LocalOnly privacy while consulting the durable cache.
        if (allowDirectConfiguredFallback
            && !string.IsNullOrEmpty(prefFallbackProvider)
            && (context.PrivacyClass != LlmPrivacyClass.LocalOnly || await IsLocalProviderAsync(prefFallbackProvider))
            && (!isUserPinnedPreference || !string.IsNullOrWhiteSpace(preference.FallbackModel)))
        {
            var fallbackModel = !string.IsNullOrEmpty(preference.FallbackModel)
                ? preference.FallbackModel
                : _settingsProvider.GetDefaultModel(prefFallbackProvider);
            if (context.PrivacyClass == LlmPrivacyClass.LocalOnly
                && !await IsLocalRouteAsync(prefFallbackProvider, fallbackModel))
            {
                return null;
            }
            var fallbackProviderInstance = _providerRegistry.GetProvider(prefFallbackProvider);
            var fallbackModelAccepted = fallbackProviderInstance is not null
                                        && (!isUserPinnedPreference
                                            || fallbackProviderInstance.SupportsModel(fallbackModel));
            if (fallbackModelAccepted
                && await IsProviderAvailableAsync(
                    prefFallbackProvider,
                    options?.RequestCancellationToken ?? CancellationToken.None,
                    honorConfiguredRouteWithoutDiscovery: true).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Preferred provider {Provider} for {FunctionType} is not available, using fallback {Fallback}",
                    preferredProvider, functionType, prefFallbackProvider);

                return new LlmModelSelection
                {
                    Provider = prefFallbackProvider,
                    Model = fallbackModel,
                    IsDefault = false,
                    AllowPreferenceMaterialization = false,
                    Reason = $"Fallback for {functionType} - preferred provider unavailable",
                    IsUserPinned = isUserPinnedPreference,
                    Context = context
                };
            }
        }

        if (isUserPinnedPreference
            && context.PrivacyClass != LlmPrivacyClass.LocalOnly)
        {
            throw new InvalidOperationException(
                $"User-pinned route {preferredProvider}/{preference.PreferredModel} is not available for {functionType}, and no explicit usable fallback was configured.");
        }

        _logger.LogWarning(
            "Function preference for {FunctionType} has unavailable providers, falling back to Model Scout/default routing",
            functionType);
        return null;
    }

    private async Task<bool> IsAutomaticModelScoutSelectionEnabledAsync()
    {
        if (_modelScoutRoutingGate is null) return true;
        try
        {
            return await _modelScoutRoutingGate.IsAutomaticSelectionEnabledAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read Model Scout automatic-selection policy; preserving configured routing");
            return false;
        }
    }

    private static bool HasExplicitModelScoutConstraint(LlmRouterOptions? options)
        => options?.ModelRequest is not null
           || options?.MinimumCapabilityScore is not null
           || options?.RequireMeasuredCapabilityScore == true
           || options?.RequiredTier is not null
           || options?.BlockWeakerPolicyFallback == true
           || options?.RequireCurrentFunctionEvidence == true;

    private static LlmRouteSelectionBlockedException MissingExplicitModelScoutConstraintRoute(
        string functionType)
        => new(
            $"Model Scout found no route satisfying the caller's explicit quality/proof constraints for {functionType}; configured/default routing was blocked rather than weakening the request.");

    private static LlmRouteSelectionBlockedException MissingCurrentWorkloadSizedRoute(
        string functionType)
        => new(
            $"Model Scout found no healthy route ranked for the current request size for {functionType}; provider execution was blocked rather than using a stale automatic route.");

    private static decimal? StrongerCapabilityFloor(decimal? first, decimal? second)
        => first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);

    private static ModelScoutTier? StrongerRequiredTier(ModelScoutTier? first, ModelScoutTier? second)
        => first is null ? second : second is null ? first : (ModelScoutTier)Math.Max((int)first.Value, (int)second.Value);

    private static decimal RequiredCapabilityFloor(
        decimal? minimumCapabilityScore,
        ModelScoutTier tier)
        => Math.Max(
            minimumCapabilityScore ?? 0m,
            ModelScoutCapabilityEvidence.MinimumScoreForTier(tier));

    private static bool MeetsCapabilityEnvelope(
        decimal capabilityScore,
        ModelScoutCapabilityEvidenceKind evidenceKind,
        decimal? minimumCapabilityScore,
        ModelScoutTier tier,
        bool requireMeasuredCapabilityScore)
        => capabilityScore >= RequiredCapabilityFloor(minimumCapabilityScore, tier)
           && (!requireMeasuredCapabilityScore
               || ModelScoutCapabilityEvidence.IsMeasured(evidenceKind));

    private static string? GetDecisionReceiptConstraintIssue(
        ModelScoutRuntimeDecisionReceipt? receipt,
        string provider,
        string model,
        decimal? minimumCapabilityScore,
        ModelScoutTier? requiredTier,
        bool requireMeasuredCapabilityScore)
    {
        if (receipt is null)
        {
            return "the route has no Model Scout decision receipt";
        }

        var selected = receipt.Selected;
        if (!selected.Eligible
            || !IsExactRouteIdentity(
                provider,
                model,
                selected.Provider,
                selected.Model))
        {
            return "the Model Scout decision receipt is not an eligible exact-route receipt";
        }

        if (requiredTier is { } tier
            && selected.CapabilityScore < ModelScoutCapabilityEvidence.MinimumScoreForTier(tier))
        {
            return $"capability {selected.CapabilityScore:0.0} is below the {tier} tier gate";
        }

        if (minimumCapabilityScore is { } minimum
            && selected.CapabilityScore < minimum)
        {
            return $"capability {selected.CapabilityScore:0.0} is below the {minimum:0.0} caller gate";
        }

        if (requireMeasuredCapabilityScore
            && !ModelScoutCapabilityEvidence.IsMeasured(selected.CapabilityProvenance))
        {
            return "the caller requires measured capability evidence";
        }

        return null;
    }

    private static string? GetExplicitCapabilityConstraintIssue(
        decimal? capabilityScore,
        ModelScoutCapabilityEvidenceKind evidenceKind,
        decimal? minimumCapabilityScore,
        ModelScoutTier? requiredTier,
        bool requireMeasuredCapabilityScore)
    {
        var requiredFloor = Math.Max(
            minimumCapabilityScore ?? 0m,
            requiredTier is { } tier
                ? ModelScoutCapabilityEvidence.MinimumScoreForTier(tier)
                : 0m);
        if (requiredFloor > 0m
            && (capabilityScore is null || capabilityScore.Value < requiredFloor))
        {
            return capabilityScore is null
                ? $"the proof authority did not attest the required capability score ({requiredFloor:0.0})"
                : $"capability {capabilityScore.Value:0.0} is below the {requiredFloor:0.0} caller gate";
        }

        if (requireMeasuredCapabilityScore
            && !ModelScoutCapabilityEvidence.IsMeasured(evidenceKind))
        {
            return "the proof authority did not attest measured capability evidence";
        }

        return null;
    }

    private async Task<LlmModelSelection?> TryGetModelScoutDefaultAsync(
        string functionType,
        string prompt,
        LlmCallContext context,
        bool requiresVision,
        LlmRouterOptions? options,
        LlmModelSelection? policySource = null)
    {
        var cancellationToken = options?.RequestCancellationToken ?? CancellationToken.None;
        // Hosts without Model Scout (SwarmCode) reach this from advisor fallbacks; there is no Scout
        // recommendation to take, so report none instead of throwing NullReferenceException (which the
        // caller logged as "recommendation failed" and turned into "no executable route").
        if (_modelScout is null) return null;
        try
        {
            var strictCurrentFunctionEvidence = policySource?.RequireCurrentFunctionEvidence == true
                                                || options?.RequireCurrentFunctionEvidence == true
                                                || RequiresStrictCurrentFunctionEvidence(functionType);
            var minimumCapabilityScore = StrongerCapabilityFloor(
                policySource?.MinimumCapabilityScore,
                options?.MinimumCapabilityScore);
            var requireMeasuredCapabilityScore = policySource?.RequireMeasuredCapabilityScore == true
                                                 || options?.RequireMeasuredCapabilityScore == true;
            var requiredTier = StrongerRequiredTier(policySource?.RequiredTier, options?.RequiredTier);
            var blockWeakerPolicyFallback = policySource?.BlockWeakerPolicyFallback == true
                                            || options?.BlockWeakerPolicyFallback == true;
            var hasExplicitQualityEnvelope = minimumCapabilityScore is not null
                                             || requireMeasuredCapabilityScore
                                             || requiredTier is not null;
            var policy = _routingPolicyStore is null
                ? null
                : await _routingPolicyStore.GetAsync(cancellationToken).ConfigureAwait(false);
            var tier = requiredTier ?? ModelScoutTierPolicy.DeriveTier(
                context.Urgency,
                context.Interactivity,
                zeroMarginalCostLane: false);
            var decision = await _modelScout!.RecommendAsync(new ModelScoutRecommendationRequest
            {
                ModelRequest = options?.ModelRequest?.Snapshot(),
                PreferredModelConnection = policy?.PreferredModelConnection ?? ModelScoutConnectionPreference.SubscriptionFirst,
                FunctionType = functionType,
                Provider = !string.IsNullOrWhiteSpace(options?.OverrideProvider)
                           && string.IsNullOrWhiteSpace(options.OverrideModel)
                    ? ResolveRegisteredProviderName(options.OverrideProvider)
                    : null,
                Tier = tier,
                Interactivity = context.Interactivity,
                Workload = context.Workload,
                Demand = options?.TaskDemand,
                SubscriptionQuotaThreshold = policy?.SubscriptionQuotaThreshold,
                PrivacyClass = context.PrivacyClass,
                RequiresVision = requiresVision,
                RequiresToolUse = options?.RequiresToolUse == true,
                RequiresStructuredOutput = options?.RequiresStructuredOutput == true,
                // Only callers that explicitly accept validated text-JSON
                // repair may use a route without provider-native schemas.
                StructuredOutputRepairAvailable = options?.AllowTextJsonContractRepair == true,
                MinimumContextTokens = options?.MinimumContextTokens,
                EstimatedInputTokens = EstimatePromptTokens(prompt),
                EstimatedOutputTokens = ResolveEstimatedOutputTokens(options),
                LatencyTargetMs = context.LatencyBudgetMs,
                MinimumCapabilityScore = minimumCapabilityScore,
                RequireMeasuredCapabilityScore = requireMeasuredCapabilityScore,
                Weights = policy is null || !policy.UseCustomModelScoutWeights
                    ? null
                    : new ModelScoutScoreWeights(
                        policy.ModelScoutCapabilityWeight,
                        policy.ModelScoutSpeedWeight,
                        policy.ModelScoutCostWeight),
                AuditSessionId = options?.SessionId,
                AuditTurnId = options?.TurnId,
                PersistAudit = false,
                RequireCurrentFunctionEvidence = strictCurrentFunctionEvidence,
                MaintainCurrentFunctionEvidenceRecovery = true,
                MaintainServingInventory = true,
            }, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(decision.AuditCorrelationId))
            {
                _logger.LogWarning(
                    "Model Scout returned candidates for {FunctionType} without an auditable correlation receipt",
                    functionType);
                if (options?.ModelRequest is { } unauditedRequest)
                    throw ModelScoutModelRequests.Unavailable(functionType, unauditedRequest, decision,
                        "Model Scout did not issue an admission receipt; route recovery must complete before execution");
                return null;
            }

            var executionRejections = options?.ModelRequest is null ? null : new List<ModelRouteRejection>();
            var executable = await GetExecutableModelScoutCandidatesAsync(
                    decision,
                    functionType,
                    context,
                    requiresVision,
                    options,
                    minimumCapabilityScore,
                    tier,
                    requireMeasuredCapabilityScore,
                    requireCurrentFunctionEvidence: strictCurrentFunctionEvidence || options?.ModelRequest?.PreferredModels.Count is not > 0,
                    executionRejections)
                .ConfigureAwait(false);
            var provisional = executable.Count > 0 && !HasCurrentFunctionEvidence(executable[0].Ranked.Candidate, functionType);

            if (executable.Count == 0
                && !strictCurrentFunctionEvidence)
            {
                // This one recommendation is independently auditable and still
                // enforces every hard capability/privacy/cost gate. The host also
                // refreshed exact website evidence and opened one deduplicated
                // recovery episode before returning general-prior candidates.
                executable = await GetExecutableModelScoutCandidatesAsync(
                        decision,
                        functionType,
                        context,
                        requiresVision,
                        options,
                        minimumCapabilityScore,
                        tier,
                        requireMeasuredCapabilityScore,
                        requireCurrentFunctionEvidence: false, executionRejections)
                    .ConfigureAwait(false);
                provisional = executable.Count > 0;
            }

            // A single Scout decision owns the full eligible set. Reissuing the
            // same request with weaker proof requirements is not recovery.
            if (executable.Count == 0)
            {
                if (options?.ModelRequest is { } requiredRequest)
                    throw ModelScoutModelRequests.Unavailable(functionType, requiredRequest, decision, executionRejections: executionRejections);
                // No configured provider is normal before first-run AI setup. The
                // caller supplies the actionable inline error when a user actually
                // requests AI, so do not turn background route discovery into a
                // startup warning in the activity strip.
                _logger.LogDebug(
                    "Model Scout found no executable route for {FunctionType}: {Reason}",
                    functionType,
                    decision.Reason);
                return null;
            }

            var selected = executable[0];
            var fallback = executable.Skip(1).FirstOrDefault();
            return new LlmModelSelection
            {
                Provider = selected.Provider,
                Model = selected.Ranked.Candidate.Model,
                AllowPreferenceMaterialization = options?.ModelRequest is null,
                FallbackProvider = fallback.Ranked is null ? null : fallback.Provider,
                FallbackModel = fallback.Ranked?.Candidate.Model,
                IsDefault = true,
                Reason = provisional
                    ? $"Provisional Model Scout automatic selection (best available while stronger {functionType} evidence is being refreshed): {decision.Reason}"
                    : $"Model Scout automatic selection from current {functionType} evidence ({selected.Ranked.Candidate.EvidenceSource ?? "audited proof"}).",
                Context = context,
                AuditCorrelationId = decision.AuditCorrelationId,
                SupportsToolUse = selected.Ranked.Candidate.SupportsToolUse,
                SupportsStructuredOutput = selected.Ranked.Candidate.SupportsStructuredOutput,
                MaxContextTokens = selected.Ranked.Candidate.MaxContextTokens,
                CapabilityEvidenceSource = selected.Ranked.Candidate.CapabilityEvidenceSource
                    ?? selected.Ranked.Candidate.EvidenceSource,
                CapabilityScore = selected.Ranked.Candidate.CapabilityScore,
                CapabilityEvidenceKind = selected.Ranked.Candidate.CapabilityEvidenceKind,
                ModelScoutDecisionReceipt = BuildRuntimeDecisionReceipt(
                    decision,
                    selected.Ranked),
                FallbackModelScoutDecisionReceipt = fallback.Ranked is null
                    ? null
                    : BuildRuntimeDecisionReceipt(decision, fallback.Ranked),
                FallbackSupportsToolUse = fallback.Ranked?.Candidate.SupportsToolUse,
                FallbackSupportsStructuredOutput = fallback.Ranked?.Candidate.SupportsStructuredOutput,
                FallbackMaxContextTokens = fallback.Ranked?.Candidate.MaxContextTokens,
                FallbackCapabilityEvidenceSource = fallback.Ranked?.Candidate.CapabilityEvidenceSource
                    ?? fallback.Ranked?.Candidate.EvidenceSource,
                FallbackCapabilityScore = fallback.Ranked?.Candidate.CapabilityScore,
                FallbackCapabilityEvidenceKind = fallback.Ranked?.Candidate.CapabilityEvidenceKind
                    ?? ModelScoutCapabilityEvidenceKind.Unknown,
                FallbackAuditCorrelationId = fallback.Ranked is null
                    ? null
                    : decision.AuditCorrelationId,
                // A provisional selection deliberately carries no stale proof
                // gate into execution.  The original request remains visible in
                // the audit/recovery episode, while this route is allowed to
                // serve the user until Scout can promote a stronger candidate.
                MinimumCapabilityScore = minimumCapabilityScore,
                RequireMeasuredCapabilityScore = requireMeasuredCapabilityScore,
                RequiredTier = requiredTier,
                BlockWeakerPolicyFallback = provisional ? false : blockWeakerPolicyFallback,
                RequireCurrentFunctionEvidence = provisional ? false : strictCurrentFunctionEvidence,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmModelRequestUnavailableException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Model Scout recommendation failed for {FunctionType}; no automatic route will be inferred",
                functionType);
            return null;
        }
    }

    private async Task<LlmModelSelection?> TrySelectValidatedAdmissionAsync(
        string functionType,
        LlmCallContext context,
        string provider,
        string model,
        bool requiresVision,
        LlmRouterOptions options)
    {
        var receipt = options.RouteAdmissionReceipt;
        if (_routeAdmissionValidator is null
            || receipt is null
            || !string.Equals(receipt.FunctionType, functionType, StringComparison.OrdinalIgnoreCase)
            || !IsExactRouteIdentity(
                provider,
                model,
                ResolveRegisteredProviderName(receipt.Provider),
                receipt.Model))
        {
            return null;
        }

        var validation = await _routeAdmissionValidator
            .ValidateAsync(receipt)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Rejected validated route admission {Provider}/{Model} for {FunctionType}: {Reason}",
                provider,
                model,
                functionType,
                validation.Reason);
            return null;
        }

        var validatedReceipt = validation.ValidatedReceipt;
        if (validatedReceipt is null
            || !string.Equals(validatedReceipt.FunctionType, functionType, StringComparison.OrdinalIgnoreCase)
            || !IsExactRouteIdentity(
                provider,
                model,
                ResolveRegisteredProviderName(validatedReceipt.Provider),
                validatedReceipt.Model)
            || validatedReceipt.ExpiresAtUtc is { } validatedExpiry
               && validatedExpiry <= DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Rejected validated route admission {Provider}/{Model} for {FunctionType}: validator returned no current exact receipt",
                provider,
                model,
                functionType);
            return null;
        }

        var capabilityConstraintIssue = GetExplicitCapabilityConstraintIssue(
            validation.CapabilityScore,
            validation.CapabilityEvidenceKind,
            options.MinimumCapabilityScore,
            options.RequiredTier,
            options.RequireMeasuredCapabilityScore);
        if (capabilityConstraintIssue is not null)
        {
            _logger.LogWarning(
                "Rejected validated route admission {Provider}/{Model} for {FunctionType}: {Reason}",
                provider,
                model,
                functionType,
                capabilityConstraintIssue);
            return null;
        }

        var selection = new LlmModelSelection
        {
            Provider = provider,
            Model = model,
            IsDefault = false,
            Reason = $"Exact route admitted by host proof authority: {validation.Reason}",
            Context = context,
            SupportsToolUse = validation.SupportsToolUse,
            SupportsStructuredOutput = validation.SupportsStructuredOutput,
            MaxContextTokens = validation.MaxContextTokens,
            CapabilityEvidenceSource = validation.CapabilityEvidenceSource
                                       ?? $"validated-admission:{validatedReceipt.Source}",
            CapabilityScore = validation.CapabilityScore,
            CapabilityEvidenceKind = validation.CapabilityEvidenceKind,
            ValidatedAdmissionReceipt = validatedReceipt,
            MinimumCapabilityScore = options.MinimumCapabilityScore,
            RequireMeasuredCapabilityScore = options.RequireMeasuredCapabilityScore,
            RequiredTier = options.RequiredTier,
            BlockWeakerPolicyFallback = options.BlockWeakerPolicyFallback,
            RequireCurrentFunctionEvidence = options.RequireCurrentFunctionEvidence,
        };
        var requirementIssue = await GetRouteRequirementIssueAsync(
                selection,
                requiresVision,
                options,
                verifyAvailability: true)
            .ConfigureAwait(false);
        if (requirementIssue is null
            && (options.RequiresToolUse != true || GetToolUseSelectionIssue(selection) is null))
        {
            return selection;
        }

        _logger.LogWarning(
            "Rejected validated route admission {Provider}/{Model} for {FunctionType}: {Reason}",
            provider,
            model,
            functionType,
            requirementIssue ?? GetToolUseSelectionIssue(selection));
        return null;
    }

    private async Task<List<(ModelScoutRankedCandidate Ranked, string Provider)>>
        GetExecutableModelScoutCandidatesAsync(
            ModelScoutRecommendationDecision decision,
        string functionType,
        LlmCallContext context,
        bool requiresVision,
        LlmRouterOptions? options,
        decimal? minimumCapabilityScore,
        ModelScoutTier tier,
        bool requireMeasuredCapabilityScore,
        bool requireCurrentFunctionEvidence,
        List<ModelRouteRejection>? executionRejections = null)
    {
        var executable = new List<(ModelScoutRankedCandidate Ranked, string Provider)>();
        executionRejections?.Clear();
        foreach (var ranked in decision.Candidates.Where(candidate =>
                     candidate.IsEligible
                     && MeetsCapabilityEnvelope(
                         candidate.Candidate.CapabilityScore,
                         candidate.Candidate.CapabilityEvidenceKind,
                         minimumCapabilityScore,
                         tier,
                         requireMeasuredCapabilityScore)
                     && (!requireCurrentFunctionEvidence
                         || HasCurrentFunctionEvidence(candidate.Candidate, functionType))))
        {
            var providerName = ResolveRegisteredProviderName(ranked.Candidate.Provider);
            void Reject(string reason) => executionRejections?.Add(new(ranked.Candidate.Provider, ranked.Candidate.Model, reason));
            if (ModelScoutModelRequests.Rejection(options?.ModelRequest, providerName, ranked.Candidate.Model,
                    ranked.Candidate.SupportsToolUse, ranked.Candidate.SupportsVision, ranked.Candidate.SupportsStructuredOutput) is { } requestIssue)
            {
                Reject(requestIssue);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(options?.OverrideProvider)
                && string.IsNullOrWhiteSpace(options.OverrideModel)
                && !string.Equals(
                    providerName,
                    ResolveRegisteredProviderName(options.OverrideProvider),
                    StringComparison.OrdinalIgnoreCase))
            {
                Reject("outside the explicit provider constraint");
                continue;
            }
            var provider = _providerRegistry.GetProvider(providerName);
            if (provider is null || !provider.SupportsModel(ranked.Candidate.Model))
            {
                Reject("the provider is not registered or does not advertise this exact model; check the configured connection");
                continue;
            }
            if (!await IsProviderAvailableAsync(
                    providerName,
                    options?.RequestCancellationToken ?? CancellationToken.None,
                    waitForDeferredCapacity: true).ConfigureAwait(false))
            {
                Reject("the provider is unavailable; check connection, sign-in, quota and cooldown status");
                continue;
            }
            if (requiresVision && !provider.SupportsVision(ranked.Candidate.Model))
            {
                Reject("vision capability is not supported by the registered provider");
                continue;
            }
            if (options?.RequiresToolUse == true
                && (_toolUseRegistry?.Supports(providerName) != true
                    || ranked.Candidate.SupportsToolUse != true))
            {
                Reject("a proven tool adapter is not available for this model and connection");
                continue;
            }
            if (RequiresNativeStructuredOutput(options)
                && ranked.Candidate.SupportsStructuredOutput != true)
            {
                Reject("native structured output is not proven for this connection");
                continue;
            }
            if (options?.MinimumContextTokens is > 0 and var minimumContextTokens
                && (ranked.Candidate.MaxContextTokens is null
                    || ranked.Candidate.MaxContextTokens < minimumContextTokens))
            {
                Reject("the required context window is not proven for this connection");
                continue;
            }
            if (context.PrivacyClass == LlmPrivacyClass.LocalOnly
                && !await IsLocalRouteAsync(providerName, ranked.Candidate.Model))
            {
                Reject("privacy policy requires a local route");
                continue;
            }
            executable.Add((ranked, providerName));
        }

        return executable;
    }

    private static bool RequiresStrictCurrentFunctionEvidence(string functionType)
        => string.Equals(
            functionType,
            FunctionTypes.MessagePipeline,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-admits an exact automatic route through a fresh, persisted Model Scout
    /// decision. Internal overrides and advisor recommendations are untrusted
    /// control-plane input until the exact provider/model appears as an eligible,
    /// current function-scoped candidate with a non-empty audit receipt.
    /// </summary>
    private async Task<LlmModelSelection?> TryReadmitExactAutomaticRouteAsync(
        string functionType,
        string prompt,
        LlmCallContext context,
        string providerName,
        string model,
        bool requiresVision,
        LlmRouterOptions? options,
        string reason,
        decimal? minimumCapabilityScore = null,
        ModelScoutTier? requiredTier = null,
        bool requireMeasuredCapabilityScore = false,
        bool blockWeakerPolicyFallback = false,
        bool requireCurrentFunctionEvidence = false)
    {
        if (string.IsNullOrWhiteSpace(providerName)
            || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var provider = ResolveRegisteredProviderName(providerName);
        var cancellationToken = options?.RequestCancellationToken ?? CancellationToken.None;
        try
        {
            var strictCurrentFunctionEvidence = requireCurrentFunctionEvidence
                                                || options?.RequireCurrentFunctionEvidence == true
                                                || RequiresStrictCurrentFunctionEvidence(functionType);
            minimumCapabilityScore = StrongerCapabilityFloor(
                minimumCapabilityScore,
                options?.MinimumCapabilityScore);
            requiredTier = StrongerRequiredTier(requiredTier, options?.RequiredTier);
            requireMeasuredCapabilityScore = requireMeasuredCapabilityScore
                                             || options?.RequireMeasuredCapabilityScore == true;
            blockWeakerPolicyFallback = blockWeakerPolicyFallback
                                        || options?.BlockWeakerPolicyFallback == true;
            var tier = requiredTier ?? ModelScoutTierPolicy.DeriveTier(
                context.Urgency,
                context.Interactivity,
                zeroMarginalCostLane: false);

            // The durable survival set is itself current exact-function Model
            // Scout proof. Consult it before requiring a fresh discovered-catalog
            // row so a transient empty catalog cannot reject the same exact route
            // that hard safety just selected from the bound survival order.
            var survivalSelection = await TryReadmitBoundSurvivalCandidateAsync(
                    functionType,
                    context,
                    provider,
                    model,
                    requiresVision,
                    options,
                    reason,
                    minimumCapabilityScore,
                    tier,
                    requireMeasuredCapabilityScore,
                    blockWeakerPolicyFallback,
                    strictCurrentFunctionEvidence)
                .ConfigureAwait(false);
            if (survivalSelection is not null)
            {
                return survivalSelection;
            }

            if (_modelScout is null)
            {
                return null;
            }

            var policy = _routingPolicyStore is null
                ? null
                : await _routingPolicyStore.GetAsync(cancellationToken).ConfigureAwait(false);
            var decision = await _modelScout.RecommendAsync(new ModelScoutRecommendationRequest
            {
                ModelRequest = options?.ModelRequest?.Snapshot(),
                PreferredModelConnection = policy?.PreferredModelConnection ?? ModelScoutConnectionPreference.SubscriptionFirst,
                FunctionType = functionType,
                Provider = provider,
                ExactModel = model,
                Tier = tier,
                Interactivity = context.Interactivity,
                Workload = context.Workload,
                Demand = options?.TaskDemand,
                SubscriptionQuotaThreshold = policy?.SubscriptionQuotaThreshold,
                PrivacyClass = context.PrivacyClass,
                RequiresVision = requiresVision,
                RequiresToolUse = options?.RequiresToolUse == true,
                RequiresStructuredOutput = options?.RequiresStructuredOutput == true,
                // Only callers that explicitly accept validated text-JSON
                // repair may use a route without provider-native schemas.
                StructuredOutputRepairAvailable = options?.AllowTextJsonContractRepair == true,
                MinimumContextTokens = options?.MinimumContextTokens,
                EstimatedInputTokens = EstimatePromptTokens(prompt),
                EstimatedOutputTokens = ResolveEstimatedOutputTokens(options),
                LatencyTargetMs = context.LatencyBudgetMs,
                MinimumCapabilityScore = minimumCapabilityScore,
                RequireMeasuredCapabilityScore = requireMeasuredCapabilityScore,
                Weights = policy is null || !policy.UseCustomModelScoutWeights
                    ? null
                    : new ModelScoutScoreWeights(
                        policy.ModelScoutCapabilityWeight,
                        policy.ModelScoutSpeedWeight,
                        policy.ModelScoutCostWeight),
                AuditSessionId = options?.SessionId,
                AuditTurnId = options?.TurnId,
                PersistAudit = strictCurrentFunctionEvidence,
                RequireCurrentFunctionEvidence = strictCurrentFunctionEvidence,
                MaintainCurrentFunctionEvidenceRecovery = true,
                MaintainServingInventory = true,
            }, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(decision.AuditCorrelationId))
            {
                _logger.LogWarning(
                    "Rejected exact automatic route {Provider}/{Model} for {FunctionType}: Model Scout returned no durable correlation receipt",
                    provider,
                    model,
                    functionType);
                return null;
            }

            var ranked = decision.Candidates.FirstOrDefault(candidate =>
                candidate.IsEligible
                && MeetsCapabilityEnvelope(
                    candidate.Candidate.CapabilityScore,
                    candidate.Candidate.CapabilityEvidenceKind,
                    minimumCapabilityScore,
                    tier,
                    requireMeasuredCapabilityScore)
                && (!strictCurrentFunctionEvidence
                    || HasCurrentFunctionEvidence(candidate.Candidate, functionType))
                && IsExactRouteIdentity(
                    provider,
                    model,
                    ResolveRegisteredProviderName(candidate.Candidate.Provider),
                    candidate.Candidate.Model));
            if (ranked is null)
            {
                // A tool-use turn can establish direct, model-level evidence
                // before the asynchronous Scout catalog has produced its first
                // eligible candidate.  The tool loop pins its successful route
                // as a Policy override for the next iteration; re-running the
                // normal Scout query here used to reject that same already
                // proven route and make the loop fall all the way back through
                // ordinary routing.  Admit only this narrow bootstrap case:
                // a cached affirmative runtime probe, no capability/tier floor,
                // and no function type that requires durable current evidence.
                var cachedToolUseProof = _toolUseProver?.GetCachedVerdict(provider, model);
                if (!strictCurrentFunctionEvidence
                    && options?.RequiresToolUse == true
                    && cachedToolUseProof == true
                    && minimumCapabilityScore is null
                    && requiredTier is null
                    && !requireMeasuredCapabilityScore
                    && !blockWeakerPolicyFallback)
                {
                    var runtimeProvenSelection = new LlmModelSelection
                    {
                        Provider = provider,
                        Model = model,
                        IsDefault = false,
                        Reason = $"{reason}; exact route re-admitted by cached runtime tool-use proof while Model Scout catalog proof catches up",
                        Context = context,
                        AuditCorrelationId = decision.AuditCorrelationId,
                        SupportsToolUse = true,
                        CapabilityEvidenceSource = "runtime-tool-use-probe",
                        MinimumCapabilityScore = minimumCapabilityScore,
                        RequireMeasuredCapabilityScore = requireMeasuredCapabilityScore,
                        RequiredTier = requiredTier,
                        BlockWeakerPolicyFallback = blockWeakerPolicyFallback,
                        RequireCurrentFunctionEvidence = false,
                    };
                    var runtimeProofIssue = await GetRouteRequirementIssueAsync(
                        runtimeProvenSelection,
                        requiresVision,
                        options,
                        verifyAvailability: true).ConfigureAwait(false);
                    if (runtimeProofIssue is null
                        && GetToolUseSelectionIssue(runtimeProvenSelection) is null)
                    {
                        _logger.LogInformation(
                            "Re-admitted exact automatic route {Provider}/{Model} for {FunctionType} using cached runtime tool-use proof while Scout catalog proof catches up",
                            provider,
                            model,
                            functionType);
                        return runtimeProvenSelection;
                    }
                }

                _logger.LogWarning(
                    "Rejected exact automatic route {Provider}/{Model} for {FunctionType}: it was not an eligible {EvidenceKind} Scout candidate",
                    provider,
                    model,
                    functionType,
                    strictCurrentFunctionEvidence
                        ? "current function-scoped"
                        : "hard-gated provisional");
                return null;
            }

            var candidate = ranked.Candidate;
            var providerImpl = _providerRegistry.GetProvider(provider);
            if (providerImpl is null
                || !providerImpl.SupportsModel(model)
                || !await IsProviderAvailableAsync(
                    provider,
                    options?.RequestCancellationToken ?? CancellationToken.None,
                    waitForDeferredCapacity: true).ConfigureAwait(false)
                || (requiresVision
                    && (candidate.SupportsVision != true || !providerImpl.SupportsVision(model)))
                || (options?.RequiresToolUse == true
                    && (_toolUseRegistry?.Supports(provider) != true || candidate.SupportsToolUse != true))
                || (RequiresNativeStructuredOutput(options)
                    && candidate.SupportsStructuredOutput != true)
                || (options?.MinimumContextTokens is > 0 and var minimumContextTokens
                    && (candidate.MaxContextTokens is null
                        || candidate.MaxContextTokens < minimumContextTokens))
                || (context.PrivacyClass == LlmPrivacyClass.LocalOnly
                    && !await IsLocalRouteAsync(provider, model)))
            {
                _logger.LogWarning(
                    "Rejected exact automatic route {Provider}/{Model} for {FunctionType}: provider availability or hard-capability proof failed",
                    provider,
                    model,
                    functionType);
                return null;
            }

            var selection = new LlmModelSelection
            {
                Provider = provider,
                Model = model,
                IsDefault = false,
                Reason = strictCurrentFunctionEvidence
                    ? $"{reason}; exact route re-admitted by current Model Scout proof"
                    : $"{reason}; exact provisional route admitted while {functionType} proof recovery continues",
                Context = context,
                AuditCorrelationId = decision.AuditCorrelationId,
                SupportsToolUse = candidate.SupportsToolUse,
                SupportsStructuredOutput = candidate.SupportsStructuredOutput,
                MaxContextTokens = candidate.MaxContextTokens,
                CapabilityEvidenceSource = candidate.CapabilityEvidenceSource ?? candidate.EvidenceSource,
                CapabilityScore = candidate.CapabilityScore,
                CapabilityEvidenceKind = candidate.CapabilityEvidenceKind,
                ModelScoutDecisionReceipt = BuildRuntimeDecisionReceipt(decision, ranked),
                MinimumCapabilityScore = minimumCapabilityScore,
                RequireMeasuredCapabilityScore = requireMeasuredCapabilityScore,
                RequiredTier = requiredTier,
                BlockWeakerPolicyFallback = blockWeakerPolicyFallback,
                RequireCurrentFunctionEvidence = strictCurrentFunctionEvidence,
            };
            var hardRequirementIssue = await GetRouteRequirementIssueAsync(
                selection,
                requiresVision,
                options,
                verifyAvailability: true).ConfigureAwait(false);
            if (hardRequirementIssue is not null
                || (options?.RequiresToolUse == true && GetToolUseSelectionIssue(selection) is not null))
            {
                _logger.LogWarning(
                    "Rejected exact automatic route {Provider}/{Model} for {FunctionType}: {Reason}",
                    provider,
                    model,
                    functionType,
                    hardRequirementIssue ?? GetToolUseSelectionIssue(selection));
                return null;
            }

            return selection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Model Scout could not re-admit exact automatic route {Provider}/{Model} for {FunctionType}",
                provider,
                model,
                functionType);
            return null;
        }
    }

    private static bool IsExactRouteIdentity(
        string leftProvider,
        string leftModel,
        string rightProvider,
        string rightModel)
    {
        if (!string.Equals(leftProvider, rightProvider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                NormalizeProviderName(leftProvider),
                "ollama",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(leftModel.Trim(), rightModel.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            NormalizeOllamaModelTag(leftModel),
            NormalizeOllamaModelTag(rightModel),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeOllamaModelTag(string model)
    {
        var normalized = model.Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        var lastSlash = normalized.LastIndexOf('/');
        return normalized.LastIndexOf(':') > lastSlash
            ? normalized
            : $"{normalized}:latest";
    }

    private static bool HasCurrentFunctionEvidence(
        ModelScoutCandidate candidate,
        string functionType)
        => ModelScoutFunctionEvidence.IsCoherent(candidate, functionType);

    private static ModelScoutRuntimeDecisionReceipt BuildRuntimeDecisionReceipt(
        ModelScoutRecommendationDecision decision,
        ModelScoutRankedCandidate selected)
        => ModelScoutRuntimeDecisionReceipt.FromDecision(decision, selected);

    private static InvalidOperationException MissingCurrentFunctionProof(string functionType)
        => new MissingCurrentFunctionProofException(
            $"No current function-scoped Model Scout proof is available for {functionType}. "
            + "Automatic routing is paused while bounded Model Scout recovery searches for a proven route.");

    private sealed class MissingCurrentFunctionProofException : InvalidOperationException
    {
        public MissingCurrentFunctionProofException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Carries non-negotiable per-call policy across every transient route object.
    /// Runtime fallback, streaming, vision, tool use and self-heal all construct
    /// fresh selections; dropping these fields there would silently lower a
    /// high-stakes capability floor after the primary fails.
    /// </summary>
    private static LlmModelSelection CarryRoutePolicy(
        LlmModelSelection source,
        LlmModelSelection target,
        bool promoteConfiguredFallback = false)
    {
        target.MinimumCapabilityScore = source.MinimumCapabilityScore;
        target.RequireMeasuredCapabilityScore = source.RequireMeasuredCapabilityScore;
        target.RequiredTier = source.RequiredTier;
        target.BlockWeakerPolicyFallback = source.BlockWeakerPolicyFallback;
        target.RequireCurrentFunctionEvidence = source.RequireCurrentFunctionEvidence;
        target.AllowPreferenceMaterialization = source.AllowPreferenceMaterialization;
        target.FallbackModelScoutDecisionReceipt = source.FallbackModelScoutDecisionReceipt;
        if (promoteConfiguredFallback)
        {
            target.ModelScoutDecisionReceipt = source.FallbackModelScoutDecisionReceipt;
            target.AuditCorrelationId = source.FallbackAuditCorrelationId ?? source.AuditCorrelationId;
            target.SupportsToolUse = source.FallbackSupportsToolUse;
            target.SupportsStructuredOutput = source.FallbackSupportsStructuredOutput;
            target.MaxContextTokens = source.FallbackMaxContextTokens;
            target.CapabilityEvidenceSource = source.FallbackCapabilityEvidenceSource;
            target.CapabilityScore = source.FallbackCapabilityScore;
            target.CapabilityEvidenceKind = source.FallbackCapabilityEvidenceKind;
        }
        else
        {
            target.ModelScoutDecisionReceipt ??= source.ModelScoutDecisionReceipt;
        }

        return target;
    }

    /// <summary>
    /// Final capability boundary shared by every primary-selection branch. The
    /// selector has several intentional fast paths (explicit overrides, durable
    /// preferences, local-only routing and compatibility defaults); none of them
    /// may bypass a caller's hard vision, structured-output or context-window
    /// requirement. A configured fallback may be used transiently when it proves
    /// every requirement. The stored user pin is never rewritten here.
    /// </summary>
    private async Task<LlmModelSelection> EnsureHardRequirementsOrFallbackAsync(
        string functionType,
        string prompt,
        LlmModelSelection selection,
        bool requiresVision,
        LlmRouterOptions? options)
    {
        var primaryIssue = await GetRouteRequirementIssueAsync(
            selection,
            requiresVision,
            options,
            verifyAvailability: false).ConfigureAwait(false);
        if (primaryIssue is null)
        {
            return selection;
        }

        if (primaryIssue == ModelNotInstalledIssue)
        {
            // The provider is healthy; only the exact artifact is missing. Give
            // the model self-healer (which rewrites the automatic preference to
            // an installed, proven sibling) first refusal before any fallback,
            // mirroring what the text path does on a model-not-found response.
            var healed = await TryHealMissingModelSelectionAsync(
                functionType,
                selection,
                requiresVision,
                options).ConfigureAwait(false);
            if (healed is not null)
            {
                return healed;
            }
        }

        if (options?.DisableFallback == true
            || string.IsNullOrWhiteSpace(selection.FallbackProvider)
            || string.IsNullOrWhiteSpace(selection.FallbackModel))
        {
            var explicitlyConstrained = selection.IsUserPinned
                                        || options?.DisableFallback == true
                                        || options?.OverrideProvenance is
                                            LlmOverrideProvenance.User or LlmOverrideProvenance.Profile;
            if (!explicitlyConstrained
                && _modelScout is not null
                && await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false))
            {
                // A durable automatic preference proves the route's ordinary
                // workload, not every one-off capability envelope a caller may
                // add later. Ask Scout once for a requirement-aware receipt
                // before treating the missing configured fallback as terminal.
                // This can re-admit the same route with fresh evidence or select
                // a different capable route without weakening any hard gate.
                var context = selection.Context ?? FunctionTypeProfiles.Resolve(
                    functionType,
                    options?.Interactivity ?? LlmInteractivity.Unspecified,
                    options?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
                    options?.Urgency ?? LlmUrgency.Unspecified,
                    options?.Workload ?? LlmWorkloadKind.Unspecified);
                var recoveredSelection = await TryGetModelScoutDefaultAsync(
                    functionType,
                    prompt,
                    context,
                    requiresVision,
                    options,
                    selection).ConfigureAwait(false);
                if (recoveredSelection is not null)
                {
                    var recoveredIssue = await GetRouteRequirementIssueAsync(
                        recoveredSelection,
                        requiresVision,
                        options,
                        verifyAvailability: true).ConfigureAwait(false);
                    if (recoveredIssue is null)
                    {
                        return recoveredSelection;
                    }
                }
            }

            var message =
                $"Selected route {selection.Provider}/{selection.Model} cannot satisfy the hard requirements for {functionType}: {primaryIssue}. "
                + "No proven configured fallback is available.";
            if (!explicitlyConstrained
                && IsAutomaticCapabilityDiscoveryPending(selection, primaryIssue, options))
            {
                // Automatic provider/catalog discovery can finish after the
                // first startup request. Surface a typed transient condition so
                // background queues can wait once and retry without weakening
                // capability, privacy, user-pin, or spend gates.
                throw new LlmRouteTemporarilyUnavailableException(message);
            }

            throw new NotSupportedException(message);
        }

        var fallbackProvider = ResolveRegisteredProviderName(selection.FallbackProvider);
        var fallbackModel = selection.FallbackModel;
        var fallbackIsExplicit = selection.IsUserPinned
                                 || options?.OverrideProvenance is
                                     LlmOverrideProvenance.User or LlmOverrideProvenance.Profile;
        var fallbackHasScoutReceipt = selection.FallbackModelScoutDecisionReceipt is { } fallbackReceipt
                                      && fallbackReceipt.Selected.Eligible
                                      && IsExactRouteIdentity(
                                          fallbackProvider,
                                          fallbackModel,
                                          ResolveRegisteredProviderName(fallbackReceipt.Selected.Provider),
                                          fallbackReceipt.Selected.Model);
        if (!fallbackIsExplicit && !fallbackHasScoutReceipt)
        {
            // A configured fallback attached to an automatic/system route is
            // not spend authority. Re-admit it through Scout (or choose another
            // Scout route) before a capability mismatch can silently turn a
            // zero-cost/subscription route into metered API traffic.
            var automaticScoutEnabled = _modelScout is not null
                                        && await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false);
            if (automaticScoutEnabled)
            {
                var context = selection.Context ?? FunctionTypeProfiles.Resolve(
                    functionType,
                    options?.Interactivity ?? LlmInteractivity.Unspecified,
                    options?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
                    options?.Urgency ?? LlmUrgency.Unspecified,
                    options?.Workload ?? LlmWorkloadKind.Unspecified);
                var readmittedFallback = await TryReadmitExactAutomaticRouteAsync(
                    functionType,
                    prompt,
                    context,
                    fallbackProvider,
                    fallbackModel,
                    requiresVision,
                    options,
                    $"Automatic configured fallback after {selection.Provider}/{selection.Model} failed hard capability admission",
                    selection.MinimumCapabilityScore,
                    selection.RequiredTier,
                    selection.RequireMeasuredCapabilityScore,
                    selection.BlockWeakerPolicyFallback,
                    selection.RequireCurrentFunctionEvidence).ConfigureAwait(false);
                if (readmittedFallback is not null)
                {
                    return readmittedFallback;
                }

                var recoveredSelection = await TryGetModelScoutDefaultAsync(
                    functionType,
                    prompt,
                    context,
                    requiresVision,
                    options,
                    selection).ConfigureAwait(false);
                if (recoveredSelection is not null)
                {
                    var recoveredIssue = await GetRouteRequirementIssueAsync(
                        recoveredSelection,
                        requiresVision,
                        options,
                        verifyAvailability: true).ConfigureAwait(false);
                    if (recoveredIssue is null)
                    {
                        return recoveredSelection;
                    }
                }
            }

            throw new LlmRouteSelectionBlockedException(
                $"Automatic fallback {fallbackProvider}/{fallbackModel} for {functionType} had no matching Model Scout decision receipt; unproven or surprise-cost routing was blocked.");
        }

        var fallback = CarryRoutePolicy(selection, new LlmModelSelection
        {
            Provider = fallbackProvider,
            Model = fallbackModel,
            IsDefault = false,
            // An explicit user fallback is still part of the user's route
            // constraint. Preserve that identity so later runtime recovery cannot
            // treat the fallback as an automatic route and rewrite it.
            IsUserPinned = selection.IsUserPinned,
            Reason = $"Capability fallback for {functionType}: primary {selection.Provider}/{selection.Model} was rejected ({primaryIssue})",
            Context = selection.Context,
            AuditCorrelationId = selection.FallbackAuditCorrelationId ?? selection.AuditCorrelationId,
            SupportsToolUse = selection.FallbackSupportsToolUse,
            SupportsStructuredOutput = selection.FallbackSupportsStructuredOutput,
            MaxContextTokens = selection.FallbackMaxContextTokens,
            CapabilityEvidenceSource = selection.FallbackCapabilityEvidenceSource,
        }, promoteConfiguredFallback: true);
        var fallbackIssue = await GetRouteRequirementIssueAsync(
            fallback,
            requiresVision,
            options,
            verifyAvailability: true).ConfigureAwait(false);
        if (fallbackIssue is not null)
        {
            throw new NotSupportedException(
                $"Selected route {selection.Provider}/{selection.Model} cannot satisfy the hard requirements for {functionType}: {primaryIssue}. "
                + $"Configured fallback {fallback.Provider}/{fallback.Model} was also rejected: {fallbackIssue}.");
        }

        _logger.LogWarning(
            "Route {Provider}/{Model} was rejected for {FunctionType} ({Reason}); using proven fallback {FallbackProvider}/{FallbackModel}",
            selection.Provider,
            selection.Model,
            functionType,
            primaryIssue,
            fallback.Provider,
            fallback.Model);
        return fallback;
    }

    /// <summary>
    /// Selection-time twin of the text path's model-not-found repair: when the
    /// selected route's exact model is missing from its provider, ask the
    /// self-healer for a proof-carrying replacement (same Scout receipt
    /// discipline as <c>TryBuildProofCarryingSelfHealSelectionAsync</c>, which
    /// also refuses to rewrite user/profile-directed routes) and validate it
    /// against the same hard requirements before admitting it.
    /// </summary>
    private async Task<LlmModelSelection?> TryHealMissingModelSelectionAsync(
        string functionType,
        LlmModelSelection selection,
        bool requiresVision,
        LlmRouterOptions? options)
    {
        if (_selfHealer is null)
        {
            return null;
        }

        LlmModelSelection? healed;
        try
        {
            if (await HasBoundSurvivalSetForPrimaryAsync(functionType, selection, options)
                    .ConfigureAwait(false))
            {
                // A bound survival set owns failover for this function; do not
                // race it with a second repair path.
                return null;
            }

            healed = await TryBuildProofCarryingSelfHealSelectionAsync(
                functionType,
                selection,
                options,
                requiresVision).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Model self-healing failed for missing route {Provider}/{Model} ({FunctionType})",
                selection.Provider,
                selection.Model,
                functionType);
            return null;
        }

        if (healed is null
            || IsExactRouteIdentity(healed.Provider, healed.Model, selection.Provider, selection.Model))
        {
            return null;
        }

        var healedIssue = await GetRouteRequirementIssueAsync(
            healed,
            requiresVision,
            options,
            verifyAvailability: true).ConfigureAwait(false);
        if (healedIssue is not null)
        {
            _logger.LogWarning(
                "Self-healed replacement {Provider}/{Replacement} for {FunctionType} cannot satisfy the hard requirements: {Issue}",
                healed.Provider,
                healed.Model,
                functionType,
                healedIssue);
            return null;
        }

        healed.Reason = $"Self-healed route: {selection.Model} is not installed on {selection.Provider}; replaced with {healed.Model} ({healed.Reason}).";
        _logger.LogWarning(
            "Route {Provider}/{Model} for {FunctionType} is not installed on the provider; self-healed to {Replacement}",
            selection.Provider,
            selection.Model,
            functionType,
            healed.Model);
        return healed;
    }

    private bool IsAutomaticCapabilityDiscoveryPending(
        LlmModelSelection selection,
        string primaryIssue,
        LlmRouterOptions? options)
    {
        var structuredEvidencePending = options?.RequiresStructuredOutput == true
                                        && primaryIssue.Contains(
                                            "structured-output capability is not proven",
                                            StringComparison.OrdinalIgnoreCase);
        var toolEvidencePending = options?.RequiresToolUse == true
                                  && primaryIssue.Contains(
                                      "tool-use",
                                      StringComparison.OrdinalIgnoreCase);
        var contextEvidencePending = options?.MinimumContextTokens is > 0
                                     && primaryIssue.Contains(
                                         "unverified",
                                         StringComparison.OrdinalIgnoreCase);
        if (!structuredEvidencePending && !toolEvidencePending && !contextEvidencePending)
        {
            return false;
        }

        LlmModelCapability? capability;
        try
        {
            capability = _capabilitiesService.GetModelCapability(selection.Provider, selection.Model);
        }
        catch
        {
            return true;
        }

        if (structuredEvidencePending)
        {
            return selection.SupportsStructuredOutput is null && capability is null;
        }

        if (toolEvidencePending)
        {
            return selection.SupportsToolUse is null && capability is null;
        }

        if (contextEvidencePending)
        {
            return selection.MaxContextTokens is null && capability?.MaxContextTokens is null;
        }

        return false;
    }

    /// <summary>
    /// Returns a fail-closed explanation when an exact route cannot prove the
    /// request's hard constraints. Runtime fallback resolution reuses this same
    /// validator so selection-time and execution-time safety cannot drift.
    /// Tool-use has an additional translator/calibration gate at its execution
    /// boundary and is intentionally not weakened here.
    /// </summary>
    private async Task<string?> GetRouteRequirementIssueAsync(
        LlmModelSelection route,
        bool requiresVision,
        LlmRouterOptions? options,
        bool verifyAvailability)
    {
        if (options?.ModelRequest is not null)
        {
            options = CloneRouterOptions(options);
            requiresVision |= options.RequiresVision == true;
        }
        if (ModelScoutModelRequests.Rejection(options?.ModelRequest, route.Provider, route.Model,
                tools: true, vision: true, structured: true) is { } modelRequestIssue)
            return modelRequestIssue; // Common capabilities are checked against the real provider below.
        if (await ViolatesLocalOnlyPrivacyAsync(route.Provider, route.Model, route.Context, options))
        {
            return "LocalOnly privacy forbids a cloud route";
        }

        // Local artifact formats are part of the physical execution contract.
        // A route copied from an Apple profile (for example an MLX tag) must not
        // reach a Windows Ollama process merely because that exact tag happens to
        // be installed or cached there. Keep this check Ollama-specific: a
        // RemotePeer route may legitimately execute the artifact on a paired Mac.
        if (route.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            && !LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(route.Model))
        {
            return "the local model artifact is not compatible with this computer";
        }

        var requiresStructuredOutput = options?.RequiresStructuredOutput == true;
        var requiresToolUse = options?.RequiresToolUse == true;
        if (ProviderPacks.ProviderPackCatalog.TryGetModelFacts(route.Provider, route.Model, out var transportFacts)
            && transportFacts?.ExecutionTransport is { } transport)
        {
            var contract = transportFacts.Transports.SingleOrDefault(t => t.Transport == transport);
            var issue = contract?.Reject(requiresToolUse, requiresVision, requiresStructuredOutput)
                ?? (contract is null ? "The selected model transport has no verified capability contract." : null);
            if (issue is not null) return issue;
        }
        var minimumContextTokens = options?.MinimumContextTokens is > 0
            ? options.MinimumContextTokens
            : null;
        if (!verifyAvailability
            && !requiresVision
            && !requiresToolUse
            && !requiresStructuredOutput
            && minimumContextTokens is null)
        {
            return null;
        }

        var provider = _providerRegistry.GetProvider(route.Provider);
        if (provider is null)
        {
            return "the provider is not registered";
        }
        if (!provider.SupportsModel(route.Model))
        {
            return "the provider does not advertise support for the exact model";
        }
        if (verifyAvailability
            && !await IsProviderAvailableAsync(
                route.Provider,
                options?.RequestCancellationToken ?? CancellationToken.None,
                waitForDeferredCapacity: true).ConfigureAwait(false))
        {
            return "the provider is not available";
        }
        // Re-read the exact artifact before rejecting a capability. A custom import may have
        // acquired its missing component since startup; cached discovery is not today's proof.
        var metadata = requiresVision || requiresToolUse
            ? await Services.LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, route.Model,
                options?.RequestCancellationToken ?? CancellationToken.None).ConfigureAwait(false)
            : null;
        if (metadata is not null) Services.LlmModelCapabilityEvidenceResolver.Apply(route, metadata);
        if (requiresVision && !(metadata?.SupportsVision ?? provider.SupportsVision(route.Model)))
        {
            return "vision capability is not proven by the registered provider";
        }

        if (requiresToolUse)
        {
            if (metadata is null && provider is ILlmModelCapabilityEvidenceProvider { RequiresModelCapabilityEvidence: true })
                return "fresh read-only model capability metadata is unavailable";
            var toolIssue = GetToolUseSelectionIssue(route);

            // "Not proven" means untested, not incapable. Rather than refusing a
            // route nobody has measured — which is how a local model got
            // rejected in 0.1s without a token ever reaching it — ask the model
            // to make one trivial tool call and let the answer decide. The
            // verdict is cached per route, so this costs one probe ever, and an
            // inconclusive probe (provider down, timeout) is not recorded.
            if (toolIssue == ToolUseCapabilityUnknownIssue && _toolUseProver is not null
                && provider is not ILlmModelCapabilityEvidenceProvider { RequiresModelCapabilityEvidence: true })
            {
                var proved = await _toolUseProver
                    .ProveToolUseAsync(
                        route.Provider,
                        route.Model,
                        options?.RequestCancellationToken ?? CancellationToken.None)
                    .ConfigureAwait(false);

                toolIssue = proved switch
                {
                    true => null,
                    false => "the model was probed and did not produce a tool call",
                    null => toolIssue,
                };

                // "Unknown" because the provider said the model is not there is
                // not a capability question at all. Surface it as a distinct,
                // non-transient issue so the caller heals or fails instead of
                // polling for evidence that can never arrive.
                if (toolIssue == ToolUseCapabilityUnknownIssue
                    && _toolUseProver.GetLastProbeFailureCategory(route.Provider, route.Model)
                        is LlmFailureCategory.ModelNotFound)
                {
                    toolIssue = ModelNotInstalledIssue;
                }
            }

            if (toolIssue is not null)
            {
                return toolIssue;
            }
        }

        LlmModelCapability? capability = null;
        if (RequiresNativeStructuredOutput(options) || minimumContextTokens is not null)
        {
            try
            {
                capability = _capabilitiesService.GetModelCapability(route.Provider, route.Model);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not verify hard model capabilities for {Provider}/{Model}",
                    route.Provider,
                    route.Model);
                return "model capability evidence could not be read";
            }
        }

        if (RequiresNativeStructuredOutput(options)
            && (route.SupportsStructuredOutput ?? capability?.SupportsStructuredOutput) != true)
        {
            return "structured-output capability is not proven";
        }

        if (minimumContextTokens is { } requiredContext)
        {
            var verifiedSizes = new[] { route.MaxContextTokens, capability?.MaxContextTokens }
                .Where(value => value is > 0)
                .Select(value => value!.Value)
                .ToList();
            if (verifiedSizes.Count == 0)
            {
                return $"a {requiredContext:N0}-token context window is required but unverified";
            }
            var verifiedContext = verifiedSizes.Min();
            if (verifiedContext < requiredContext)
            {
                return $"verified context window {verifiedContext:N0} is below the {requiredContext:N0}-token requirement";
            }
        }

        return null;
    }

    private async Task<bool> ViolatesLocalOnlyPrivacyAsync(
        string provider,
        string? model,
        LlmCallContext? context,
        LlmRouterOptions? options)
        => (context?.PrivacyClass == LlmPrivacyClass.LocalOnly
            || options?.PrivacyClass == LlmPrivacyClass.LocalOnly)
           && !await IsLocalRouteAsync(provider, model).ConfigureAwait(false);

    private async Task<LlmFunctionPreference?> TryReadPreferenceForAuditAsync(string functionType)
    {
        if (string.Equals(functionType, FunctionTypes.MessagePipeline, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await _settingsProvider.GetFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read pre-selection preference for audit on {FunctionType}", functionType);
            return null;
        }
    }

    private async Task AppendSelectionAuditAsync(
        string functionType,
        string prompt,
        LlmRouterOptions? options,
        LlmFunctionPreference? before,
        LlmModelSelection? selection,
        string correlationId,
        bool requiresVision,
        string? error,
        Exception? failure,
        RoutingSelectionTrace trace)
    {
        try
        {
            var explicitOverride = !string.IsNullOrWhiteSpace(options?.OverrideProvider)
                                   && !string.IsNullOrWhiteSpace(options.OverrideModel);
            var userOverride = explicitOverride
                               && options?.OverrideProvenance == LlmOverrideProvenance.User;
            var modelScoutDecision = selection?.ModelScoutDecisionReceipt;
            var functionEvidence = modelScoutDecision?.FunctionEvidence;
            var failureScope = ClassifySelectionFailureScope(failure);
            var evidence = JsonSerializer.Serialize(new
            {
                failureScope,
                requestedOverride = explicitOverride
                    ? new { provider = options!.OverrideProvider, model = options.OverrideModel }
                    : null,
                validatedAdmission = selection?.ValidatedAdmissionReceipt is null
                    ? null
                    : new
                    {
                        functionType = selection.ValidatedAdmissionReceipt.FunctionType,
                        provider = selection.ValidatedAdmissionReceipt.Provider,
                        model = selection.ValidatedAdmissionReceipt.Model,
                        source = selection.ValidatedAdmissionReceipt.Source,
                        admissionMode = selection.ValidatedAdmissionReceipt.AdmissionMode.ToString(),
                        policyLane = selection.ValidatedAdmissionReceipt.PolicyLane.ToString(),
                        proofIdentitySha256 = selection.ValidatedAdmissionReceipt.ProofIdentitySha256,
                        promptSha256 = selection.ValidatedAdmissionReceipt.PromptSha256,
                        suiteSha256 = selection.ValidatedAdmissionReceipt.SuiteSha256,
                        evaluatedAtUtc = selection.ValidatedAdmissionReceipt.EvaluatedAtUtc,
                        expiresAtUtc = selection.ValidatedAdmissionReceipt.ExpiresAtUtc,
                    },
                configuredRoute = before is null
                    ? null
                    : new
                    {
                        provider = before.PreferredProvider,
                        model = before.PreferredModel,
                        fallbackProvider = before.FallbackProvider,
                        fallbackModel = before.FallbackModel,
                        updatedBy = before.UpdatedBy,
                        userPinned = LlmPreferenceAuthors.IsUserPinned(before.PinnedByUserAt)
                    },
                selectedCandidate = selection is null
                    ? null
                    : new
                    {
                        provider = selection.Provider,
                        model = selection.Model,
                        fallbackProvider = selection.FallbackProvider,
                        fallbackModel = selection.FallbackModel,
                        selection.IsDefault,
                        selection.IsUserPinned,
                        selection.SupportsToolUse,
                        selection.SupportsStructuredOutput,
                        selection.MaxContextTokens,
                        selection.CapabilityEvidenceSource,
                        selection.CapabilityScore,
                        capabilityEvidenceKind = selection.CapabilityEvidenceKind.ToString(),
                        selection.Reason
                    },
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
                requirements = new
                {
                    requiresVision,
                    requiresToolUse = options?.RequiresToolUse == true,
                    requiresStructuredOutput = options?.RequiresStructuredOutput == true,
                    minimumContextTokens = options?.MinimumContextTokens,
                    minimumCapabilityScore = StrongerCapabilityFloor(
                        selection?.MinimumCapabilityScore,
                        options?.MinimumCapabilityScore),
                    requireMeasuredCapabilityScore = selection?.RequireMeasuredCapabilityScore == true
                                                       || options?.RequireMeasuredCapabilityScore == true,
                    requiredTier = StrongerRequiredTier(
                        selection?.RequiredTier,
                        options?.RequiredTier)?.ToString(),
                    blockWeakerPolicyFallback = selection?.BlockWeakerPolicyFallback == true
                                                || options?.BlockWeakerPolicyFallback == true,
                    requireCurrentFunctionEvidence = selection?.RequireCurrentFunctionEvidence == true
                                                     || options?.RequireCurrentFunctionEvidence == true,
                    requireCurrentWorkloadSizing = options?.RequireCurrentWorkloadSizing == true,
                    estimatedOutputTokens = ResolveEstimatedOutputTokens(options),
                    disableFallback = options?.DisableFallback == true,
                    allowAnyProviderFallback = options?.AllowAnyProviderFallback == true,
                    overrideProvenance = options?.OverrideProvenance.ToString(),
                    interactivity = selection?.Context?.Interactivity.ToString(),
                    privacy = selection?.Context?.PrivacyClass.ToString(),
                    urgency = selection?.Context?.Urgency.ToString(),
                    latencyBudgetMs = selection?.Context?.LatencyBudgetMs
                },
                governorCalls = trace.GovernorCalls.Select(call => new
                {
                    call.HardSafetyOnly,
                    advisorName = RoutingDiagnosticSanitizer.Sanitize(call.AdvisorName, 160),
                    policyKind = call.PolicyKind.ToString(),
                    call.Priority,
                    routingPolicy = new
                    {
                        call.Policy.MonthlyCloudBudgetUsd,
                        call.Policy.UrgencyBoostMonthlyBudgetUsd,
                        call.Policy.BudgetPressureThreshold,
                        call.Policy.SubscriptionQuotaThreshold,
                        call.Policy.AllowUrgencyBoost,
                        call.Policy.AllowMeteredPipelineFallback,
                        call.Policy.MeteredPipelineDailyCapUsd,
                        call.Policy.ModelScoutCapabilityWeight,
                        call.Policy.ModelScoutSpeedWeight,
                        call.Policy.ModelScoutCostWeight,
                        call.Policy.UseCustomModelScoutWeights,
                    },
                    budgetPressure = new
                    {
                        call.BudgetPressure.MonthToDateUsd,
                        call.BudgetPressure.MonthlyBudgetUsd,
                        call.BudgetPressure.BurnFraction,
                        call.BudgetPressure.ProjectedMonthUsd,
                    },
                    evaluations = call.Evaluations.Select(evaluation => new
                    {
                        advisorName = RoutingDiagnosticSanitizer.Sanitize(evaluation.AdvisorName, 160),
                        policyKind = evaluation.PolicyKind.ToString(),
                        evaluation.Priority,
                        evaluation.Recommended,
                        provider = RoutingDiagnosticSanitizer.Sanitize(evaluation.Provider, 80),
                        model = RoutingDiagnosticSanitizer.Sanitize(evaluation.Model, 240),
                        evaluation.PersistPreference,
                        reason = RoutingDiagnosticSanitizer.Sanitize(evaluation.Reason, 512),
                        errorMessage = RoutingDiagnosticSanitizer.Sanitize(evaluation.ErrorMessage, 512),
                    }),
                    candidates = call.Candidates.Select(candidate => new
                    {
                        advisorName = RoutingDiagnosticSanitizer.Sanitize(candidate.AdvisorName, 160),
                        policyKind = candidate.PolicyKind.ToString(),
                        candidate.Priority,
                        provider = RoutingDiagnosticSanitizer.Sanitize(candidate.Provider, 80),
                        model = RoutingDiagnosticSanitizer.Sanitize(candidate.Model, 240),
                        fallbackProvider = RoutingDiagnosticSanitizer.Sanitize(candidate.FallbackProvider, 80),
                        fallbackModel = RoutingDiagnosticSanitizer.Sanitize(candidate.FallbackModel, 240),
                        candidate.MinimumCapabilityScore,
                        candidate.RequireMeasuredCapabilityScore,
                        requiredTier = candidate.RequiredTier?.ToString(),
                        candidate.BlockWeakerPolicyFallback,
                        candidate.RequireCurrentFunctionEvidence,
                        candidate.RequiresVision,
                        candidate.SupportsToolUse,
                        candidate.SupportsStructuredOutput,
                        candidate.MaxContextTokens,
                        capabilityEvidenceSource = RoutingDiagnosticSanitizer.Sanitize(candidate.CapabilityEvidenceSource, 256),
                        candidate.PersistPreference,
                        reason = RoutingDiagnosticSanitizer.Sanitize(candidate.Reason, 512),
                    })
                })
            }, RoutingAuditJsonOptions);

            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = LlmRoutingAuditEventKinds.Selection,
                Actor = userOverride
                    ? options?.UserId ?? "user"
                    : explicitOverride
                        ? $"llm-router:{options?.OverrideProvenance.ToString().ToLowerInvariant() ?? "unspecified"}"
                        : "llm-router",
                Trigger = explicitOverride
                    ? $"explicit-override:{options?.OverrideProvenance.ToString().ToLowerInvariant() ?? "unspecified"}"
                    : ClassifySelectionTrigger(selection?.Reason),
                FunctionType = functionType,
                Context = selection?.Context is null
                    ? null
                    : $"{selection.Context.Interactivity}/{selection.Context.PrivacyClass}/{selection.Context.Urgency}",
                BeforeProvider = before?.PreferredProvider,
                BeforeModel = before?.PreferredModel,
                AfterProvider = selection?.Provider,
                AfterModel = selection?.Model,
                FallbackProvider = selection?.FallbackProvider,
                FallbackModel = selection?.FallbackModel,
                AdvisorOrPolicy = selection?.Reason,
                Reason = selection?.Reason ?? error,
                EvidenceJson = evidence,
                PromptSha256 = ComputeHash(prompt),
                BenchmarkPromptSha256 = selection?.ValidatedAdmissionReceipt?.PromptSha256,
                BenchmarkSuiteSha256 = selection?.ValidatedAdmissionReceipt?.SuiteSha256
                                       ?? functionEvidence?.SuiteSha256,
                CorrelationId = correlationId,
                SessionId = options?.SessionId,
                TurnId = options?.TurnId,
                IsUserOverride = userOverride,
                Succeeded = selection is not null && error is null,
                Error = error
            }).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            _logger.LogError(auditEx, "Failed to append routing audit for {FunctionType}", functionType);
            // A route choice must be durably explainable before provider
            // execution. Host stores may commit to an emergency outbox when the
            // primary ledger is unavailable; reaching this catch means neither
            // durable path succeeded, so fail closed before any model call.
            throw new InvalidOperationException(
                $"Routing selection for '{functionType}' was blocked because its audit receipt could not be committed.",
                auditEx);
        }
    }

    private static string ClassifySelectionTrigger(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "router-selection";
        if (reason.Contains("advisor", StringComparison.OrdinalIgnoreCase)) return "route-advisor";
        if (reason.Contains("preference", StringComparison.OrdinalIgnoreCase)) return "function-preference";
        if (reason.Contains("fallback", StringComparison.OrdinalIgnoreCase)) return "configured-fallback";
        if (reason.Contains("default", StringComparison.OrdinalIgnoreCase)) return "default-selection";
        if (reason.Contains("privacy", StringComparison.OrdinalIgnoreCase)) return "privacy-policy";
        return "router-selection";
    }

    private static string? ClassifySelectionFailureScope(Exception? failure)
        => failure switch
        {
            null => null,
            LlmRouteSelectionBlockedException => LlmRoutingAuditFailureScopes.RequestProofOrPolicyGate,
            MissingCurrentFunctionProofException => LlmRoutingAuditFailureScopes.RequestProofOrPolicyGate,
            _ => LlmRoutingAuditFailureScopes.FunctionRouteFailure,
        };

    private static IReadOnlyList<RoutingGovernorCandidateTrace> CaptureGovernorCandidates(
        LlmRoutingGovernorDecision decision)
    {
        var candidates = decision.Candidates.Count > 0
            ? decision.Candidates
            : decision.Recommendation is null
                ? []
                :
                [
                    new LlmRoutingGovernorCandidate
                    {
                        AdvisorName = decision.AdvisorName ?? "Route advisor",
                        PolicyKind = decision.PolicyKind,
                        Priority = decision.Priority,
                        Recommendation = decision.Recommendation,
                    }
                ];

        return candidates
            .Take(16)
            .Select(candidate =>
            {
                var recommendation = candidate.Recommendation;
                return new RoutingGovernorCandidateTrace(
                    candidate.AdvisorName,
                    candidate.PolicyKind,
                    candidate.Priority,
                    recommendation.Provider,
                    recommendation.Model,
                    recommendation.FallbackProvider,
                    recommendation.FallbackModel,
                    recommendation.MinimumCapabilityScore,
                    recommendation.RequireMeasuredCapabilityScore,
                    recommendation.RequiredTier,
                    recommendation.BlockWeakerPolicyFallback,
                    recommendation.RequireCurrentFunctionEvidence,
                    recommendation.RequiresVision,
                    recommendation.SupportsToolUse,
                    recommendation.SupportsStructuredOutput,
                    recommendation.MaxContextTokens,
                    recommendation.CapabilityEvidenceSource,
                    recommendation.PersistPreference,
                    recommendation.Reason);
            })
            .ToList();
    }

    private async Task<LlmModelSelection?> TryGetAdvisedSelectionAsync(
        string functionType,
        string prompt,
        LlmCallContext context,
        LlmFunctionPreference? preference,
        bool requiresVision,
        LlmRouterOptions? options,
        RoutingSelectionTrace trace,
        bool hardSafetyOnly = false,
        bool includeNonBlockingHardSafetyRecommendations = false,
        string? exactProviderConstraint = null,
        string? exactModelConstraint = null,
        string? exactFallbackProviderConstraint = null,
        string? exactFallbackModelConstraint = null)
    {
        var systemActivity = _systemActivity?.Current() ?? SystemActivitySnapshot.Active();
        var policy = _routingPolicyStore is null
            ? RoutingPolicy.Default
            : await _routingPolicyStore.GetAsync().ConfigureAwait(false);
        var budgetPressure = _budgetPressureSource is null
            ? BudgetPressureSnapshot.Unconstrained
            : await SafeReadBudgetPressureAsync().ConfigureAwait(false);

        var advisorContext = new LlmRouteAdvisorContext
        {
            FunctionType = functionType,
            CallContext = context,
            CurrentPreference = preference,
            PromptText = prompt,
            UserRequestText = options?.RoutingUserText,
            AvailableToolIds = options?.RoutingAvailableToolIds ?? [],
            CompletedToolIds = options?.RoutingCompletedToolIds ?? [],
            TurnId = options?.TurnId,
            SessionId = options?.SessionId,
            TaskDemand = options?.TaskDemand,
            RequiresVision = requiresVision,
            RequiresToolUse = options?.RequiresToolUse == true,
            RequiresStructuredOutput = options?.RequiresStructuredOutput == true,
            StructuredOutputRepairAvailable = options?.AllowTextJsonContractRepair == true,
            MinimumContextTokens = options?.MinimumContextTokens,
            WorkloadKind = LocalLlmWorkloadKind.Text,
            PromptTokenEstimate = EstimatePromptTokens(prompt),
            EstimatedOutputTokens = ResolveEstimatedOutputTokens(options),
            SystemActivity = systemActivity,
            Policy = policy,
            BudgetPressure = budgetPressure,
        };

        LlmRoutingGovernorDecision decision;
        var requestCancellation = options?.RequestCancellationToken
                                  ?? CancellationToken.None;
        try
        {
            decision = hardSafetyOnly
                ? await GetHardSafetyAdvisorDecisionAsync(
                        advisorContext,
                        requestCancellation)
                    .ConfigureAwait(false)
                : await GetRoutingGovernorDecisionAsync(
                        advisorContext,
                        requestCancellation)
                    .ConfigureAwait(false);
        }
        catch (LlmRouteSelectionBlockedException ex)
            when (!hardSafetyOnly
                  && string.IsNullOrWhiteSpace(exactProviderConstraint)
                  && IsRecoverableAdvisorRouteBlock(ex))
        {
            // An advisor is an optimizer, not the chat circuit breaker.  A
            // high-stakes advisor may quite correctly refuse to promote a
            // route while its exact proof is being refreshed, but ordinary
            // chat must continue to Model Scout/configured fallback routing.
            // Exact user/profile/provider constraints and the hard-safety lane
            // still fail closed below; this branch only turns a recoverable
            // proof wait into an empty advisory result.
            _logger.LogWarning(
                "Route advisor deferred {FunctionType} while exact proof recovers; continuing automatic route discovery: {Reason}",
                functionType,
                RoutingDiagnosticSanitizer.Sanitize(ex.Message, 512));
            decision = LlmRoutingGovernorDecision.None();
        }
        catch (Exception ex)
        {
            trace.GovernorCalls.Add(new RoutingGovernorTrace(
                hardSafetyOnly,
                AdvisorName: null,
                LlmRoutePolicyKind.General,
                LlmRoutePolicyPriorities.General,
                RoutingPolicy.NormalizeUntrusted(policy),
                budgetPressure,
                [new LlmRouteAdvisorEvaluation
                {
                    AdvisorName = hardSafetyOnly ? "hard-safety-governor" : "routing-governor",
                    Recommended = false,
                    ErrorMessage = RoutingDiagnosticSanitizer.Sanitize(ex.Message, 512),
                }],
                []));
            throw;
        }

        var evaluations = decision.Evaluations.Count > 0
            ? decision.Evaluations
            : decision.Candidates
                .Select(candidate => new LlmRouteAdvisorEvaluation
                {
                    AdvisorName = candidate.AdvisorName,
                    PolicyKind = candidate.PolicyKind,
                    Priority = candidate.Priority,
                    Recommended = true,
                    Provider = candidate.Recommendation.Provider,
                    Model = candidate.Recommendation.Model,
                    PersistPreference = candidate.Recommendation.PersistPreference,
                    Reason = candidate.Recommendation.Reason,
                })
                .ToList();
        trace.GovernorCalls.Add(new RoutingGovernorTrace(
            hardSafetyOnly,
            decision.AdvisorName,
            decision.PolicyKind,
            decision.Priority,
            RoutingPolicy.NormalizeUntrusted(policy),
            budgetPressure,
            evaluations.Take(16).ToList(),
            CaptureGovernorCandidates(decision)));
        if (!decision.HasRecommendation)
        {
            return null;
        }

        IReadOnlyList<LlmRoutingGovernorCandidate> candidates = decision.Candidates.Count > 0
            ? decision.Candidates
            :
            [
                new LlmRoutingGovernorCandidate
                {
                    AdvisorName = decision.AdvisorName ?? "Route advisor",
                    PolicyKind = decision.PolicyKind,
                    Priority = decision.Priority,
                    IsHardSafetyPolicy = hardSafetyOnly,
                    Recommendation = decision.Recommendation!
                }
            ];

        foreach (var candidate in candidates)
        {
            var recommendation = candidate.Recommendation;
            var advisorName = string.IsNullOrWhiteSpace(candidate.AdvisorName)
                ? "Route advisor"
                : candidate.AdvisorName;

            if (hardSafetyOnly
                && !includeNonBlockingHardSafetyRecommendations
                && (!recommendation.BlockWeakerPolicyFallback
                    || !recommendation.RequireCurrentFunctionEvidence))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(recommendation.Provider)
                || string.IsNullOrWhiteSpace(recommendation.Model))
            {
                _logger.LogWarning(
                    "Route advisor {Advisor} returned an incomplete recommendation for {FunctionType}",
                    advisorName,
                    functionType);
                if (recommendation.BlockWeakerPolicyFallback)
                {
                    throw new LlmRouteSelectionBlockedException(
                        $"Route policy {advisorName} blocked weaker routing for {functionType} after returning an incomplete safety-constrained route.");
                }
                continue;
            }

            var provider = ResolveRegisteredProviderName(recommendation.Provider);
            if (!string.IsNullOrWhiteSpace(exactProviderConstraint)
                && !string.IsNullOrWhiteSpace(exactModelConstraint)
                && !IsExactRouteIdentity(
                    ResolveRegisteredProviderName(exactProviderConstraint),
                    exactModelConstraint,
                    provider,
                    recommendation.Model)
                && (string.IsNullOrWhiteSpace(exactFallbackProviderConstraint)
                    || string.IsNullOrWhiteSpace(exactFallbackModelConstraint)
                    || !IsExactRouteIdentity(
                        ResolveRegisteredProviderName(exactFallbackProviderConstraint),
                        exactFallbackModelConstraint,
                        provider,
                        recommendation.Model)))
            {
                throw new LlmRouteSelectionBlockedException(
                    $"Hard route policy {advisorName} could not admit the exact constrained route {exactProviderConstraint}/{exactModelConstraint} for {functionType}; the user/configured target was not replaced.");
            }
            if (context.PrivacyClass == LlmPrivacyClass.LocalOnly
                && !await IsLocalRouteAsync(provider, recommendation.Model))
            {
                _logger.LogWarning(
                    "Route advisor {Advisor} recommended cloud provider {Provider} for LocalOnly {FunctionType}; skipping",
                    advisorName,
                    provider,
                    functionType);
                if (recommendation.BlockWeakerPolicyFallback)
                {
                    throw new LlmRouteSelectionBlockedException(
                        $"Route policy {advisorName} blocked weaker routing because its safety-constrained route violates LocalOnly privacy for {functionType}.");
                }
                continue;
            }

            var hasFallbackProvider = !string.IsNullOrWhiteSpace(recommendation.FallbackProvider);
            var hasFallbackModel = !string.IsNullOrWhiteSpace(recommendation.FallbackModel);
            if (hasFallbackProvider != hasFallbackModel)
            {
                _logger.LogWarning(
                    "Route advisor {Advisor} returned an incomplete optional fallback for {FunctionType}; ignoring that incomplete route",
                    advisorName,
                    functionType);
                hasFallbackProvider = false;
                hasFallbackModel = false;
            }

            bool RespectsExactRouteConstraint(string candidateProvider, string candidateModel)
                => string.IsNullOrWhiteSpace(exactProviderConstraint)
                   || string.IsNullOrWhiteSpace(exactModelConstraint)
                   || IsExactRouteIdentity(
                       ResolveRegisteredProviderName(exactProviderConstraint),
                       exactModelConstraint,
                       candidateProvider,
                       candidateModel)
                   || (!string.IsNullOrWhiteSpace(exactFallbackProviderConstraint)
                       && !string.IsNullOrWhiteSpace(exactFallbackModelConstraint)
                       && IsExactRouteIdentity(
                           ResolveRegisteredProviderName(exactFallbackProviderConstraint),
                           exactFallbackModelConstraint,
                           candidateProvider,
                           candidateModel));

            var provenPrimary = candidate.IsHardSafetyPolicy
                                && recommendation.AllowDegradedExecutionWithoutCurrentScoutProof
                ? await TryAdmitExplicitDegradedHardSafetyRouteAsync(
                        functionType,
                        context,
                        provider,
                        recommendation,
                        requiresVision || recommendation.RequiresVision,
                        options,
                        advisorName)
                    .ConfigureAwait(false)
                : null;
            provenPrimary ??= await TryReadmitExactAutomaticRouteAsync(
                    functionType,
                    prompt,
                    context,
                    provider,
                    recommendation.Model,
                    requiresVision || recommendation.RequiresVision,
                    options,
                    $"Route advisor {advisorName}",
                    recommendation.MinimumCapabilityScore,
                    recommendation.RequiredTier,
                    recommendation.RequireMeasuredCapabilityScore,
                    recommendation.BlockWeakerPolicyFallback,
                    recommendation.RequireCurrentFunctionEvidence)
                .ConfigureAwait(false);
            var promotedFallbackToPrimary = false;
            if (provenPrimary is null && hasFallbackProvider)
            {
                var fallbackProvider = ResolveRegisteredProviderName(recommendation.FallbackProvider!);
                if (RespectsExactRouteConstraint(fallbackProvider, recommendation.FallbackModel!))
                {
                    provenPrimary = await TryReadmitExactAutomaticRouteAsync(
                        functionType,
                        prompt,
                        context,
                        fallbackProvider,
                        recommendation.FallbackModel!,
                        requiresVision || recommendation.RequiresVision,
                        options,
                        $"Route advisor {advisorName} promoted fallback",
                        recommendation.MinimumCapabilityScore,
                        recommendation.RequiredTier,
                        recommendation.RequireMeasuredCapabilityScore,
                        recommendation.BlockWeakerPolicyFallback,
                        recommendation.RequireCurrentFunctionEvidence).ConfigureAwait(false);
                    promotedFallbackToPrimary = provenPrimary is not null;
                }

                if (promotedFallbackToPrimary)
                {
                    _logger.LogInformation(
                        "Route advisor {Advisor} primary was unavailable for {FunctionType}; promoted independently re-admitted fallback {Provider}/{Model}",
                        advisorName,
                        functionType,
                        provenPrimary!.Provider,
                        provenPrimary.Model);
                }
            }
            if (provenPrimary is null)
            {
                _logger.LogWarning(
                    "Route advisor {Advisor} recommendation {Provider}/{Model} was skipped for {FunctionType}: exact current Scout proof was not available",
                    advisorName,
                    provider,
                    recommendation.Model,
                    functionType);
                if (recommendation.BlockWeakerPolicyFallback)
                {
                    // A system advisor's proof floor is a promotion preference,
                    // not permission to strand the conversation.  User pins and
                    // explicit provider/model constraints are handled by the
                    // hard-safety path above; an automatic advisor with no exact
                    // constraint must fall back to Scout's best reachable route.
                    if (string.IsNullOrWhiteSpace(exactProviderConstraint)
                        && !RequiresStrictCurrentFunctionEvidence(functionType))
                    {
                        // Preserve an advisor's explicit quality floor while
                        // dropping only its temporary proof-gate requirement.
                        // This keeps typed/high-stakes JSON recovery from being
                        // silently retried at a weaker capability envelope.
                        var qualitySource = new LlmModelSelection
                        {
                            Provider = recommendation.Provider,
                            Model = recommendation.Model,
                            MinimumCapabilityScore = recommendation.MinimumCapabilityScore,
                            RequiredTier = recommendation.RequiredTier,
                            RequireMeasuredCapabilityScore = recommendation.RequireMeasuredCapabilityScore,
                        };
                        var bestAvailable = await TryGetModelScoutDefaultAsync(
                                functionType,
                                prompt,
                                context,
                                requiresVision,
                                options,
                                qualitySource)
                            .ConfigureAwait(false);
                        if (bestAvailable is not null)
                        {
                            bestAvailable.AllowPreferenceMaterialization =
                                recommendation.PersistPreference
                                || recommendation.AllowSuccessfulBootstrapMaterialization;
                            bestAvailable.Reason =
                                $"Advisor {advisorName} is still refreshing stronger evidence; {bestAvailable.Reason}";
                            return bestAvailable;
                        }
                    }

                    throw new LlmRouteSelectionBlockedException(
                        $"Route policy {advisorName} found no independently re-admitted route satisfying its safety floor for {functionType}; weaker routing was blocked.");
                }
                continue;
            }

            LlmModelSelection? provenFallback = null;
            if (hasFallbackProvider && !promotedFallbackToPrimary)
            {
                var fallbackProvider = ResolveRegisteredProviderName(recommendation.FallbackProvider!);
                if (RespectsExactRouteConstraint(fallbackProvider, recommendation.FallbackModel!))
                {
                    provenFallback = await TryReadmitExactAutomaticRouteAsync(
                        functionType,
                        prompt,
                        context,
                        fallbackProvider,
                        recommendation.FallbackModel!,
                        requiresVision || recommendation.RequiresVision,
                        options,
                        $"Route advisor {advisorName} fallback",
                        recommendation.MinimumCapabilityScore,
                        recommendation.RequiredTier,
                        recommendation.RequireMeasuredCapabilityScore,
                        recommendation.BlockWeakerPolicyFallback,
                        recommendation.RequireCurrentFunctionEvidence).ConfigureAwait(false);
                }
                if (provenFallback is null)
                {
                    _logger.LogWarning(
                        "Route advisor {Advisor} optional fallback {Provider}/{Model} was not re-admitted for {FunctionType}; retaining the independently re-admitted primary without that fallback",
                        advisorName,
                        fallbackProvider,
                        recommendation.FallbackModel,
                        functionType);
                    // Keep the independently re-admitted primary for every policy
                    // kind. Runtime failover will re-evaluate current evidence.
                    provenFallback = null;
                }
            }

            if (recommendation.PersistPreference)
            {
                await PersistAdvisedPreferenceAsync(
                    functionType,
                    preference,
                    provenPrimary.Provider,
                    provenPrimary.Model,
                    provenFallback?.Provider,
                    provenFallback?.Model,
                    recommendation.RequiresVision || requiresVision,
                    recommendation.UpdatedBy ?? advisorName);
            }

            provenPrimary.FallbackProvider = provenFallback?.Provider;
            provenPrimary.FallbackModel = provenFallback?.Model;
            provenPrimary.FallbackAuditCorrelationId = provenFallback?.AuditCorrelationId;
            provenPrimary.FallbackSupportsToolUse = provenFallback?.SupportsToolUse;
            provenPrimary.FallbackSupportsStructuredOutput = provenFallback?.SupportsStructuredOutput;
            provenPrimary.FallbackMaxContextTokens = provenFallback?.MaxContextTokens;
            provenPrimary.FallbackCapabilityEvidenceSource = provenFallback?.CapabilityEvidenceSource;
            provenPrimary.FallbackCapabilityScore = provenFallback?.CapabilityScore;
            provenPrimary.FallbackCapabilityEvidenceKind = provenFallback?.CapabilityEvidenceKind
                ?? ModelScoutCapabilityEvidenceKind.Unknown;
            provenPrimary.FallbackModelScoutDecisionReceipt = provenFallback?.ModelScoutDecisionReceipt;
            provenPrimary.AllowPreferenceMaterialization =
                recommendation.PersistPreference
                || recommendation.AllowSuccessfulBootstrapMaterialization;
            provenPrimary.IsDefault = false;
            provenPrimary.Reason = promotedFallbackToPrimary
                ? $"{recommendation.Reason ?? $"Route advisor {advisorName} for {functionType}"}; primary route unavailable, so the independently re-admitted fallback was promoted."
                : recommendation.Reason ?? $"Route advisor {advisorName} for {functionType}";
            provenPrimary.ExecutionSettings = recommendation.ExecutionSettings;
            return provenPrimary;
        }

        return null;
    }

    private static bool IsRecoverableAdvisorRouteBlock(LlmRouteSelectionBlockedException exception)
    {
        var message = exception.Message;
        return message.Contains("no current exact", StringComparison.OrdinalIgnoreCase)
               || message.Contains("current function-scoped", StringComparison.OrdinalIgnoreCase)
               || message.Contains("bounded proof recovery", StringComparison.OrdinalIgnoreCase)
               || message.Contains("proof recovery remains active", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Admits the one intentionally degraded route returned by a hard-safety
    /// advisor after its stronger proof search failed. This is not generic
    /// advisor trust: the governor supplies the hard-safety provenance, this
    /// method refuses every explicit proof/quality envelope and strict-proof
    /// function, and the route must still pass the router's physical privacy,
    /// availability, exact-model, and request-capability checks.
    /// </summary>
    private async Task<LlmModelSelection?> TryAdmitExplicitDegradedHardSafetyRouteAsync(
        string functionType,
        LlmCallContext context,
        string provider,
        LlmRouteAdvisorRecommendation recommendation,
        bool requiresVision,
        LlmRouterOptions? options,
        string advisorName)
    {
        var hasRecommendationGate = recommendation.MinimumCapabilityScore is not null
                                    || recommendation.RequiredTier is not null
                                    || recommendation.RequireMeasuredCapabilityScore
                                    || recommendation.RequireCurrentFunctionEvidence
                                    || recommendation.BlockWeakerPolicyFallback;
        if (RequiresStrictCurrentFunctionEvidence(functionType)
            || HasExplicitModelScoutConstraint(options)
            || options?.RequireCurrentWorkloadSizing == true
            || hasRecommendationGate
            || recommendation.PersistPreference
            || recommendation.AllowSuccessfulBootstrapMaterialization)
        {
            _logger.LogWarning(
                "Rejected degraded hard-safety route {Provider}/{Model} from {Advisor} for {FunctionType}: the call still carries a proof, quality, strict-function, or persistence contract",
                provider,
                recommendation.Model,
                advisorName,
                functionType);
            return null;
        }

        var selection = new LlmModelSelection
        {
            Provider = provider,
            Model = recommendation.Model,
            IsDefault = false,
            Reason = $"Route advisor {advisorName} explicitly selected this transient degraded route after stronger proof was unavailable",
            Context = context,
            AuditCorrelationId = recommendation.AuditCorrelationId,
            SupportsToolUse = recommendation.SupportsToolUse,
            SupportsStructuredOutput = recommendation.SupportsStructuredOutput,
            MaxContextTokens = recommendation.MaxContextTokens,
            CapabilityEvidenceSource = recommendation.CapabilityEvidenceSource,
            MinimumCapabilityScore = null,
            RequireMeasuredCapabilityScore = false,
            RequiredTier = null,
            BlockWeakerPolicyFallback = false,
            RequireCurrentFunctionEvidence = false,
            AllowPreferenceMaterialization = false,
        };
        var requirementIssue = await GetRouteRequirementIssueAsync(
                selection,
                requiresVision,
                options,
                verifyAvailability: true)
            .ConfigureAwait(false);
        if (requirementIssue is not null)
        {
            _logger.LogWarning(
                "Rejected degraded hard-safety route {Provider}/{Model} from {Advisor} for {FunctionType}: {Reason}",
                provider,
                recommendation.Model,
                advisorName,
                functionType,
                requirementIssue);
            return null;
        }

        _logger.LogWarning(
            "Admitted transient degraded hard-safety route {Provider}/{Model} from {Advisor} for {FunctionType} after independent availability, privacy, and request-capability checks",
            provider,
            recommendation.Model,
            advisorName,
            functionType);
        return selection;
    }

    private string? GetAdvisorCapabilityRejection(
        LlmRouteAdvisorRecommendation recommendation,
        string provider,
        string model,
        bool requiresVision,
        LlmRouterOptions? options)
    {
        var providerImpl = _providerRegistry.GetProvider(provider);
        var capability = _capabilitiesService.GetModelCapability(provider, model);

        if ((requiresVision || recommendation.RequiresVision)
            && providerImpl?.SupportsVision(model) != true
            && _capabilitiesService.SupportsVision(provider, model) != true)
        {
            return "vision capability is not proven";
        }

        if (options?.RequiresToolUse == true)
        {
            if (_toolUseRegistry?.Supports(provider) != true)
                return "no native tool-use translator is registered";
            var supportsToolUse = recommendation.SupportsToolUse
                                  ?? capability?.SupportsFunctionCalling;
            if (supportsToolUse != true)
                return "model-level tool-use capability is not proven";
        }

        if (RequiresNativeStructuredOutput(options))
        {
            var supportsStructuredOutput = recommendation.SupportsStructuredOutput
                                           ?? (capability is null
                                               ? null
                                               : capability.SupportsStructuredOutput);
            if (supportsStructuredOutput != true)
                return "structured-output capability is not proven";
        }

        if (options?.MinimumContextTokens is > 0 and var minimumContextTokens)
        {
            var contextSizes = new[] { recommendation.MaxContextTokens, capability?.MaxContextTokens }
                .Where(value => value is > 0)
                .Select(value => value!.Value)
                .ToList();
            if (contextSizes.Count == 0)
                return $"a {minimumContextTokens:N0}-token context window is required but unverified";
            if (contextSizes.Min() < minimumContextTokens)
                return $"verified context window is below the {minimumContextTokens:N0}-token requirement";
        }

        return null;
    }

    /// <summary>
    /// A typed caller may explicitly accept the router's strict parse/repair boundary.
    /// In that case provider-native schema support is not a hard admission prerequisite;
    /// malformed output still fails validation and normal recovery remains in force.
    /// Keep every selection/re-admission path on this single predicate so Scout cannot
    /// recommend a repair-capable route that a later internal filter rejects.
    /// </summary>
    private static bool RequiresNativeStructuredOutput(LlmRouterOptions? options)
        => options?.RequiresStructuredOutput == true
           && options.AllowTextJsonContractRepair != true;

    private async Task<LlmRoutingGovernorDecision> GetRoutingGovernorDecisionAsync(
        LlmRouteAdvisorContext advisorContext,
        CancellationToken cancellationToken)
    {
        if (_routingGovernor is not null)
        {
            return await _routingGovernor
                .GetRecommendationAsync(advisorContext, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_routeAdvisors.Count == 0)
        {
            return LlmRoutingGovernorDecision.None();
        }

        var evaluations = new List<LlmRouteAdvisorEvaluation>(_routeAdvisors.Count);
        var candidates = new List<LlmRoutingGovernorCandidate>();
        foreach (var advisor in _routeAdvisors)
        {
            LlmRouteAdvisorRecommendation? recommendation;
            var advisorName = advisor.GetType().Name;
            try
            {
                recommendation = await LlmRouteAdvisorPolicy.EvaluateAsync(advisor, advisorContext, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LlmRouteSelectionBlockedException)
            {
                // Preserve non-negotiable per-call policy floors when hosts use
                // the router without the shared governor implementation.
                throw;
            }
            catch (Exception ex) when (advisor is ILlmHardSafetyRouteAdvisor hardSafetyAdvisor)
            {
                // Hosts may omit the shared governor, but that must not turn a
                // failed hard-safety dependency into permission to route.
                throw new LlmRouteSelectionBlockedException(
                    $"Hard safety policy {hardSafetyAdvisor.PolicyName} could not validate {advisorContext.FunctionType}; weaker routing was blocked.",
                    ex);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Route advisor {Advisor} failed for {FunctionType}; continuing normal routing",
                    advisorName,
                    advisorContext.FunctionType);

                evaluations.Add(new LlmRouteAdvisorEvaluation
                {
                    AdvisorName = advisorName,
                    Recommended = false,
                    ErrorMessage = ex.Message
                });
                continue;
            }

            if (recommendation is null)
            {
                evaluations.Add(new LlmRouteAdvisorEvaluation
                {
                    AdvisorName = advisorName,
                    Recommended = false
                });
                continue;
            }

            evaluations.Add(new LlmRouteAdvisorEvaluation
            {
                AdvisorName = advisorName,
                Recommended = true,
                Provider = recommendation.Provider,
                Model = recommendation.Model,
                PersistPreference = recommendation.PersistPreference,
                Reason = recommendation.Reason
            });

            var candidate = new LlmRoutingGovernorCandidate
            {
                AdvisorName = advisorName,
                IsHardSafetyPolicy = advisor is ILlmHardSafetyRouteAdvisor,
                Recommendation = recommendation
            };

            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            return LlmRoutingGovernorDecision.None(evaluations);
        }

        return new LlmRoutingGovernorDecision
        {
            Recommendation = candidates[0].Recommendation,
            AdvisorName = candidates[0].AdvisorName,
            Candidates = candidates,
            Evaluations = evaluations
        };
    }

    private async Task<BudgetPressureSnapshot> SafeReadBudgetPressureAsync()
    {
        try
        {
            return await _budgetPressureSource!.GetCurrentAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Budget pressure read failed; treating as unconstrained for this call");
            return BudgetPressureSnapshot.Unconstrained;
        }
    }

    private static int EstimatePromptTokens(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return 0;
        }

        return (int)Math.Ceiling(prompt.Length / 4.0);
    }

    /// <summary>
    /// Scout must rank the workload the caller actually asked for. Most calls
    /// use the conservative 500-token prior, while long-form callers such as
    /// authored Vibe surfaces provide an explicit portable output budget.
    /// </summary>
    internal static int ResolveEstimatedOutputTokens(LlmRouterOptions? options)
    {
        var requested = options?.GenerationParameters?.MaxOutputTokens
            ?? options?.MaxTokens
            ?? 500;
        return Math.Clamp(requested, 1, 1_000_000);
    }

    private async Task PersistAdvisedPreferenceAsync(
        string functionType,
        LlmFunctionPreference? existing,
        string provider,
        string model,
        string? fallbackProvider,
        string? fallbackModel,
        bool requiresVision,
        string updatedBy)
    {
        if (LlmPreferenceAuthors.IsUserPinned(existing?.PinnedByUserAt))
        {
            _logger.LogInformation(
                "Route advisor left {FunctionType} unchanged because it is explicitly pinned by the user",
                functionType);
            return;
        }

        var existingMatches = existing is { IsEnabled: true }
            && string.Equals(existing.FunctionType, functionType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.PreferredProvider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.PreferredModel, model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.FallbackProvider, fallbackProvider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.FallbackModel, fallbackModel, StringComparison.OrdinalIgnoreCase)
            && existing.RequiresVision == requiresVision;

        if (existingMatches)
        {
            return;
        }

        var preference = new LlmFunctionPreference
        {
            Id = existing?.FunctionType.Equals(functionType, StringComparison.OrdinalIgnoreCase) == true
                ? existing.Id
                : 0,
            FunctionType = functionType,
            PreferredProvider = provider,
            PreferredModel = model,
            FallbackProvider = fallbackProvider,
            FallbackModel = fallbackModel,
            IsEnabled = true,
            RequiresVision = requiresVision,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
            CreatedBy = existing?.CreatedBy ?? updatedBy,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = updatedBy,
            KnownGoodProvider = existing?.KnownGoodProvider,
            KnownGoodModel = existing?.KnownGoodModel,
            KnownGoodConfirmedAt = existing?.KnownGoodConfirmedAt
        };

        var saved = await _settingsProvider.SetFunctionPreferenceAsync(
            preference,
            FunctionPreferenceWriteAuthority.RespectUserPin);
        if (saved)
        {
            _knownGoodTracker?.NotifyConfigChanged(functionType, provider, model);
            _logger.LogInformation(
                "Route advisor set {FunctionType}: {Provider}/{Model}",
                functionType,
                provider,
                model);
        }
    }

    /// <summary>
    /// Applies an enabled user preference as an exact LocalOnly constraint. A route that
    /// is remote, unreachable, unsupported, or incompatible with vision may be replaced
    /// only by the user's complete explicit local fallback.
    /// </summary>
    private async Task<LlmModelSelection> PickLocalOnlyUserPreferenceAsync(
        string functionType,
        LlmFunctionPreference preference,
        bool requiresVision)
    {
        var preferredProvider = ResolveRegisteredProviderName(preference.PreferredProvider);
        var effectiveRequiresVision = requiresVision || preference.RequiresVision;
        var primaryIssue = await GetLocalOnlyPinnedRouteIssueAsync(
            preferredProvider,
            preference.PreferredModel,
            effectiveRequiresVision).ConfigureAwait(false);
        var fallbackProvider = string.IsNullOrWhiteSpace(preference.FallbackProvider)
            ? null
            : ResolveRegisteredProviderName(preference.FallbackProvider);

        if (primaryIssue is null)
        {
            return new LlmModelSelection
            {
                Provider = preferredProvider,
                Model = preference.PreferredModel,
                FallbackProvider = fallbackProvider,
                FallbackModel = preference.FallbackModel,
                IsDefault = false,
                IsUserPinned = true,
                Reason = $"LocalOnly user-pinned function preference for {functionType}",
            };
        }

        var hasCompleteFallback = !string.IsNullOrWhiteSpace(fallbackProvider)
                                  && !string.IsNullOrWhiteSpace(preference.FallbackModel);
        if (hasCompleteFallback)
        {
            var fallbackIssue = await GetLocalOnlyPinnedRouteIssueAsync(
                fallbackProvider!,
                preference.FallbackModel!,
                effectiveRequiresVision).ConfigureAwait(false);
            if (fallbackIssue is null)
            {
                _logger.LogWarning(
                    "LocalOnly user pin {Provider}/{Model} cannot be used for {FunctionType} ({Reason}); using explicit local fallback {FallbackProvider}/{FallbackModel}",
                    preferredProvider,
                    preference.PreferredModel,
                    functionType,
                    primaryIssue,
                    fallbackProvider,
                    preference.FallbackModel);
                return new LlmModelSelection
                {
                    Provider = fallbackProvider!,
                    Model = preference.FallbackModel!,
                    IsDefault = false,
                    IsUserPinned = true,
                    Reason = $"LocalOnly explicit user fallback for {functionType}: {primaryIssue}",
                };
            }

            throw new InvalidOperationException(
                $"User-pinned route {preferredProvider}/{preference.PreferredModel} cannot be used for LocalOnly routing on {functionType}: {primaryIssue}. "
                + $"Explicit fallback {fallbackProvider}/{preference.FallbackModel} is also unusable: {fallbackIssue}.");
        }

        throw new InvalidOperationException(
            $"User-pinned route {preferredProvider}/{preference.PreferredModel} cannot be used for LocalOnly routing on {functionType}: {primaryIssue}. "
            + "No explicit compatible local fallback was configured.");
    }

    private async Task<string?> GetLocalOnlyPinnedRouteIssueAsync(
        string providerName,
        string model,
        bool requiresVision)
    {
        var providerSettings = await _settingsProvider.GetProviderSettingsAsync(providerName)
            .ConfigureAwait(false);
        if (!LlmProviderLocalityPolicy.IsTrustedLocalRoute(providerName, model, providerSettings))
            return "LocalOnly privacy forbids the provider";
        if (!await IsProviderAvailableAsync(providerName).ConfigureAwait(false))
            return "the provider is unavailable";

        var provider = _providerRegistry.GetProvider(providerName);
        if (provider?.SupportsModel(model) != true)
            return "the provider does not advertise support for the exact model";
        if (requiresVision && !provider.SupportsVision(model))
            return "vision capability is not proven for the exact model";

        return null;
    }

    private async Task<LlmRoutingGovernorDecision> GetHardSafetyAdvisorDecisionAsync(
        LlmRouteAdvisorContext context,
        CancellationToken cancellationToken)
    {
        var candidates = new List<LlmRoutingGovernorCandidate>();
        foreach (var advisor in _routeAdvisors
                     .OfType<ILlmHardSafetyRouteAdvisor>()
                     .OrderBy(item => item.Priority))
        {
            LlmRouteAdvisorRecommendation? recommendation;
            try
            {
                recommendation = await advisor
                    .GetRecommendationAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LlmRouteSelectionBlockedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LlmRouteSelectionBlockedException(
                    $"Hard safety policy {advisor.PolicyName} could not validate {context.FunctionType}; weaker routing was blocked.",
                    ex);
            }

            if (recommendation is null)
            {
                continue;
            }

            candidates.Add(new LlmRoutingGovernorCandidate
            {
                AdvisorName = advisor.PolicyName,
                PolicyKind = advisor.PolicyKind,
                Priority = advisor.Priority,
                IsHardSafetyPolicy = true,
                Recommendation = recommendation,
            });
        }

        var winner = candidates.FirstOrDefault();
        return winner is null
            ? LlmRoutingGovernorDecision.None()
            : new LlmRoutingGovernorDecision
            {
                AdvisorName = winner.AdvisorName,
                PolicyKind = winner.PolicyKind,
                Priority = winner.Priority,
                Recommendation = winner.Recommendation,
                Candidates = candidates,
            };
    }

    /// <summary>
    /// Find any available local provider + model to serve this request. Used to honour
    /// LocalOnly privacy class when no user pin constrains the route. Returns null if no
    /// local provider is currently configured or the required vision capability can't be met.
    /// </summary>
    private async Task<LlmModelSelection?> TryPickLocalSelectionAsync(string functionType, bool requiresVision)
    {
        var providerSettings = await _settingsProvider.GetAllProviderSettingsAsync();
        foreach (var settings in providerSettings
                     .Where(p => p.IsEnabled && !p.DisabledByHealthCheck && !string.IsNullOrWhiteSpace(p.ProviderName))
                     .Where(LlmProviderLocalityPolicy.IsTrustedLocalProvider)
                     .OrderBy(p => p.Priority))
        {
            var candidateProvider = settings.ProviderName;
            if (!await IsProviderAvailableAsync(candidateProvider)) continue;

            var candidateModel = !string.IsNullOrWhiteSpace(settings.DefaultModel)
                ? settings.DefaultModel!
                : requiresVision
                    ? GetVisionFallbackModel(candidateProvider)
                    : _settingsProvider.GetDefaultModel(candidateProvider);

            if (!LlmProviderLocalityPolicy.IsTrustedLocalRoute(
                    candidateProvider,
                    candidateModel,
                    settings)) continue;
            if (requiresVision && !_capabilitiesService.SupportsVision(candidateProvider, candidateModel)) continue;

            return new LlmModelSelection
            {
                Provider = candidateProvider,
                Model = candidateModel,
                IsDefault = false,
                Reason = $"LocalOnly privacy class for {functionType} -> local provider {candidateProvider}"
            };
        }
        return null;
    }

    /// <summary>
    /// Check if a provider is available (has API key or is a local provider)
    /// </summary>
    private async Task<bool> IsProviderAvailableAsync(
        string providerName,
        CancellationToken cancellationToken = default,
        bool waitForDeferredCapacity = false,
        bool bypassCooldownGate = false,
        bool honorPinWithoutDiscovery = false,
        bool honorConfiguredRouteWithoutDiscovery = false)
    {
        // Cooldown gate first — a provider that hit its rate limit ten
        // minutes ago is "available" by any other measure but every call
        // to it for the next hour would just round-trip another 429. Skip
        // it so the fallback chain picks something we can actually use.
        // Exact user/profile pins bypass this gate: cooldowns protect
        // automatic rotation, they do not override an explicit instruction.
        if (!bypassCooldownGate
            && _cooldownTracker is not null
            && !_cooldownTracker.IsAvailable(providerName))
        {
            return false;
        }

        var providerSettings = await _settingsProvider.GetProviderSettingsAsync(providerName);

        // DisabledByHealthCheck is checked independently of IsEnabled: the two
        // flags are written together by the health validator, but a row copied
        // or synced from another profile can carry IsEnabled=1 alongside a
        // health pause. A deliberate user re-enable clears the flag
        // (SaveProviderAsync normalizes it), so this never traps one.
        if (providerSettings is not null
            && (!providerSettings.IsEnabled || providerSettings.DisabledByHealthCheck))
        {
            return false;
        }

        // Only a policy-validated local provider bypasses API-key checks. A
        // mutable IsLocalProvider bit alone is not a privacy/security boundary.
        if (LlmProviderLocalityPolicy.IsTrustedLocalProvider(providerName, providerSettings))
        {
            if (honorConfiguredRouteWithoutDiscovery
                && _providerRegistry.GetProvider(providerName) is not null)
            {
                // FunctionPreferences is the durable last-known-good route cache. Do not
                // turn an ordinary cold-start request into a foreground runtime/model-list
                // probe; the provider call and background maintenance are the live checks.
                return true;
            }

            if (await HasAvailableProviderModelsAsync(
                providerName,
                cancellationToken,
                waitForDeferredCapacity))
            {
                return true;
            }

            // A local runtime can be healthy while model discovery exceeds the
            // router's deliberately short foreground budget. This is common when
            // Ollama is refreshing /api/show capability metadata for several large
            // installed models. An explicit user/profile pin is sovereign: once the
            // provider is registered, enabled, and trusted-local, invoke the exact
            // model and let the provider return the authoritative model-not-found or
            // runtime error. Do not turn a slow discovery probe into an immediate
            // "Exact User route ... unavailable" refusal before inference.
            if (honorPinWithoutDiscovery
                && _providerRegistry.GetProvider(providerName) is not null)
            {
                _logger.LogWarning(
                    "Model discovery advertised no models in time for pinned local provider {Provider}; honoring the exact pin anyway",
                    providerName);
                return true;
            }

            return false;
        }

        // RemotePeer provider forwards to a paired machine's advertised models
        // over the Swarm transport. It is available only after at least one
        // paired machine has announced something it can serve.
        if (providerName.Equals("RemotePeer", StringComparison.OrdinalIgnoreCase))
        {
            if (honorConfiguredRouteWithoutDiscovery
                && _providerRegistry.GetProvider(providerName) is not null)
            {
                return true;
            }

            return await HasAvailableProviderModelsAsync(
                providerName,
                cancellationToken,
                waitForDeferredCapacity);
        }

        // Credential-free providers (approved compute nodes, an operator's model server) have no key to find.
        if (_providerRegistry.GetProvider(providerName) is ICredentialFreeLlmProvider)
        {
            if (honorConfiguredRouteWithoutDiscovery || honorPinWithoutDiscovery) return true;
            return await HasAvailableProviderModelsAsync(providerName, cancellationToken, waitForDeferredCapacity);
        }

        // Subscription CLI providers do not use Talavine-held API keys.
        // Availability is whether the logged-in local CLI can be launched. A
        // persisted automatic FunctionPreference is not proof of a current
        // subscription session: users sign out, OAuth sessions expire, and CLI
        // binaries move between app launches. Unlike API providers, therefore,
        // subscription routes must not use the cold-start configured-route
        // shortcut. The provider's bounded auth/model probe is the cheap live
        // admission check; without it a stale cached route can fail thousands of
        // times while a healthy subscription CLI is sitting beside it.
        if (SwarmCore.LlmRouting.Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(providerName))
        {
            if (await HasAvailableProviderModelsAsync(
                    providerName,
                    cancellationToken,
                    waitForDeferredCapacity))
            {
                return true;
            }

            // Discovery for a subscription CLI means launching the CLI and
            // reading its model list — a slow or hiccuping launch produced an
            // instant "Exact User route … is unavailable" three seconds into a
            // turn. An explicit pin is an instruction, not a suggestion: if the
            // provider is registered, enabled, and claims to support the model,
            // try it. A genuinely bad route then fails at the call with the
            // provider's own message instead of a routing refusal.
            if (honorPinWithoutDiscovery)
            {
                _logger.LogWarning(
                    "Model discovery advertised no models for pinned subscription CLI provider {Provider}; honoring the pin anyway",
                    providerName);
                return true;
            }

            return false;
        }

        // Check if API key is configured
        return await _apiKeyProvider.HasApiKeyAsync(providerName);
    }

    private async Task<string> GetDefaultAvailableProviderAsync()
    {
        var configuredDefault = _settingsProvider.GetDefaultProvider();
        if (!string.IsNullOrWhiteSpace(configuredDefault)
            && await IsProviderAvailableAsync(configuredDefault))
        {
            return configuredDefault;
        }

        var providers = await _settingsProvider.GetAllProviderSettingsAsync() ?? [];
        foreach (var provider in providers
                     .Where(p => p.IsEnabled
                                 && !p.DisabledByHealthCheck
                                 && !string.IsNullOrWhiteSpace(p.ProviderName)
                                 && !p.ProviderName.Equals(configuredDefault, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Priority))
        {
            var providerName = ResolveRegisteredProviderName(provider.ProviderName);
            if (await IsProviderAvailableAsync(providerName))
            {
                return providerName;
            }
        }

        foreach (var candidate in new[] { "OpenAI", "Claude", "Gemini", "RemotePeer", "Ollama" })
        {
            if (await IsProviderAvailableAsync(candidate))
            {
                return candidate;
            }
        }

        var fallbackDefault = string.IsNullOrWhiteSpace(configuredDefault) ? "Ollama" : configuredDefault;
        // Typed as temporarily unavailable, not as a plain invalid operation:
        // the storm supervisor repairs the local runtime autonomously, so
        // background callers should defer with their escalating route backoff
        // (and chat turns wait briefly with backoff) instead of treating a cold
        // profile as a permanent, unclassified failure that each caller logs
        // at its own cadence.
        throw new LlmRouteTemporarilyUnavailableException(
            $"No enabled LLM provider is available. Last configured default was {fallbackDefault}. "
            + "Set up a provider in LLM Setup, or wait for the local runtime repair to finish.");
    }

    private async Task<bool> HasAvailableProviderModelsAsync(
        string providerName,
        CancellationToken cancellationToken = default,
        bool waitForDeferredCapacity = false)
    {
        var provider = _providerRegistry.GetProvider(providerName);
        if (provider is null)
        {
            return false;
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var configurationIdentity = await GetProviderDiscoveryConfigurationIdentityAsync(providerName)
                .ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                var remaining = ProviderDiscoveryTimeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                var catalog = await _providerDiscovery.GetModelCatalogAsync(
                        provider,
                        remaining,
                        cancellationToken,
                        highPriority: true,
                        configurationIdentity: configurationIdentity)
                    .ConfigureAwait(false);
                if (catalog.Completed)
                {
                    return catalog.Models.Count > 0;
                }

                if (!waitForDeferredCapacity || !catalog.Deferred)
                {
                    return false;
                }

                remaining = ProviderDiscoveryTimeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(75)
                            ? remaining
                            : TimeSpan.FromMilliseconds(75),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Provider {Provider} did not report available models", providerName);
            return false;
        }
    }

    private async Task<string> GetProviderDiscoveryConfigurationIdentityAsync(string providerName)
    {
        var settings = await _settingsProvider.GetProviderSettingsAsync(providerName)
            .ConfigureAwait(false)
            ?? new LlmProviderSettings { ProviderName = providerName };
        return Services.LlmProviderDiscoveryCoordinator.CreateConfigurationIdentity(settings);
    }

    // User-facing provider names (UI, settings DB) don't always match the canonical
    // names used by ILlmProvider.ProviderName in the registry. The UI saves the
    // company name ("Anthropic", "Google") while providers register under product
    // names ("Claude", "Gemini"). This map lets both forms resolve to the same
    // registered provider. Keys are normalized (lowercased, letters/digits only).
    private static readonly Dictionary<string, string[]> ProviderNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new[] { "anthropic" },
        ["anthropic"] = new[] { "claude" },
        ["gemini"] = new[] { "google" },
        ["google"] = new[] { "gemini" },
        ["grok"] = new[] { "xai" },
        ["xai"] = new[] { "grok" },
        ["lmstudio"] = new[] { "lmstudio" },
        ["lm"] = new[] { "lmstudio" },
        ["claudecode"] = new[] { "claudecode", "claudecodesubscription" },
        ["claudecodesubscription"] = new[] { "claudecode" },
        ["codexcli"] = new[] { "codexcli", "codexsubscription" },
        ["codexsubscription"] = new[] { "codexcli" },
        ["geminicli"] = new[] { "geminicli", "geminisubscription" },
        ["geminisubscription"] = new[] { "geminicli" },
    };

    private string ResolveRegisteredProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return providerName;
        }

        if (_providerRegistry.HasProvider(providerName))
        {
            return providerName;
        }

        var registered = _providerRegistry.GetAllProviders()
            .Select(p => p.ProviderName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (registered.Count == 0)
        {
            return providerName;
        }

        var normalizedIncoming = NormalizeProviderName(providerName);
        var exactNormalized = registered.FirstOrDefault(r =>
            string.Equals(NormalizeProviderName(r), normalizedIncoming, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactNormalized))
        {
            return exactNormalized;
        }

        if (ProviderNameAliases.TryGetValue(normalizedIncoming, out var aliases))
        {
            var aliasMatch = registered.FirstOrDefault(r =>
                aliases.Contains(NormalizeProviderName(r), StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(aliasMatch))
            {
                return aliasMatch;
            }
        }

        return providerName;
    }

    private static string NormalizeProviderName(string providerName)
    {
        var chars = providerName.Trim().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    /// <summary>
    /// Returns true if the provider is a local provider (like Ollama) where all functions
    /// should use the same model to avoid model load/unload thrashing.
    /// Genuinely async: these run on selection/fallback hot paths that are
    /// invoked from UI SynchronizationContexts (MAUI/Blazor hybrid), where a
    /// blocking GetAwaiter().GetResult() on the settings provider deadlocks.
    /// </summary>
    private async Task<bool> IsLocalProviderAsync(string providerName)
    {
        var settings = await _settingsProvider.GetProviderSettingsAsync(providerName).ConfigureAwait(false);
        return LlmProviderLocalityPolicy.IsTrustedLocalProvider(providerName, settings);
    }

    private async Task<bool> IsLocalRouteAsync(string providerName, string? model)
    {
        var settings = await _settingsProvider.GetProviderSettingsAsync(providerName).ConfigureAwait(false);
        return LlmProviderLocalityPolicy.IsTrustedLocalRoute(providerName, model, settings);
    }

    private async Task<bool> IsMeteredApiProviderAsync(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return false;
        if (providerName.Equals("RemotePeer", StringComparison.OrdinalIgnoreCase)) return false;
        if (SwarmCore.LlmRouting.Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(providerName)) return false;
        return !await IsLocalProviderAsync(providerName);
    }

    private async Task<bool> ShouldSuppressConfiguredFallbackAsync(LlmModelSelection selectedModel, string? fallbackProvider)
    {
        // A manual pin may include an explicit fallback. That is user intent, not
        // an autonomous cost escalation, so preserve it (LocalOnly is enforced
        // independently before execution).
        if (selectedModel.IsUserPinned)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(fallbackProvider) || !await IsMeteredApiProviderAsync(fallbackProvider))
        {
            return false;
        }

        return SwarmCore.LlmRouting.Configuration.SubscriptionCliProviders.IsSubscriptionCliProvider(selectedModel.Provider)
               || await IsLocalProviderAsync(selectedModel.Provider);
    }

    private async Task<bool> ShouldUseConfiguredFallbackModelAsync(LlmModelSelection selectedModel, string? fallbackProvider)
    {
        return !string.IsNullOrWhiteSpace(fallbackProvider)
            && !string.IsNullOrWhiteSpace(selectedModel.FallbackProvider)
            && !string.IsNullOrWhiteSpace(selectedModel.FallbackModel)
            && !await ShouldSuppressConfiguredFallbackAsync(selectedModel, selectedModel.FallbackProvider)
            && string.Equals(
                ResolveRegisteredProviderName(selectedModel.FallbackProvider!),
                fallbackProvider,
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LlmModelSelection> BuildFallbackFailureSelectionAsync(LlmModelSelection selectedModel, bool requiresVision)
    {
        var configuredFallbackProvider = string.IsNullOrWhiteSpace(selectedModel.FallbackProvider)
            ? null
            : ResolveRegisteredProviderName(selectedModel.FallbackProvider!);

        var fallbackProvider = configuredFallbackProvider is not null
            && !await ShouldSuppressConfiguredFallbackAsync(selectedModel, configuredFallbackProvider)
                ? configuredFallbackProvider
                : await GetFallbackProviderAsync(selectedModel.Provider);

        var fallbackModel = await ShouldUseConfiguredFallbackModelAsync(selectedModel, fallbackProvider)
            ? selectedModel.FallbackModel!
            : requiresVision
                ? GetVisionFallbackModel(fallbackProvider ?? "unknown")
                : await GetFallbackModelAsync(fallbackProvider);

        return new LlmModelSelection
        {
            Provider = fallbackProvider ?? "unknown",
            Model = fallbackModel ?? "unknown",
            IsDefault = false,
            Context = selectedModel.Context
        };
    }

    private async Task<string?> GetVisionCapableModelAsync(string provider)
    {
        var visionModels = _capabilitiesService.GetVisionCapableModels(provider).ToList();
        if (visionModels.Count == 0) return null;

        // For local providers (Ollama), the hardcoded family list may contain
        // families the user hasn't pulled. Cross-reference with the provider's
        // actual installed models so we return something that will load.
        if (await IsLocalProviderAsync(provider))
        {
            var registered = _providerRegistry.GetProvider(provider);
            if (registered is not null)
            {
                try
                {
                    var configurationIdentity = await GetProviderDiscoveryConfigurationIdentityAsync(provider)
                        .ConfigureAwait(false);
                    var installed = await _providerDiscovery.GetModelCatalogAsync(
                            registered,
                            ProviderDiscoveryTimeout,
                            highPriority: true,
                            configurationIdentity: configurationIdentity)
                        .ConfigureAwait(false);
                    if (!installed.Completed)
                        return null;

                    foreach (var installedModel in installed.Models)
                    {
                        if (registered.SupportsVision(installedModel)) return installedModel;
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        return visionModels.FirstOrDefault();
    }

    private async Task<string> SelectModelByComplexityAsync(string provider, decimal complexity, bool requiresVision)
    {
        if (provider.Equals("RemotePeer", StringComparison.OrdinalIgnoreCase))
        {
            var remoteModel = await GetRemotePeerDefaultModelAsync();
            if (!string.IsNullOrWhiteSpace(remoteModel))
                return remoteModel;
        }

        // Always prefer the user's configured default model for this provider.
        // Complexity-based tiering was removed because it overrides user intent.
        // If the user wants a more capable model for high-complexity tasks, they
        // configure that via function preferences (the primary selection path).
        var configuredDefault = _settingsProvider.GetDefaultModel(provider);
        if (!string.IsNullOrEmpty(configuredDefault)
            && !configuredDefault.Equals("unknown-model", StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureInstalledLocalModelAsync(provider, configuredDefault).ConfigureAwait(false);
        }

        // Safety net: the ship-with default per provider, sourced from
        // ModelDefaults so this branch never drifts out of sync with the
        // provider-class fallbacks. This branch is only hit the very first
        // time before the user has configured anything.
        var shipDefault = SwarmCore.Abstractions.LlmRouting.ModelDefaults.ForProvider(provider)
                          ?? configuredDefault
                          ?? "unknown-model";
        return await EnsureInstalledLocalModelAsync(provider, shipDefault).ConfigureAwait(false);
    }

    /// <summary>
    /// A default for a trusted local runtime is only a suggestion until the
    /// runtime confirms the artifact exists. The ship-with default (for example
    /// Ollama's <c>qwen2.5:7b</c>) and a stale persisted default are both just
    /// names; routing to a name the runtime does not have produces a 404 on
    /// every request. When the runtime's catalog is known and the name is not
    /// in it, use an installed chat-capable model for this request instead and
    /// say so once, loudly enough to fix the configuration.
    /// </summary>
    private async Task<string> EnsureInstalledLocalModelAsync(string providerName, string model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || model.Equals("unknown-model", StringComparison.OrdinalIgnoreCase))
        {
            return model;
        }

        try
        {
            var settings = await _settingsProvider.GetProviderSettingsAsync(providerName).ConfigureAwait(false);
            if (!LlmProviderLocalityPolicy.IsTrustedLocalProvider(providerName, settings))
            {
                return model;
            }

            var provider = _providerRegistry.GetProvider(providerName);
            if (provider is null)
            {
                return model;
            }

            var configurationIdentity = await GetProviderDiscoveryConfigurationIdentityAsync(providerName)
                .ConfigureAwait(false);
            var catalog = await _providerDiscovery.GetModelCatalogAsync(
                    provider,
                    ProviderDiscoveryTimeout,
                    CancellationToken.None,
                    highPriority: true,
                    configurationIdentity: configurationIdentity)
                .ConfigureAwait(false);
            if (!catalog.Completed || catalog.Models.Count == 0)
            {
                // Unknown inventory is not evidence of absence.
                return model;
            }

            if (catalog.Models.Any(installed => IsExactRouteIdentity(providerName, model, providerName, installed)))
            {
                return model;
            }

            var replacement = catalog.Models.FirstOrDefault(installed =>
                !string.IsNullOrWhiteSpace(installed)
                && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(installed)
                && !ModelWorkloadFamilies.IsEmbeddingOnly(installed)
                && !ModelWorkloadFamilies.IsVisionOnly(providerName, installed));
            if (replacement is null)
            {
                _logger.LogWarning(
                    "Default model {Model} is not installed on local provider {Provider} and no installed chat model can stand in; pull it or change the provider's default model",
                    model,
                    providerName);
                return model;
            }

            _logger.LogWarning(
                "Default model {Model} is not installed on local provider {Provider}; using installed {Replacement} for this request. Update the provider's default model or pull {Model}",
                model,
                providerName,
                replacement,
                model);
            return replacement;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not verify that {Model} is installed on {Provider}", model, providerName);
            return model;
        }
    }

    private async Task<string?> GetRemotePeerDefaultModelAsync()
    {
        try
        {
            var provider = _providerRegistry.GetProvider("RemotePeer");
            if (provider is null) return null;
            var configurationIdentity = await GetProviderDiscoveryConfigurationIdentityAsync("RemotePeer")
                .ConfigureAwait(false);
            var models = await _providerDiscovery.GetAvailableModelsAsync(
                    provider,
                    ProviderDiscoveryTimeout,
                    highPriority: true,
                    configurationIdentity: configurationIdentity)
                .ConfigureAwait(false);
            return models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        }
        catch
        {
            return null;
        }
    }

    // NOTE: this and the helpers below stay genuinely async the whole way down.
    // They used to block with GetAwaiter().GetResult(), which deadlocks when the
    // router is called on a UI SynchronizationContext (MAUI/Blazor hybrid apps).
    private async Task<string?> GetFallbackProviderAsync(string primaryProvider)
    {
        // Determine the preferred fallback order for each provider.
        // Prefer local providers first (Ollama on this machine, then a paired
        // peer's Ollama/LM Studio via RemotePeer), then cloud providers. The
        // RemotePeer fallback is what lets a PC without its own Ollama still
        // run local inference against the paired Mac's Ollama.
        var primaryIsLanNode = OllamaProviderFamily.IsLanNode(primaryProvider);
        string[] staticCandidates = (primaryIsLanNode ? "ollama@node" : primaryProvider.ToLowerInvariant()) switch
        {
            "openai" => ["Ollama", "RemotePeer", "CodexCli", "ClaudeCode", "Claude", "Gemini"],
            "claude" => ["Ollama", "RemotePeer", "CodexCli", "OpenAI", "Gemini"],
            "gemini" => ["Ollama", "RemotePeer", "CodexCli", "OpenAI", "Claude"],
            "ollama" => ["RemotePeer", "CodexCli", "OpenAI", "Claude", "Gemini"],
            // A LAN node falls back to this machine's daemon first, then the other
            // nodes (spliced in below), then the same chain the local daemon uses.
            "ollama@node" => ["Ollama", "RemotePeer", "CodexCli", "OpenAI", "Claude", "Gemini"],
            "remotepeer" => ["Ollama", "CodexCli", "OpenAI", "Claude", "Gemini"],
            "codexcli" => ["Ollama", "RemotePeer", "ClaudeCode"],
            "claudecode" => ["Ollama", "RemotePeer", "CodexCli"],
            _ => ["Ollama", "RemotePeer", "CodexCli", "ClaudeCode", "OpenAI", "Claude", "Gemini"]
        };

        // A LAN node's persisted priority is its user-owned routing mode:
        // Prefer = 1, Auto = 10, Avoid = 900. The node service projects that
        // mode into LlmProviderSettings so the router does not need a dependency
        // on the host application's LAN-node model. Keep this classification here
        // rather than alphabetically splicing every node after Ollama: doing that
        // would silently ignore a user's Avoid choice and could put it ahead of
        // normal fallback routes.
        const int preferLanNodePriority = 1;
        const int avoidLanNodePriority = 900;

        var lanNodes = _providerRegistry.GetAllProviders()
            .Select(p => p.ProviderName)
            .Where(OllamaProviderFamily.IsLanNode)
            .Where(name => !name.Equals(primaryProvider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lanNodePriorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (lanNodes.Count > 0)
        {
            try
            {
                var settings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
                foreach (var setting in settings)
                {
                    if (OllamaProviderFamily.IsLanNode(setting.ProviderName))
                    {
                        lanNodePriorities[setting.ProviderName] = setting.Priority;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // If the settings snapshot cannot be read, defer any unknown
                // node to the Avoid tail. That preserves the user's no-preference
                // fallback chain instead of treating an unclassified node as Auto.
                _logger.LogDebug(ex, "Could not read LAN Ollama node priorities for fallback routing");
            }

            // A settings store may filter disabled rows from GetAllProviderSettingsAsync.
            // Read any omitted registered node individually so a persisted mode is
            // still honored. A missing/unreadable row remains at Int32.MaxValue and
            // is only considered after every normal fallback; IsProviderAvailableAsync
            // will then reject it unless it is actually configured and enabled.
            foreach (var lanNode in lanNodes.Where(node => !lanNodePriorities.ContainsKey(node)))
            {
                try
                {
                    var settings = await _settingsProvider.GetProviderSettingsAsync(lanNode).ConfigureAwait(false);
                    if (settings is not null)
                    {
                        lanNodePriorities[lanNode] = settings.Priority;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not read fallback priority for LAN Ollama node {Provider}", lanNode);
                }
            }
        }

        var prioritizedLanNodes = lanNodes
            .Select(name => new
            {
                Provider = name,
                Priority = lanNodePriorities.TryGetValue(name, out var priority)
                    ? priority
                    : int.MaxValue
            })
            .OrderBy(node => node.Priority)
            .ThenBy(node => node.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preferredLanNodes = prioritizedLanNodes
            .Where(node => node.Priority <= preferLanNodePriority)
            .Select(node => node.Provider)
            .ToList();
        var automaticLanNodes = prioritizedLanNodes
            .Where(node => node.Priority > preferLanNodePriority && node.Priority < avoidLanNodePriority)
            .Select(node => node.Provider)
            .ToList();
        var avoidedLanNodes = prioritizedLanNodes
            .Where(node => node.Priority >= avoidLanNodePriority)
            .Select(node => node.Provider)
            .ToList();

        // Prefer nodes outrank this machine's daemon. Auto nodes run after the
        // daemon but before the ordinary RemotePeer/subscription/cloud chain. An
        // Avoid node is intentionally appended only after all normal fallbacks.
        // Exact user/profile pins bypass this automatic selection path entirely.
        var candidates = new List<string>(staticCandidates.Length + lanNodes.Count);
        candidates.AddRange(preferredLanNodes);
        var insertedAutomaticLanNodes = false;
        foreach (var candidate in staticCandidates)
        {
            candidates.Add(candidate);
            if (!insertedAutomaticLanNodes
                && candidate.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                candidates.AddRange(automaticLanNodes);
                insertedAutomaticLanNodes = true;
            }
        }

        // The local daemon itself is absent from its own fallback chain. In that
        // case, both Prefer and Auto LAN nodes belong before the first ordinary
        // fallback because the local daemon has already failed.
        if (!insertedAutomaticLanNodes)
        {
            candidates.InsertRange(preferredLanNodes.Count, automaticLanNodes);
        }

        candidates.AddRange(avoidedLanNodes);

        // Only return a fallback that is actually configured and available.
        foreach (var candidate in candidates)
        {
            if (await IsProviderAvailableAsync(candidate))
                return candidate;
        }

        // No configured fallback — don't invent one.
        return null;
    }

    private async Task<string?> GetFallbackModelAsync(string? fallbackProvider)
    {
        if (string.IsNullOrEmpty(fallbackProvider)) return null;
        if (fallbackProvider.Equals("RemotePeer", StringComparison.OrdinalIgnoreCase))
        {
            var advertised = await GetRemotePeerDefaultModelAsync();
            if (!string.IsNullOrWhiteSpace(advertised)) return advertised;
        }

        var configured = _settingsProvider.GetDefaultModel(fallbackProvider);
        return string.IsNullOrWhiteSpace(configured)
            ? configured
            : await EnsureInstalledLocalModelAsync(fallbackProvider, configured).ConfigureAwait(false);
    }

    private decimal CalculatePromptComplexity(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
            return 0m;

        var complexity = 0m;
        var length = prompt.Length;

        // Length-based complexity
        if (length > 4000) complexity += 0.3m;
        else if (length > 1000) complexity += 0.2m;
        else if (length > 500) complexity += 0.1m;

        // Technical content indicators
        if (prompt.Contains("```") || prompt.Contains("code", StringComparison.OrdinalIgnoreCase))
            complexity += 0.2m;
        if (prompt.Contains("analyze", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("compare", StringComparison.OrdinalIgnoreCase))
            complexity += 0.15m;
        if (prompt.Contains("JSON", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("structured", StringComparison.OrdinalIgnoreCase))
            complexity += 0.1m;

        return Math.Min(complexity, 1m);
    }
}
