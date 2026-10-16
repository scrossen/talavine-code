using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Pure completion/retry policy driven by an injected clock. It deliberately
/// has no attempt, call-count, or total-runtime terminal condition, but a run
/// of <see cref="ContinualObjectiveOptions.NoProgressAttentionThreshold"/>
/// consecutive no-progress attempts escalates to AttentionRequired on the long
/// attention backoff, so an unclassified persistent failure stops re-running the
/// executor every few minutes while staying resumable.
/// </summary>
public sealed class ContinualObjectiveStateMachine
{
    private readonly TimeProvider _time;
    private readonly ContinualObjectiveOptions _options;

    public ContinualObjectiveStateMachine(
        TimeProvider timeProvider,
        ContinualObjectiveOptions options)
    {
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public ContinualObjectiveTransition FromVerdict(
        ContinualObjectiveSnapshot current,
        string finalOutput,
        ContinualAcceptanceVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(finalOutput);
        ArgumentNullException.ThrowIfNull(verdict);

        var isAuditHold = verdict.Outcome is ContinualAcceptanceOutcome.ClosedAuditHold
            or ContinualAcceptanceOutcome.AuditClosureUnknownHold or ContinualAcceptanceOutcome.LocalResultContractHold;
        if (verdict.Outcome == ContinualAcceptanceOutcome.LocalResultContractHold)
            verdict = verdict with { Reason = ContinualLocalResultHoldPolicy.FirstCause(current, verdict.Reason) };
        if (!isAuditHold && current.Mission is not null && MissionInterruptionPolicy.IsRoutineConfirmation(finalOutput))
            verdict = new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry,
                "Unnecessary confirmation defect: the assigned mission already authorizes routine implementation. Inspect the current state and continue the authorized work without requesting renewal of authority.");
        if (verdict.Outcome == ContinualAcceptanceOutcome.Delivered && MissionTransitions.MissingEvidence(current) is { } missing)
            verdict = new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry, missing);
        if (!isAuditHold && current.Mission is not null && verdict.Outcome == ContinualAcceptanceOutcome.NeedsUserHelp
            && current.ExternalDependency is not { Resolved: false } && current.TargetClarification is not { SelectedTarget: null })
            verdict = new(ContinualAcceptanceOutcome.Retry,
                "No host-verified indispensable input need was recorded. Diagnose the failure, exhaust authorized alternatives and continue independent work; do not ask to continue.");
        var now = _time.GetUtcNow();
        // A hold is tied to the host's exact saved bytes and opaque persisted
        // digest. Do not rewrite its fingerprint format/casing during settlement.
        var fingerprint = isAuditHold ? current.LastOutputFingerprint : Fingerprint(finalOutput);
        // A retry the acceptance check could not evaluate is no progress either,
        // even when a reworded output changes the fingerprint; otherwise an
        // auditor outage pins every continuation to BaseRetryDelay.
        var noProgress = isAuditHold ? current.ConsecutiveNoProgress
            : verdict.Outcome == ContinualAcceptanceOutcome.Retry
                         && (verdict.EvaluationUnavailable
                             || string.Equals(
                                 current.LastOutputFingerprint,
                                 fingerprint,
                                 StringComparison.Ordinal))
            ? current.ConsecutiveNoProgress == int.MaxValue ? int.MaxValue : current.ConsecutiveNoProgress + 1
            : 0;
        var status = verdict.Outcome switch
        {
            ContinualAcceptanceOutcome.Delivered => ContinualObjectiveStatus.Completed,
            ContinualAcceptanceOutcome.NeedsUserHelp => ContinualObjectiveStatus.WaitingForInput,
            ContinualAcceptanceOutcome.VerificationUnavailable => ContinualObjectiveStatus.VerificationPending,
            ContinualAcceptanceOutcome.ClosedAuditHold => ContinualObjectiveStatus.Uncertain,
            ContinualAcceptanceOutcome.AuditClosureUnknownHold => ContinualObjectiveStatus.Uncertain,
            ContinualAcceptanceOutcome.LocalResultContractHold => ContinualObjectiveStatus.Uncertain,
            _ => ContinualObjectiveStatus.RetryScheduled,
        };
        if (!isAuditHold && current.TargetClarification is { SelectedTarget: null }) status = ContinualObjectiveStatus.WaitingForInput;
        if (!isAuditHold && current.ExternalDependency is { Resolved: false }) status = ContinualObjectiveStatus.WaitingForInput;
        if (!isAuditHold && current.ConstraintConflict is { } conflict)
        {
            status = ContinualObjectiveStatus.Failed;
            verdict = new(ContinualAcceptanceOutcome.Retry, "Constraint conflict: " + conflict.VerifiedReason);
        }
        if (status == ContinualObjectiveStatus.Completed && MissionVerification.ValidateCompletion(current, verdict.Verification, finalOutput) is { } invalid)
        {
            status = ContinualObjectiveStatus.VerificationPending;
            verdict = verdict with { Outcome = ContinualAcceptanceOutcome.VerificationUnavailable, Reason = invalid };
        }
        // A checked closed audit is a non-due verification hold, not a retry
        // opportunity. Keep its exact first-causal reason and retained output;
        // the store CAS below binds the hold to the candidate the host inspected.
        if (isAuditHold)
            status = ContinualObjectiveStatus.Uncertain;
        // Re-verifying the same saved output, or an audit that could not be evaluated at all, is
        // no progress either. Without counting it, a structurally unauditable receipt or a
        // persistent auditor/provider failure re-ran the same audit every VerificationRetryDelay
        // forever, and the live objective kept its employee from starting new cycles.
        if (status == ContinualObjectiveStatus.VerificationPending
            && (verdict.EvaluationUnavailable
                || string.Equals(current.LastOutputFingerprint, fingerprint, StringComparison.Ordinal)))
        {
            noProgress = current.ConsecutiveNoProgress == int.MaxValue ? int.MaxValue : current.ConsecutiveNoProgress + 1;
        }
        if (!isAuditHold
            && (status is ContinualObjectiveStatus.RetryScheduled or ContinualObjectiveStatus.VerificationPending)
            && current.Mission?.MaximumAttempts is { } limit && current.AttemptCount >= limit)
        {
            status = ContinualObjectiveStatus.Failed;
            verdict = verdict with { Reason = $"Assigned attempt allowance ({limit}) exhausted. " + verdict.Reason };
        }
        if (status == ContinualObjectiveStatus.RetryScheduled && ReachedNoProgressThreshold(noProgress))
        {
            status = ContinualObjectiveStatus.AttentionRequired;
            verdict = verdict with { Reason = NoProgressAttentionReason(noProgress, verdict.Reason) };
        }

        return new ContinualObjectiveTransition(
            status,
            now,
            isAuditHold ? current.LastProgressAtUtc : now,
            status switch
            {
                ContinualObjectiveStatus.RetryScheduled => now + RetryBackoff(noProgress),
                ContinualObjectiveStatus.AttentionRequired => now + AttentionRetryBackoff(noProgress),
                ContinualObjectiveStatus.VerificationPending => now + VerificationBackoff(noProgress),
                _ => null,
            },
            isAuditHold ? current.LastOutput : finalOutput,
            status == ContinualObjectiveStatus.Completed ? null : verdict.Reason,
            fingerprint,
            status == ContinualObjectiveStatus.Completed ? null : verdict.Reason,
            noProgress)
        {
            Verification = verdict.Verification,
            RetainVerificationCandidate = isAuditHold,
            ClearVerificationCandidate = status == ContinualObjectiveStatus.RetryScheduled && verdict.Outcome == ContinualAcceptanceOutcome.Retry && verdict.NoEffectConfirmed,
            RetainedHoldKind = verdict.Outcome == ContinualAcceptanceOutcome.LocalResultContractHold
                ? ContinualRetainedVerificationHoldKind.LocalResultOrEffectContractDenied : null,
        };
    }

    /// <param name="requiresAttention">
    /// The executor reported a failure only a person can clear (no provider
    /// credit, a rejected credential). The objective waits in AttentionRequired
    /// on <see cref="AttentionRetryBackoff"/> instead of the transient backoff,
    /// whose five-minute ceiling repeated a doomed call forever.
    /// </param>
    public ContinualObjectiveTransition FromTurnError(
        ContinualObjectiveSnapshot current,
        string error,
        bool requiresAttention = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Mission?.MaximumAttempts is { } limit && current.AttemptCount >= limit)
            return FromTerminalError(current, $"Assigned attempt allowance ({limit}) exhausted. " + error);
        var now = _time.GetUtcNow();
        // Provider/engine failures are no-progress attempts too. Leaving this counter unchanged
        // pins every recovery attempt to BaseRetryDelay, which turns a retired model or provider
        // outage into an unbounded request storm. Advance the same bounded exponential backoff
        // used for repeated non-delivery verdicts; a later successful/progressing verdict resets it.
        var noProgress = current.ConsecutiveNoProgress == int.MaxValue
            ? int.MaxValue
            : current.ConsecutiveNoProgress + 1;
        if (!requiresAttention && ReachedNoProgressThreshold(noProgress))
        {
            requiresAttention = true;
            error = NoProgressAttentionReason(noProgress, error);
        }
        return new ContinualObjectiveTransition(
            requiresAttention ? ContinualObjectiveStatus.AttentionRequired : ContinualObjectiveStatus.RetryScheduled,
            now,
            current.LastProgressAtUtc,
            now + (requiresAttention ? AttentionRetryBackoff(noProgress) : RetryBackoff(noProgress)),
            current.LastOutput,
            error,
            current.LastOutputFingerprint,
            error,
            noProgress);
    }

    private bool ReachedNoProgressThreshold(int consecutiveNoProgress)
        => consecutiveNoProgress >= _options.NoProgressAttentionThreshold;

    private static string NoProgressAttentionReason(int consecutiveNoProgress, string? reason)
        => $"No progress after {consecutiveNoProgress} consecutive attempts; retrying on the long attention schedule. "
           + (reason ?? string.Empty);

    /// <summary>
    /// Settles an executor-declared terminal failure without putting the same
    /// user request back on the retry queue. This is for operations whose own
    /// contract forbids replay (for example, a billable Vibes generation).
    /// </summary>
    public ContinualObjectiveTransition FromTerminalError(
        ContinualObjectiveSnapshot current,
        string error)
    {
        ArgumentNullException.ThrowIfNull(current);
        var now = _time.GetUtcNow();
        return new ContinualObjectiveTransition(
            ContinualObjectiveStatus.Failed,
            now,
            current.LastProgressAtUtc,
            NextAttemptAtUtc: null,
            current.LastOutput,
            error,
            current.LastOutputFingerprint,
            error,
            current.ConsecutiveNoProgress);
    }

    public bool IsDue(ContinualObjectiveSnapshot objective)
    {
        ArgumentNullException.ThrowIfNull(objective);
        var now = _time.GetUtcNow();
        return objective.Status switch
        {
            ContinualObjectiveStatus.RetryScheduled
                or ContinualObjectiveStatus.VerificationPending
                or ContinualObjectiveStatus.AttentionRequired
                => objective.NextAttemptAtUtc is null || objective.NextAttemptAtUtc <= now,
            ContinualObjectiveStatus.InProgress
                => objective.LeaseExpiresAtUtc is null || objective.LeaseExpiresAtUtc <= now,
            _ => false,
        };
    }

    public static bool IsTerminal(ContinualObjectiveStatus status)
        => status is ContinualObjectiveStatus.Completed
            or ContinualObjectiveStatus.Idle
            or ContinualObjectiveStatus.Failed
            or ContinualObjectiveStatus.Cancelled;

    public TimeSpan RetryBackoff(int consecutiveNoProgress)
    {
        var exponent = Math.Clamp(consecutiveNoProgress, 0, 30);
        var multiplier = Math.Pow(2, exponent);
        var ticks = Math.Min(
            _options.MaximumRetryDelay.Ticks,
            _options.BaseRetryDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Wait before retrying a failure only a person can clear: AttentionRetryDelay
    /// after the first such attempt, doubling per further no-progress attempt up to
    /// MaximumAttentionRetryDelay. The objective then resumes by itself once someone
    /// fixes the account, without repeating a call that cannot succeed every few minutes.
    /// </summary>
    public TimeSpan AttentionRetryBackoff(int consecutiveNoProgress)
    {
        var exponent = Math.Clamp(consecutiveNoProgress - 1, 0, 30);
        var ticks = Math.Min(
            _options.MaximumAttentionRetryDelay.Ticks,
            _options.AttentionRetryDelay.Ticks * Math.Pow(2, exponent));
        return TimeSpan.FromTicks(Math.Max((long)ticks, RetryBackoff(consecutiveNoProgress).Ticks));
    }

    /// <summary>
    /// Wait before re-verifying a saved output: VerificationRetryDelay on a fresh output,
    /// doubling per consecutive unproductive re-verification up to MaximumAttentionRetryDelay.
    /// </summary>
    public TimeSpan VerificationBackoff(int consecutiveNoProgress)
    {
        var exponent = Math.Clamp(consecutiveNoProgress, 0, 30);
        var ticks = Math.Min(
            _options.MaximumAttentionRetryDelay.Ticks,
            _options.VerificationRetryDelay.Ticks * Math.Pow(2, exponent));
        return TimeSpan.FromTicks(Math.Max((long)ticks, _options.VerificationRetryDelay.Ticks));
    }

    public DateTimeOffset NewLeaseExpiry()
        => _time.GetUtcNow() + _options.ExecutionLeaseDuration;

    internal static string Fingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Scheduling and liveness settings; none is a completion budget.</summary>
public sealed class ContinualObjectiveOptions
{
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>First wait after a failure only a person can clear (see <see cref="ContinualObjectiveStateMachine.AttentionRetryBackoff"/>).</summary>
    public TimeSpan AttentionRetryDelay { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan MaximumAttentionRetryDelay { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan VerificationRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Consecutive no-progress attempts (unchanged or unevaluated output, executor errors)
    /// after which a retry escalates to AttentionRequired on the attention backoff. Without
    /// it an unclassified persistent failure re-ran the executor every MaximumRetryDelay forever.
    /// </summary>
    public int NoProgressAttentionThreshold { get; set; } = 20;
    public TimeSpan ExecutionLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int DueBatchSize { get; set; } = 8;

    internal void Validate()
    {
        if (BaseRetryDelay <= TimeSpan.Zero) throw Invalid(nameof(BaseRetryDelay));
        if (MaximumRetryDelay < BaseRetryDelay) throw Invalid(nameof(MaximumRetryDelay));
        if (AttentionRetryDelay <= TimeSpan.Zero) throw Invalid(nameof(AttentionRetryDelay));
        if (MaximumAttentionRetryDelay < AttentionRetryDelay) throw Invalid(nameof(MaximumAttentionRetryDelay));
        if (VerificationRetryDelay <= TimeSpan.Zero) throw Invalid(nameof(VerificationRetryDelay));
        if (NoProgressAttentionThreshold < 1) throw Invalid(nameof(NoProgressAttentionThreshold));
        if (ExecutionLeaseDuration <= TimeSpan.Zero) throw Invalid(nameof(ExecutionLeaseDuration));
        if (HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval >= ExecutionLeaseDuration)
            throw Invalid(nameof(HeartbeatInterval));
        if (PollInterval <= TimeSpan.Zero) throw Invalid(nameof(PollInterval));
        if (DueBatchSize is < 1 or > 1000) throw Invalid(nameof(DueBatchSize));
    }

    private static ArgumentOutOfRangeException Invalid(string name)
        => new(name, "Continual-objective timing and batch settings must be positive and internally consistent.");
}
