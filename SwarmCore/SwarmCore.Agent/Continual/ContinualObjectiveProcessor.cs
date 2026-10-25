using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Agent.Continual;

public interface IContinualObjectiveProcessor
{
    Task<ContinualObjectiveSnapshot> StartOrJoinAsync(
        ContinualObjectiveStart start,
        CancellationToken ct = default);

    Task<ContinualObjectiveProcessResult> ProcessAsync(
        Guid objectiveId,
        string ownerId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ContinualObjectiveProcessResult>> ProcessDueAsync(
        string ownerId,
        CancellationToken ct = default);

    Task<bool> StopAsync(
        Guid objectiveId,
        string reason = "Stopped by the user.",
        CancellationToken ct = default);
}

public enum ContinualObjectiveProcessDisposition
{
    Processed = 0,
    NotFound = 1,
    NotDue = 2,
    ClaimLost = 3,
    LeaseLost = 4,
    Stopped = 5,
}

public sealed record ContinualObjectiveProcessResult(
    Guid ObjectiveId,
    ContinualObjectiveProcessDisposition Disposition,
    ContinualObjectiveStatus? Status,
    bool Executed,
    bool Reverified,
    string? Detail = null)
{
    public bool Claimed => Disposition is ContinualObjectiveProcessDisposition.Processed
        or ContinualObjectiveProcessDisposition.LeaseLost
        or ContinualObjectiveProcessDisposition.Stopped;
}

/// <summary>
/// One host-pumped continual-objective cycle. It has no BackgroundService and
/// no completion budget: desktop, web, worker, and test hosts decide when to
/// call <see cref="ProcessDueAsync"/>. Claims and renewable leases provide
/// cross-process exclusion while the processor owns verification and retries.
/// </summary>
public sealed class ContinualObjectiveProcessor : IContinualObjectiveProcessor
{
    private readonly IContinualObjectiveStore _store;
    private readonly ContinualObjectiveStateMachine _stateMachine;
    private readonly IContinualObjectiveAcceptanceService _acceptance;
    private readonly IContinualObjectiveExecutorRegistry _executors;
    private readonly IContinualObjectiveActivitySink _activity;
    private readonly IContinualObjectiveTextSanitizer _sanitizer;
    private readonly IContinualObjectiveContinuationPromptBuilder _prompts;
    private readonly IContinualObjectiveExecutionControl _executionControl;
    private readonly TimeProvider _time;
    private readonly ContinualObjectiveOptions _options;
    private readonly SwarmCore.Abstractions.Budgets.IMissionBudgetLedger? _budgets;
    private readonly ILogger<ContinualObjectiveProcessor>? _logger;

    public ContinualObjectiveProcessor(
        IContinualObjectiveStore store,
        ContinualObjectiveStateMachine stateMachine,
        IContinualObjectiveAcceptanceService acceptance,
        IContinualObjectiveExecutorRegistry executors,
        IContinualObjectiveActivitySink activity,
        IContinualObjectiveTextSanitizer sanitizer,
        IContinualObjectiveContinuationPromptBuilder prompts,
        IContinualObjectiveExecutionControl executionControl,
        TimeProvider timeProvider,
        ContinualObjectiveOptions options,
        SwarmCore.Abstractions.Budgets.IMissionBudgetLedger? budgets = null,
        ILogger<ContinualObjectiveProcessor>? logger = null)
    {
        _logger = logger;
        _store = store;
        _stateMachine = stateMachine;
        _acceptance = acceptance;
        _executors = executors;
        _activity = activity;
        _sanitizer = sanitizer;
        _prompts = prompts;
        _executionControl = executionControl;
        _time = timeProvider;
        _options = options;
        _budgets = budgets;
    }

    /// <summary>
    /// Best-effort recovery for the reservation gap between <c>ReserveAsync</c> and
    /// <c>MarkDispatchedAsync</c>: a process that reserved a physical operation for this exact
    /// mission and died before dispatch leaves a row that would otherwise consume capacity forever.
    /// Only this mission's own still-Reserved, non-allocation rows are touched — a fresh exclusive
    /// execution lease on this objective proves no other attempt can be concurrently dispatching them.
    /// Dispatched/Settled rows are never released here; their liability is retained until authoritative
    /// settlement, per the shared budget-accounting contract.
    /// </summary>
    private async Task ReleaseUndispatchedBudgetReservationsAsync(Guid objectiveId, string budgetReference, string partitionKey, CancellationToken ct)
    {
        if (_budgets is null || !Guid.TryParse(partitionKey, out var profile)) return;
        var snapshot = await _budgets.ReadAsync(profile, budgetReference, ct);
        if (snapshot is null) return;
        foreach (var reservation in snapshot.Reservations)
        {
            if (reservation.MissionId != objectiveId
                || reservation.State != SwarmCore.Abstractions.Budgets.MissionBudgetReservationState.Reserved
                || reservation.OperationId.StartsWith("allocation:", StringComparison.Ordinal))
                continue;
            try { await _budgets.ReleaseUnusedAsync(profile, budgetReference, reservation.OperationId, ct); }
            catch (InvalidOperationException) { /* transitioned concurrently since the snapshot was read; leave it alone */ }
        }
    }

    public async Task<ContinualObjectiveSnapshot> StartOrJoinAsync(
        ContinualObjectiveStart start,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var safe = start with
        {
            // A mission's trailing restrictions are authority, not expendable summary text.
            ObjectiveText = _sanitizer.Sanitize(start.ObjectiveText, start.Mission is null ? 16_000 : int.MaxValue) ?? string.Empty,
            ObjectiveSummary = _sanitizer.Sanitize(start.ObjectiveSummary, 300) ?? "Agent objective",
        };
        var objective = await _store.CreateOrJoinAsync(safe, _time.GetUtcNow(), ct);
        await PublishAsync(
            objective,
            ContinualObjectiveActivityKind.Started,
            $"Objective registered: {objective.ObjectiveSummary}",
            ct: ct);
        return objective;
    }

    public async Task<ContinualObjectiveProcessResult> ProcessAsync(
        Guid objectiveId,
        string ownerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("A processor owner id is required.", nameof(ownerId));

        var objective = await _store.GetAsync(objectiveId, ct);
        if (objective is null)
            return Result(objectiveId, ContinualObjectiveProcessDisposition.NotFound);
        if (!_stateMachine.IsDue(objective))
            return Result(objectiveId, ContinualObjectiveProcessDisposition.NotDue, objective.Status);

        var statusBeforeClaim = objective.Status;
        // Register the in-process execution before claiming, so losing that race neither spends an
        // attempt nor leaves a freshly claimed lease behind until it expires.
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_executionControl.TryRegister(objective.Id, executionCancellation))
            return Result(objectiveId, ContinualObjectiveProcessDisposition.ClaimLost, objective.Status);
        var now = _time.GetUtcNow();
        ContinualObjectiveLease? lease = null;
        try
        {
            lease = await _store.TryClaimAsync(
                objective.Id,
                objective.Version,
                ownerId.Trim(),
                now,
                now + _options.ExecutionLeaseDuration,
                ct);
            if (lease is null)
            {
                _executionControl.Release(objective.Id, executionCancellation);
                return Result(objectiveId, ContinualObjectiveProcessDisposition.ClaimLost, objective.Status);
            }
            objective = await _store.GetAsync(objective.Id, ct) ?? objective;
        }
        catch
        {
            _executionControl.Release(objective.Id, executionCancellation);
            if (lease is not null && _store is IMissionStore claimedMissions)
                await claimedMissions.ReleaseExecutionAsync(objective.Id, lease.Token, CancellationToken.None);
            throw;
        }
        var deadlineState = new DeadlineState();
        var verificationOnlyCandidate = ContinualVerificationCandidatePolicy.RequiresCorrectionProvenance(objective.VerificationCandidate)
            || _acceptance.RequiresRetainedVerificationCandidate && objective.VerificationCandidate is not null;
        var deadlineTask = objective.Mission is null || verificationOnlyCandidate ? Task.CompletedTask
            : WatchDeadlineAsync(objective.Id, executionCancellation, deadlineState);

        var heartbeat = new HeartbeatState();
        var heartbeatTask = RunHeartbeatAsync(
            objective,
            lease.Token,
            executionCancellation,
            heartbeat);
        try
        {
            // Only explicitly opted-in non-mission hosts can reconcile historical physical
            // uncertainty here. Keep correction/retained-audit verification gates unchanged.
            if (!verificationOnlyCandidate && !_acceptance.RequiresRetainedVerificationCandidate && objective.Mission is null
                && _executors.Get(objective.ExecutorKey) is IContinualObjectiveReceiptRecovery receiptRecovery
                && objective.Receipts.Any(receipt => receiptRecovery.NeedsInspection(objective, receipt)))
            {
                string? inspectionFailure = null;
                try
                {
                    await receiptRecovery.ReconcileAsync(new(objective, _prompts.Build(objective), lease.Token),
                        new DurableExecutionReporter(objective, lease.Token, _store, _activity, _sanitizer, _time), executionCancellation.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    // An unavailable inspection/verifier cannot turn a physical uncertainty
                    // into execution permission or leave an automatic verification loop.
                    inspectionFailure = $"Host inspection failed ({error.GetType().Name}). ";
                }
                objective = await _store.GetAsync(objective.Id, executionCancellation.Token) ?? objective;
                if (objective.Receipts.Any(receipt => receiptRecovery.NeedsInspection(objective, receipt)))
                {
                    var pending = objective.Receipts.Where(receipt => receiptRecovery.NeedsInspection(objective, receipt)).ToArray();
                    var binding = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                        string.Join("\n", pending.OrderBy(receipt => receipt.Attempt).ThenBy(receipt => receipt.ToolCallId, StringComparer.Ordinal)
                            .Select(ContinualReceiptResolutionPolicy.Fingerprint)))));
                    var unresolved = _stateMachine.FromTerminalError(objective,
                        inspectionFailure + "Prior operations still have unverified outcomes. Host inspection is required; no action was replayed. "
                        + "Unresolved set SHA-256 " + binding + ": "
                        + string.Join(", ", pending.Select(receipt => $"attempt {receipt.Attempt}/{receipt.ToolCallId}")))
                        with { Status = ContinualObjectiveStatus.Uncertain, RetainVerificationCandidate = objective.VerificationCandidate is not null };
                    var applied = await _store.ApplyTransitionAsync(objective.Id, lease.Token, unresolved, executionCancellation.Token);
                    return Result(objective.Id, applied ? ContinualObjectiveProcessDisposition.Processed : ContinualObjectiveProcessDisposition.LeaseLost,
                        unresolved.Status, detail: unresolved.LastFailure);
                }
            }
            // Correction labels/metadata always require retained verification.
            // Transient Pending flags and older host capability defaults cannot
            // turn the saved result back into execution or mission-budget recovery.
            if (verificationOnlyCandidate)
                return await ReverifyAsync(objective, lease.Token, executionCancellation.Token);
            // The execution lease was just (re)claimed exclusively for this exact objective, so any
            // budget reservation this mission still owns in the Reserved state cannot belong to a
            // concurrently running attempt: it was left behind by a process that reserved a physical
            // operation and then died (crash, kill, restart) before ever marking it dispatched. Recover
            // that capacity before the mission resumes; leave Dispatched/Settled rows alone because a
            // dispatched operation's outcome is ambiguous and its liability must be retained.
            if (objective.Mission?.BudgetReference is { } staleBudgetReference)
                await ReleaseUndispatchedBudgetReservationsAsync(objective.Id, staleBudgetReference, objective.PartitionKey, executionCancellation.Token);

            await PublishAsync(
                objective,
                ContinualObjectiveActivityKind.Resumed,
                $"Processing: {objective.ObjectiveSummary}",
                statusBeforeClaim.ToString(),
                ct: executionCancellation.Token);

            // Read-only calls left Started by an earlier, torn-down attempt (e.g. cancelled at a
            // task-correction boundary) have no side effect to inspect. Close them immediately
            // instead of leaving them Started forever and leaking into PendingCallIds/continuation
            // prompts after the mission otherwise completes.
            if (objective.Mission is not null)
            {
                var abandonedReads = objective.Receipts.Where(r => r.State == ContinualToolReceiptState.Started
                    && r.SideEffect == ContinualSideEffectClass.ReadOnly).ToArray();
                if (abandonedReads.Length > 0)
                {
                    foreach (var stale in abandonedReads)
                        await _store.CloseAbandonedReadOnlyReceiptAsync(objective.Id, lease.Token, stale.Attempt, stale.ToolCallId,
                            _time.GetUtcNow(), executionCancellation.Token);
                    objective = await _store.GetAsync(objective.Id, executionCancellation.Token) ?? objective;
                }
            }

            if (objective.Mission is not null && objective.Receipts.Any(MissionTransitions.NeedsInspection))
            {
                if (_executors.Get(objective.ExecutorKey) is IContinualObjectiveRecovery recovery)
                {
                    await recovery.ReconcileAsync(new(objective, _prompts.Build(objective), lease.Token), executionCancellation.Token);
                    objective = await _store.GetAsync(objective.Id, executionCancellation.Token) ?? objective;
                }
            }
            if (objective.Mission is not null && objective.Receipts.Any(MissionTransitions.NeedsInspection))
            {
                var uncertain = _stateMachine.FromTerminalError(objective,
                    "An operation started but its outcome was not committed. Inspect the external state before any retry.")
                    with { Status = ContinualObjectiveStatus.Uncertain };
                var applied = await _store.ApplyTransitionAsync(objective.Id, lease.Token, uncertain, executionCancellation.Token);
                return Result(objective.Id, applied ? ContinualObjectiveProcessDisposition.Processed : ContinualObjectiveProcessDisposition.LeaseLost,
                    uncertain.Status, detail: uncertain.LastFailure);
            }
            if (objective.Mission?.DeadlineUtc <= now)
            {
                return await ApplyTerminalErrorAsync(objective, lease.Token, "The mission's explicit deadline has expired.", false, executionCancellation.Token);
            }

            // A retained candidate with possible effects is verification-only even when a
            // host continuation moved it through RetryScheduled/AttentionRequired and reset
            // the transient Pending flag. Status is not permission to replay an old effect.
            if (statusBeforeClaim == ContinualObjectiveStatus.VerificationPending || objective.VerificationCandidatePending
                || _acceptance.RequiresRetainedVerificationCandidate
                    // A receipt that was never physically dispatched, or whose host-proven failure already produced a
                    // no-effect retry, is not a possible effect; every other effect receipt keeps this verification-only.
                    && (objective.VerificationCandidate is not null || objective.HasPossibleSideEffects || HasSucceededEffectReceipt(objective)))
            {
                return await ReverifyAsync(objective, lease.Token, executionCancellation.Token);
            }

            return await ExecuteAsync(objective, lease.Token, executionCancellation.Token);
        }
        catch (ContinualVerificationCommitUnknownException)
        {
            return Result(objective.Id, ContinualObjectiveProcessDisposition.LeaseLost,
                detail: "Verification commit outcome is unknown; no executor or business action was replayed.");
        }
        catch (OperationCanceledException) when (heartbeat.LeaseLost)
        {
            return Result(
                objective.Id,
                ContinualObjectiveProcessDisposition.LeaseLost,
                (await _store.GetAsync(objective.Id, CancellationToken.None))?.Status,
                detail: "The execution lease was lost.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadlineState.Expired)
        {
            return await ApplyTerminalErrorAsync(objective, lease.Token, "The mission's explicit deadline expired during execution.", true, CancellationToken.None);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var saved = await _store.GetAsync(objective.Id, CancellationToken.None);
            return Result(
                objective.Id,
                saved?.Status == ContinualObjectiveStatus.Idle
                    ? ContinualObjectiveProcessDisposition.Stopped
                    : ContinualObjectiveProcessDisposition.LeaseLost,
                saved?.Status,
                detail: deadlineState.Fault);
        }
        finally
        {
            _executionControl.Release(objective.Id, executionCancellation);
            await executionCancellation.CancelAsync();
            try
            {
                try { await heartbeatTask; } catch (OperationCanceledException) { }
                try { await deadlineTask; } catch (OperationCanceledException) { }
            }
            finally
            {
                // A watcher fault can never skip releasing the execution lease.
                if (_store is IMissionStore missions)
                    await missions.ReleaseExecutionAsync(objective.Id, lease.Token, CancellationToken.None);
            }
        }
    }

    public async Task<IReadOnlyList<ContinualObjectiveProcessResult>> ProcessDueAsync(
        string ownerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("A processor owner id is required.", nameof(ownerId));
        var due = await _store.GetDueAsync(_time.GetUtcNow(), _options.DueBatchSize, ct);
        var results = new List<ContinualObjectiveProcessResult>(due.Count);
        foreach (var objective in due)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ProcessAsync(objective.Id, ownerId, ct));
        }
        return results;
    }

    public async Task<bool> StopAsync(
        Guid objectiveId,
        string reason = "Stopped by the user.",
        CancellationToken ct = default)
    {
        _executionControl.Cancel(objectiveId);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var objective = await _store.GetAsync(objectiveId, ct);
            if (objective is null) return false;
            if (objective.Status == ContinualObjectiveStatus.Idle) return true;
            if (ContinualObjectiveStateMachine.IsTerminal(objective.Status)) return false;
            var safeReason = _sanitizer.Sanitize(reason, 2_000) ?? "Stopped by the user.";
            if (await _store.TryStopAsync(
                    objectiveId,
                    objective.Version,
                    safeReason,
                    _time.GetUtcNow(),
                    ct))
            {
                await PublishAsync(
                    objective,
                    ContinualObjectiveActivityKind.Stopped,
                    safeReason,
                    ct: ct);
                return true;
            }
            await Task.Yield();
        }
    }

    /// <summary>Successful dispatched effects remain a replay fence even after a host clears a no-effect failure.</summary>
    private static bool HasSucceededEffectReceipt(ContinualObjectiveSnapshot objective)
        => objective.Receipts.Any(r => r.WasDispatched && !r.IsError && ContinualSideEffects.MayAffectTaskResources(r.SideEffect));

    /// <summary>Verification faults back off without repeating execution.</summary>
    private async Task<ContinualObjectiveProcessResult> ReverifyAsync(
        ContinualObjectiveSnapshot objective,
        string leaseToken,
        CancellationToken ct)
    {
        try
        {
            return await ReverifyCoreAsync(objective, leaseToken, ct);
        }
        catch (ContinualVerificationCommitUnknownException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var safeError = _sanitizer.Sanitize(ex.Message, 4_000) ?? "Verification failed.";
            var refreshed = await _store.GetAsync(objective.Id, ct) ?? objective;
            var transition = _stateMachine.FromVerdict(refreshed, refreshed.LastOutput ?? string.Empty,
                new(ContinualAcceptanceOutcome.VerificationUnavailable,
                    safeError + " Verification will be retried without repeating execution.") { EvaluationUnavailable = true });
            if (_acceptance.RequiresRetainedVerificationCandidate
                || ContinualVerificationCandidatePolicy.RequiresCorrectionProvenance(refreshed.VerificationCandidate))
                transition = transition with { ExpectedVerificationCandidate = ContinualVerificationCandidatePolicy.Expect(refreshed) };
            if (!await _store.ApplyTransitionAsync(objective.Id, leaseToken, transition, ct))
                return await LeaseLostOrStoppedAsync(objective.Id, reverified: true);
            await PublishAsync(refreshed, ContinualObjectiveActivityKind.Error,
                $"Verification could not run; it will be retried after {transition.NextAttemptAtUtc:u}.", safeError, ct: ct);
            return Result(objective.Id, ContinualObjectiveProcessDisposition.Processed, transition.Status,
                reverified: true, detail: safeError);
        }
    }

    private async Task<ContinualObjectiveProcessResult> ReverifyCoreAsync(
        ContinualObjectiveSnapshot objective,
        string leaseToken,
        CancellationToken ct)
    {
        if (_acceptance.RequiresRetainedVerificationCandidate
            && !ContinualVerificationCandidatePolicy.RequiresCorrectionProvenance(objective.VerificationCandidate)
            && (objective.HasPossibleSideEffects || HasSucceededEffectReceipt(objective))
            && !ContinualVerificationCandidatePolicy.Matches(objective, objective.LastOutput ?? string.Empty))
        {
            // Missing or changed retained proof cannot be repaired by running the executor.
            // Hold the existing effects for host reconciliation; do not ask an auditor to
            // infer authority from an output that has no valid execution checkpoint.
            var held = _stateMachine.FromVerdict(objective, objective.LastOutput ?? string.Empty,
                new(ContinualAcceptanceOutcome.VerificationUnavailable,
                    "The retained candidate is missing or changed. Reconcile existing effects without repeating execution.")
                { EvaluationUnavailable = true })
                with { ExpectedVerificationCandidate = ContinualVerificationCandidatePolicy.Expect(objective) };
            var appliedHold = await _store.ApplyTransitionAsync(objective.Id, leaseToken, held, ct);
            if (!appliedHold)
                return await LeaseLostOrStoppedAsync(objective.Id);
            await PublishTransitionAsync(objective, held, ct);
            return Result(objective.Id, ContinualObjectiveProcessDisposition.Processed,
                held.Status, detail: held.LastFailure);
        }
        if (_acceptance.RequiresRetainedVerificationCandidate && objective.VerificationCandidate is null
            && !objective.HasPossibleSideEffects && !HasSucceededEffectReceipt(objective))
        {
            // Nothing checkpointed and nothing happened: there is no candidate to verify, and a host that
            // requires one cannot accept the bare output. A verification hold here would never resolve;
            // running the work again is the only progress and is safe because no effect exists to repeat.
            return await ExecuteAsync(objective, leaseToken, ct);
        }
        var verdict = await _acceptance.EvaluateAsync(ToAcceptanceRequest(objective), ct);
        verdict = await ValidateCurrentEvidenceAsync(objective, verdict, ct);
        var transition = _stateMachine.FromVerdict(
            objective,
            objective.LastOutput ?? string.Empty,
            verdict);
        if (_acceptance.RequiresRetainedVerificationCandidate
            || ContinualVerificationCandidatePolicy.RequiresCorrectionProvenance(objective.VerificationCandidate)
            || verdict.Outcome == ContinualAcceptanceOutcome.LocalResultContractHold
            || (objective.VerificationCandidate is not null && verdict.Outcome is (ContinualAcceptanceOutcome.ClosedAuditHold or ContinualAcceptanceOutcome.AuditClosureUnknownHold)))
            transition = transition with { ExpectedVerificationCandidate = ContinualVerificationCandidatePolicy.Expect(objective) };
        var applied = await _store.ApplyTransitionAsync(objective.Id, leaseToken, transition, ct);
        if (!applied)
            return await LeaseLostOrStoppedAsync(objective.Id, reverified: true);
        await PublishTransitionAsync(objective, transition, ct);
        return Result(
            objective.Id,
            ContinualObjectiveProcessDisposition.Processed,
            transition.Status,
            reverified: true,
            detail: transition.LastFailure);
    }

    private async Task<ContinualObjectiveProcessResult> ExecuteAsync(
        ContinualObjectiveSnapshot objective,
        string leaseToken,
        CancellationToken ct)
    {
        if (_acceptance.RequiresRetainedVerificationCandidate && !_store.SupportsRetainedVerificationCandidates)
            return await ApplyTerminalErrorAsync(objective, leaseToken, "This host cannot durably checkpoint its verification candidate; execution was not started.", false, ct);
        var executor = _executors.Get(objective.ExecutorKey);
        if (executor is null)
        {
            return await ApplyErrorAsync(
                objective,
                leaseToken,
                $"No continual executor is registered for '{objective.ExecutorKey}'.",
                executed: false,
                ct);
        }

        try
        {
            var reporter = new DurableExecutionReporter(
                objective,
                leaseToken,
                _store,
                _activity,
                _sanitizer,
                _time);
            var execution = await executor.ExecuteAsync(
                new ContinualObjectiveExecutionRequest(
                    objective,
                    _prompts.Build(objective),
                    leaseToken),
                reporter,
                ct);
            if (execution.TurnErrored)
            {
                if (execution.FailureIsTerminal)
                {
                    return await ApplyTerminalErrorAsync(
                        objective,
                        leaseToken,
                        execution.Error ?? "The continual executor reported a terminal error.",
                        executed: true,
                        ct);
                }

                return await ApplyErrorAsync(
                    objective,
                    leaseToken,
                    execution.Error ?? "The continual executor reported an error.",
                    executed: true,
                    ct,
                    requiresAttention: execution.FailureRequiresAttention);
            }

            var refreshed = await _store.GetAsync(objective.Id, ct) ?? objective;
            var safeOutput = _sanitizer.Sanitize(execution.FinalOutput, 24_000) ?? string.Empty;
            if (refreshed.Mission is not null || _acceptance.RequiresRetainedVerificationCandidate)
            {
                if (!await _store.SaveVerificationCandidateAsync(objective.Id, leaseToken, safeOutput, _time.GetUtcNow(), ct))
                    throw new ContinualObjectiveLeaseLostException(objective.Id);
                refreshed = await _store.GetAsync(objective.Id, ct) ?? refreshed;
                if (_acceptance.RequiresRetainedVerificationCandidate && !ContinualVerificationCandidatePolicy.Matches(refreshed, safeOutput))
                {
                    var held = _stateMachine.FromVerdict(refreshed, safeOutput, new(ContinualAcceptanceOutcome.VerificationUnavailable,
                        "The retained candidate readback is missing or changed. Reconcile existing effects without repeating execution.") { EvaluationUnavailable = true });
                    var appliedCandidate = await _store.ApplyTransitionAsync(objective.Id, leaseToken, held, ct);
                    return Result(objective.Id, appliedCandidate ? ContinualObjectiveProcessDisposition.Processed : ContinualObjectiveProcessDisposition.LeaseLost,
                        held.Status, executed: true, detail: held.LastFailure);
                }
            }
            var routineConfirmation = refreshed.Mission is not null && MissionInterruptionPolicy.IsRoutineConfirmation(safeOutput);
            if (routineConfirmation)
            {
                await reporter.ToolCompletedAsync(new ContinualToolReceipt(objective.Id, objective.AttemptCount,
                    refreshed.LastEventSequence + 1, $"confirmation:{objective.AttemptCount}",
                    MissionInterruptionPolicy.DefectToolName, ContinualToolReceiptState.Completed,
                    ContinualSideEffectClass.ReadOnly, null,
                    "source=model-final; reason=unnecessary-authority-renewal; recovery=continue-authorized-work",
                    true, _time.GetUtcNow(), _time.GetUtcNow()), ct);
                refreshed = await _store.GetAsync(objective.Id, ct) ?? refreshed;
            }
            var verdict = execution.AwaitingOwnedWork
                ? new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry, "Waiting for owned child evidence.")
                : routineConfirmation
                ? new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry, "Routine work requires no renewed confirmation.")
                : execution.CompletionVerifiedByExecutor && refreshed.Mission is null && !_acceptance.RequiresRetainedVerificationCandidate
                ? ContinualAcceptanceVerdict.Delivered
                : await _acceptance.EvaluateAsync(
                    ToAcceptanceRequest(refreshed) with { FinalOutput = safeOutput },
                    ct);
            verdict = await ValidateCurrentEvidenceAsync(refreshed, verdict, ct);
            var transition = _stateMachine.FromVerdict(refreshed, safeOutput, verdict);
            if (execution.VerifiedHostProgress && execution.AwaitingOwnedWork && refreshed.Mission is null
                && !_acceptance.RequiresRetainedVerificationCandidate && verdict.Outcome == ContinualAcceptanceOutcome.Retry
                && !verdict.EvaluationUnavailable
                && transition.Status is ContinualObjectiveStatus.RetryScheduled or ContinualObjectiveStatus.AttentionRequired)
            {
                // A host-confirmed inbox/control batch can progress while preserving the exact
                // artifact. Do not manufacture a new artifact or tool receipt to avoid backoff.
                transition = transition with
                {
                    Status = ContinualObjectiveStatus.RetryScheduled, ConsecutiveNoProgress = 0,
                    NextAttemptAtUtc = _time.GetUtcNow(), LastFailure = null,
                    ContinuationFeedback = "Verified host progress was saved. Continue this same objective from its retained result without replaying actions.",
                };
            }
            if (_acceptance.RequiresRetainedVerificationCandidate)
                transition = transition with { ExpectedVerificationCandidate = ContinualVerificationCandidatePolicy.Expect(refreshed) };
            var applied = await _store.ApplyTransitionAsync(objective.Id, leaseToken, transition, ct);
            if (!applied)
                return await LeaseLostOrStoppedAsync(objective.Id, executed: true);
            await PublishTransitionAsync(refreshed, transition, ct);
            return Result(
                objective.Id,
                ContinualObjectiveProcessDisposition.Processed,
                transition.Status,
                executed: true,
                detail: transition.LastFailure);
        }
        catch (ContinualVerificationCommitUnknownException) { throw; }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await ApplyErrorAsync(objective, leaseToken, ex.Message, executed: true, ct);
        }
    }

    private async Task<ContinualObjectiveProcessResult> ApplyErrorAsync(
        ContinualObjectiveSnapshot objective,
        string leaseToken,
        string error,
        bool executed,
        CancellationToken ct,
        bool requiresAttention = false)
    {
        var safeError = _sanitizer.Sanitize(error, 4_000) ?? "Continual execution failed.";
        var refreshed = await _store.GetAsync(objective.Id, ct) ?? objective;
        var transition = _acceptance.RequiresRetainedVerificationCandidate
            && (refreshed.HasPossibleSideEffects || HasSucceededEffectReceipt(refreshed))
            ? _stateMachine.FromVerdict(refreshed, refreshed.LastOutput ?? string.Empty,
                new(ContinualAcceptanceOutcome.VerificationUnavailable, safeError + " Reconcile existing effects without repeating execution.") { EvaluationUnavailable = true })
            : _stateMachine.FromTurnError(refreshed, safeError, requiresAttention);
        var applied = await _store.ApplyTransitionAsync(objective.Id, leaseToken, transition, ct);
        if (!applied)
            return await LeaseLostOrStoppedAsync(objective.Id, executed: executed);
        await PublishAsync(
            refreshed,
            ContinualObjectiveActivityKind.Error,
            requiresAttention
                ? $"Objective needs attention before it can continue; it will try again after {transition.NextAttemptAtUtc:u}."
                : "Objective remains scheduled after an execution error.",
            safeError,
            ct: ct);
        return Result(
            objective.Id,
            ContinualObjectiveProcessDisposition.Processed,
            transition.Status,
            executed: executed,
            detail: safeError);
    }

    private async Task<ContinualObjectiveProcessResult> ApplyTerminalErrorAsync(
        ContinualObjectiveSnapshot objective,
        string leaseToken,
        string error,
        bool executed,
        CancellationToken ct)
    {
        var safeError = _sanitizer.Sanitize(error, 4_000) ?? "Continual execution failed.";
        var refreshed = await _store.GetAsync(objective.Id, ct) ?? objective;
        var transition = _stateMachine.FromTerminalError(refreshed, safeError);
        var applied = await _store.ApplyTransitionAsync(objective.Id, leaseToken, transition, ct);
        if (!applied)
            return await LeaseLostOrStoppedAsync(objective.Id, executed: executed);
        await PublishAsync(
            refreshed,
            ContinualObjectiveActivityKind.Error,
            "Objective ended after a terminal execution error.",
            safeError,
            ct: ct);
        return Result(
            objective.Id,
            ContinualObjectiveProcessDisposition.Processed,
            transition.Status,
            executed: executed,
            detail: safeError);
    }

    private sealed class DeadlineState
    {
        public volatile bool Expired;
        /// <summary>Why the watcher stopped the execution, when it was not the deadline.</summary>
        public volatile string? Fault;
    }

    /// <summary>
    /// How often a running mission re-reads its deadline, which a host may move or remove mid-run.
    /// Between reads the watcher sleeps until the earlier of the known deadline and the next refresh.
    /// </summary>
    internal static readonly TimeSpan DeadlineRefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>Never faults: a failed read keeps the last known deadline, and only a vanished budget stops the run.</summary>
    private async Task WatchDeadlineAsync(Guid id, CancellationTokenSource cancellation, DeadlineState state)
    {
        DateTimeOffset? effectiveDeadline = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var latest = await _store.GetAsync(id, cancellation.Token);
                    var read = latest?.Mission?.DeadlineUtc;
                    if (SwarmCore.Abstractions.Budgets.MissionBudgetExecutionScope.Current is { } budget)
                    {
                        var stateNow = await budget.Ledger.ReadAsync(budget.ProfileId, budget.Reference, cancellation.Token);
                        if (stateNow is null)
                        {
                            state.Fault = "The mission budget disappeared during execution; the run was stopped.";
                            await cancellation.CancelAsync();
                            return;
                        }
                        if (stateNow.Limits.DeadlineUtc is { } sharedDeadline && (read is null || sharedDeadline < read))
                            read = sharedDeadline;
                    }
                    effectiveDeadline = read;
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                {
                    _logger?.LogWarning("Deadline watch for objective {ObjectiveId} could not refresh ({ErrorType}); keeping the last known deadline.",
                        id, ex.GetType().Name);
                }
                var now = _time.GetUtcNow();
                if (effectiveDeadline is { } deadline && deadline <= now)
                {
                    state.Expired = true;
                    await cancellation.CancelAsync();
                    return;
                }
                var wait = effectiveDeadline is { } next && next - now < DeadlineRefreshInterval ? next - now : DeadlineRefreshInterval;
                await Task.Delay(wait, _time, cancellation.Token);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
            // The execution is over; nothing the watcher observed after that matters.
        }
    }
    private async Task RunHeartbeatAsync(
        ContinualObjectiveSnapshot objective,
        string leaseToken,
        CancellationTokenSource executionCancellation,
        HeartbeatState state)
    {
        while (!executionCancellation.IsCancellationRequested)
        {
            try
            {
                var now = _time.GetUtcNow();
                var renewed = await _store.HeartbeatAsync(
                    objective.Id,
                    leaseToken,
                    now,
                    now + _options.ExecutionLeaseDuration,
                    executionCancellation.Token);
                if (!renewed)
                {
                    // A successful status transition atomically releases the
                    // lease. A heartbeat racing just after that write is not a
                    // lease failure and must not turn Completed into a
                    // process-level cancellation result.
                    var saved = await _store.GetAsync(objective.Id, CancellationToken.None);
                    if (saved?.Status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.Cancelled)
                    {
                        await executionCancellation.CancelAsync();
                        return;
                    }
                    if (saved is not null
                        && saved.LeaseToken is null
                        && saved.Status != ContinualObjectiveStatus.InProgress)
                    {
                        return;
                    }
                    state.LeaseLost = true;
                    await executionCancellation.CancelAsync();
                    return;
                }
                await PublishAsync(
                    objective,
                    ContinualObjectiveActivityKind.LeaseHeartbeat,
                    "Execution lease renewed.",
                    ct: executionCancellation.Token);
                await Task.Delay(_options.HeartbeatInterval, _time, executionCancellation.Token);
            }
            catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                state.LeaseLost = true;
                await executionCancellation.CancelAsync();
                return;
            }
        }
    }

    private async Task<ContinualObjectiveProcessResult> LeaseLostOrStoppedAsync(
        Guid objectiveId,
        bool executed = false,
        bool reverified = false)
    {
        var saved = await _store.GetAsync(objectiveId, CancellationToken.None);
        return Result(
            objectiveId,
            saved?.Status == ContinualObjectiveStatus.Idle
                ? ContinualObjectiveProcessDisposition.Stopped
                : ContinualObjectiveProcessDisposition.LeaseLost,
            saved?.Status,
            executed,
            reverified);
    }

    private async Task<ContinualAcceptanceVerdict> ValidateCurrentEvidenceAsync(ContinualObjectiveSnapshot objective,
        ContinualAcceptanceVerdict verdict, CancellationToken ct)
    {
        if (verdict.Outcome == ContinualAcceptanceOutcome.Delivered && objective.Mission is not null
            && _executors.Get(objective.ExecutorKey) is IContinualObjectiveEvidenceValidator validator
            && await validator.ValidateEvidenceAsync(objective, ct) is { } reason)
            return new(ContinualAcceptanceOutcome.Retry, reason) { Verification = verdict.Verification is { } run
                ? run with { Criteria = run.Criteria.Select(c => c with { Outcome = CriterionOutcome.Inconclusive, Reason = reason }).ToArray() } : null };
        return verdict;
    }

    private ContinualAcceptanceRequest ToAcceptanceRequest(ContinualObjectiveSnapshot objective)
        => new(
            objective.Id,
            objective.Mission is null ? objective.ObjectiveText : objective.ObjectiveText + "\nRequired success criteria:\n"
                + string.Join("\n", objective.Mission.SuccessCriteria),
            objective.LastOutput ?? string.Empty,
            objective.Receipts,
            objective.HasPossibleSideEffects,
            objective.Provider,
            objective.Model) { Objective = objective };

    private async Task PublishTransitionAsync(
        ContinualObjectiveSnapshot objective,
        ContinualObjectiveTransition transition,
        CancellationToken ct)
        => await PublishAsync(
            objective,
            ContinualObjectiveActivityKind.StatusChanged,
            $"Objective is {transition.Status}.",
            transition.LastFailure,
            new Dictionary<string, string>
            {
                ["status"] = transition.Status.ToString(),
            },
            ct);

    private async Task PublishAsync(
        ContinualObjectiveSnapshot objective,
        ContinualObjectiveActivityKind kind,
        string description,
        string? detail = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        try
        {
            await _activity.PublishAsync(new ContinualObjectiveActivity(
                objective.Id,
                objective.PartitionKey,
                objective.Surface,
                kind,
                _sanitizer.Sanitize(description, 500) ?? description,
                _time.GetUtcNow(),
                _sanitizer.Sanitize(detail, 2_000),
                Metadata: metadata), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Activity is observability, never objective authority.
        }
    }

    private static ContinualObjectiveProcessResult Result(
        Guid id,
        ContinualObjectiveProcessDisposition disposition,
        ContinualObjectiveStatus? status = null,
        bool executed = false,
        bool reverified = false,
        string? detail = null)
        => new(id, disposition, status, executed, reverified, detail);

    private sealed class HeartbeatState
    {
        public volatile bool LeaseLost;
    }

    private sealed class DurableExecutionReporter : IContinualObjectiveExecutionReporter
    {
        private readonly ContinualObjectiveSnapshot _objective;
        private readonly string _leaseToken;
        private readonly IContinualObjectiveStore _store;
        private readonly IContinualObjectiveActivitySink _activity;
        private readonly IContinualObjectiveTextSanitizer _sanitizer;
        private readonly TimeProvider _time;

        public DurableExecutionReporter(
            ContinualObjectiveSnapshot objective,
            string leaseToken,
            IContinualObjectiveStore store,
            IContinualObjectiveActivitySink activity,
            IContinualObjectiveTextSanitizer sanitizer,
            TimeProvider time)
        {
            _objective = objective;
            _leaseToken = leaseToken;
            _store = store;
            _activity = activity;
            _sanitizer = sanitizer;
            _time = time;
        }

        public Task ToolStartedAsync(ContinualToolReceipt receipt, CancellationToken ct = default)
            => WriteReceiptAsync(receipt with { State = ContinualToolReceiptState.Started }, ct);

        public Task ToolCompletedAsync(ContinualToolReceipt receipt, CancellationToken ct = default)
            => WriteReceiptAsync(receipt with { State = ContinualToolReceiptState.Completed }, ct);

        public async Task ProgressAsync(
            long sequence,
            string? toolName,
            string status,
            double? fraction = null,
            CancellationToken ct = default)
        {
            var safe = _sanitizer.Sanitize(status, 2_000) ?? "Working";
            if (!await _store.RecordProgressAsync(
                    _objective.Id,
                    _leaseToken,
                    sequence,
                    toolName,
                    safe,
                    _time.GetUtcNow(),
                    ct))
            {
                throw new ContinualObjectiveLeaseLostException(_objective.Id);
            }
            await PublishToolActivityAsync(
                ContinualObjectiveActivityKind.ToolProgress,
                safe,
                null,
                toolName,
                fraction,
                ct);
        }

        private async Task WriteReceiptAsync(
            ContinualToolReceipt receipt,
            CancellationToken ct)
        {
            if (receipt.ObjectiveId != _objective.Id || receipt.Attempt != _objective.AttemptCount)
                throw new InvalidOperationException("A continual tool receipt must identify the active objective and attempt.");
            var safe = receipt with
            {
                InputJson = _sanitizer.Sanitize(receipt.InputJson, int.MaxValue),
                Result = _sanitizer.Sanitize(receipt.Result, int.MaxValue),
            };
            if (_objective.Mission is not null && safe.State == ContinualToolReceiptState.Completed)
                safe = safe with { Evidence = new(MissionVerification.Digest(safe.Result ?? ""),
                    "host-tool:" + safe.ToolName, safe.CompletedAtUtc ?? _time.GetUtcNow(),
                    _objective.PartitionKey, "LocalOnly; retained with mission history", safe.Evidence?.RepositoryRevision,
                    safe.Evidence?.ArchiveReference) };
            if (!await _store.UpsertReceiptAsync(_objective.Id, _leaseToken, safe, ct))
                throw new ContinualObjectiveLeaseLostException(_objective.Id);
            await PublishToolActivityAsync(
                safe.State == ContinualToolReceiptState.Started
                    ? ContinualObjectiveActivityKind.ToolStarted
                    : ContinualObjectiveActivityKind.ToolCompleted,
                safe.State == ContinualToolReceiptState.Started
                    ? $"Tool {safe.ToolName} started."
                    : $"Tool {safe.ToolName} {(safe.IsError ? "failed" : "completed")}.",
                safe.ToolCallId,
                safe.ToolName,
                null,
                ct);
        }

        private async Task PublishToolActivityAsync(
            ContinualObjectiveActivityKind kind,
            string description,
            string? toolCallId,
            string? toolName,
            double? fraction,
            CancellationToken ct)
        {
            try
            {
                await _activity.PublishAsync(new ContinualObjectiveActivity(
                    _objective.Id,
                    _objective.PartitionKey,
                    _objective.Surface,
                    kind,
                    _sanitizer.Sanitize(description, 500) ?? description,
                    _time.GetUtcNow(),
                    ToolCallId: toolCallId,
                    ToolName: toolName,
                    Fraction: fraction), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
    }
}

public sealed class ContinualObjectiveLeaseLostException : InvalidOperationException
{
    public ContinualObjectiveLeaseLostException(Guid objectiveId)
        : base($"The execution lease for continual objective '{objectiveId}' was lost.")
    {
        ObjectiveId = objectiveId;
    }

    public Guid ObjectiveId { get; }
}

public interface IContinualObjectiveExecutionControl
{
    bool TryRegister(Guid objectiveId, CancellationTokenSource cancellation);
    bool Cancel(Guid objectiveId);
    void Release(Guid objectiveId, CancellationTokenSource cancellation);
}

public sealed class ContinualObjectiveExecutionControl : IContinualObjectiveExecutionControl
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public bool TryRegister(Guid objectiveId, CancellationTokenSource cancellation)
        => _active.TryAdd(objectiveId, cancellation);

    public bool Cancel(Guid objectiveId)
    {
        if (!_active.TryGetValue(objectiveId, out var cancellation)) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Release(Guid objectiveId, CancellationTokenSource cancellation)
        => _active.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(objectiveId, cancellation));
}
