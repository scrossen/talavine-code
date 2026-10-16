using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Models;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Work-local adaptation delegates every route selection to the canonical router.
/// It never writes function preferences or grants provider/spend authority.
/// Hosts share one coordinator across agent turns while the router is scoped, so each
/// observation resolves the router from its own scope instead of capturing one.</summary>
public sealed class AdaptiveExecutionCoordinator(IAdaptiveExecutionStore store, IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<AdaptiveReasoningOptions> options, TimeProvider time)
{
    public double? LatencyTargetMs => options.Value.LatencyTargetMs;

    public async Task<AdaptiveExecutionSnapshot> LoadAsync(AdaptiveWorkIdentity identity, AdaptiveWorkAuthority authority, CancellationToken ct)
    {
        var loaded = await store.LoadAsync(identity, ct).ConfigureAwait(false);
        if (loaded.ReconstructionRequired)
        {
            await store.DeleteAsync(identity, ct).ConfigureAwait(false);
            loaded = await store.LoadAsync(identity, ct).ConfigureAwait(false);
        }
        var state = loaded.Snapshot ?? new AdaptiveExecutionSnapshot
        {
            Identity = identity, Authority = authority, Generation = loaded.Generation,
            PolicyState = new() { WorkId = identity.WorkId }, UpdatedAtUtc = time.GetUtcNow()
        };
        if (state.Authority.Digest != authority.Digest)
        {
            var superseded = state.Decisions.Where(d => d.Outcome == AdaptiveDecisionOutcome.Proposed
                && !state.Decisions.Any(t => t.Decision == d.Decision && t.Outcome != AdaptiveDecisionOutcome.Proposed))
                .Select(d => d with { Id = Guid.NewGuid(), AtUtc = time.GetUtcNow(), Outcome = AdaptiveDecisionOutcome.Superseded,
                    OutcomeReason = "work-authority-changed" }).ToArray();
            state = await SaveAsync(state with { Authority = authority, CurrentRoute = null,
                Decisions = [..state.Decisions, ..superseded],
                PolicyState = state.PolicyState with { ConsecutiveFailures = 0, ConsecutiveSuccesses = 0, ConsecutiveRetryFailures = 0 } }, ct).ConfigureAwait(false);
        }
        return state with { Authority = authority };
    }

    public async Task<AdaptiveExecutionSnapshot> ObserveAsync(AdaptiveExecutionSnapshot state,
        AdaptiveReasoningObservation observation, AdaptiveRouteState? actualRoute, LlmRouterOptions requestOptions,
        string selectionPrompt, string? budgetReference, CancellationToken ct)
    {
        if (state.Decisions.Any(d => d.Decision.EvidenceId == observation.EvidenceId)) return state;
        observation = observation with { WorkId = state.Identity.WorkId, Sequence = state.PolicyState.LastSequence + 1 };
        var adequate = observation.Evidence is AdaptiveEvidence.AdmittedComplete or AdaptiveEvidence.NotRequired;
        var wantsChange = adequate && observation.Failure is not (AdaptiveFailure.Authentication or AdaptiveFailure.Configuration or AdaptiveFailure.Permission or AdaptiveFailure.QuotaExhausted)
            && (observation.RequiredCapabilityMissing || observation.Failure is AdaptiveFailure.Acceptance or AdaptiveFailure.Synthesis or AdaptiveFailure.MalformedOutput or AdaptiveFailure.ToolProtocol
            || observation.NoProgressObserved || (observation.AcceptancePassed || observation.VerifiedStepCompleted)
                && (observation.BudgetPressure || observation.LatencyAboveTarget));
        // Most observations never select a route, so the router is resolved on first use,
        // before each admission catch: a composition error must surface rather than read
        // as a blocked route.
        await using var routing = scopes.CreateAsyncScope();
        ILlmRouter? scopedRouter = null;
        ILlmRouter Router() => scopedRouter ??= routing.ServiceProvider.GetRequiredService<ILlmRouter>();
        LlmModelSelection? current = null;
        string? blocked = null;
        if (wantsChange && actualRoute is not null)
        {
            var router = Router();
            try
            {
                current = await router.SelectOptimalModelAsync(state.Authority.FunctionType, selectionPrompt,
                    CopyOptions(requestOptions, actualRoute, ct)).ConfigureAwait(false);
                if (current.Provider != actualRoute.Provider || current.Model != actualRoute.Model)
                    blocked = "current-route-changed-during-canonical-admission";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { blocked = ClassifyAdmission(e); }
        }
        var pin = requestOptions.OverrideProvenance is LlmOverrideProvenance.User or LlmOverrideProvenance.Profile || current?.IsUserPinned == true;
        var activeBudget = SwarmCore.Abstractions.Budgets.MissionBudgetExecutionScope.Current;
        // A proposal does not reserve money. The canonical router admits each eventual
        // physical attempt against this same host-bound account, including effort changes.
        var configuredBudgetUnbound = !string.IsNullOrWhiteSpace(budgetReference)
            && (activeBudget is null || activeBudget.Reference != budgetReference
                || activeBudget.ProfileId != state.Identity.ProfileId || activeBudget.MissionId != state.Identity.MissionId);
        var increase = NextEffort(actualRoute, increase: true);
        var decrease = NextEffort(actualRoute, increase: false);
        var floor = Math.Max(requestOptions.MinimumCapabilityScore ?? 0, current?.MinimumCapabilityScore ?? 0);
        var eligibility = new AdaptiveReasoningEligibility
        {
            CanIncreaseEffort = blocked is null && current is not null && !pin && !configuredBudgetUnbound && increase is not null,
            CanDecreaseEffort = blocked is null && current is not null && !pin && decrease is not null && !observation.RequiredCapabilityMissing,
            CanRequestStrongerRoute = blocked is null && current?.CapabilityScore is not null && !pin && !configuredBudgetUnbound,
            CanRequestCheaperRoute = blocked is null && current is not null && !pin && !observation.RequiredCapabilityMissing,
            EffortIncreaseBlockedReason = blocked ?? (pin ? "explicit-route-pin" : configuredBudgetUnbound ? "mission-budget-adapter-unavailable" : "effort-increase-not-supported"),
            StrongerRouteBlockedReason = blocked ?? (pin ? "explicit-route-pin" : configuredBudgetUnbound ? "mission-budget-adapter-unavailable" : "current-capability-score-unavailable"),
            CheaperRouteBlockedReason = blocked ?? (pin ? "explicit-route-pin" : "no-admitted-cheaper-route"),
            EffortDecreaseBlockedReason = pin ? "explicit-route-pin" : "effort-floor"
        };
        var evaluation = AdaptiveReasoningPolicy.Evaluate(state.PolicyState, observation, eligibility, options.Value, time.GetUtcNow());
        var decision = evaluation.Decision;
        AdaptiveRouteState? after = null;
        decimal? estimatedCost = null;
        var outcome = AdaptiveDecisionOutcome.Applied;
        string? reason = decision.Reason;
        if (decision.Action is AdaptiveAction.IncreaseEffort or AdaptiveAction.DecreaseEffort)
            after = actualRoute! with { Effort = decision.Action == AdaptiveAction.IncreaseEffort ? increase : decrease,
                CapabilityScore = current!.CapabilityScore, MinimumCapabilityScore = floor,
                SelectionAuditReference = current.AuditCorrelationId, IsUserPinned = current.IsUserPinned };
        else if (decision.Action is AdaptiveAction.RequestStrongerRoute or AdaptiveAction.RequestCheaperRoute)
        {
            var router = Router();
            try
            {
                var candidateOptions = CopyOptions(requestOptions, null, ct);
                candidateOptions.OverrideProvider = null; candidateOptions.OverrideModel = null;
                candidateOptions.OverrideProvenance = LlmOverrideProvenance.Unspecified;
                candidateOptions.MinimumCapabilityScore = decision.Action == AdaptiveAction.RequestStrongerRoute
                    ? Math.Max(floor, current!.CapabilityScore!.Value + 0.001m) : floor;
                candidateOptions.RequireMeasuredCapabilityScore = true;
                candidateOptions.BlockWeakerPolicyFallback = true;
                candidateOptions.RequireCurrentWorkloadSizing = true;
                candidateOptions.Urgency = decision.Action == AdaptiveAction.RequestCheaperRoute ? LlmUrgency.Batch : requestOptions.Urgency;
                var selected = await router.SelectOptimalModelAsync(state.Authority.FunctionType, selectionPrompt, candidateOptions).ConfigureAwait(false);
                if (selected.IsUserPinned || selected.CapabilityScore is not { } score || score < candidateOptions.MinimumCapabilityScore
                    || selected.Provider == actualRoute!.Provider && selected.Model == actualRoute.Model)
                    throw new InvalidOperationException("No eligible distinct route satisfies the requested floor.");
                estimatedCost = SelectionCost(selected);
                if (decision.Action == AdaptiveAction.RequestCheaperRoute
                    && !IsCheaper(selected, current!))
                    throw new InvalidOperationException("No comparable lower-cost selection estimate.");
                after = new(selected.Provider, selected.Model, null) { CapabilityScore = score,
                    MinimumCapabilityScore = floor, SelectionAuditReference = selected.AuditCorrelationId };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { outcome = AdaptiveDecisionOutcome.Refused; reason = ClassifyAdmission(e); }
        }
        if (after is not null) outcome = AdaptiveDecisionOutcome.Proposed;
        var record = new AdaptiveDecisionRecord { Id = Guid.NewGuid(), Decision = decision, Observation = observation,
            AuthorityDigest = state.Authority.Digest, AtUtc = time.GetUtcNow(), Before = actualRoute, After = after,
            Outcome = outcome, OutcomeReason = reason, EstimatedCost = estimatedCost };
        var superseded = after is null ? [] : state.Decisions
            .Where(d => d.Outcome == AdaptiveDecisionOutcome.Proposed
                && !state.Decisions.Any(t => t.Decision == d.Decision && t.Outcome != AdaptiveDecisionOutcome.Proposed))
            .Select(d => d with { Id = Guid.NewGuid(), AtUtc = time.GetUtcNow(), Outcome = AdaptiveDecisionOutcome.Superseded,
                OutcomeReason = "newer-observation-proposal" }).ToArray();
        return await SaveAsync(state with { PolicyState = evaluation.State, CurrentRoute = actualRoute ?? state.CurrentRoute,
            Decisions = [..state.Decisions, ..superseded, record] }, ct).ConfigureAwait(false);
    }

    public async Task<AdaptiveExecutionSnapshot> RecordBoundaryAsync(AdaptiveExecutionSnapshot state,
        AdaptiveDecisionRecord proposal, AdaptiveRouteState actual, string boundary, CancellationToken ct,
        IReadOnlyList<string>? usageReferences = null)
    {
        if (!state.Decisions.Any(d => d.Id == proposal.Id && d == proposal && d.Outcome == AdaptiveDecisionOutcome.Proposed))
            throw new InvalidOperationException("The boundary must reference a saved proposal for this work.");
        if (state.Decisions.Any(d => d.Decision == proposal.Decision && d.Outcome != AdaptiveDecisionOutcome.Proposed)) return state;
        if (proposal.AuthorityDigest != state.Authority.Digest)
            throw new AdaptiveExecutionConflictException();
        var applied = proposal.After is { } intended && intended.Provider == actual.Provider && intended.Model == actual.Model && intended.Effort == actual.Effort;
        var terminal = proposal with { Id = Guid.NewGuid(), AtUtc = time.GetUtcNow(), After = actual,
            Outcome = applied ? AdaptiveDecisionOutcome.Applied : AdaptiveDecisionOutcome.Refused,
            EffectiveBoundary = boundary, OutcomeReason = applied ? "observed-provider-boundary" : "actual-route-differs-from-proposal",
            UsageReferences = (usageReferences ?? []).Distinct(StringComparer.Ordinal)
                .Where(reference => state.Usage.Any(u => u.Reference == reference)).ToArray() };
        return await SaveAsync(state with { CurrentRoute = actual,
            PolicyState = applied ? AdaptiveReasoningPolicy.RecordApplied(state.PolicyState, proposal.Decision, time.GetUtcNow()) : state.PolicyState,
            Decisions = [..state.Decisions, terminal] }, ct).ConfigureAwait(false);
    }

    public async Task<AdaptiveExecutionSnapshot> RecordUsageAsync(AdaptiveExecutionSnapshot state, AdaptiveUsageReference usage, CancellationToken ct)
    {
        if (state.Usage.FirstOrDefault(u => u.Reference == usage.Reference) is { } recorded)
        {
            if (recorded != usage) throw new InvalidOperationException("A physical usage reference cannot change its measured outcome.");
            return state;
        }
        return await SaveAsync(state with { Usage = [..state.Usage, usage] }, ct).ConfigureAwait(false);
    }

    public Task<AdaptiveExecutionSnapshot> RecordObservedRouteAsync(AdaptiveExecutionSnapshot state, AdaptiveRouteState actualRoute, CancellationToken ct)
        => state.CurrentRoute == actualRoute ? Task.FromResult(state) : SaveAsync(state with { CurrentRoute = actualRoute }, ct);

    public Task<AdaptiveExecutionSnapshot> RefuseProposalAsync(AdaptiveExecutionSnapshot state, AdaptiveDecisionRecord proposal,
        string reason, string boundary, CancellationToken ct)
    {
        if (!state.Decisions.Any(d => d.Id == proposal.Id && d == proposal && d.Outcome == AdaptiveDecisionOutcome.Proposed))
            throw new InvalidOperationException("Refusal must reference a saved proposal for this work.");
        if (state.Decisions.Any(d => d.Decision == proposal.Decision && d.Outcome != AdaptiveDecisionOutcome.Proposed))
            return Task.FromResult(state);
        return SaveAsync(state with { Decisions = [..state.Decisions, proposal with
        {
            Id = Guid.NewGuid(), AtUtc = time.GetUtcNow(), Outcome = AdaptiveDecisionOutcome.Refused,
            OutcomeReason = reason, EffectiveBoundary = boundary
        }] }, ct);
    }

    private async Task<AdaptiveExecutionSnapshot> SaveAsync(AdaptiveExecutionSnapshot state, CancellationToken ct)
    {
        if (state.Generation == long.MaxValue) throw new AdaptiveExecutionConflictException();
        var next = state with { Generation = state.Generation + 1, UpdatedAtUtc = time.GetUtcNow() };
        if (!await store.TrySaveAsync(next, state.Generation, ct).ConfigureAwait(false))
            throw new AdaptiveExecutionConflictException();
        return next;
    }

    public static string? NextEffort(AdaptiveRouteState? route, bool increase)
    {
        if (route is null) return null;
        var levels = SwarmCore.LlmRouting.Providers.ModelRouteCapabilities.ReasoningEfforts(route.Provider, route.Model).ToArray();
        var index = Array.IndexOf(levels, route.Effort);
        var next = index + (increase ? 1 : -1);
        return index < 0 || next < 0 || next >= levels.Length ? null : levels[next];
    }

    private static bool IsCheaper(LlmModelSelection candidate, LlmModelSelection current)
    {
        static (int Channel, decimal Consumption) Cost(LlmModelSelection route)
            => ModelScoutCostPolicy.RoutineOrder(route.Provider, route.Model,
                LlmProviderLocalityPolicy.IsLocalProviderName(route.Provider), SelectionCost(route));
        return candidate.ModelScoutDecisionReceipt is not null
            && current.ModelScoutDecisionReceipt is not null
            && Cost(candidate).CompareTo(Cost(current)) < 0;
    }

    private static decimal? SelectionCost(LlmModelSelection selection)
    {
        var receipt = selection.ModelScoutDecisionReceipt?.Selected;
        return receipt is { EstimatedCostUsd: >= 0 } && receipt.Provider == selection.Provider && receipt.Model == selection.Model
            ? receipt.EstimatedCostUsd : null;
    }

    private static LlmRouterOptions CopyOptions(LlmRouterOptions source, AdaptiveRouteState? route, CancellationToken ct)
    {
        var copy = LlmRouter.CloneRouterOptions(source);
        copy.EmbeddingGenerationId = source.EmbeddingGenerationId;
        copy.RequestCancellationToken = ct;
        copy.RequireCurrentWorkloadSizing = true;
        copy.OverrideProvider = route?.Provider; copy.OverrideModel = route?.Model;
        copy.OverrideProvenance = source.OverrideProvenance is LlmOverrideProvenance.User or LlmOverrideProvenance.Profile
            ? source.OverrideProvenance : LlmOverrideProvenance.Policy;
        return copy;
    }

    private static string ClassifyAdmission(Exception exception) => exception switch
    {
        LlmRouteSelectionBlockedException => "canonical-route-admission-blocked",
        _ => "canonical-route-unavailable"
    };
}

public sealed class AdaptiveExecutionConflictException() : InvalidOperationException("Adaptive work state changed; re-admit at a fresh boundary.");
