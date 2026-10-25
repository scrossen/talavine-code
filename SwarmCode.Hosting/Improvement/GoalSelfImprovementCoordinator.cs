using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Improvement;

/// <summary>The existing improvement worker repairs code; this adapter links its durable receipt back to the stopped goal.</summary>
public sealed class GoalSelfImprovementCoordinator(SelfImprovementReporter reporter, ICodingGoalStore goals,
    ISelfImprovementInstalledFixVerifier installed, ILogger<GoalSelfImprovementCoordinator>? logger = null, Func<bool>? paused = null,
    GoalRepairActivationFeedback? activationFeedback = null)
{
    public const string Source = "goal-harness-recovery";

    /// <summary>Observe only; no goal, worker, execution lease or external operation is created.</summary>
    public async Task<SelfImprovementItem?> ObserveAsync(ContinualObjectiveSnapshot goal, CancellationToken ct = default)
    {
        if (!reporter.Options.Enabled || !reporter.Options.CaptureDefects || goal.Mission is not null
            || goal.PartitionKey != CodingGoalOrchestrator.Partition
            || goal.Status is not (ContinualObjectiveStatus.Uncertain or ContinualObjectiveStatus.Failed
                or ContinualObjectiveStatus.AttentionRequired) || string.IsNullOrWhiteSpace(goal.LastFailure)) return null;
        var candidate = DefectClassifier.FromErrorText(goal.LastFailure, null);
        var inspectionGap = goal.Status == ContinualObjectiveStatus.Uncertain && goal.Receipts.Any(MissionTransitions.NeedsInspection);
        if (candidate is null && !inspectionGap) return null; // Credentials, timeouts, and ordinary input needs are not code defects.
        var link = GoalRepairLink.From(goal, DateTimeOffset.UtcNow);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"goal-harness-recovery\n{goal.Id:D}\n{link.WorkIntentDigest}\n{candidate?.Fingerprint ?? "outcome-inspection-contract"}"))).ToLowerInvariant();
        var existing = (await reporter.Backlog.ListAsync(ct).ConfigureAwait(false)).SingleOrDefault(i => i.Fingerprint == fingerprint);
        var item = existing ?? await reporter.ReportAsync(new(SelfImprovementKind.Defect,
            inspectionGap ? "Goal recovery needs an outcome inspection contract" : "Goal stopped on a harness defect",
            "A saved goal stopped before achieving its objective. Reproduce the harness failure using synthetic inputs, " +
            "repair the generic contract and verify it with independent tests. The original conversation retains all tool receipts. " +
            "Do not send messages, repeat commands from the original goal, clear uncertain receipts, replace its objective, " +
            "or resume paused/cancelled work. A code change alone never proves an external operation's outcome. " +
            "Continue the same goal only after the repaired build is verified and all effect holds are independently resolved.",
            fingerprint, Source, goal.ConversationId), ct).ConfigureAwait(false);
        if (item is null) return null;
        if (item.GoalRepair is { } prior)
        {
            // One root defect keeps one backlog identity across retries. Do not change an active repair's
            // baseline or resurrect a dismissal/human decision. A fix that is verified but not installed stays verified.
            if (prior.GoalVersion >= goal.Version || prior.WorkIntentDigest != link.WorkIntentDigest
                || item.Stage is not (SelfImprovementStage.Queued or SelfImprovementStage.Merged)) return item;
            var installedButRecurring = item.Stage == SelfImprovementStage.Merged
                && await installed.VerifyAsync(item, ct).ConfigureAwait(false) is not null;
            try
            {
                return await reporter.Backlog.UpdateAsync(item.Id, fresh => fresh with
                {
                    GoalRepair = link, Occurrences = fresh.Occurrences + 1, LastSeenUtc = DateTime.UtcNow,
                    Stage = installedButRecurring ? SelfImprovementStage.Queued : fresh.Stage,
                    Note = installedButRecurring ? "The same harness failure recurred after this fix was installed; another verified repair is queued." : fresh.Note,
                }, item.Revision, ct).ConfigureAwait(false);
            }
            catch (SelfImprovementConflictException) { return await reporter.Backlog.GetAsync(item.Id, ct).ConfigureAwait(false); }
        }
        // Apply only the field this writer owns to the fresh record. A competing dismissal or repair stage is preserved.
        return await reporter.Backlog.UpdateAsync(item.Id, fresh => fresh.GoalRepair is null
            ? fresh with { GoalRepair = link } : null, ct: ct).ConfigureAwait(false);
    }

    /// <summary>After restart, verify installed bytes before CAS-scheduling the same goal. Does not start an executor.</summary>
    public async Task<ContinualObjectiveSnapshot?> ResumeAfterVerifiedFixAsync(Guid goalId, CancellationToken ct = default)
    {
        if (activationFeedback is not null) await activationFeedback.RequeueAsync(goalId, ct).ConfigureAwait(false);
        var current = await goals.GetAsync(goalId, ct).ConfigureAwait(false);
        bool Allowed() => reporter.Options.Enabled && reporter.Options.AutoApplyGoalRepairs && paused?.Invoke() != true;
        if (current is null || !Allowed()) return null;
        foreach (var item in (await reporter.Backlog.ListAsync(ct).ConfigureAwait(false))
            .Where(i => i.GoalRepair?.GoalId == goalId && i.Stage == SelfImprovementStage.Merged))
        {
            var pending = CodingGoalPayload.Read(current).RepairContinuation;
            var scheduled = current.Status == ContinualObjectiveStatus.RetryScheduled && current.LeaseToken is null
                && pending is not null && pending.ItemId == item.Id && pending.ScheduledVersion == current.Version
                && pending.IntentDigest == GoalRepairLink.Intent(current) && pending.IntentDigest == item.GoalRepair!.IntentDigest;
            if (!scheduled && !item.GoalRepair!.CanResume(current, DateTimeOffset.UtcNow)) continue;
            var evidence = await installed.VerifyAsync(item, ct).ConfigureAwait(false);
            if (evidence is null || !Allowed()) continue;
            if (scheduled) return current;
            if (!await goals.TryResumeAfterRepairAsync(item.GoalRepair!, item.Id, evidence, DateTimeOffset.UtcNow, ct).ConfigureAwait(false)) continue;
            logger?.LogInformation("Resuming goal {Goal} after verified installed repair {Item}.", goalId, item.Id);
            return await goals.GetAsync(goalId, ct).ConfigureAwait(false);
        }
        return null;
    }
}

/// <summary>Trusted host service, never a model tool. A branch merge or model claim is insufficient evidence.</summary>
public interface ISelfImprovementInstalledFixVerifier
{
    Task<string?> VerifyAsync(SelfImprovementItem item, CancellationToken ct = default);
}
