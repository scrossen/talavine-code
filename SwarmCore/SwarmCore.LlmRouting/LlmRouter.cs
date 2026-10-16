using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Services;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Database-agnostic LLM router implementation.
/// Routes requests to optimal models based on function type and preferences.
///
/// ARCHITECTURE:
/// - Partial class split across multiple files (ModelSelection, Streaming, Utilities, Helpers, Embeddings, Preferences)
/// - Supports multi-provider routing (OpenAI, Claude, Gemini, Ollama) with automatic fallback
/// - Integrates with SwarmCore.LlmRouting for cost optimization, usage tracking, and model benchmarking
/// - Provides both text and vision capabilities with provider-specific fallback chains
/// - Includes comprehensive error handling for rate limits, timeouts, and model failures
/// - Uses debug logging for request/response tracing and performance monitoring
///
/// KEY FEATURES:
/// 1. Intelligent Model Selection: Chooses optimal model based on function type, cost, and performance
/// 2. Automatic Fallback: Gracefully degrades to fallback providers on errors (rate limits, timeouts, model not found)
/// 3. Usage Tracking: Fire-and-forget tracking of token usage and costs for analytics
/// 4. Vision Support: Dedicated vision response generation with vision-capable model selection
/// 5. JSON Deserialization: Resilient JSON parsing with LLM-specific coercion (handles malformed JSON)
/// 6. Debug Logging: Comprehensive request/response/error logging for troubleshooting
/// 7. Model Validation Cache: Caches model availability checks to reduce API calls
///
/// DEPENDENCIES:
/// - ILlmSettingsProvider: Retrieves user preferences and function-type routing rules
/// - ILlmUsageTracker: Tracks token usage and costs for billing/analytics
/// - ILlmProviderRegistry: Registry of available LLM providers (OpenAI, Claude, etc.)
/// - ILlmCapabilitiesService: Queries model capabilities (vision, function calling, etc.)
/// - ILlmCostService: Provides pricing information for cost-aware routing
/// - ILlmApiKeyProvider: Manages API keys for all providers (Singleton)
/// - ILlmModelSelfHealer: Optional service for automatic model downloading (Ollama)
/// - ILlmDebugLogger: Optional debug logging for request/response tracing
/// </summary>
public partial class LlmRouter : ILlmRouter
{
    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILlmProviderRegistry _providerRegistry;
    private readonly ILlmCapabilitiesService _capabilitiesService;
    private readonly ILlmCostService _costService;
    private readonly ILlmApiKeyProvider _apiKeyProvider;
    private readonly ILlmModelSelfHealer? _selfHealer;
    private readonly IKnownGoodConfigTracker? _knownGoodTracker;
    private readonly ILlmOperationalAlertSink? _operationalAlerts;
    private readonly IReadOnlyList<ILlmRouteAdvisor> _routeAdvisors;
    private readonly ILlmRoutingGovernor? _routingGovernor;
    // Persistent rate-limit / usage-cap cooldown tracker. Optional — the
    // router still works without it (rate-limit errors just fall back per-call
    // instead of cooling the provider out of rotation), but with it wired up
    // a hit on Claude Pro's 5-hour window instantly rotates to Codex/Gemini
    // for every subsequent call until the cooldown clears.
    private readonly ILlmProviderCooldownTracker? _cooldownTracker;
    // ILlmAbTestJudge is invoked by AbTestReplayService, not by the router —
    // live routing never consults A/B tests. Field intentionally removed; see
    // AbTestReplayService for the offline replay path.
    private readonly ILocalLlmRuntimeManager? _localRuntimeManager;
    private readonly ILlmRouteLoadTracker? _routeLoadTracker;
    private readonly ILlmRouteContextWindowObservations? _contextWindows;
    private readonly ILlmDebugLogger _debugLogger;
    // Dynamic-routing inputs. All optional so existing tests with hand-built routers
    // continue to compile; the production DI registration wires concrete defaults.
    private readonly ISystemActivitySignal? _systemActivity;
    private readonly IRoutingPolicyStore? _routingPolicyStore;
    private readonly IBudgetPressureSource? _budgetPressureSource;
    private readonly ISubscriptionQuotaTracker? _quotaTracker;
    private readonly IEmbeddingModelPin? _embeddingPin;
    private readonly IEmbeddingRebuildOrchestrator? _embeddingRebuild;
    private readonly ILlmRoutingAuditStore _routingAuditStore;
    private readonly IModelScout? _modelScout;
    private readonly IModelScoutRoutingGate? _modelScoutRoutingGate;
    private readonly ILlmRouteAdmissionValidator? _routeAdmissionValidator;
    private readonly LlmProviderDiscoveryCoordinator _providerDiscovery;
    private readonly LlmModelValidationCoordinator _modelValidationCoordinator;
    private readonly ILogger<LlmRouter> _logger;

    private static readonly TimeSpan ProviderDiscoveryTimeout = TimeSpan.FromSeconds(2);

    private static string CanonicalizeRoutingFunctionType(string? functionType)
        => ModelScoutFunctionEvidence.NormalizeFunctionType(functionType);

    /// <summary>
    /// Per-router/profile dedup set for lazy function-preference materialization. It must not
    /// be process-static: the desktop host can switch profile data roots without restarting,
    /// and one profile's materialized function must not suppress another profile's row.
    /// The persistence boundary still serializes concurrent first writes.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _materializedFunctionTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ensure a <c>FunctionPreferences</c> row exists for
    /// <paramref name="functionType"/> using a route that just completed successfully. First
    /// success for a given function type writes the row; subsequent calls are no-ops (both via
    /// the in-memory set and via the provider's existing-row check). The first write is awaited so
    /// request-scoped hosts cannot dispose the settings provider before its atomic row +
    /// receipt commit. Failures remain non-fatal and are retried on the next call.
    /// </summary>
    private async Task MaterializeFunctionPreferenceIfMissingAsync(
        string functionType,
        LlmModelSelection selection,
        LlmRouterOptions? options,
        bool requiresVision = false)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        if (string.IsNullOrWhiteSpace(functionType)) return;
        if (selection is null || string.IsNullOrWhiteSpace(selection.Provider) || string.IsNullOrWhiteSpace(selection.Model)) return;
        if (!selection.AllowPreferenceMaterialization) return;
        // Explicit overrides are request/profile/provider constraints, not global
        // function policy. Persisting one here would silently broaden its scope.
        if (!string.IsNullOrWhiteSpace(options?.OverrideProvider)
            || !string.IsNullOrWhiteSpace(options?.OverrideModel))
        {
            return;
        }
        var intrinsicallyVision = ModelScoutFunctionRequirements.RequiresVision(functionType);
        var intrinsicallyToolUse = ModelScoutFunctionRequirements.RequiresToolUse(functionType);
        var stableStructuredOutput = ModelScoutFunctionRequirements.UsesStableStructuredOutput(functionType);
        var validatedStableStructuredOutput = stableStructuredOutput
                                              && options?.RequiresStructuredOutput == true
                                              && options.ForceJson;
        var profile = FunctionTypeProfiles.Resolve(functionType);
        var hasNonDefaultInteractivity = options is not null
                                         && options.Interactivity != LlmInteractivity.Unspecified
                                         && options.Interactivity != profile.Interactivity;
        var hasNonDefaultPrivacy = options is not null
                                   && options.PrivacyClass != LlmPrivacyClass.Unspecified
                                   && options.PrivacyClass != profile.PrivacyClass;
        var hasNonDefaultUrgency = options is not null
                                   && options.Urgency != LlmUrgency.Unspecified
                                   && options.Urgency != profile.DefaultUrgency;
        if ((requiresVision && !intrinsicallyVision)
            || (options?.RequiresVision == true && !intrinsicallyVision)
            || (options?.RequiresToolUse == true && !intrinsicallyToolUse)
            // Typed JSON is the normal function-level contract for a small set of
            // repeatable workloads (notably spam detection). The raw execution path
            // reaches here only after ForceJson passed the JSON quality gate, so that
            // successful route is safe to cache. Keep rejecting structured output on
            // mixed-purpose functions, where it is merely a one-call constraint.
            || (options?.RequiresStructuredOutput == true && !stableStructuredOutput)
            // The durable author marker becomes structured-capability evidence on
            // restart. Stable structured workloads may therefore mint it only when
            // this exact call passed the router-owned ForceJson quality boundary.
            || (stableStructuredOutput && !validatedStableStructuredOutput)
            || options?.MinimumContextTokens is not null
            || hasNonDefaultInteractivity
            || hasNonDefaultUrgency
            || options?.Workload is not (null or LlmWorkloadKind.Unspecified)
            // Declared complex/high-stakes work or an evidence workspace chose
            // this route for one kind of request, not for the whole function.
            || options?.TaskDemand is { Complexity: > LlmTaskComplexity.Routine }
                or { Surface: LlmWorkSurface.EvidenceWorkspace }
            || HasExplicitModelScoutConstraint(options))
        {
            return;
        }

        if (hasNonDefaultPrivacy)
        {
            // Local-first classification deliberately narrows CloudOk to LocalOnly.
            // Persisting the route cannot weaken privacy, but only when the normal
            // workload is stable structured output and the settings-backed security
            // boundary independently proves that the selected provider is local.
            if (!stableStructuredOutput || options?.PrivacyClass != LlmPrivacyClass.LocalOnly)
            {
                return;
            }

            try
            {
                var providerSettings = await _settingsProvider
                    .GetProviderSettingsAsync(selection.Provider)
                    .ConfigureAwait(false);
                if (!LlmProviderLocalityPolicy.IsTrustedLocalRoute(
                        selection.Provider,
                        selection.Model,
                        providerSettings))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                _logger.LogDebug(
                    ex,
                    "Could not verify trusted-local route {Provider}/{Model} before materializing {FunctionType}",
                    selection.Provider,
                    selection.Model,
                    functionType);
                return;
            }
        }

        // A local route that served a waiting assistant turn while a subscription
        // route is healthy is a transient fallback, not the function's route.
        if (AssistantRoutePersistencePolicy.IsConversationalAssistant(functionType)
            && options?.PrivacyClass != LlmPrivacyClass.LocalOnly)
        {
            try
            {
                var routeIsLocal = OllamaProviderFamily.IsLanNode(selection.Provider)
                                   || await IsLocalProviderAsync(selection.Provider).ConfigureAwait(false);
                if (routeIsLocal
                    && !AssistantRoutePersistencePolicy.MayPersistAutomaticRoute(
                        functionType,
                        routeIsLocal,
                        await AssistantRoutePersistencePolicy.HasSubscriptionRouteAsync(
                            _settingsProvider, _providerRegistry).ConfigureAwait(false)))
                {
                    _logger.LogDebug(
                        "Not materializing local {Provider}/{Model} as the {FunctionType} preference while a subscription route is healthy",
                        selection.Provider,
                        selection.Model,
                        functionType);
                    return;
                }
            }
            catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                _logger.LogDebug(ex, "Could not check subscription health before materializing {FunctionType}", functionType);
                return;
            }
        }

        // In-memory dedup is scoped to this router/profile graph. The provider still
        // serializes and checks the database, so concurrent/multi-process safety does
        // not depend on this cache.
        if (!_materializedFunctionTypes.TryAdd(functionType, 0)) return;

        try
        {
            await _settingsProvider.EnsureFunctionPreferenceAsync(
                functionType,
                selection.Provider,
                selection.Model,
                selection.FallbackProvider,
                selection.FallbackModel,
                requiresVision).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogDebug(ex,
                "MaterializeFunctionPreferenceIfMissing failed for {FunctionType} (non-fatal)",
                functionType);
            // Allow retry on the next call — remove from the dedup set.
            _materializedFunctionTypes.TryRemove(functionType, out _);
        }
    }

    /// <summary>
    /// Self-heal a vision selection whose primary model cannot actually do vision.
    /// User/profile-directed routes may use only their exact configured capable
    /// fallback (or retry the same exact repaired tag). Automatic replacement must
    /// carry current function-scoped proof from the self-healer.
    /// </summary>
    private async Task<LlmModelSelection?> TryHealVisionPrimaryAsync(
        string functionType,
        LlmModelSelection broken,
        LlmRouterOptions? options)
    {
        try
        {
            if (IsUserOrProfileDirectedSelection(broken, options)
                && !string.IsNullOrWhiteSpace(broken.FallbackProvider)
                && !string.IsNullOrWhiteSpace(broken.FallbackModel))
            {
                var configuredFallback = CarryRoutePolicy(broken, new LlmModelSelection
                {
                    Provider = ResolveRegisteredProviderName(broken.FallbackProvider),
                    Model = broken.FallbackModel,
                    IsDefault = false,
                    IsUserPinned = broken.IsUserPinned,
                    Reason = "explicit configured user/profile vision fallback",
                    Context = broken.Context,
                    AuditCorrelationId = broken.FallbackAuditCorrelationId ?? broken.AuditCorrelationId,
                    SupportsToolUse = broken.FallbackSupportsToolUse,
                    SupportsStructuredOutput = broken.FallbackSupportsStructuredOutput,
                    MaxContextTokens = broken.FallbackMaxContextTokens,
                    CapabilityEvidenceSource = broken.FallbackCapabilityEvidenceSource,
                }, promoteConfiguredFallback: true);
                var fallbackIssue = await GetRouteRequirementIssueAsync(
                    configuredFallback,
                    requiresVision: true,
                    options,
                    verifyAvailability: true).ConfigureAwait(false);
                if (fallbackIssue is null
                    && (options?.RequiresToolUse != true
                        || GetToolUseSelectionIssue(configuredFallback) is null))
                {
                    return configuredFallback;
                }
            }

            return await TryBuildProofCarryingSelfHealSelectionAsync(
                functionType,
                broken,
                options,
                requiresVision: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Vision primary self-heal attempt failed for {FunctionType}", functionType);
        }
        return null;
    }

    private async Task<LlmModelSelection?> TryBuildProofCarryingSelfHealSelectionAsync(
        string functionType,
        LlmModelSelection broken,
        LlmRouterOptions? options,
        bool requiresVision)
    {
        if (_selfHealer is null
            || broken.BlockWeakerPolicyFallback
            || HasExactValidatedAdmission(broken, options))
        {
            return null;
        }

        var replacement = await _selfHealer.GetFunctionScopedReplacementAsync(
            functionType,
            broken.Provider,
            broken.Model,
            requiresVision).ConfigureAwait(false);
        if (replacement is null
            || string.IsNullOrWhiteSpace(replacement.Model)
            || !replacement.HasCurrentFunctionEvidence
            || string.IsNullOrWhiteSpace(replacement.AuditCorrelationId)
            || string.IsNullOrWhiteSpace(replacement.EvidenceSource)
            || !string.Equals(
                replacement.EvidenceFunctionType,
                functionType,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Rejected self-heal for {Provider}/{Model} on {FunctionType}: current function proof, source, or audit correlation was missing",
                broken.Provider,
                broken.Model,
                functionType);
            return null;
        }

        var providerName = ResolveRegisteredProviderName(broken.Provider);
        var decisionReceipt = replacement.ModelScoutDecisionReceipt;
        var receiptConstraintIssue = GetDecisionReceiptConstraintIssue(
            decisionReceipt,
            providerName,
            replacement.Model,
            broken.MinimumCapabilityScore,
            broken.RequiredTier,
            broken.RequireMeasuredCapabilityScore);
        if (receiptConstraintIssue is not null
            || decisionReceipt?.FunctionEvidence is not { } functionEvidence
            || !ModelScoutFunctionEvidence.IsCoherent(
                functionEvidence,
                functionType,
                providerName,
                replacement.Model,
                replacement.EvidenceSource))
        {
            _logger.LogWarning(
                "Rejected self-heal replacement {Provider}/{Replacement} for {FunctionType}: {Reason}",
                providerName,
                replacement.Model,
                functionType,
                receiptConstraintIssue ?? "the current exact function-evidence receipt is missing or incoherent");
            return null;
        }

        if ((IsUserOrProfileDirectedSelection(broken, options)
             || options?.DisableFallback == true)
            && !IsExactRouteIdentity(
                providerName,
                broken.Model,
                providerName,
                replacement.Model))
        {
            _logger.LogWarning(
                "Rejected self-heal replacement {Provider}/{Replacement} for constrained exact route {OriginalProvider}/{Model} on {FunctionType}",
                providerName,
                replacement.Model,
                providerName,
                broken.Model,
                functionType);
            return null;
        }

        var provider = _providerRegistry.GetProvider(providerName);
        if (provider is null || !provider.SupportsModel(replacement.Model))
        {
            return null;
        }

        var selection = CarryRoutePolicy(broken, new LlmModelSelection
        {
            Provider = providerName,
            Model = replacement.Model,
            FallbackProvider = broken.FallbackProvider,
            FallbackModel = broken.FallbackModel,
            IsDefault = false,
            IsUserPinned = broken.IsUserPinned,
            Reason = requiresVision
                ? "self-heal: proof-carrying vision model repair"
                : "self-heal: proof-carrying model repair",
            Context = broken.Context,
            AuditCorrelationId = replacement.AuditCorrelationId,
            SupportsToolUse = replacement.SupportsToolUse,
            SupportsStructuredOutput = replacement.SupportsStructuredOutput,
            MaxContextTokens = replacement.MaxContextTokens,
            CapabilityEvidenceSource = replacement.CapabilityEvidenceSource ?? replacement.EvidenceSource,
            CapabilityScore = decisionReceipt.Selected.CapabilityScore,
            CapabilityEvidenceKind = decisionReceipt.Selected.CapabilityProvenance,
            ModelScoutDecisionReceipt = replacement.ModelScoutDecisionReceipt,
            FallbackAuditCorrelationId = broken.FallbackAuditCorrelationId,
            FallbackSupportsToolUse = broken.FallbackSupportsToolUse,
            FallbackSupportsStructuredOutput = broken.FallbackSupportsStructuredOutput,
            FallbackMaxContextTokens = broken.FallbackMaxContextTokens,
            FallbackCapabilityEvidenceSource = broken.FallbackCapabilityEvidenceSource,
        });
        var capabilityIssue = await GetRouteRequirementIssueAsync(
            selection,
            requiresVision,
            options,
            verifyAvailability: true).ConfigureAwait(false);
        if (capabilityIssue is not null
            || (options?.RequiresToolUse == true && GetToolUseSelectionIssue(selection) is not null))
        {
            _logger.LogWarning(
                "Rejected proof-carrying self-heal {Provider}/{Model} for {FunctionType}: {Reason}",
                selection.Provider,
                selection.Model,
                functionType,
                capabilityIssue ?? GetToolUseSelectionIssue(selection));
            return null;
        }

        return selection;
    }

    private static bool IsUserOrProfileDirectedSelection(
        LlmModelSelection selection,
        LlmRouterOptions? options)
        => selection.IsUserPinned
           || options?.OverrideProvenance is
               LlmOverrideProvenance.User or
               LlmOverrideProvenance.Profile;

    /// <summary>
    /// Persist a proven replacement on the requested exact function row so future
    /// calls skip the broken primary. The failed route is deliberately not retained
    /// as fallback: fallbacks are separately proved exact-route state, not a parking
    /// place for a route that just failed. An existing proven fallback that is
    /// neither the broken route nor the replacement is kept — discarding it would
    /// throw away Scout-proved exact-route state the heal never invalidated.
    /// </summary>
    private async Task PersistPreferenceSwapAsync(
        string functionType,
        LlmModelSelection broken,
        LlmModelSelection replacement)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(replacement.AuditCorrelationId)
                || string.IsNullOrWhiteSpace(replacement.CapabilityEvidenceSource))
            {
                _logger.LogWarning(
                    "Skipped self-heal persistence for {FunctionType}: replacement proof correlation or evidence source was missing",
                    functionType);
                return;
            }

            var evaluated = await _settingsProvider
                .GetFunctionPreferenceAsync(functionType)
                .ConfigureAwait(false);
            if (evaluated is null
                || !evaluated.IsEnabled
                || !IsExactRouteIdentity(
                    evaluated.PreferredProvider,
                    evaluated.PreferredModel,
                    broken.Provider,
                    broken.Model))
            {
                _logger.LogInformation(
                    "Skipped self-heal persistence for {FunctionType}: the evaluated route changed before commit",
                    functionType);
                return;
            }

            var retainExistingFallback =
                !string.IsNullOrWhiteSpace(evaluated.FallbackProvider)
                && !string.IsNullOrWhiteSpace(evaluated.FallbackModel)
                && !IsExactRouteIdentity(
                    evaluated.FallbackProvider!,
                    evaluated.FallbackModel!,
                    broken.Provider,
                    broken.Model)
                && !IsExactRouteIdentity(
                    evaluated.FallbackProvider!,
                    evaluated.FallbackModel!,
                    replacement.Provider,
                    replacement.Model);

            var updated = new LlmFunctionPreference
            {
                Id = evaluated.Id,
                FunctionType = functionType,
                PreferredProvider = replacement.Provider,
                PreferredModel = replacement.Model,
                FallbackProvider = retainExistingFallback ? evaluated.FallbackProvider : null,
                FallbackModel = retainExistingFallback ? evaluated.FallbackModel : null,
                IsEnabled = evaluated.IsEnabled,
                RequiresVision = evaluated.RequiresVision,
                CreatedAt = evaluated.CreatedAt,
                CreatedBy = evaluated.CreatedBy,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = "llm-router:self-heal",
                AuditCorrelationId = replacement.AuditCorrelationId,
                KnownGoodProvider = evaluated.KnownGoodProvider,
                KnownGoodModel = evaluated.KnownGoodModel,
                KnownGoodConfirmedAt = evaluated.KnownGoodConfirmedAt,
            };
            var write = await _settingsProvider.TrySetFunctionPreferenceAsync(
                updated,
                FunctionPreferenceWriteAuthority.RespectUserPin,
                FunctionPreferenceVersion.Capture(functionType, evaluated)).ConfigureAwait(false);
            if (!write.Applied)
            {
                _logger.LogInformation(
                    "Skipped self-heal persistence for {FunctionType}: conditional write returned {Status}",
                    functionType,
                    write.Status);
                return;
            }

            _logger.LogWarning(
                "🔧 Self-healed {FunctionType}: {OldProv}/{OldModel} → {NewProv}/{NewModel}; failed route was not retained as fallback; existing fallback {FallbackRetained}",
                functionType,
                broken.Provider,
                broken.Model,
                replacement.Provider,
                replacement.Model,
                retainExistingFallback ? "retained" : "cleared");
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            _logger.LogWarning(ex, "Failed to persist self-healed preference for {FunctionType}", functionType);
        }
    }

    private async Task<string?> TryHealAndRetryPrimaryAsync(
        string functionType,
        string prompt,
        LlmModelSelection broken,
        LlmRouterOptions? options,
        string requestId,
        Stopwatch stopwatch,
        string? failureMessage)
    {
        if (_selfHealer is null)
        {
            return null;
        }

        if (await HasBoundSurvivalSetForPrimaryAsync(functionType, broken, options)
                .ConfigureAwait(false))
        {
            return null;
        }

        LlmModelSelection? healedSelection = null;
        var executionStarted = false;
        try
        {
            healedSelection = await TryBuildProofCarryingSelfHealSelectionAsync(
                functionType,
                broken,
                options,
                requiresVision: false).ConfigureAwait(false);
            if (healedSelection is null)
            {
                return null;
            }

            var provider = _providerRegistry.GetProvider(healedSelection.Provider);
            if (provider is null)
            {
                return null;
            }

            _logger.LogWarning(
                "🔧 Retrying {FunctionType} on self-healed primary {Provider}/{Model} after model-not-found from {OldModel}: {Error}",
                functionType,
                healedSelection.Provider,
                healedSelection.Model,
                broken.Model,
                failureMessage);

            _debugLogger.LogRequest(new LlmDebugRequest(
                RequestId: $"{requestId}-heal",
                FunctionType: functionType,
                Provider: healedSelection.Provider,
                Model: healedSelection.Model,
                Prompt: prompt,
                Temperature: options?.Temperature,
                MaxTokens: options?.MaxTokens,
                IsFallback: true,
                FallbackReason: "self_healed_primary"));

            var executionSettings = BuildExecutionSettings(options, healedSelection);
            using var localSlot = await AcquireLocalSlotAsync(
                healedSelection.Provider,
                healedSelection.Model,
                healedSelection.Context,
                executionSettings);
            await AppendFallbackSelectionAuditAsync(
                functionType,
                prompt,
                broken,
                healedSelection,
                options,
                "self-healed-primary").ConfigureAwait(false);
            await AppendExecutionStartedAuditAsync(
                functionType,
                healedSelection,
                options,
                fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            stopwatch.Restart();
            executionStarted = true;
            var response = await provider.GenerateResponseAsync(prompt, healedSelection.Model, executionSettings);
            stopwatch.Stop();

            if (!response.WasSuccessful)
            {
                await TrackErrorAsync(
                    functionType,
                    healedSelection,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    response.ErrorMessage ?? "self_healed_primary_failed",
                    fallbackUsed: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "Self-healed primary {Provider}/{Model} still failed for {FunctionType}: {Error}",
                    healedSelection.Provider,
                    healedSelection.Model,
                    functionType,
                    response.ErrorMessage);
                return null;
            }

            var qualityIssue = ValidateResponseQuality(response, options, out var normalizedContent);
            if (qualityIssue is not null)
            {
                await TrackQualityFailureAsync(
                    functionType,
                    healedSelection,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    qualityIssue,
                    fallbackUsed: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "Self-healed primary {Provider}/{Model} failed quality gate for {FunctionType}: {Issue}",
                    healedSelection.Provider,
                    healedSelection.Model,
                    functionType,
                    qualityIssue);
                if (IsTerminalCallerContractFailure(qualityIssue, options))
                {
                    throw new LlmResponseContractException(
                        functionType,
                        healedSelection.Provider,
                        healedSelection.Model);
                }
                return null;
            }
            if (normalizedContent is not null)
            {
                response.Content = normalizedContent;
            }

            _debugLogger.LogResponse(new LlmDebugResponse(
                RequestId: $"{requestId}-heal",
                FunctionType: functionType,
                Provider: healedSelection.Provider,
                Model: healedSelection.Model,
                Response: response.Content,
                InputTokens: response.InputTokens,
                OutputTokens: response.OutputTokens,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                IsFallback: true));

            await TrackUsageAsync(
                functionType,
                healedSelection,
                prompt,
                response,
                stopwatch.ElapsedMilliseconds,
                options,
                fallbackUsed: true);
            if (!IsUserOrProfileDirectedSelection(broken, options)
                && !IsExactRouteIdentity(
                    broken.Provider,
                    broken.Model,
                    healedSelection.Provider,
                    healedSelection.Model))
            {
                await PersistPreferenceSwapAsync(
                    functionType,
                    broken,
                    healedSelection).ConfigureAwait(false);
            }
            return response.Content;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            if (executionStarted && healedSelection is not null)
            {
                await TrackErrorAsync(
                    functionType,
                    healedSelection,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    ex.Message,
                    fallbackUsed: true).ConfigureAwait(false);
            }
            _logger.LogWarning(ex,
                "Self-healed primary retry failed for {FunctionType} on {Provider}/{Model}",
                functionType,
                broken.Provider,
                broken.Model);
            return null;
        }
    }

    private async Task<string?> TryHealAndRetryVisionPrimaryAsync(
        string functionType,
        string prompt,
        string imageBase64,
        LlmModelSelection broken,
        LlmRouterOptions? options,
        string requestId,
        Stopwatch stopwatch,
        string? failureMessage)
    {
        if (_selfHealer is null)
        {
            return null;
        }

        if (await HasBoundSurvivalSetForPrimaryAsync(functionType, broken, options)
                .ConfigureAwait(false))
        {
            return null;
        }

        LlmModelSelection? healedSelection = null;
        var executionStarted = false;
        try
        {
            healedSelection = await TryBuildProofCarryingSelfHealSelectionAsync(
                functionType,
                broken,
                options,
                requiresVision: true).ConfigureAwait(false);
            if (healedSelection is null)
            {
                return null;
            }

            var provider = _providerRegistry.GetProvider(healedSelection.Provider);
            if (provider is null)
            {
                return null;
            }

            _logger.LogWarning(
                "🔧 Retrying vision {FunctionType} on self-healed primary {Provider}/{Model} after model-not-found from {OldModel}: {Error}",
                functionType,
                healedSelection.Provider,
                healedSelection.Model,
                broken.Model,
                failureMessage);

            _debugLogger.LogRequest(new LlmDebugRequest(
                RequestId: $"{requestId}-vheal",
                FunctionType: functionType,
                Provider: healedSelection.Provider,
                Model: healedSelection.Model,
                Prompt: prompt,
                ImageBase64Summary: !string.IsNullOrEmpty(imageBase64) ? $"Base64 image ({imageBase64.Length} chars)" : null,
                Temperature: options?.Temperature,
                MaxTokens: options?.MaxTokens,
                IsFallback: true,
                FallbackReason: "self_healed_vision_primary"));

            var executionSettings = BuildExecutionSettings(options, healedSelection);
            using var localSlot = await AcquireLocalSlotAsync(
                healedSelection.Provider,
                healedSelection.Model,
                healedSelection.Context,
                executionSettings,
                LocalLlmWorkloadKind.Vision);
            await AppendFallbackSelectionAuditAsync(
                functionType,
                prompt,
                broken,
                healedSelection,
                options,
                "self-healed-vision-primary",
                requiresVision: true).ConfigureAwait(false);
            await AppendExecutionStartedAuditAsync(
                functionType,
                healedSelection,
                options,
                fallbackUsed: true, physicalSettings: executionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            stopwatch.Restart();
            executionStarted = true;
            var response = await provider.GenerateVisionResponseAsync(prompt, imageBase64, healedSelection.Model, executionSettings);
            stopwatch.Stop();

            if (!response.WasSuccessful)
            {
                await TrackErrorAsync(
                    functionType,
                    healedSelection,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    response.ErrorMessage ?? "self_healed_vision_primary_failed",
                    fallbackUsed: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "Self-healed vision primary {Provider}/{Model} still failed for {FunctionType}: {Error}",
                    healedSelection.Provider,
                    healedSelection.Model,
                    functionType,
                    response.ErrorMessage);
                return null;
            }

            var qualityIssue = ValidateResponseQuality(response, options, out var normalizedContent);
            if (qualityIssue is not null)
            {
                await TrackQualityFailureAsync(
                    functionType,
                    healedSelection,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    qualityIssue,
                    fallbackUsed: true,
                    requiresVision: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "Self-healed vision primary {Provider}/{Model} failed quality gate for {FunctionType}: {Issue}",
                    healedSelection.Provider,
                    healedSelection.Model,
                    functionType,
                    qualityIssue);
                if (IsTerminalCallerContractFailure(qualityIssue, options))
                {
                    throw new LlmResponseContractException(
                        functionType,
                        healedSelection.Provider,
                        healedSelection.Model);
                }
                return null;
            }
            if (normalizedContent is not null)
            {
                response.Content = normalizedContent;
            }

            _debugLogger.LogResponse(new LlmDebugResponse(
                RequestId: $"{requestId}-vheal",
                FunctionType: functionType,
                Provider: healedSelection.Provider,
                Model: healedSelection.Model,
                Response: response.Content,
                InputTokens: response.InputTokens,
                OutputTokens: response.OutputTokens,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                IsFallback: true));

            await TrackUsageAsync(
                functionType,
                healedSelection,
                prompt,
                response,
                stopwatch.ElapsedMilliseconds,
                options,
                fallbackUsed: true,
                requiresVision: true);
            if (!IsUserOrProfileDirectedSelection(broken, options)
                && !IsExactRouteIdentity(
                    broken.Provider,
                    broken.Model,
                    healedSelection.Provider,
                    healedSelection.Model))
            {
                await PersistPreferenceSwapAsync(
                    functionType,
                    broken,
                    healedSelection).ConfigureAwait(false);
            }
            return response.Content;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            if (executionStarted && healedSelection is not null)
            {
                await TrackErrorAsync(
                    functionType,
                    healedSelection,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    ex.Message,
                    fallbackUsed: true).ConfigureAwait(false);
            }
            _logger.LogWarning(ex,
                "Self-healed vision primary retry failed for {FunctionType} on {Provider}/{Model}",
                functionType,
                broken.Provider,
                broken.Model);
            return null;
        }
    }

    /// <summary>
    /// Initialize the LLM router with all required dependencies.
    ///
    /// DEPENDENCY INJECTION NOTES:
    /// - ILlmApiKeyProvider MUST be Singleton (injected into Singleton ILlmProvider implementations)
    /// - All other dependencies can be Scoped or Transient
    /// - ILlmModelSelfHealer and ILlmDebugLogger are optional (null-safe)
    /// - Production hosts must register a durable ILlmRoutingAuditStore. AddSwarmCoreRouting
    ///   installs a fail-closed sentinel when the host omits one. Direct construction is
    ///   held to the same auditable-routing contract as DI composition.
    ///
    /// INITIALIZATION ORDER:
    /// 1. Store all dependencies for use in routing decisions
    /// 2. Initialize debug logger to NullLlmDebugLogger if not provided (prevents null checks)
    /// 3. Ready for GenerateResponseAsync() calls
    /// </summary>
    public LlmRouter(
        ILlmSettingsProvider settingsProvider,
        ILlmUsageTracker usageTracker,
        ILlmProviderRegistry providerRegistry,
        ILlmCapabilitiesService capabilitiesService,
        ILlmCostService costService,
        ILlmApiKeyProvider apiKeyProvider,
        ILogger<LlmRouter> logger,
        ILlmModelSelfHealer? selfHealer = null,
        ILlmDebugLogger? debugLogger = null,
        IKnownGoodConfigTracker? knownGoodTracker = null,
        ILocalLlmRuntimeManager? localRuntimeManager = null,
        ILlmOperationalAlertSink? operationalAlerts = null,
        IEnumerable<ILlmRouteAdvisor>? routeAdvisors = null,
        ILlmRoutingGovernor? routingGovernor = null,
        ILlmProviderCooldownTracker? cooldownTracker = null,
        ISystemActivitySignal? systemActivity = null,
        IRoutingPolicyStore? routingPolicyStore = null,
        IBudgetPressureSource? budgetPressureSource = null,
        ISubscriptionQuotaTracker? quotaTracker = null,
        IEmbeddingModelPin? embeddingPin = null,
        IEmbeddingRebuildOrchestrator? embeddingRebuild = null,
        ILlmRoutingAuditStore? routingAuditStore = null,
        IModelScout? modelScout = null,
        IModelScoutRoutingGate? modelScoutRoutingGate = null,
        ILlmRouteAdmissionValidator? routeAdmissionValidator = null,
        LlmProviderDiscoveryCoordinator? providerDiscovery = null,
        LlmModelValidationCoordinator? modelValidationCoordinator = null,
        ILlmRouteLoadTracker? routeLoadTracker = null,
        ILlmRouteContextWindowObservations? contextWindows = null)
    {
        _routeLoadTracker = routeLoadTracker;
        _contextWindows = contextWindows;
        _settingsProvider = settingsProvider;
        _usageTracker = usageTracker;
        _providerRegistry = providerRegistry;
        _capabilitiesService = capabilitiesService;
        _costService = costService;
        _apiKeyProvider = apiKeyProvider;
        _selfHealer = selfHealer;
        _knownGoodTracker = knownGoodTracker;
        _operationalAlerts = operationalAlerts;
        _routeAdvisors = routeAdvisors?.ToList() ?? [];
        _routingGovernor = routingGovernor;
        _localRuntimeManager = localRuntimeManager;
        _debugLogger = debugLogger ?? NullLlmDebugLogger.Instance;
        _cooldownTracker = cooldownTracker;
        _systemActivity = systemActivity;
        _routingPolicyStore = routingPolicyStore;
        _budgetPressureSource = budgetPressureSource;
        _quotaTracker = quotaTracker;
        _embeddingPin = embeddingPin;
        _embeddingRebuild = embeddingRebuild;
        _routingAuditStore = routingAuditStore ?? new MissingLlmRoutingAuditStore();
        _modelScout = modelScout;
        _modelScoutRoutingGate = modelScoutRoutingGate;
        _routeAdmissionValidator = routeAdmissionValidator;
        _providerDiscovery = providerDiscovery ?? LlmProviderDiscoveryCoordinator.Shared;
        _modelValidationCoordinator = modelValidationCoordinator ?? new LlmModelValidationCoordinator();
        _logger = logger;
    }

    /// <summary>
    /// Acquire the local-LLM priority slot if the resolved provider is local;
    /// otherwise return a no-op disposable. The interactivity is taken from
    /// the resolved <see cref="LlmCallContext"/> (function-type profile + caller
    /// override), so background processors stay out of the way of chat/applet
    /// requests without per-call-site annotation.
    /// </summary>
    private async Task<IDisposable> AcquireLocalSlotAsync(
        string providerName,
        string model,
        LlmCallContext? context,
        Dictionary<string, object>? executionSettings = null,
        LocalLlmWorkloadKind workloadKind = LocalLlmWorkloadKind.Text,
        CancellationToken ct = default)
    {
        // An Ollama@node LAN provider is "local" for privacy purposes but runs on
        // another computer's GPU; it must never queue behind or hold this
        // machine's runtime lease.
        var lanNode = OllamaProviderFamily.IsLanNode(providerName);
        if (_localRuntimeManager is null && _routeLoadTracker is null) return NoOpReleaser.Instance;
        if (!lanNode && !await IsLocalProviderAsync(providerName)) return NoOpReleaser.Instance;

        if (!ct.CanBeCanceled)
        {
            ct = LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        }

        var interactivity = context?.Interactivity ?? LlmInteractivity.Unspecified;
        if (lanNode && _routeLoadTracker is not null && interactivity != LlmInteractivity.UserWaiting)
        {
            // This machine's runtime lease already puts waiting users first
            // locally. A LAN node has no such lease, so background work lets
            // the user-waiting calls this process started there finish first.
            await _routeLoadTracker.WaitForUserWaitingQuietAsync(
                providerName, LanBackgroundYieldLimit, ct).ConfigureAwait(false);
        }

        var loadLease = _routeLoadTracker?.Begin(providerName, model, interactivity) ?? NoOpReleaser.Instance;
        if (lanNode || _localRuntimeManager is null) return loadLease;
        try
        {
            var request = new LocalLlmRuntimeRequest(providerName, model, context, workloadKind);
            if (executionSettings is not null)
            {
                _localRuntimeManager.ApplyExecutionPolicy(request, executionSettings);
            }

            var runtimeLease = await _localRuntimeManager.AcquireAsync(request, ct).ConfigureAwait(false);
            return new CompositeReleaser(runtimeLease, loadLease);
        }
        catch
        {
            loadLease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// How long background work waits for user-waiting calls on a shared LAN
    /// node before it proceeds anyway. Bounded so bulk work is delayed, never
    /// stranded, by a long interactive session.
    /// </summary>
    private static readonly TimeSpan LanBackgroundYieldLimit = TimeSpan.FromMinutes(2);

    private sealed class NoOpReleaser : IDisposable
    {
        public static readonly NoOpReleaser Instance = new();
        public void Dispose() { }
    }

    private sealed class CompositeReleaser(IDisposable first, IDisposable second) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try { first.Dispose(); }
            finally { second.Dispose(); }
        }
    }

    /// <summary>
    /// Generate a text response from the optimal LLM for the given function type.
    ///
    /// FLOW:
    /// 1. Clean HTML from prompt (LLMs don't handle HTML well)
    /// 2. Generate unique request ID for tracing
    /// 3. Select optimal model based on function type, cost, and performance
    /// 4. Log request to debug logger
    /// 5. Call provider's GenerateResponseAsync()
    /// 6. Handle errors with automatic fallback to secondary provider
    /// 7. Commit the execution receipt, then persist best-effort usage telemetry
    /// 8. Return response content
    ///
    /// ERROR HANDLING:
    /// - Rate Limit: Fallback to secondary provider
    /// - Model Not Found: Fallback to secondary provider
    /// - Timeout/Transient: Fallback to secondary provider
    /// - Other Exceptions: Fallback if configured, else throw LlmFailoverException
    ///
    /// PERFORMANCE:
    /// - Durable audit commits are awaited; disposable usage telemetry may be asynchronous
    /// - Model validation cache prevents repeated API calls
    /// - Model latency measures the physical provider call, excluding selection/audit/queue work
    /// </summary>
    public async Task<string> GenerateResponseAsync(string functionType, string prompt, LlmRouterOptions? options = null)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var stopwatch = Stopwatch.StartNew();
        var cleanedPrompt = CleanHtmlFromPrompt(prompt);
        var requestId = Guid.NewGuid().ToString("N")[..8];

        var selectedModel = await SelectOptimalModelInternalAsync(functionType, cleanedPrompt, options);
        var primaryExecutionStarted = false;
        var primaryTerminalReceiptWritten = false;

        // Debug log the request for troubleshooting
        _debugLogger.LogRequest(new LlmDebugRequest(
            RequestId: requestId,
            FunctionType: functionType,
            Provider: selectedModel.Provider,
            Model: selectedModel.Model,
            Prompt: cleanedPrompt,
            Temperature: options?.Temperature,
            MaxTokens: options?.MaxTokens));

        try
        {
            _logger.LogInformation("🤖 LlmRouter: Using {Provider}/{Model} for {FunctionType}",
                selectedModel.Provider, selectedModel.Model, functionType);

            var provider = _providerRegistry.GetProvider(selectedModel.Provider);
            if (provider == null)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} not registered in provider registry");
            }

            var executionSettings = BuildExecutionSettings(options, selectedModel);

            // Do not promote to fallback merely because a fixed wall-clock budget expired.
            // Route selection should be smart before the call; fallback is for concrete
            // provider failures, model-not-found, rate limits, cancellation, or quality gates.
            IDisposable? localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                executionSettings);
            try
            {
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selectedModel,
                    options,
                    fallbackUsed: false, physicalSettings: executionSettings).ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                stopwatch.Restart();
                primaryExecutionStarted = true;
                var response = await provider.GenerateResponseAsync(
                    cleanedPrompt,
                    selectedModel.Model,
                    executionSettings);

                stopwatch.Stop();

                if (response is null)
                {
                    throw new InvalidOperationException($"Provider {selectedModel.Provider}/{selectedModel.Model} returned a null response");
                }

                // Safety net: if provider returned a failed response without
                // throwing, release the physical local slot before any same-
                // provider heal or local fallback tries to acquire it again.
                if (!response.WasSuccessful)
                {
                    localSlot.Dispose();
                    localSlot = null;
                    // Classify once and carry BOTH the wire-compatible reason
                    // token and the typed category: the token collapses
                    // distinct categories (auth/content/invalid/malformed all
                    // travel as "provider_error", quota as "rate_limit"), so
                    // the failover exception must receive the classification
                    // made here rather than re-deriving a lossy one later.
                    var (reason, failureCategory) = ClassifyFailureReasonWithCategory(response);
                    await TrackErrorAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        response.ErrorMessage ?? $"provider_failure:{reason}").ConfigureAwait(false);
                    primaryTerminalReceiptWritten = true;
                    if (reason == "model_not_found")
                    {
                        var healedResponse = await TryHealAndRetryPrimaryAsync(
                            functionType,
                            cleanedPrompt,
                            selectedModel,
                            options,
                            requestId,
                            stopwatch,
                            response.ErrorMessage);
                        if (healedResponse is not null)
                        {
                            return healedResponse;
                        }
                    }

                    await RecordFailedResponseCooldownAsync(selectedModel.Provider, reason, response.ErrorMessage, response.HttpStatusCode, response.FailureCategory);

                    _logger.LogWarning("Provider {Provider}/{Model} returned unsuccessful response: {Error}. Trying fallback.",
                        selectedModel.Provider, selectedModel.Model, response.ErrorMessage);
                    return await HandleFallbackAsync(
                        functionType, cleanedPrompt, selectedModel, stopwatch, options, reason, requestId,
                        failureCategory);
                }

                // Quality gate: detect empty/garbage responses that providers report as "successful"
                var qualityFailureReason = ValidateResponseQuality(response, options, out var normalizedContent);
                if (qualityFailureReason != null)
                {
                    _logger.LogWarning(
                        "Quality gate failed for {Provider}/{Model} on {FunctionType}: {Reason}. Content length: {Length}. Trying JSON repair/fallback.{Sample}",
                        selectedModel.Provider, selectedModel.Model, functionType, qualityFailureReason,
                        response.Content?.Length ?? 0,
                        DescribeRejectedContent(response.Content));
                    await TrackQualityFailureAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        response,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        qualityFailureReason);
                    primaryTerminalReceiptWritten = true;

                    if (IsTerminalCallerContractFailure(qualityFailureReason, options))
                    {
                        throw new LlmResponseContractException(
                            functionType,
                            selectedModel.Provider,
                            selectedModel.Model);
                    }

                    var jsonRepairResponse = options?.SuppressStructuredOutputRetry == true
                        ? null
                        : await TryRetryJsonContractAsync(
                            functionType,
                            cleanedPrompt,
                            selectedModel,
                            options,
                            requestId,
                            qualityFailureReason,
                            response,
                            isFallback: false);
                    if (jsonRepairResponse is not null)
                    {
                        return jsonRepairResponse;
                    }

                    localSlot.Dispose();
                    localSlot = null;
                    return await HandleFallbackAsync(functionType, cleanedPrompt, selectedModel, stopwatch, options,
                        qualityFailureReason, requestId);
                }
                if (normalizedContent is not null)
                {
                    response.Content = normalizedContent;
                }

                // Debug log the response for troubleshooting
                _debugLogger.LogResponse(new LlmDebugResponse(
                    RequestId: requestId,
                    FunctionType: functionType,
                    Provider: selectedModel.Provider,
                    Model: selectedModel.Model,
                    Response: response.Content,
                    InputTokens: response.InputTokens,
                    OutputTokens: response.OutputTokens,
                    ElapsedMs: stopwatch.ElapsedMilliseconds));

                // Track usage (fire-and-forget - doesn't block response).
                // The captured prompt + response feed AbTestReplayService later;
                // judging is offline, never on the live path.
                await TrackUsageAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options);
                primaryTerminalReceiptWritten = true;

                return response.Content;
            }
            finally
            {
                localSlot?.Dispose();
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (FallbackDisabledException)
        {
            throw;
        }
        catch (LlmRouteTemporarilyUnavailableException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (LlmFailoverException)
        {
            // The fallback helper has already attributed and audited the physical
            // fallback attempt. Do not treat it as a fresh primary failure or run
            // the fallback chain a second time.
            throw;
        }
        catch (Exception)
            when (IsRequestCancellationRequested(options))
        {
            // Caller cancellation and local-runtime preemption are scheduling
            // outcomes. They must not trigger fallback, cooldown, or negative
            // route-quality evidence. A provider call that already started still
            // needs a terminal receipt so audit timelines cannot remain pending.
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
            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    ex.Message).ConfigureAwait(false);
                primaryTerminalReceiptWritten = true;
            }
            await RecordCooldownIfRateLimitAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            // The wire-compatible reason stays "rate_limit"; the category
            // resolved from the actual message (QuotaExhausted vs RateLimit)
            // travels alongside so a thrown quota exhaustion cannot surface
            // mislabeled as a transient rate limit on the failover exception.
            return await HandleFallbackAsync(
                functionType, cleanedPrompt, selectedModel, stopwatch, options, "rate_limit", requestId,
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
                primaryTerminalReceiptWritten = true;
            }
            var healedResponse = await TryHealAndRetryPrimaryAsync(
                functionType,
                cleanedPrompt,
                selectedModel,
                options,
                requestId,
                stopwatch,
                ex.Message);
            if (healedResponse is not null)
            {
                return healedResponse;
            }

            return await HandleFallbackAsync(functionType, cleanedPrompt, selectedModel, stopwatch, options, "model_not_found", requestId);
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
                primaryTerminalReceiptWritten = true;
            }
            // A timeout is an uncertain post-send failure: the provider may have
            // accepted and billed the request. Commit cooldown before exposing a
            // retryable automatic-route failure so the next attempt cannot repeat
            // the same physical route.
            await RecordCooldownIfTimeoutOrTransientAsync(selectedModel.Provider, ex).ConfigureAwait(false);
            // The wire-compatible reason stays "timeout"; the classification of
            // the actual message rides alongside so a thrown adverse HTTP
            // status (a 401 travelling as HttpRequestException) surfaces its
            // real category on the failover exception, matching the cooldown
            // row written above. Unclassifiable network-shaped transients
            // (ProviderError under Classify) stay typed Timeout — same as the
            // legacy token — so retry semantics do not silently widen.
            var thrownTransientCategory = Services.LlmErrorPolicy.Classify(ex);
            return await HandleFallbackAsync(
                functionType, cleanedPrompt, selectedModel, stopwatch, options, "timeout", requestId,
                thrownTransientCategory is LlmFailureCategory.ProviderError
                    ? LlmFailureCategory.Timeout
                    : thrownTransientCategory);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            // A proven automatic primary can request a fresh Scout fallback even
            // when no optional fallback was precomputed.
            if (CanAttemptRuntimeFallback(selectedModel, options))
            {
                _logger.LogWarning(ex, "Primary {Provider}/{Model} failed for {FunctionType}; trying fallback",
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
                    primaryTerminalReceiptWritten = true;
                }

                try
                {
                    return await HandleFallbackAsync(functionType, cleanedPrompt, selectedModel, stopwatch, options, "primary_exception", requestId);
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

                    // Debug log the primary error (fallback error is logged by HandleFallbackAsync when it succeeds;
                    // when it throws, we still emit a primary error record for the requestId).
                    _debugLogger.LogError(new LlmDebugError(
                        RequestId: requestId,
                        FunctionType: functionType,
                        Provider: selectedModel.Provider,
                        Model: selectedModel.Model,
                        ErrorMessage: ex.Message,
                        StackTrace: ex.StackTrace,
                        ElapsedMs: stopwatch.ElapsedMilliseconds));

                    var fallbackSelection = await BuildFallbackFailureSelectionAsync(selectedModel, requiresVision: false);

                    // Both raw messages are in scope: classify them so this
                    // failover carries typed categories instead of forcing
                    // consumers back onto prose parsing.
                    throw new LlmFailoverException(
                        functionType: functionType,
                        promptId: requestId,
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

            // Debug log the error
            _debugLogger.LogError(new LlmDebugError(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                ErrorMessage: ex.Message,
                StackTrace: ex.StackTrace,
                ElapsedMs: stopwatch.ElapsedMilliseconds));

            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(functionType, selectedModel, prompt, stopwatch.ElapsedMilliseconds, options, ex.Message);
            }
            // A pinned/no-fallback route that was rate-limited or timed out is
            // still evidence about the PROVIDER: record the cooldown so the next
            // call (from any function) skips it, regardless of whether this
            // request was allowed to fall back. (Same precedence as the tool-use
            // path: a rate-limited HttpRequestException is recorded once.)
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

    public async Task<string> GenerateVisionResponseAsync(string functionType, string prompt, string imageBase64,
        LlmRouterOptions? options = null, object? payloadMetadata = null)
    {
        await using var physicalInvocation = new PhysicalInvocation();
        functionType = CanonicalizeRoutingFunctionType(functionType);

        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];

        // Select model with vision requirement
        var modifiedOptions = options ?? new LlmRouterOptions();
        var selectedModel = await SelectOptimalModelInternalAsync(functionType, prompt, modifiedOptions, requiresVision: true);
        var primaryExecutionStarted = false;
        var primaryTerminalReceiptWritten = false;

        // Debug log the request (with image summary, not full base64)
        var imageSummary = !string.IsNullOrEmpty(imageBase64)
            ? $"Base64 image ({imageBase64.Length} chars)"
            : null;
        _debugLogger.LogRequest(new LlmDebugRequest(
            RequestId: requestId,
            FunctionType: functionType,
            Provider: selectedModel.Provider,
            Model: selectedModel.Model,
            Prompt: prompt,
            ImageBase64Summary: imageSummary,
            Temperature: options?.Temperature,
            MaxTokens: options?.MaxTokens));

        try
        {
            _logger.LogInformation("🤖 LlmRouter: Using {Provider}/{Model} for vision {FunctionType}",
                selectedModel.Provider, selectedModel.Model, functionType);

            var provider = _providerRegistry.GetProvider(selectedModel.Provider);
            if (provider == null)
            {
                throw new InvalidOperationException($"Provider {selectedModel.Provider} not registered");
            }

            if (!provider.SupportsVision(selectedModel.Model))
            {
                if (options?.DisableFallback == true)
                {
                    throw new InvalidOperationException(
                        $"Exact model {selectedModel.Provider}/{selectedModel.Model} does not support vision.");
                }

                // The materialized preference points at a non-vision model. Self-heal in
                // place so the next call doesn't repeat the same mistake, then continue.
                var rejectedSelection = selectedModel;
                var healed = await TryHealVisionPrimaryAsync(functionType, rejectedSelection, options);
                if (healed != null)
                {
                    selectedModel = healed;
                    provider = _providerRegistry.GetProvider(selectedModel.Provider)
                        ?? throw new InvalidOperationException($"Provider {selectedModel.Provider} not registered after self-heal");
                    await AppendFallbackSelectionAuditAsync(
                        functionType,
                        prompt,
                        rejectedSelection,
                        selectedModel,
                        options,
                        "vision-capability-self-heal",
                        requiresVision: true).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException($"Model {selectedModel.Model} does not support vision");
                }
            }

            var executionSettings = BuildExecutionSettings(options, selectedModel);
            IDisposable? localSlot = await AcquireLocalSlotAsync(
                selectedModel.Provider,
                selectedModel.Model,
                selectedModel.Context,
                executionSettings,
                LocalLlmWorkloadKind.Vision);
            try
            {
                await AppendExecutionStartedAuditAsync(
                    functionType,
                    selectedModel,
                    options,
                    fallbackUsed: false, physicalSettings: executionSettings).ConfigureAwait(false);
                await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
                stopwatch.Restart();
                primaryExecutionStarted = true;
                var response = await provider.GenerateVisionResponseAsync(
                    prompt,
                    imageBase64,
                    selectedModel.Model,
                    executionSettings);

                stopwatch.Stop();

                if (response is null)
                {
                    throw new InvalidOperationException($"Vision provider {selectedModel.Provider}/{selectedModel.Model} returned a null response");
                }

                // A failed-response result remains inside the outer try. Release
                // the local runtime before same-provider healing or another local
                // vision fallback attempts to acquire the single physical slot.
                if (!response.WasSuccessful)
                {
                    localSlot.Dispose();
                    localSlot = null;
                    // Classify once; see the text path — the typed category
                    // travels alongside the collapsed legacy reason token.
                    var (reason, failureCategory) = ClassifyFailureReasonWithCategory(response);
                    await TrackErrorAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        response.ErrorMessage ?? $"vision_provider_failure:{reason}").ConfigureAwait(false);
                    primaryTerminalReceiptWritten = true;
                    if (reason == "model_not_found")
                    {
                        var healedResponse = await TryHealAndRetryVisionPrimaryAsync(
                            functionType,
                            prompt,
                            imageBase64,
                            selectedModel,
                            options,
                            requestId,
                            stopwatch,
                            response.ErrorMessage);
                        if (healedResponse is not null)
                        {
                            return healedResponse;
                        }
                    }

                    await RecordFailedResponseCooldownAsync(selectedModel.Provider, reason, response.ErrorMessage, response.HttpStatusCode, response.FailureCategory);

                    _logger.LogWarning("Vision provider {Provider}/{Model} returned unsuccessful response: {Error}. Trying fallback.",
                        selectedModel.Provider, selectedModel.Model, response.ErrorMessage);
                    return await HandleVisionFallbackAsync(
                        functionType, prompt, imageBase64, selectedModel, stopwatch, options, reason, requestId,
                        failureCategory);
                }

                // Quality gate: detect empty/garbage responses that providers report as "successful"
                var qualityFailureReason = ValidateResponseQuality(response, options, out var normalizedContent);
                if (qualityFailureReason != null)
                {
                    _logger.LogWarning(
                        "Quality gate failed for vision {Provider}/{Model} on {FunctionType}: {Reason}. Content length: {Length}. Trying fallback.",
                        selectedModel.Provider, selectedModel.Model, functionType, qualityFailureReason,
                        response.Content?.Length ?? 0);
                    await TrackQualityFailureAsync(
                        functionType,
                        selectedModel,
                        prompt,
                        response,
                        stopwatch.ElapsedMilliseconds,
                        options,
                        qualityFailureReason,
                        requiresVision: true);
                    primaryTerminalReceiptWritten = true;

                    if (IsTerminalCallerContractFailure(qualityFailureReason, options))
                    {
                        throw new LlmResponseContractException(
                            functionType,
                            selectedModel.Provider,
                            selectedModel.Model);
                    }

                    var thinkingRetryResponse = await TryRetryVisionThinkingChannelAsync(
                        functionType,
                        prompt,
                        imageBase64,
                        selectedModel,
                        options,
                        requestId,
                        qualityFailureReason,
                        response,
                        isFallback: false);
                    if (thinkingRetryResponse is not null)
                    {
                        return thinkingRetryResponse;
                    }

                    localSlot.Dispose();
                    localSlot = null;
                    return await HandleVisionFallbackAsync(functionType, prompt, imageBase64, selectedModel, stopwatch, options,
                        qualityFailureReason, requestId);
                }
                if (normalizedContent is not null)
                {
                    response.Content = normalizedContent;
                }

                // Debug log the response
                _debugLogger.LogResponse(new LlmDebugResponse(
                    RequestId: requestId,
                    FunctionType: functionType,
                    Provider: selectedModel.Provider,
                    Model: selectedModel.Model,
                    Response: response.Content,
                    InputTokens: response.InputTokens,
                    OutputTokens: response.OutputTokens,
                    ElapsedMs: stopwatch.ElapsedMilliseconds));

                await TrackUsageAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    response,
                    stopwatch.ElapsedMilliseconds,
                    options,
                    requiresVision: true);

                return response.Content;
            }
            finally
            {
                localSlot?.Dispose();
            }
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (LlmResponseContractException)
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
            // See the text path: the reason token stays wire-compatible while
            // the classification-time category rides alongside.
            return await HandleVisionFallbackAsync(
                functionType, prompt, imageBase64, selectedModel, stopwatch, options, "rate_limit", requestId,
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
            var healedResponse = await TryHealAndRetryVisionPrimaryAsync(
                functionType,
                prompt,
                imageBase64,
                selectedModel,
                options,
                requestId,
                stopwatch,
                ex.Message);
            if (healedResponse is not null)
            {
                return healedResponse;
            }

            return await HandleVisionFallbackAsync(functionType, prompt, imageBase64, selectedModel, stopwatch, options, "model_not_found", requestId);
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
            // See the text path: a thrown adverse HTTP status surfaces its real
            // category; unclassifiable transients stay typed Timeout.
            var thrownTransientCategory = Services.LlmErrorPolicy.Classify(ex);
            return await HandleVisionFallbackAsync(
                functionType, prompt, imageBase64, selectedModel, stopwatch, options, "timeout", requestId,
                thrownTransientCategory is LlmFailureCategory.ProviderError
                    ? LlmFailureCategory.Timeout
                    : thrownTransientCategory);
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            // A proven automatic primary can request a fresh Scout fallback even
            // when no optional fallback was precomputed.
            if (CanAttemptRuntimeFallback(selectedModel, options))
            {
                _logger.LogWarning(ex, "Vision primary {Provider}/{Model} failed for {FunctionType}; trying fallback",
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

                try
                {
                    return await HandleVisionFallbackAsync(functionType, prompt, imageBase64, selectedModel, stopwatch, options, "primary_exception", requestId);
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

                    _debugLogger.LogError(new LlmDebugError(
                        RequestId: requestId,
                        FunctionType: functionType,
                        Provider: selectedModel.Provider,
                        Model: selectedModel.Model,
                        ErrorMessage: ex.Message,
                        StackTrace: ex.StackTrace,
                        ElapsedMs: stopwatch.ElapsedMilliseconds));

                    var fallbackSelection = await BuildFallbackFailureSelectionAsync(selectedModel, requiresVision: true);

                    // Both raw messages are in scope: classify them so this
                    // failover carries typed categories instead of forcing
                    // consumers back onto prose parsing.
                    throw new LlmFailoverException(
                        functionType: functionType,
                        promptId: requestId,
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

            // Debug log the error
            _debugLogger.LogError(new LlmDebugError(
                RequestId: requestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                ErrorMessage: ex.Message,
                StackTrace: ex.StackTrace,
                ElapsedMs: stopwatch.ElapsedMilliseconds));

            if (primaryExecutionStarted)
            {
                await TrackErrorAsync(functionType, selectedModel, prompt, stopwatch.ElapsedMilliseconds, options, ex.Message);
            }
            // A pinned/no-fallback route that was rate-limited or timed out is
            // still evidence about the PROVIDER: record the cooldown so the next
            // call (from any function) skips it, regardless of whether this
            // request was allowed to fall back. (Same precedence as the tool-use
            // path: a rate-limited HttpRequestException is recorded once.)
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

    private async Task<string?> TryRetryJsonContractAsync(
        string functionType,
        string prompt,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options,
        string requestId,
        string qualityFailureReason,
        LlmResponse? failedResponse,
        bool isFallback)
    {
        var isJsonContractRetry = options?.ForceJson == true
            && string.Equals(qualityFailureReason, "invalid_json_response", StringComparison.OrdinalIgnoreCase);
        var isThinkingChannelRetry = string.Equals(
            qualityFailureReason,
            "thinking_channel_without_content",
            StringComparison.OrdinalIgnoreCase);
        var isEmptyResponseRetry = IsEmptyResponseQualityFailure(qualityFailureReason);

        if (!isJsonContractRetry && !isThinkingChannelRetry && !isEmptyResponseRetry)
        {
            return null;
        }

        // The adapter knows which settings actually reached the provider. Do
        // not replay a failed suppression repair just because the router did
        // not itself issue the previous physical request.
        if (isThinkingChannelRetry && failedResponse?.HiddenReasoningSuppressionRequested == true)
        {
            return null;
        }

        // A metered MessagePipeline admission is single-use. A malformed or
        // truncated response must return through the ordinary failure path so
        // the host can obtain a fresh admission; never spend it a second time
        // inside the router's quality retry.
        if (options?.RouteAdmissionReceipt?.MeteredReservationId is not null)
        {
            return null;
        }

        var executionSettings = BuildExecutionSettings(options, selectedModel);
        if (isJsonContractRetry || isEmptyResponseRetry || options?.ForceJson == true)
        {
            executionSettings["temperature"] = 0;
            if (options?.ForceJson == true
                && OllamaProviderFamily.IsOllamaFamily(selectedModel.Provider))
            {
                executionSettings["format"] = "json";
            }
        }
        if (options?.SuppressOutputTokenLimit != true
            && isJsonContractRetry
            && StoppedAtMaxTokens(failedResponse))
        {
            IncreaseJsonRetryTokenBudget(executionSettings, failedResponse);
        }

        if (isThinkingChannelRetry || isEmptyResponseRetry || options?.ForceJson == true)
        {
            ApplyHiddenReasoningRecoverySettings(executionSettings);
        }
        if (isEmptyResponseRetry && options?.SuppressOutputTokenLimit != true)
        {
            EnsureRecoveryTokenBudget(executionSettings);
        }

        var provider = _providerRegistry.GetProvider(selectedModel.Provider);
        if (provider is null)
        {
            return null;
        }

        var retryKind = ResolveQualityRetryKind(qualityFailureReason, options);
        var retryRequestId = $"{requestId}-{ResolveQualityRetrySuffix(qualityFailureReason, options)}";
        var retryPrompt = isJsonContractRetry
                          && !StoppedAtMaxTokens(failedResponse)
                          && string.Equals(selectedModel.Provider, "GenieX", StringComparison.OrdinalIgnoreCase)
            ? BuildGenieXJsonRecoveryPrompt(prompt)
            : prompt;
        _debugLogger.LogRequest(new LlmDebugRequest(
            RequestId: retryRequestId,
            FunctionType: functionType,
            Provider: selectedModel.Provider,
            Model: selectedModel.Model,
            Prompt: retryPrompt,
            Temperature: 0,
            MaxTokens: TryGetMaxTokens(executionSettings),
            IsFallback: isFallback,
            FallbackReason: retryKind));

        var retryStopwatch = Stopwatch.StartNew();
        try
        {
            // The original call still owns the local runtime slot when this retry is invoked.
            // Re-entering the slot here would deadlock serialized local providers such as Ollama.
            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                options,
                fallbackUsed: isFallback, physicalSettings: executionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            var retry = await provider.GenerateResponseAsync(retryPrompt, selectedModel.Model, executionSettings);
            retryStopwatch.Stop();

            if (!retry.WasSuccessful)
            {
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    retryPrompt,
                    retryStopwatch.ElapsedMilliseconds,
                    options,
                    retry.ErrorMessage ?? $"{retryKind}_failed",
                    fallbackUsed: isFallback).ConfigureAwait(false);
                _logger.LogWarning(
                    "{RetryKind} failed for {Provider}/{Model} on {FunctionType}: {Error}",
                    retryKind,
                    selectedModel.Provider,
                    selectedModel.Model,
                    functionType,
                    retry.ErrorMessage ?? "unknown error");
                return null;
            }

            var retryQualityIssue = ValidateResponseQuality(retry, options, out var normalizedContent);
            if (retryQualityIssue is not null)
            {
                await TrackQualityFailureAsync(
                    functionType,
                    selectedModel,
                    retryPrompt,
                    retry,
                    retryStopwatch.ElapsedMilliseconds,
                    options,
                    retryQualityIssue,
                    fallbackUsed: isFallback).ConfigureAwait(false);
                _logger.LogWarning(
                    "{RetryKind} still failed quality gate for {Provider}/{Model} on {FunctionType}: {Reason}. Content length: {Length}",
                    retryKind,
                    selectedModel.Provider,
                    selectedModel.Model,
                    functionType,
                    retryQualityIssue,
                    retry.Content?.Length ?? 0);
                if (IsTerminalCallerContractFailure(retryQualityIssue, options))
                {
                    throw new LlmResponseContractException(
                        functionType,
                        selectedModel.Provider,
                        selectedModel.Model);
                }
                return null;
            }
            if (normalizedContent is not null)
            {
                retry.Content = normalizedContent;
            }

            _logger.LogWarning(
                "{RetryKind} recovered {FunctionType} for {Provider}/{Model}",
                retryKind,
                functionType,
                selectedModel.Provider,
                selectedModel.Model);

            _debugLogger.LogResponse(new LlmDebugResponse(
                RequestId: retryRequestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                Response: retry.Content,
                InputTokens: retry.InputTokens,
                OutputTokens: retry.OutputTokens,
                ElapsedMs: retryStopwatch.ElapsedMilliseconds,
                IsFallback: isFallback));

            await TrackUsageAsync(functionType, selectedModel, retryPrompt, retry, retryStopwatch.ElapsedMilliseconds, options, fallbackUsed: isFallback);
            return retry.Content;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            retryStopwatch.Stop();
            await TrackErrorAsync(
                functionType,
                selectedModel,
                retryPrompt,
                retryStopwatch.ElapsedMilliseconds,
                options,
                ex.Message,
                fallbackUsed: isFallback).ConfigureAwait(false);
            _logger.LogWarning(
                ex,
                "{RetryKind} threw for {Provider}/{Model} on {FunctionType}",
                retryKind,
                selectedModel.Provider,
                selectedModel.Model,
                functionType);
            return null;
        }
    }

    private async Task<string?> TryRetryVisionThinkingChannelAsync(
        string functionType,
        string prompt,
        string imageBase64,
        LlmModelSelection selectedModel,
        LlmRouterOptions? options,
        string requestId,
        string qualityFailureReason,
        LlmResponse failedResponse,
        bool isFallback)
    {
        if (failedResponse.HiddenReasoningSuppressionRequested
            || !string.Equals(qualityFailureReason, "thinking_channel_without_content", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var provider = _providerRegistry.GetProvider(selectedModel.Provider);
        if (provider is null)
        {
            return null;
        }

        var executionSettings = BuildExecutionSettings(options, selectedModel);
        ApplyHiddenReasoningRecoverySettings(executionSettings);

        var retryRequestId = $"{requestId}-think";
        _debugLogger.LogRequest(new LlmDebugRequest(
            RequestId: retryRequestId,
            FunctionType: functionType,
            Provider: selectedModel.Provider,
            Model: selectedModel.Model,
            Prompt: prompt,
            ImageBase64Summary: !string.IsNullOrEmpty(imageBase64) ? $"Base64 image ({imageBase64.Length} chars)" : null,
            Temperature: options?.Temperature,
            MaxTokens: options?.MaxTokens,
            IsFallback: isFallback,
            FallbackReason: "thinking_channel_retry"));

        var retryStopwatch = Stopwatch.StartNew();
        try
        {
            await AppendExecutionStartedAuditAsync(
                functionType,
                selectedModel,
                options,
                fallbackUsed: isFallback, physicalSettings: executionSettings).ConfigureAwait(false);
            await NotifyProviderExecutionStartingAsync(options).ConfigureAwait(false);
            var retry = await provider.GenerateVisionResponseAsync(prompt, imageBase64, selectedModel.Model, executionSettings);
            retryStopwatch.Stop();

            if (!retry.WasSuccessful)
            {
                await TrackErrorAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    retryStopwatch.ElapsedMilliseconds,
                    options,
                    retry.ErrorMessage ?? "vision_thinking_channel_retry_failed",
                    fallbackUsed: isFallback).ConfigureAwait(false);
                return null;
            }

            var retryQualityIssue = ValidateResponseQuality(retry, options, out var normalizedContent);
            if (retryQualityIssue is not null)
            {
                await TrackQualityFailureAsync(
                    functionType,
                    selectedModel,
                    prompt,
                    retry,
                    retryStopwatch.ElapsedMilliseconds,
                    options,
                    retryQualityIssue,
                    fallbackUsed: isFallback,
                    requiresVision: true).ConfigureAwait(false);
                _logger.LogWarning(
                    "Vision thinking-channel retry still failed quality gate for {Provider}/{Model} on {FunctionType}: {Reason}",
                    selectedModel.Provider,
                    selectedModel.Model,
                    functionType,
                    retryQualityIssue);
                if (IsTerminalCallerContractFailure(retryQualityIssue, options))
                {
                    throw new LlmResponseContractException(
                        functionType,
                        selectedModel.Provider,
                        selectedModel.Model);
                }
                return null;
            }
            if (normalizedContent is not null)
            {
                retry.Content = normalizedContent;
            }

            _logger.LogWarning(
                "Vision thinking-channel retry recovered {FunctionType} for {Provider}/{Model}",
                functionType,
                selectedModel.Provider,
                selectedModel.Model);

            _debugLogger.LogResponse(new LlmDebugResponse(
                RequestId: retryRequestId,
                FunctionType: functionType,
                Provider: selectedModel.Provider,
                Model: selectedModel.Model,
                Response: retry.Content,
                InputTokens: retry.InputTokens,
                OutputTokens: retry.OutputTokens,
                ElapsedMs: retryStopwatch.ElapsedMilliseconds,
                IsFallback: isFallback));

            await TrackUsageAsync(
                functionType,
                selectedModel,
                prompt,
                retry,
                retryStopwatch.ElapsedMilliseconds,
                options,
                fallbackUsed: isFallback,
                requiresVision: true);
            return retry.Content;
        }
        catch (RoutingAuditPersistenceException)
        {
            throw;
        }
        catch (LlmResponseContractException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
        {
            retryStopwatch.Stop();
            await TrackErrorAsync(
                functionType,
                selectedModel,
                prompt,
                retryStopwatch.ElapsedMilliseconds,
                options,
                ex.Message,
                fallbackUsed: isFallback).ConfigureAwait(false);
            _logger.LogWarning(ex,
                "Vision thinking-channel retry failed for {FunctionType} on {Provider}/{Model}",
                functionType,
                selectedModel.Provider,
                selectedModel.Model);
            return null;
        }
    }

    /// <summary>
    /// Shared options for deserializing LLM JSON responses. Configured for resilience
    /// against common LLM output quirks (case variations, numbers as strings,
    /// objects/arrays where strings are expected, inline comments).
    /// </summary>
    private static readonly JsonSerializerOptions LlmJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new LlmStringCoercionConverter() },
    };

    /// <summary>
    /// Converts any JSON value to a string. LLMs sometimes return structured objects,
    /// arrays, numbers, or booleans where a plain string is expected.
    /// </summary>
    private sealed class LlmStringCoercionConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.ToString();
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Maximum number of physical provider calls one typed-JSON request may
    /// spend across the primary call (and its internal failover), the typed
    /// retry, and the self-heal repair. Every call is paid work; a request
    /// that cannot produce parseable JSON in three tries will not on the sixth.
    /// </summary>
    internal const int MaxTypedJsonPhysicalCalls = 3;

    /// <summary>
    /// Per-request bookkeeping for the typed-JSON boundary: which physical
    /// route produced the (unparseable) answer and how many provider calls
    /// have been spent. Flows through the string overload via AsyncLocal so
    /// the typed path learns the answering route even when the string path's
    /// internal failover, not the selected primary, produced the reply.
    /// </summary>
    private sealed class TypedJsonAttemptScope
    {
        public LlmModelSelection? InitialSelection;
        public string? AnsweringProvider;
        public string? AnsweringModel;
        public int PhysicalCalls;
    }

    private static readonly AsyncLocal<TypedJsonAttemptScope?> TypedJsonScope = new();

    private static void RecordTypedJsonPhysicalCall(bool succeeded, LlmModelSelection route)
    {
        if (TypedJsonScope.Value is not { } scope)
        {
            return;
        }

        scope.PhysicalCalls++;
        if (succeeded)
        {
            scope.AnsweringProvider = route.Provider;
            scope.AnsweringModel = route.Model;
        }
    }

    public async Task<T> GenerateResponseAsync<T>(string functionType, string prompt, LlmRouterOptions? options = null)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        var jsonOptions = EnsureJsonContractOptions<T>(options);
        var scope = new TypedJsonAttemptScope();
        var previousScope = TypedJsonScope.Value;
        TypedJsonScope.Value = scope;
        try
        {
            return await GenerateTypedJsonResponseCoreAsync<T>(functionType, prompt, jsonOptions, scope)
                .ConfigureAwait(false);
        }
        finally
        {
            TypedJsonScope.Value = previousScope;
        }
    }

    private async Task<T> GenerateTypedJsonResponseCoreAsync<T>(
        string functionType,
        string prompt,
        LlmRouterOptions jsonOptions,
        TypedJsonAttemptScope scope)
    {
        var response = await GenerateResponseAsync(functionType, prompt, jsonOptions);
        var primaryRaw = response;

        try
        {
            return DeserializeJsonResponse<T>(response);
        }
        catch (JsonException ex)
        {
            // Exact/no-fallback routes (notably the metered MessagePipeline path)
            // carry a single-use admission receipt. Do not select again merely to
            // format this error: re-selection would replay and consume that receipt
            // after the one authorized provider call has already settled it.
            if (jsonOptions.DisableFallback)
            {
                throw new InvalidOperationException(
                    $"Fallback disabled for typed JSON route on {functionType}; selected route returned invalid JSON.",
                    ex);
            }

            // If the primary model returned non-JSON (or malformed JSON), resolve
            // an independently admitted fallback. Automatic routes need not have
            // carried an optional precomputed fallback into this parse boundary.
            var cleanedPrompt = CleanHtmlFromPrompt(prompt);
            var selection = scope.InitialSelection
                ?? throw new ModelScoutAdmissionException("The typed response has no physical selection receipt.");

            var (fallbackProvider, fallbackModel) = await ResolveFallbackRouteAsync(
                functionType,
                selection,
                cleanedPrompt,
                requiresVision: false,
                reason: "typed_json_parse",
                options: jsonOptions);

            // The route that just answered with unusable JSON is excluded from
            // the retry: re-sending the identical prompt to it is a paid call
            // with no new information. The string path's internal failover may
            // have made the configured fallback the answering route, in which
            // case the re-selected primary (if it is a different physical route
            // and not cooling down) is the only untried candidate.
            var answeringProvider = scope.AnsweringProvider;
            var answeringModel = scope.AnsweringModel;
            var answeringRouteKnown = !string.IsNullOrWhiteSpace(answeringProvider)
                                      && !string.IsNullOrWhiteSpace(answeringModel);
            var retryRouteIsAnsweringRoute = answeringRouteKnown
                && IsSameRoute(fallbackProvider, fallbackModel, answeringProvider!, answeringModel!);
            if (retryRouteIsAnsweringRoute)
            {
                var primaryIsUntried = !IsSameRoute(selection.Provider, selection.Model, answeringProvider!, answeringModel!)
                                       && _cooldownTracker?.IsAvailable(selection.Provider) != false;
                if (primaryIsUntried)
                {
                    (fallbackProvider, fallbackModel) = (selection.Provider, selection.Model);
                }
            }

            var retryRouteExcluded = answeringRouteKnown
                && IsSameRoute(fallbackProvider, fallbackModel, answeringProvider!, answeringModel!);
            var attemptBudgetExhausted = scope.PhysicalCalls >= MaxTypedJsonPhysicalCalls;

            string fallbackRaw;
            Exception? fbParseEx = null;
            if (retryRouteExcluded || attemptBudgetExhausted)
            {
                fallbackRaw = string.Empty;
                fbParseEx = new InvalidOperationException(retryRouteExcluded
                    ? $"Typed JSON retry skipped: the only resolvable retry route {fallbackProvider}/{fallbackModel} is the route that just returned unusable JSON."
                    : $"Typed JSON retry skipped: {scope.PhysicalCalls} physical provider calls already spent (limit {MaxTypedJsonPhysicalCalls}).");
                _logger.LogWarning(
                    "Typed JSON retry for {FunctionType} skipped: {Reason}",
                    functionType,
                    fbParseEx.Message);
            }
            else
            {
                var fallbackOptions = CloneRouterOptions(jsonOptions);
                fallbackOptions.OverrideProvider = fallbackProvider;
                fallbackOptions.OverrideModel = fallbackModel;
                fallbackOptions.OverrideProvenance = LlmOverrideProvenance.Fallback;
                // Exactly one retry call: the string path must not bounce back to
                // the answering route through its own failover chain.
                fallbackOptions.DisableFallback = true;
                ApplyRoutePolicyToOptions(selection, fallbackOptions);

                string? fbResponse;
                try
                {
                    fbResponse = await GenerateResponseAsync(functionType, prompt, fallbackOptions);
                }
                catch (FallbackDisabledException retryEx)
                {
                    // The single-call retry hit its own quality gate; that is a
                    // reply failure on the retry route, not a reason to stop
                    // the typed boundary from attempting repair.
                    fbResponse = null;
                    fbParseEx = retryEx;
                }

                fallbackRaw = fbResponse ?? string.Empty;
                if (fbResponse is not null)
                {
                    try
                    {
                        return DeserializeJsonResponse<T>(fbResponse);
                    }
                    catch (Exception parseEx) when (parseEx is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
                    {
                        fbParseEx = parseEx;
                    }
                }
            }

            fbParseEx ??= new InvalidOperationException("Typed JSON retry produced no response.");

            string? repairError = null;
            if (scope.PhysicalCalls < MaxTypedJsonPhysicalCalls)
            {
                var (healed, repairedValue, repairedRoute, healError) =
                    await TryTypedJsonSelfHealAsync<T>(
                        functionType,
                        prompt,
                        jsonOptions,
                        selection,
                        primaryRaw,
                        fallbackRaw,
                        ex,
                        fbParseEx);
                repairError = healError;

                if (healed && repairedValue is not null)
                {
                    await TryPromoteTypedJsonRepairPreferenceAsync(
                        functionType,
                        selection,
                        repairedRoute,
                        jsonOptions);
                    return repairedValue;
                }
            }
            else
            {
                repairError = $"JSON self-heal skipped: {scope.PhysicalCalls} physical provider calls already spent (limit {MaxTypedJsonPhysicalCalls}).";
            }

            // Both failures are known exactly: each provider answered and
            // its reply was unusable JSON — MalformedReply by the
            // taxonomy (evidence against the reply, not the provider).
            throw new LlmFailoverException(
                functionType: functionType,
                promptId: "typed-json",
                primary: selection,
                fallback: new LlmModelSelection { Provider = fallbackProvider, Model = fallbackModel, IsDefault = false },
                primaryError: $"Primary JSON parse failed: {ex.Message}",
                fallbackError: $"Fallback JSON parse failed: {fbParseEx.Message}" +
                               (string.IsNullOrWhiteSpace(repairError) ? "" : $" | JSON self-heal failed: {repairError}"),
                primaryException: ex,
                fallbackException: fbParseEx,
                primaryCategory: LlmFailureCategory.MalformedReply,
                fallbackCategory: LlmFailureCategory.MalformedReply);
        }
    }

    public async Task<T> GenerateVisionResponseAsync<T>(string functionType, string prompt, string imageBase64,
        LlmRouterOptions? options = null, object? payloadMetadata = null)
    {
        var previousScope = TypedJsonScope.Value;
        var scope = new TypedJsonAttemptScope();
        TypedJsonScope.Value = scope;
        try
        {
            return await GenerateTypedVisionJsonCoreAsync<T>(functionType, prompt, imageBase64, options, payloadMetadata, scope)
                .ConfigureAwait(false);
        }
        finally { TypedJsonScope.Value = previousScope; }
    }

    private async Task<T> GenerateTypedVisionJsonCoreAsync<T>(string functionType, string prompt, string imageBase64,
        LlmRouterOptions? options, object? payloadMetadata, TypedJsonAttemptScope scope)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        var jsonOptions = EnsureJsonContractOptions<T>(options);
        var response = await GenerateVisionResponseAsync(functionType, prompt, imageBase64, jsonOptions, payloadMetadata);
        try
        {
            if (JsonRepair.TryDeserialize<T>(response, LlmJsonOptions, out var parsed, out var issue)
                && parsed is not null)
            {
                return parsed;
            }

            throw new JsonException(issue);
        }
        catch (JsonException ex)
        {
            // See the text typed-contract path above: a disabled fallback is an
            // exact-route constraint, and its admission receipt must never be
            // replayed just to rediscover the already executed route.
            if (jsonOptions.DisableFallback)
            {
                throw new InvalidOperationException(
                    $"Vision fallback disabled for typed JSON route on {functionType}; selected route returned invalid JSON.",
                    ex);
            }

            var selection = scope.InitialSelection
                ?? throw new ModelScoutAdmissionException("The typed vision response has no physical selection receipt.");

            var (fallbackProvider, fallbackModel) = await ResolveFallbackRouteAsync(
                functionType,
                selection,
                prompt,
                requiresVision: true,
                reason: "vision_typed_json_parse",
                options: jsonOptions);

            var fallbackOptions = CloneRouterOptions(jsonOptions);
            fallbackOptions.OverrideProvider = fallbackProvider;
            fallbackOptions.OverrideModel = fallbackModel;
            fallbackOptions.OverrideProvenance = LlmOverrideProvenance.Fallback;
            ApplyRoutePolicyToOptions(selection, fallbackOptions);

            var fbResponse = await GenerateVisionResponseAsync(functionType, prompt, imageBase64, fallbackOptions, payloadMetadata);
            try
            {
                if (JsonRepair.TryDeserialize<T>(fbResponse, LlmJsonOptions, out var parsed, out var issue)
                    && parsed is not null)
                {
                    return parsed;
                }

                throw new JsonException(issue);
            }
            catch (Exception fbParseEx) when (fbParseEx is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                // Both failures are known exactly: each provider answered and
                // its reply was unusable JSON — MalformedReply by the
                // taxonomy (evidence against the reply, not the provider).
                throw new LlmFailoverException(
                    functionType: functionType,
                    promptId: "vision-typed-json",
                    primary: selection,
                    fallback: new LlmModelSelection { Provider = fallbackProvider, Model = fallbackModel, IsDefault = false },
                    primaryError: $"Primary JSON parse failed: {ex.Message}",
                    fallbackError: $"Fallback JSON parse failed: {fbParseEx.Message}",
                    primaryException: ex,
                    fallbackException: fbParseEx,
                    primaryCategory: LlmFailureCategory.MalformedReply,
                    fallbackCategory: LlmFailureCategory.MalformedReply);
            }
        }
    }

    private async Task<(bool Success, T? Value, LlmModelSelection? Route, string? Error)> TryTypedJsonSelfHealAsync<T>(
        string functionType,
        string prompt,
        LlmRouterOptions? options,
        LlmModelSelection primarySelection,
        string primaryRaw,
        string fallbackRaw,
        Exception primaryParseException,
        Exception fallbackParseException)
    {
        var repairPrompt = BuildTypedJsonRepairPrompt(
            prompt,
            primaryRaw,
            fallbackRaw,
            primaryParseException.Message,
            fallbackParseException.Message);
        if (_modelScout is null)
        {
            return (false, default, null,
                $"No current function-scoped Model Scout proof is available for typed JSON recovery on {functionType}.");
        }

        var context = primarySelection.Context ?? FunctionTypeProfiles.Resolve(
            functionType,
            options?.Interactivity ?? LlmInteractivity.Unspecified,
            options?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
            options?.Urgency ?? LlmUrgency.Unspecified,
            options?.Workload ?? LlmWorkloadKind.Unspecified);
        var provenRepairRoute = await TryGetModelScoutDefaultAsync(
            functionType,
            repairPrompt,
            context,
            requiresVision: false,
            options,
            policySource: primarySelection).ConfigureAwait(false);
        if (provenRepairRoute is null
            || string.IsNullOrWhiteSpace(provenRepairRoute.AuditCorrelationId))
        {
            return (false, default, null,
                $"Model Scout could not produce a current auditable typed JSON recovery route for {functionType}.");
        }

        var candidates = new List<LlmModelSelection>
        {
            CarryRoutePolicy(primarySelection, new LlmModelSelection
            {
                Provider = provenRepairRoute.Provider,
                Model = provenRepairRoute.Model,
                IsDefault = false,
                Context = provenRepairRoute.Context,
                AuditCorrelationId = provenRepairRoute.AuditCorrelationId,
                SupportsToolUse = provenRepairRoute.SupportsToolUse,
                SupportsStructuredOutput = provenRepairRoute.SupportsStructuredOutput,
                MaxContextTokens = provenRepairRoute.MaxContextTokens,
                CapabilityEvidenceSource = provenRepairRoute.CapabilityEvidenceSource,
            })
        };
        if (options?.DisableFallback != true
            && !string.IsNullOrWhiteSpace(provenRepairRoute.FallbackProvider)
            && !string.IsNullOrWhiteSpace(provenRepairRoute.FallbackModel))
        {
            candidates.Add(CarryRoutePolicy(primarySelection, new LlmModelSelection
            {
                Provider = provenRepairRoute.FallbackProvider,
                Model = provenRepairRoute.FallbackModel,
                IsDefault = false,
                Context = provenRepairRoute.Context,
                AuditCorrelationId = provenRepairRoute.FallbackAuditCorrelationId
                    ?? provenRepairRoute.AuditCorrelationId,
                SupportsToolUse = provenRepairRoute.FallbackSupportsToolUse,
                SupportsStructuredOutput = provenRepairRoute.FallbackSupportsStructuredOutput,
                MaxContextTokens = provenRepairRoute.FallbackMaxContextTokens,
                CapabilityEvidenceSource = provenRepairRoute.FallbackCapabilityEvidenceSource,
            }, promoteConfiguredFallback: true));
        }

        candidates = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Provider) && !string.IsNullOrWhiteSpace(c.Model))
            .DistinctBy(c => $"{c.Provider}:{c.Model}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                var repairOptions = CloneForJsonRepair(options, candidate);
                var repairedText = await GenerateResponseAsync(functionType, repairPrompt, repairOptions);
                if (!JsonRepair.TryDeserialize<T>(repairedText, LlmJsonOptions, out var repaired, out var issue)
                    || repaired is null)
                {
                    errors.Add($"{candidate.Provider}/{candidate.Model}: {issue}");
                    continue;
                }

                _logger.LogWarning(
                    "🩹 Typed JSON self-heal succeeded for {FunctionType} using {Provider}/{Model}",
                    functionType, candidate.Provider, candidate.Model);

                return (true, repaired, candidate, null);
            }
            catch (Exception healEx) when (healEx is not SwarmCore.Abstractions.Budgets.MissionBudgetExceededException)
            {
                errors.Add($"{candidate.Provider}/{candidate.Model}: {healEx.Message}");
            }
        }

        return (false, default, null, string.Join(" || ", errors));
    }

    private async Task TryPromoteTypedJsonRepairPreferenceAsync(
        string functionType,
        LlmModelSelection originalSelection,
        LlmModelSelection? repairedRoute,
        LlmRouterOptions? options)
    {
        if (repairedRoute is null
            || string.IsNullOrWhiteSpace(repairedRoute.Provider)
            || string.IsNullOrWhiteSpace(repairedRoute.Model)
            || string.IsNullOrWhiteSpace(repairedRoute.AuditCorrelationId)
            || string.IsNullOrWhiteSpace(repairedRoute.CapabilityEvidenceSource))
        {
            return;
        }

        if (options?.DisableFallback == true)
        {
            return;
        }

        if (originalSelection.IsUserPinned
            || options?.OverrideProvenance is
                LlmOverrideProvenance.User or
                LlmOverrideProvenance.Profile)
        {
            return;
        }

        if (IsExactRouteIdentity(
                repairedRoute.Provider,
                repairedRoute.Model,
                originalSelection.Provider,
                originalSelection.Model))
        {
            return;
        }

        var current = await _settingsProvider.GetFunctionPreferenceAsync(functionType);
        if (current is null
            || !current.IsEnabled
            || !IsExactRouteIdentity(
                current.PreferredProvider,
                current.PreferredModel,
                originalSelection.Provider,
                originalSelection.Model))
        {
            return;
        }

        var updated = new LlmFunctionPreference
        {
            Id = current.Id,
            FunctionType = functionType,
            PreferredProvider = repairedRoute.Provider,
            PreferredModel = repairedRoute.Model,
            FallbackProvider = null,
            FallbackModel = null,
            IsEnabled = current.IsEnabled,
            RequiresVision = current.RequiresVision,
            CreatedAt = current.CreatedAt,
            CreatedBy = current.CreatedBy,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "llm-router:typed-json-self-heal",
            AuditCorrelationId = repairedRoute.AuditCorrelationId,
            KnownGoodProvider = current.KnownGoodProvider,
            KnownGoodModel = current.KnownGoodModel,
            KnownGoodConfirmedAt = current.KnownGoodConfirmedAt,
        };

        var write = await _settingsProvider.TrySetFunctionPreferenceAsync(
            updated,
            FunctionPreferenceWriteAuthority.RespectUserPin,
            FunctionPreferenceVersion.Capture(functionType, current));
        if (!write.Applied)
        {
            _logger.LogInformation(
                "Skipped typed-JSON self-heal persistence for {FunctionType}: conditional write returned {Status}",
                functionType,
                write.Status);
            return;
        }

        _logger.LogWarning(
            "🔧 Promoted typed-JSON self-heal route for {FunctionType}: {OldProvider}/{OldModel} -> {NewProvider}/{NewModel}",
            functionType,
            current.PreferredProvider,
            current.PreferredModel,
            repairedRoute.Provider,
            repairedRoute.Model);
    }

    private static LlmRouterOptions CloneForJsonRepair(LlmRouterOptions? source, LlmModelSelection candidate)
    {
        var clone = CloneRouterOptions(source);
        clone.OverrideProvider = candidate.Provider;
        clone.OverrideModel = candidate.Model;
        clone.OverrideProvenance = LlmOverrideProvenance.SelfHeal;
        ApplyRoutePolicyToOptions(candidate, clone);
        // Candidates are enumerated from one persisted Scout decision. Execute
        // each candidate exactly (after the exact-override admission gate proves
        // it again) instead of allowing nested provider-default discovery.
        clone.DisableFallback = true;
        clone.Temperature = 0;
        clone.ExpectedFormat = "json";
        clone.ForceJson = true;
        return clone;
    }

    private static void ApplyRoutePolicyToOptions(
        LlmModelSelection selection,
        LlmRouterOptions options)
    {
        options.MinimumCapabilityScore = selection.MinimumCapabilityScore;
        options.RequireMeasuredCapabilityScore = selection.RequireMeasuredCapabilityScore;
        options.RequiredTier = selection.RequiredTier;
        options.BlockWeakerPolicyFallback = selection.BlockWeakerPolicyFallback;
        options.RequireCurrentFunctionEvidence = selection.RequireCurrentFunctionEvidence;
    }

    private static LlmRouterOptions EnsureJsonContractOptions(LlmRouterOptions? source)
    {
        var clone = CloneRouterOptions(source);
        clone.ForceJson = true;
        clone.RequiresStructuredOutput = true;
        clone.ExpectedFormat = string.IsNullOrWhiteSpace(clone.ExpectedFormat) ? "json_object" : clone.ExpectedFormat;
        return clone;
    }

    private static LlmRouterOptions EnsureJsonContractOptions<T>(LlmRouterOptions? source)
    {
        var clone = EnsureJsonContractOptions(source);
        if (string.IsNullOrWhiteSpace(source?.ExpectedFormat) && IsJsonArrayType(typeof(T)))
        {
            clone.ExpectedFormat = "json_array";
        }
        return clone;
    }

    /// <summary>
    /// Privacy-safe shape diagnostics for a response rejected by the quality
    /// gate. Model output is untrusted and may echo prompts, private records, or
    /// credentials, so ordinary logs must never contain even a bounded sample.
    /// The structural signals are enough to distinguish a non-JSON answer, an
    /// unclosed/fenced answer, and a model that promised a later continuation.
    /// </summary>
    internal static string DescribeRejectedContent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var trimmed = content.AsSpan().Trim();
        var startsJson = !trimmed.IsEmpty && trimmed[0] is '{' or '[';
        var endsJson = !trimmed.IsEmpty && trimmed[^1] is '}' or ']';
        var fenceCount = CountOccurrences(content, "```");
        var continuation = ContainsContinuationPromise(content);

        return " Rejected content diagnostics:"
               + $" startsJson={startsJson};"
               + $" endsJson={endsJson};"
               + $" fenceCount={fenceCount};"
               + $" continuationPromise={continuation}.";
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static bool ContainsContinuationPromise(string value)
    {
        string[] markers =
        [
            "continue in the next",
            "continue with the rest",
            "continued in the next",
            "remaining functions in the next",
            "rest in the next piece",
            "to be continued",
        ];

        return markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJsonArrayType(Type type)
    {
        if (type == typeof(string)) return false;
        if (type.IsArray) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return false;
        return type.IsGenericType
               && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>
    /// Close the routing/provider deadline race at the last boundary the router
    /// owns. A selector that returns after caller cancellation must never start
    /// a costly provider call. If execution is still admitted, the once-only
    /// callback switches the caller to its provider budget; a retry or fallback
    /// observes the same callback and cannot extend that budget.
    /// </summary>
    private static async Task NotifyProviderExecutionStartingAsync(LlmRouterOptions? options)
    {
        var ct = options?.RequestCancellationToken ?? CancellationToken.None;
        ct.ThrowIfCancellationRequested();
        options?.ProviderExecutionStartCallback?.TryInvoke();
        if (PhysicalInvocationSlot.Value is { Budget: { } budget, Attempt: { Reserved: true } attempt })
        {
            await budget.Ledger.MarkDispatchedAsync(budget.ProfileId, budget.Reference, attempt.Id.ToString("N"), ct).ConfigureAwait(false);
            attempt.Dispatched = true;
        }
        // Cancellation can race the phase switch at the exact routing deadline.
        // Recheck before any provider receives the request.
        ct.ThrowIfCancellationRequested();
    }

    internal static LlmRouterOptions CloneRouterOptions(LlmRouterOptions? source)
    {
        var clone = new LlmRouterOptions
        {
            ModelRequest = source?.ModelRequest?.Snapshot(),
            StrategyId = source?.StrategyId,
            SuppressStructuredOutputRetry = source?.SuppressStructuredOutputRetry ?? false,
            ResponseContentValidator = source?.ResponseContentValidator,
            SuppressFallbackOnResponseContentValidationFailure =
                source?.SuppressFallbackOnResponseContentValidationFailure ?? false,
            RequireCurrentWorkloadSizing = source?.RequireCurrentWorkloadSizing ?? false,
            ExecutionSettings = source?.ExecutionSettings is null
                ? null
                : new Dictionary<string, object>(source.ExecutionSettings, StringComparer.OrdinalIgnoreCase),
            GenerationParameters = source?.GenerationParameters,
            RequestControl = source?.RequestControl,
            RequestCancellationToken = source?.RequestCancellationToken ?? CancellationToken.None,
            ProviderExecutionStartCallback = source?.ProviderExecutionStartCallback,
            UserId = source?.UserId,
            SessionId = source?.SessionId,
            TurnId = source?.TurnId,
            RoutingUserText = source?.RoutingUserText,
            RoutingAvailableToolIds = source?.RoutingAvailableToolIds.ToArray() ?? [],
            RoutingCompletedToolIds = source?.RoutingCompletedToolIds.ToArray() ?? [],
            UsageCompletionObserver = source?.UsageCompletionObserver,
            ForceRefresh = source?.ForceRefresh ?? false,
            OverrideProvider = source?.OverrideProvider,
            OverrideModel = source?.OverrideModel,
            OverrideProvenance = source?.OverrideProvenance ?? LlmOverrideProvenance.Unspecified,
            RouteAdmissionReceipt = source?.RouteAdmissionReceipt,
            MaxTokens = source?.MaxTokens,
            SuppressOutputTokenLimit = source?.SuppressOutputTokenLimit ?? false,
            Temperature = source?.Temperature,
            ExpectedFormat = source?.ExpectedFormat,
            SourceType = source?.SourceType,
            SourceId = source?.SourceId,
            ForceJson = source?.ForceJson ?? false,
            RequiresVision = source?.RequiresVision,
            RequiresToolUse = source?.RequiresToolUse ?? false,
            RequiresStructuredOutput = source?.RequiresStructuredOutput ?? false,
            AllowTextJsonContractRepair = source?.AllowTextJsonContractRepair ?? false,
            MinimumContextTokens = source?.MinimumContextTokens,
            DisableFallback = source?.DisableFallback ?? false,
            AllowAnyProviderFallback = source?.AllowAnyProviderFallback ?? false,
            Interactivity = source?.Interactivity ?? LlmInteractivity.Unspecified,
            Workload = source?.Workload ?? LlmWorkloadKind.Unspecified,
            TaskDemand = source?.TaskDemand,
            PrivacyClass = source?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
            Urgency = source?.Urgency ?? LlmUrgency.Unspecified,
            MinimumCapabilityScore = source?.MinimumCapabilityScore,
            RequireMeasuredCapabilityScore = source?.RequireMeasuredCapabilityScore ?? false,
            RequiredTier = source?.RequiredTier,
            BlockWeakerPolicyFallback = source?.BlockWeakerPolicyFallback ?? false,
            RequireCurrentFunctionEvidence = source?.RequireCurrentFunctionEvidence ?? false,
        };
        if (clone.ModelRequest is { } request)
        {
            var required = request.RequiredCapabilities;
            clone.RequiresVision = clone.RequiresVision == true || required.Contains(LlmRequiredCapability.Vision)
                || required.Contains(LlmRequiredCapability.NativeComputerUse);
            clone.RequiresToolUse |= required.Contains(LlmRequiredCapability.ToolCalling)
                || required.Contains(LlmRequiredCapability.NativeAsyncTools) || required.Contains(LlmRequiredCapability.NativeComputerUse);
            clone.RequiresStructuredOutput |= required.Contains(LlmRequiredCapability.StructuredOutput);
            if (required.Contains(LlmRequiredCapability.StructuredOutput)) clone.AllowTextJsonContractRepair = false;
        }
        return clone;
    }

    private static string BuildTypedJsonRepairPrompt(
        string originalPrompt,
        string primaryRaw,
        string fallbackRaw,
        string primaryParseError,
        string fallbackParseError)
    {
        return $"""
You are repairing a failed typed JSON response.
Return ONLY valid JSON. No markdown, no commentary.

Original task prompt:
{originalPrompt}

Primary model output (failed JSON parse):
{primaryRaw}

Primary parse error:
{primaryParseError}

Fallback model output (failed JSON parse):
{fallbackRaw}

Fallback parse error:
{fallbackParseError}

Requirements:
1) Produce one complete JSON document that satisfies the original task.
2) Do not truncate arrays/objects.
3) Do not include trailing prose.
4) If unsure, keep conservative defaults but preserve required structure.
""";
    }
}
