using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    // Advisors describe workload constraints. Their proposed provider/model never
    // pins an automatic request: Scout ranks the entire eligible set once.
    private async Task<LlmModelSelection?> TryGetScoutPolicySelectionAsync(
        string functionType, string prompt, LlmCallContext context,
        LlmFunctionPreference? preference, bool requiresVision,
        LlmRouterOptions? options, RoutingSelectionTrace trace)
    {
        var policy = _routingPolicyStore is null ? RoutingPolicy.Default
            : await _routingPolicyStore.GetAsync().ConfigureAwait(false);
        var pressure = _budgetPressureSource is null ? BudgetPressureSnapshot.Unconstrained
            : await SafeReadBudgetPressureAsync().ConfigureAwait(false);
        var advisorContext = new LlmRouteAdvisorContext
        {
            ConstraintsOnly = true,
            FunctionType = functionType, CallContext = context, CurrentPreference = preference,
            PromptText = prompt, UserRequestText = options?.RoutingUserText,
            AvailableToolIds = options?.RoutingAvailableToolIds ?? [],
            CompletedToolIds = options?.RoutingCompletedToolIds ?? [],
            TurnId = options?.TurnId, SessionId = options?.SessionId,
            TaskDemand = options?.TaskDemand,
            RequiresVision = requiresVision, RequiresToolUse = options?.RequiresToolUse == true,
            RequiresStructuredOutput = options?.RequiresStructuredOutput == true,
            StructuredOutputRepairAvailable = options?.AllowTextJsonContractRepair == true,
            MinimumContextTokens = options?.MinimumContextTokens,
            PromptTokenEstimate = EstimatePromptTokens(prompt),
            EstimatedOutputTokens = ResolveEstimatedOutputTokens(options),
            SystemActivity = _systemActivity?.Current() ?? SystemActivitySnapshot.Active(),
            Policy = policy, BudgetPressure = pressure,
        };
        var policies = await GetRoutingGovernorDecisionAsync(advisorContext,
            options?.RequestCancellationToken ?? CancellationToken.None).ConfigureAwait(false);
        trace.GovernorCalls.Add(new RoutingGovernorTrace(false, policies.AdvisorName,
            policies.PolicyKind, policies.Priority, RoutingPolicy.NormalizeUntrusted(policy),
            pressure, policies.Evaluations.Take(16).ToList(), CaptureGovernorCandidates(policies)));
        var constrained = CloneRouterOptions(options);
        var recommendations = policies.Candidates.Select(candidate => candidate.Recommendation).ToList();
        if (recommendations.Count == 0 && policies.Recommendation is { } recommendation)
            recommendations.Add(recommendation);
        foreach (var requirement in recommendations)
        {
            if (requirement.MinimumCapabilityScore is { } minimum)
                constrained.MinimumCapabilityScore = Math.Max(constrained.MinimumCapabilityScore ?? 0, minimum);
            if (requirement.RequiredTier is { } tier
                && (constrained.RequiredTier is null || tier > constrained.RequiredTier))
                constrained.RequiredTier = tier;
            constrained.RequireMeasuredCapabilityScore |= requirement.RequireMeasuredCapabilityScore;
            constrained.RequireCurrentFunctionEvidence |= requirement.RequireCurrentFunctionEvidence;
            constrained.BlockWeakerPolicyFallback |= requirement.BlockWeakerPolicyFallback;
            requiresVision |= requirement.RequiresVision;
        }
        // A safety floor at the frontier tier is high-stakes demand: Scout must
        // rank frontier cloud/subscription routes ahead of a local route whose
        // catalog clearance merely reaches the same number.
        if (constrained.RequiredTier == ModelScoutTier.Frontier)
        {
            constrained.TaskDemand = LlmTaskDemand.Merge(constrained.TaskDemand, new LlmTaskDemand
            {
                Complexity = LlmTaskComplexity.HighStakes,
                Signals = ["frontier-safety-floor"],
            });
        }
        var selected = await TryGetModelScoutDefaultAsync(
            functionType, prompt, context, requiresVision, constrained).ConfigureAwait(false);
        if (selected is null && constrained.BlockWeakerPolicyFallback)
            throw new LlmRouteSelectionBlockedException(
                $"Model Scout found no route satisfying the policy safety floor for {functionType}.");
        if (selected is not null && recommendations.Any(requirement => !requirement.PersistPreference
                && !requirement.AllowSuccessfulBootstrapMaterialization))
            selected.AllowPreferenceMaterialization = false;
        return selected;
    }

    private async Task<LlmModelSelection> AdmitScoutSelectionAsync(
        string functionType, string prompt, LlmModelSelection selection,
        LlmRouterOptions? options, bool requiresVision)
    {
        if (selection.ValidatedAdmissionReceipt is not null || IsScoutResearchRequest(options))
            return selection;
        if (selection.ModelScoutDecisionReceipt is not null)
        {
            ModelScoutExecutionAdmission.ValidateDecision(functionType, selection);
            return selection;
        }

        var context = selection.Context ?? FunctionTypeProfiles.Resolve(functionType,
            LlmInteractivity.Unspecified, LlmPrivacyClass.Unspecified, LlmUrgency.Unspecified);
        var exact = selection.IsUserPinned
            || options?.OverrideProvenance is LlmOverrideProvenance.User or LlmOverrideProvenance.Profile
            || !await IsAutomaticModelScoutSelectionEnabledAsync().ConfigureAwait(false);
        if (exact)
        {
            // The selector has already applied the explicit pin, privacy,
            // registered-provider and capability constraints. Scout admits this
            // exact result without a catalog refresh or an optimization request.
            ModelScoutExecutionAdmission.AdmitExactConstraint(functionType, selection,
                selection.IsUserPinned || options?.OverrideProvenance is LlmOverrideProvenance.User or LlmOverrideProvenance.Profile
                    ? "exact-user" : "manual");
            return selection;
        }
        var admitted = await TryGetModelScoutDefaultAsync(functionType, prompt, context, requiresVision, options)
            .ConfigureAwait(false);
        if (admitted is null)
            throw new ModelScoutAdmissionException($"Model Scout could not admit {functionType}.");
        admitted.IsUserPinned = selection.IsUserPinned;
        return admitted;
    }

    private static bool IsScoutResearchRequest(LlmRouterOptions? options)
        => options?.OverrideProvenance is LlmOverrideProvenance.Calibration or LlmOverrideProvenance.Benchmark
            && options.DisableFallback
            && !string.IsNullOrWhiteSpace(options.OverrideProvider)
            && !string.IsNullOrWhiteSpace(options.OverrideModel);

    // All router physical inference paths join here, including retry, fallback,
    // healing, tools, vision, streaming and embedding batches. No provider work
    // may start on the strength of a saved default or opaque correlation alone.
    private async Task<string> AdmitScoutExecutionAsync(
        string functionType, LlmModelSelection selection, LlmRouterOptions? options)
    {
        ValidateModelRequestRoute(functionType, options, selection.Provider, selection.Model);
        if (options?.ModelRequest is { } modelRequest
            && await GetRouteRequirementIssueAsync(selection, options.RequiresVision == true, options, verifyAvailability: false)
                .ConfigureAwait(false) is { } requestIssue)
            throw new LlmModelRequestUnavailableException(functionType, modelRequest,
                [new(selection.Provider, selection.Model, requestIssue)]);
        if (selection.ValidatedAdmissionReceipt is { } receipt)
        {
            // Selection already validated and consumed the workload's admission
            // (including a single-use metered reservation). Execution checks its
            // exact binding and expiry; it must not consume that reservation twice.
            if (!SameScoutRoute(functionType, receipt.FunctionType)
                || !SameScoutRoute(selection.Provider, receipt.Provider)
                || !SameScoutRoute(selection.Model, receipt.Model)
                || receipt.ExpiresAtUtc <= DateTime.UtcNow)
                throw new ModelScoutAdmissionException("Scout workload admission changed before execution.");
            return "workload-admission";
        }
        if (functionType is FunctionTypes.TextEmbedding or FunctionTypes.ImageEmbedding)
        {
            var current = await SelectEmbeddingModelAsync(functionType, options).ConfigureAwait(false);
            if (!SameScoutRoute(selection.Provider, current.Provider)
                || !SameScoutRoute(selection.Model, current.Model)
                || selection.EmbeddingIdentity is null || selection.EmbeddingIdentity != current.EmbeddingIdentity)
                throw new ModelScoutAdmissionException("The embedding generation changed before execution.");
            return "embedding-generation";
        }
        if (IsScoutResearchRequest(options))
        {
            if (!SameScoutRoute(selection.Provider, options!.OverrideProvider)
                || !SameScoutRoute(selection.Model, options.OverrideModel))
                throw new ModelScoutAdmissionException("Research admission cannot substitute its exact candidate.");
            return "exact-research";
        }
        ModelScoutExecutionAdmission.ValidateDecision(functionType, selection);
        return selection.ModelScoutConstraintAdmission?.Kind ?? "scout-decision";
    }

    private static bool SameScoutRoute(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void ValidateModelRequestRoute(string functionType, LlmRouterOptions? options, string provider, string model)
    {
        if (options?.ModelRequest is { } request
            && ModelScoutModelRequests.Rejection(request, provider, model, true, true, true) is { } issue)
            throw new LlmModelRequestUnavailableException(functionType, request, [new(provider, model, issue)]);
    }
}
