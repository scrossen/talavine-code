using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Goals;
using SwarmCode.Hosting.Improvement;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalSelfImprovementTests : TempDirTest
{
    private sealed class Verified : ISelfImprovementInstalledFixVerifier
    {
        public bool Ready;
        public Task<string?> VerifyAsync(SelfImprovementItem item, CancellationToken ct = default)
            => Task.FromResult(Ready ? "Exact installed artifact independently verified." : null);
    }

    private sealed class DeferredVerification : ISelfImprovementInstalledFixVerifier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> VerifyAsync(SelfImprovementItem item, CancellationToken ct = default)
        { Entered.TrySetResult(); return Finished.Task; }
    }

    private sealed class ForbiddenExecutor : ICodingTurnOrchestrator, IContinualObjectiveAcceptanceService
    {
        public int Calls;
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane? ResolveLane(string? name) => null;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("A fresh Stop must prevent any execution."); }
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("A stopped goal cannot reach completion verification.");
    }

    private async Task<ContinualObjectiveSnapshot> StoppedAsync(FileCodingGoalStore store, bool unresolved = false)
    {
        var now = DateTimeOffset.UtcNow;
        var goal = await store.CreateOrJoinAsync(new()
        {
            Surface = "coding", PartitionKey = CodingGoalOrchestrator.Partition,
            ConversationId = Guid.NewGuid().ToString("N"), IdempotencyKey = Guid.NewGuid().ToString("N"), ExecutorKey = "coding",
            ObjectiveText = "Finish the remaining synthetic work.", ObjectiveSummary = "Synthetic goal",
            Authority = ContinualObjectiveAuthority.UserRequested("synthetic-user"),
            ExecutorPayloadJson = JsonSerializer.Serialize(new CodingGoalPayload([], Tmp)),
        }, now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "original", now, now.AddMinutes(1)))!;
        if (unresolved)
            Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, new(goal.Id, 1, 1, "one-send", "synthetic.send",
                ContinualToolReceiptState.Completed, ContinualSideEffectClass.ExternalSend, "{}", "unknown", true, now, now)));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.Uncertain,
            now, now, null, "Saved completed output", "InvalidOperationException: synthetic harness defect", null,
            "Inspect the saved results.", 0)));
        return (await store.GetAsync(goal.Id))!;
    }

    private async Task<SelfImprovementItem> ReadyAsync(ISelfImprovementBacklog backlog, GoalSelfImprovementCoordinator coordinator, ContinualObjectiveSnapshot goal)
    {
        var item = (await coordinator.ObserveAsync(goal))!;
        return (await backlog.UpdateAsync(item.Id, current => current with
        { Stage = SelfImprovementStage.Merged, Commit = "synthetic-verified-commit", Evidence = "Independent build and regression checks passed.", LastAttemptUtc = DateTime.UtcNow }))!;
    }

    [Fact]
    public async Task Failure_link_survives_restart_and_unchanged_observations_do_not_spawn_duplicate_repairs()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await StoppedAsync(store);
        var folder = Path.Combine(Tmp, "backlog");
        var backlog = new JsonFileSelfImprovementBacklog(folder);
        var first = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, new Verified());
        var items = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => first.ObserveAsync(goal)));
        var id = Assert.Single(items.Select(i => i!.Id).Distinct());
        var restarted = new JsonFileSelfImprovementBacklog(folder);
        var second = new GoalSelfImprovementCoordinator(new(restarted, () => new() { AutoApplyGoalRepairs = true }), new FileCodingGoalStore(Paths), new Verified());
        Assert.Equal(id, (await second.ObserveAsync(goal))!.Id);
        var saved = Assert.Single(await restarted.ListAsync());
        Assert.Equal(goal.Id, saved.GoalRepair!.GoalId);
        Assert.Equal(goal.Version, saved.GoalRepair.GoalVersion);
        Assert.Equal(JsonSerializer.Serialize(goal), JsonSerializer.Serialize(await store.GetAsync(goal.Id)));
    }

    [Fact]
    public async Task Only_verified_installed_fix_can_schedule_one_same_goal_and_preserve_saved_results_and_user_authority()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await StoppedAsync(store);
        var backlog = new InMemorySelfImprovementBacklog();
        var verifier = new Verified();
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, verifier);
        await ReadyAsync(backlog, coordinator, goal);
        Assert.Null(await coordinator.ResumeAfterVerifiedFixAsync(goal.Id)); // Merge alone is insufficient.
        verifier.Ready = true;
        var restarted = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), new FileCodingGoalStore(Paths), verifier);
        var winners = await Task.WhenAll(coordinator.ResumeAfterVerifiedFixAsync(goal.Id), restarted.ResumeAfterVerifiedFixAsync(goal.Id));
        Assert.NotEmpty(winners.Where(g => g is not null));
        var after = (await store.GetAsync(goal.Id))!;
        Assert.Equal(goal.Id, after.Id); Assert.Equal(ContinualObjectiveStatus.RetryScheduled, after.Status);
        Assert.Equal(goal.Version + 1, after.Version); Assert.Equal(goal.AttemptCount, after.AttemptCount);
        Assert.Equal(GoalRepairLink.Intent(goal), GoalRepairLink.Intent(after));
        Assert.Null(CodingGoalPayload.Read(after).LastUserControl);
        Assert.NotNull(CodingGoalPayload.Read(after).RepairContinuation);
        Assert.Equal(JsonSerializer.Serialize(goal.Authority), JsonSerializer.Serialize(after.Authority));
        Assert.Equal(goal.ObjectiveText, after.ObjectiveText); Assert.Equal(goal.LastOutput, after.LastOutput);
        Assert.Equal(JsonSerializer.Serialize(goal.Receipts), JsonSerializer.Serialize(after.Receipts));
        Assert.Null(after.LeaseToken);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("cancel")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("lease")]
    public async Task Newer_controls_deleted_goals_and_effect_holds_cannot_be_overridden_by_a_repair(string interleaving)
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await StoppedAsync(store, interleaving == "uncertain-effect");
        var backlog = new InMemorySelfImprovementBacklog();
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, new Verified { Ready = true });
        await ReadyAsync(backlog, coordinator, goal);
        if (interleaving == "pause") await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow);
        if (interleaving == "cancel") await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Cancel, DateTimeOffset.UtcNow);
        if (interleaving == "edit") await store.UpdateAsync(goal.Id, goal.Version, "A different objective", DateTimeOffset.UtcNow);
        if (interleaving == "delete") await store.DeleteConversationAsync(goal.ConversationId);
        if (interleaving == "lease")
        {
            Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
            var due = (await store.GetAsync(goal.Id))!;
            Assert.NotNull(await store.TryClaimAsync(goal.Id, due.Version, "other-owner", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        var before = JsonSerializer.Serialize(await store.GetAsync(goal.Id));
        Assert.Null(await coordinator.ResumeAfterVerifiedFixAsync(goal.Id));
        Assert.Equal(before, JsonSerializer.Serialize(await store.GetAsync(goal.Id)));
    }

    [Fact]
    public async Task Unrelated_environment_failures_and_explicit_pauses_do_not_queue_source_rewrites()
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store);
        var backlog = new InMemorySelfImprovementBacklog();
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, new Verified());
        Assert.Null(await coordinator.ObserveAsync(goal with { LastFailure = "HttpRequestException: upstream unavailable" }));
        Assert.Null(await coordinator.ObserveAsync(goal with { Status = ContinualObjectiveStatus.Paused }));
        Assert.Empty(await backlog.ListAsync());
    }

    [Fact]
    public async Task Installed_repair_schedules_inspection_without_clearing_or_replaying_uncertain_effects()
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store, unresolved: true);
        var backlog = new InMemorySelfImprovementBacklog();
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, new Verified { Ready = true });
        await ReadyAsync(backlog, coordinator, goal);
        var after = (await coordinator.ResumeAfterVerifiedFixAsync(goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.RetryScheduled, after.Status);
        Assert.Equal(JsonSerializer.Serialize(goal.Receipts), JsonSerializer.Serialize(after.Receipts));
        Assert.True(MissionTransitions.NeedsInspection(Assert.Single(after.Receipts)));
        Assert.Contains("host recovery gate", after.ContinuationFeedback);
        Assert.Equal(goal.AttemptCount, after.AttemptCount); Assert.Null(after.LeaseToken);
    }

    [Fact]
    public async Task Pending_verified_repair_survives_a_restart_before_dispatch_and_newer_pause_still_wins()
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store);
        var backlog = new JsonFileSelfImprovementBacklog(Path.Combine(Tmp, "repair-backlog"));
        var options = new SelfImprovementOptions { AutoApplyGoalRepairs = true };
        var paused = false;
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => options), store, new Verified { Ready = true }, paused: () => paused);
        await ReadyAsync(backlog, coordinator, goal);
        options.AutoApplyGoalRepairs = false; Assert.Null(await coordinator.ResumeAfterVerifiedFixAsync(goal.Id));
        options.AutoApplyGoalRepairs = true; paused = true; Assert.Null(await coordinator.ResumeAfterVerifiedFixAsync(goal.Id));
        paused = false;
        var scheduled = (await coordinator.ResumeAfterVerifiedFixAsync(goal.Id))!;
        var restarted = new GoalSelfImprovementCoordinator(new(new JsonFileSelfImprovementBacklog(Path.Combine(Tmp, "repair-backlog")), () => options),
            new FileCodingGoalStore(Paths), new Verified { Ready = true });
        var pending = (await restarted.ResumeAfterVerifiedFixAsync(goal.Id))!;
        Assert.Equal(JsonSerializer.Serialize(scheduled), JsonSerializer.Serialize(pending));
        Assert.True(await store.ControlAsync(goal.Id, pending.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        Assert.Null(await restarted.ResumeAfterVerifiedFixAsync(goal.Id));
        Assert.Equal(ContinualObjectiveStatus.Paused, (await store.GetAsync(goal.Id))!.Status);
    }

    [Fact]
    public async Task Stop_during_installed_artifact_verification_persists_a_real_pause_and_prevents_dispatch_after_restart()
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store);
        var backlog = new InMemorySelfImprovementBacklog();
        var verifier = new DeferredVerification(); var executor = new ForbiddenExecutor();
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, verifier);
        await ReadyAsync(backlog, coordinator, goal);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<ICodingGoalStore>(store);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(coordinator);
        services.AddSingleton(new CodingGoalOrchestrator(executor, store, executor));
        await using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, goal.ConversationId);
        var loading = runner.LoadHistoryAsync();
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Cancel(); // Fresh Stop while the verifier is awaiting its independent result.
        verifier.Finished.TrySetResult("Exact installed artifact verified.");
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, executor.Calls); Assert.False(runner.IsRunning);
        var paused = (await store.GetAsync(goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Paused, paused.Status);
        Assert.Equal(CodingGoalControl.Pause, CodingGoalPayload.Read(paused).LastUserControl!.Control);
        Assert.Null(await coordinator.ResumeAfterVerifiedFixAsync(goal.Id));
    }

    [Fact]
    public async Task A_newer_explicit_resume_after_Stop_wins_over_the_late_repair_verifier()
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store);
        var backlog = new InMemorySelfImprovementBacklog(); var verifier = new DeferredVerification(); var executor = new ForbiddenExecutor();
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, verifier);
        await ReadyAsync(backlog, coordinator, goal);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ICodingGoalStore>(store);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths)); services.AddSingleton(coordinator);
        services.AddSingleton(new CodingGoalOrchestrator(executor, store, executor));
        await using var provider = services.BuildServiceProvider(); await using var runner = new ConversationRunner(provider, goal.ConversationId);
        var loading = runner.LoadHistoryAsync(); await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Cancel();
        Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
        var explicitResume = JsonSerializer.Serialize(await store.GetAsync(goal.Id));
        verifier.Finished.TrySetResult("Exact installed artifact verified."); await loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, executor.Calls);
        Assert.Equal(explicitResume, JsonSerializer.Serialize(await store.GetAsync(goal.Id)));
        Assert.Equal(CodingGoalControl.Resume, CodingGoalPayload.Read((await store.GetAsync(goal.Id))!).LastUserControl!.Control);
    }

    [Fact]
    public async Task Disabling_automatic_repairs_while_verification_waits_does_not_schedule_a_goal()
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store);
        var backlog = new InMemorySelfImprovementBacklog(); var verifier = new DeferredVerification();
        var options = new SelfImprovementOptions { AutoApplyGoalRepairs = true };
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => options), store, verifier);
        await ReadyAsync(backlog, coordinator, goal);
        var verifying = coordinator.ResumeAfterVerifiedFixAsync(goal.Id);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        options.AutoApplyGoalRepairs = false; verifier.Finished.TrySetResult("Exact artifact verified.");
        Assert.Null(await verifying.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(JsonSerializer.Serialize(goal), JsonSerializer.Serialize(await store.GetAsync(goal.Id)));
    }

    [Theory]
    [InlineData(SelfImprovementStage.Queued)]
    [InlineData(SelfImprovementStage.Merged)]
    [InlineData(SelfImprovementStage.InProgress)]
    [InlineData(SelfImprovementStage.Verified)]
    [InlineData(SelfImprovementStage.Dismissed)]
    [InlineData(SelfImprovementStage.NeedsHuman)]
    public async Task Recurring_same_root_defect_has_one_durable_item_and_preserves_active_or_human_decisions(SelfImprovementStage stage)
    {
        var store = new FileCodingGoalStore(Paths); var goal = await StoppedAsync(store);
        var backlog = new InMemorySelfImprovementBacklog(); var verifier = new Verified { Ready = true };
        var coordinator = new GoalSelfImprovementCoordinator(new(backlog, () => new() { AutoApplyGoalRepairs = true }), store, verifier);
        var item = await ReadyAsync(backlog, coordinator, goal);
        await backlog.UpdateAsync(item.Id, fresh => fresh with { Stage = stage });
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Resume, now));
        var due = (await store.GetAsync(goal.Id))!;
        var lease = (await store.TryClaimAsync(goal.Id, due.Version, "same-goal-next-attempt", now, now.AddMinutes(1)))!;
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.Uncertain, now, now,
            null, "Saved completed output", goal.LastFailure, null, "Inspect current saved results.", 0)));
        var stoppedAgain = (await store.GetAsync(goal.Id))!;
        var observed = (await coordinator.ObserveAsync(stoppedAgain))!;
        Assert.Equal(item.Id, observed.Id); Assert.Single(await backlog.ListAsync());
        if (stage is SelfImprovementStage.Queued or SelfImprovementStage.Merged)
        {
            Assert.Equal(stoppedAgain.Version, observed.GoalRepair!.GoalVersion);
            Assert.Equal(SelfImprovementStage.Queued, observed.Stage);
        }
        else
        {
            Assert.Equal(goal.Version, observed.GoalRepair!.GoalVersion); Assert.Equal(stage, observed.Stage);
        }
    }
}
