using SwarmCore.LlmRouting.Models;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Pure advisory policy. Route, capability, privacy, cost and provider-boundary admission
/// remain with the governor. Only a confirmed applied change advances the change budget.</summary>
public static class AdaptiveReasoningPolicy
{
    public static AdaptiveReasoningEvaluation Evaluate(AdaptiveReasoningState state,
        AdaptiveReasoningObservation observation, AdaptiveReasoningEligibility eligibility,
        AdaptiveReasoningOptions options, DateTimeOffset now)
    {
        Validate(options);
        if (state.WorkId != observation.WorkId || string.IsNullOrWhiteSpace(observation.WorkId)
            || string.IsNullOrWhiteSpace(observation.EvidenceId) || observation.Sequence < 0)
            throw new ArgumentException("Observation must identify the same work and its evidence.");
        AdaptiveReasoningEvaluation Result(AdaptiveAction action, string reason) => new(state,
            new(action, reason, state.WorkId, observation.Sequence, observation.EvidenceId, options.Version));
        if (observation.Sequence <= state.LastSequence) return Result(AdaptiveAction.Keep, "observation-already-consumed");
        var retryFailure = observation.Failure is AdaptiveFailure.Transport or AdaptiveFailure.RateLimited
            or AdaptiveFailure.MalformedOutput or AdaptiveFailure.ToolProtocol;
        var adequateEvidence = observation.Evidence is AdaptiveEvidence.NotRequired or AdaptiveEvidence.AdmittedComplete;
        var qualityFailure = adequateEvidence && (observation.NoProgressObserved && observation.Failure == AdaptiveFailure.None || observation.Failure is AdaptiveFailure.Acceptance or AdaptiveFailure.Synthesis
            or AdaptiveFailure.MalformedOutput or AdaptiveFailure.ToolProtocol);
        var accepted = adequateEvidence && !qualityFailure && observation.Failure == AdaptiveFailure.None && observation.AcceptancePassed;
        // A successful intermediate read does not resolve the verifier's outstanding
        // failure. Only accepted work or invalidated evidence resets that streak.
        var unresolvedFailures = qualityFailure ? state.ConsecutiveFailures + 1
            : accepted || !adequateEvidence ? 0 : state.ConsecutiveFailures;
        var success = accepted || adequateEvidence && !qualityFailure && observation.Failure == AdaptiveFailure.None
            && observation.MadeProgress && observation.VerifiedStepCompleted && unresolvedFailures == 0;
        state = state with
        {
            LastSequence = observation.Sequence,
            ConsecutiveFailures = unresolvedFailures,
            ConsecutiveSuccesses = success ? state.ConsecutiveSuccesses + 1 : 0,
            ConsecutiveRetryFailures = retryFailure ? state.ConsecutiveRetryFailures + 1 : 0
        };
        if (observation.Failure is AdaptiveFailure.Authentication or AdaptiveFailure.Configuration
            or AdaptiveFailure.Permission or AdaptiveFailure.QuotaExhausted)
            return Result(AdaptiveAction.Diagnose, "provider-or-authority-repair-required:" + observation.Failure);
        if (observation.Evidence is not (AdaptiveEvidence.NotRequired or AdaptiveEvidence.AdmittedComplete))
            return Result(AdaptiveAction.RepairEvidence, "evidence-repair-required:" + observation.Evidence);
        if (retryFailure && state.ConsecutiveRetryFailures <= options.ProviderRetries)
            return Result(AdaptiveAction.RetryProvider, "bounded-provider-retry:" + observation.Failure);
        if (observation.Failure is AdaptiveFailure.Transport or AdaptiveFailure.RateLimited)
            return Result(AdaptiveAction.Diagnose, "provider-retries-exhausted");
        if (observation.RequiredCapabilityMissing || qualityFailure && state.ConsecutiveFailures >= options.FailuresBeforeEscalation)
        {
            if (state.LastChangeAt is { } changed && now - changed < options.ChangeCooldown)
                return Result(AdaptiveAction.Keep, "change-cooldown");
            if (options.MaximumEscalations is { } maximum && state.AppliedEscalations >= maximum)
                return Result(AdaptiveAction.Diagnose, "configured-escalation-limit");
            // Reasoning effort cannot create a missing tool/modality capability.
            if (!observation.RequiredCapabilityMissing && eligibility.CanIncreaseEffort)
                return Result(AdaptiveAction.IncreaseEffort, "repeated-failure-with-admitted-evidence");
            if (eligibility.CanRequestStrongerRoute)
                return Result(AdaptiveAction.RequestStrongerRoute, observation.RequiredCapabilityMissing
                    ? "required-capability-missing" : "repeated-failure-with-admitted-evidence");
            return Result(AdaptiveAction.Diagnose, eligibility.StrongerRouteBlockedReason
                ?? eligibility.EffortIncreaseBlockedReason ?? "no-admitted-escalation-action");
        }
        if (success && state.ConsecutiveSuccesses >= options.SuccessesBeforeDeescalation
            && (observation.BudgetPressure || observation.LatencyAboveTarget))
        {
            if (state.LastChangeAt is { } changed && now - changed < options.ChangeCooldown)
                return Result(AdaptiveAction.Keep, "change-cooldown");
            if (eligibility.CanDecreaseEffort)
                return Result(AdaptiveAction.DecreaseEffort, "sustained-progress-with-cost-or-latency-pressure");
            return eligibility.CanRequestCheaperRoute
                ? Result(AdaptiveAction.RequestCheaperRoute, "sustained-progress-with-cost-or-latency-pressure")
                : Result(AdaptiveAction.Keep, eligibility.CheaperRouteBlockedReason ?? eligibility.EffortDecreaseBlockedReason ?? "deescalation-not-admitted");
        }
        return Result(AdaptiveAction.Keep, success ? "progress-without-required-change" : "insufficient-observed-failure");
    }

    public static AdaptiveReasoningState RecordApplied(AdaptiveReasoningState state,
        AdaptiveReasoningDecision decision, DateTimeOffset appliedAt)
    {
        if (decision.WorkId != state.WorkId || decision.ObservationSequence != state.LastSequence)
            throw new ArgumentException("Applied decision must match the current work observation.");
        if (decision.ObservationSequence <= state.LastAppliedSequence) return state;
        if (decision.Action is not (AdaptiveAction.IncreaseEffort or AdaptiveAction.RequestStrongerRoute or AdaptiveAction.RequestCheaperRoute or AdaptiveAction.DecreaseEffort))
            throw new ArgumentException("Only a confirmed route or effort change advances adaptation state.");
        return state with { LastAppliedSequence = decision.ObservationSequence, LastChangeAt = appliedAt,
            AppliedEscalations = state.AppliedEscalations + (decision.Action is AdaptiveAction.RequestCheaperRoute or AdaptiveAction.DecreaseEffort ? 0 : 1),
            ConsecutiveFailures = 0, ConsecutiveSuccesses = 0, ConsecutiveRetryFailures = 0 };
    }

    private static void Validate(AdaptiveReasoningOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Version) || options.FailuresBeforeEscalation < 1
            || options.SuccessesBeforeDeescalation < 1 || options.ProviderRetries < 0
            || options.MaximumEscalations < 0 || options.ChangeCooldown < TimeSpan.Zero
            || options.LatencyTargetMs is { } latency && (!double.IsFinite(latency) || latency <= 0))
            throw new ArgumentException("Invalid adaptive reasoning policy configuration.");
    }
}
