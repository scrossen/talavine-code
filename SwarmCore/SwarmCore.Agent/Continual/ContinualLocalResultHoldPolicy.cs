namespace SwarmCore.Agent.Continual;

/// <summary>Non-due preservation shape only. The owning host must independently prove the current canonical result/effect denial under its checked persistence transaction.</summary>
public static class ContinualLocalResultHoldPolicy
{
    public const string Reason = "The retained local result or immutable dispatched effect contract is not verified. Preserve its original candidate and effects without execution, correction, audit or release.";
    public static ContinualAcceptanceVerdict Verdict(string? reason = null)
        => new(ContinualAcceptanceOutcome.LocalResultContractHold, string.IsNullOrWhiteSpace(reason) ? Reason : reason)
        { EvaluationUnavailable = true };
    /// <summary>
    /// The exact non-due shape this policy parks: a pre-audit execution checkpoint whose first cause is this
    /// denial. A host may offer its owner one checked re-verification of such a hold after a host repair;
    /// the retained output, candidate, attempt and effects stay immutable and nothing is executed.
    /// </summary>
    public static bool IsHeld(ContinualObjectiveSnapshot snapshot)
        => snapshot.Status == ContinualObjectiveStatus.Uncertain && snapshot.NextAttemptAtUtc is null
            && snapshot.LeaseToken is null && snapshot.VerificationCandidatePending
            && snapshot.VerificationCandidate is { Provenance: "execution-checkpoint" } candidate
            // A verification-only claim after the checkpoint advances the attempt count; the retained execution attempt never moves.
            && candidate.ExecutionAttempt >= 1 && candidate.ExecutionAttempt <= snapshot.AttemptCount
            && ContinualVerificationCandidatePolicy.Matches(snapshot, snapshot.LastOutput ?? "");
    // The failure text is not part of the shape: a parked hold keeps its first cause (often the failed tool's own
    // error) as LastFailure. The host separately refuses a wake once any audit allowance exists, and a wake of a
    // report-gap hold only re-runs the same host verification and parks again without execution or allowance.
    public static string FirstCause(ContinualObjectiveSnapshot current, string? reason)
        => !string.IsNullOrWhiteSpace(current.LastFailure) ? current.LastFailure
            : string.IsNullOrWhiteSpace(reason) ? Reason : reason;
    public static bool IsTransition(ContinualObjectiveSnapshot current, ContinualObjectiveTransition transition)
        => transition.RetainedHoldKind == ContinualRetainedVerificationHoldKind.LocalResultOrEffectContractDenied
            && current.VerificationCandidate is not null && transition.ExpectedVerificationCandidate is not null
            && transition.ExpectedVerificationCandidate == ContinualVerificationCandidatePolicy.Expect(current)
            && transition.RetainVerificationCandidate && transition.Status == ContinualObjectiveStatus.Uncertain
            && transition.NextAttemptAtUtc is null && transition.ReleaseLease && transition.ClearCurrentTool
            && transition.LastOutput == current.LastOutput && transition.LastOutputFingerprint == current.LastOutputFingerprint
            && transition.LastProgressAtUtc == current.LastProgressAtUtc && transition.ConsecutiveNoProgress == current.ConsecutiveNoProgress
            && !string.IsNullOrWhiteSpace(transition.LastFailure)
            && (string.IsNullOrWhiteSpace(current.LastFailure) || transition.LastFailure == current.LastFailure)
            && transition.ContinuationFeedback == transition.LastFailure;
}
