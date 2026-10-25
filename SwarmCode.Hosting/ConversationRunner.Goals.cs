using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Files;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting;

public sealed partial class ConversationRunner
{
    private ContinualObjectiveSnapshot? _goal;
    private bool _runningGoal;
    private bool _jobIsGoal;

    public ContinualObjectiveSnapshot? Goal { get { lock (_gate) return _goal; } }
    public bool IsGoalRunning { get { lock (_gate) return _running && _runningGoal; } }
    public bool IsGoalJob { get { lock (_gate) return _jobIsGoal; } }
    public bool HasUnfinishedGoal { get { lock (_gate) return _goal is { } goal && !GoalEnded(goal); } }
    public string GoalStatusText => FormatGoal(Goal);

    public static string FormatGoal(ContinualObjectiveSnapshot? goal)
    {
        if (goal is null) return "No goal set. Use /goal <objective> to keep working until it is verified.";
        var status = goal.Status switch
        {
            ContinualObjectiveStatus.Completed => "Goal completed and verified",
            ContinualObjectiveStatus.Paused => "Goal paused — /goal resume to continue",
            ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Idle => "Goal cancelled",
            ContinualObjectiveStatus.WaitingForInput => "Goal needs your input — /goal resume after providing it",
            ContinualObjectiveStatus.AttentionRequired => "Goal retry scheduled",
            ContinualObjectiveStatus.Failed or ContinualObjectiveStatus.DeliveryBlocked => "Goal needs attention — /goal resume to retry",
            ContinualObjectiveStatus.Uncertain => "Goal interrupted — /goal resume to inspect saved results",
            ContinualObjectiveStatus.VerificationPending => "Checking goal completion",
            ContinualObjectiveStatus.RetryScheduled => "Continuing goal",
            _ => "Working toward goal",
        };
        return $"{status}: {goal.ObjectiveSummary}";
    }

    private static bool GoalEnded(ContinualObjectiveSnapshot goal)
        => goal.Status is ContinualObjectiveStatus.Completed or ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Idle;

    public Task<bool> StartGoalAsync(string objective, string? laneName,
        CodingPermissionMode permissionMode = CodingPermissionMode.Auto, bool? allowHandOff = null,
        string? modelOverride = null, IReadOnlyList<CodingAttachment>? attachments = null)
    {
        lock (_gate)
        {
            if (_planMode || _goal is { } goal && !GoalEnded(goal)) return Task.FromResult(false);
            return Task.FromResult(TrySendCore(objective, laneName, permissionMode, allowHandOff, modelOverride,
                false, attachments, null, null, startGoal: true));
        }
    }

    public bool ResumeGoal(string? laneName, CodingPermissionMode permissionMode = CodingPermissionMode.Auto,
        bool? allowHandOff = null, string? modelOverride = null)
    {
        lock (_gate)
        {
            if (_planMode || _goal is not { } goal || GoalEnded(goal)) return false;
            // A later ordinary message must never replace the saved goal's authenticated intent.
            return TrySendCore(goal.ObjectiveText, laneName, permissionMode, allowHandOff, modelOverride,
                true, CodingGoalPayload.Read(goal).Attachments, null, new CodingContinuationContext(goal.ObjectiveText, []), goalId: goal.Id);
        }
    }

    public Task PauseGoalAsync() => ControlGoalAsync(CodingGoalControl.Pause);
    public Task CancelGoalAsync() => ControlGoalAsync(CodingGoalControl.Cancel);

    private async Task ControlGoalAsync(CodingGoalControl control)
    {
        if (_services.GetService<CodingGoalOrchestrator>() is not { } goals) return;
        // Stop dispatch immediately, including the small interval before initial registration.
        bool stoppingGoal;
        lock (_gate)
        {
            stoppingGoal = _runningGoal;
            if (stoppingGoal) { ++_stopGeneration; _resumeSteering = false; _queuePaused = true; _turnCts?.Cancel(); }
        }
        var snapshot = await goals.ControlAsync(ConversationId, control).ConfigureAwait(false);
        lock (_gate) _goal = snapshot ?? _goal;
        if (stoppingGoal) DeclinePendingQuestions();
        Changed?.Invoke();
    }

    private async Task LoadGoalAsync(CancellationToken ct)
    {
        if (_services.GetService<CodingGoalOrchestrator>() is not { } goals) return;
        var snapshot = await goals.GetAsync(ConversationId, ct).ConfigureAwait(false);
        lock (_gate) _goal = snapshot;
        if (snapshot is not null && _services.GetService<Improvement.GoalSelfImprovementCoordinator>() is { } repair)
            await repair.ObserveAsync(snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Called after restored history is applied. Only a verified newly installed repair can continue stopped work.</summary>
    public async Task<bool> TryContinueAfterInstalledRepairAsync(CancellationToken ct = default)
    {
        if (_services.GetService<Improvement.GoalSelfImprovementCoordinator>() is not { } repair) return false;
        ContinualObjectiveSnapshot? before;
        long stopGeneration;
        lock (_gate)
        {
            if (_disposed || _running || _planMode || _goal is null) return false;
            before = _goal;
            stopGeneration = _stopGeneration;
        }
        var resumed = await Task.Run(() => repair.ResumeAfterVerifiedFixAsync(before.Id, ct), ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_stopGeneration == stopGeneration)
            {
                if (resumed is null || _disposed || _running || _planMode || _goal?.Id != resumed.Id) return false;
                _goal = resumed;
                return TrySendCore(resumed.ObjectiveText, ActiveLane, CodingPermissionMode.Auto, null, ActiveModel ?? resumed.Model,
                    true, CodingGoalPayload.Read(resumed).Attachments,
                    new TurnOrigin(resumed.Authority.AuthorityId, resumed.Authority.Unattended,
                        new Dictionary<string, string> { ["swarmcode.installed-goal-repair"] = resumed.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                    new CodingContinuationContext(resumed.ObjectiveText, []), goalId: resumed.Id);
            }
        }
        {
            // Stop was pressed while the host was checking the new artifact. It is a new user control,
            // unlike the queue pause left by the old failed turn; persist it so restart cannot undo it.
            if (_services.GetService<ICodingGoalStore>() is { } store)
            {
                var current = await store.GetAsync(before.Id, CancellationToken.None).ConfigureAwait(false);
                var capturedIntent = SwarmCore.Coding.SelfImprovement.GoalRepairLink.Intent(before);
                while (current is not null && !GoalEnded(current) && current.Status != ContinualObjectiveStatus.Paused
                    && SwarmCore.Coding.SelfImprovement.GoalRepairLink.Intent(current) == capturedIntent
                    && (current.Version == before.Version || current.Version == before.Version + 1
                        && CodingGoalPayload.Read(current).RepairContinuation is { } pending
                        && pending.ScheduledVersion == current.Version && pending.IntentDigest == capturedIntent))
                {
                    if (await store.ControlAsync(current.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false)) break;
                    current = await store.GetAsync(before.Id, CancellationToken.None).ConfigureAwait(false);
                }
                await LoadGoalAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return false;
        }
    }

    private async IAsyncEnumerable<TurnEvent> RunGoalStreamAsync(CodingTurnRequest request, bool start,
        Guid? goalId, CodingContinuationContext? correction, [EnumeratorCancellation] CancellationToken ct)
    {
        var goals = _services.GetRequiredService<CodingGoalOrchestrator>();
        ContinualObjectiveSnapshot? snapshot;
        if (start) snapshot = await goals.StartAsync(request, ct).ConfigureAwait(false);
        else
        {
            if (correction is { Updates.Count: > 0 })
                await goals.UpdateAsync(ConversationId, correction.AuthenticatedUserRequest, ct, request.Attachments).ConfigureAwait(false);
            if (request.Metadata?.TryGetValue("swarmcode.installed-goal-repair", out var repairedVersion) == true)
            {
                snapshot = await goals.GetAsync(ConversationId, ct).ConfigureAwait(false);
                if (snapshot?.Status != ContinualObjectiveStatus.RetryScheduled
                    || snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) != repairedVersion)
                    throw new InvalidOperationException("The repaired goal changed before continuation. Its saved progress was retained.");
            }
            else snapshot = await goals.ControlAsync(ConversationId, CodingGoalControl.Resume, ct).ConfigureAwait(false);
            if (snapshot is null || snapshot.Id != goalId)
                throw new InvalidOperationException("The saved goal changed. Check /goal status before resuming.");
        }
        lock (_gate) _goal = snapshot;
        await foreach (var evt in goals.RunAsync(request, snapshot.Id, ct).ConfigureAwait(false)) yield return evt;
    }
}
