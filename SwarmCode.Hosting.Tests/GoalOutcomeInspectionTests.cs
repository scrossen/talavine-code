using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalOutcomeInspectionTests : TempDirTest
{
    private sealed class Runner : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane ResolveLane(string? name) => new("synthetic", "Synthetic", "synthetic");
        public int Calls;
        public List<CodingTurnRequest> Requests { get; } = [];
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++; Requests.Add(request);
            await Task.Yield();
            yield return new ToolCallStarted(request.ConversationId, "fresh-read", "file.read", new JsonObject())
                { SideEffect = SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly };
            yield return new ToolCallCompleted(request.ConversationId, "fresh-read", "file.read", "Current requested result verified.", false, TimeSpan.Zero)
                { SideEffect = SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly };
            yield return new TurnCompleted(request.ConversationId, "The remaining work is verified.", TimeSpan.Zero);
        }
    }

    private sealed class Acceptance : IContinualObjectiveAcceptanceService
    {
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => Task.FromResult(new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Delivered, "Synthetic requested outcome verified."));
    }

    private sealed class Inspector(Func<ContinualObjectiveSnapshot, ContinualToolReceipt, Task<CodingGoalOutcomeInspection?>> inspect)
        : ICodingGoalOutcomeInspector
    {
        public string Identity => "synthetic-host-physical-readback-v1";
        public int Calls;
        public Task<CodingGoalOutcomeInspection?> InspectAsync(ContinualObjectiveSnapshot goal, ContinualToolReceipt target, CancellationToken ct = default)
        { Calls++; return inspect(goal, target); }
    }

    private sealed class Policy(Func<CodingTurnRequest, CodingTurnRequest> prepare) : ICodingGoalRequestPolicy
    {
        public Task<CodingTurnRequest> PrepareAsync(CodingTurnRequest request, CancellationToken ct = default)
            => Task.FromResult(prepare(request));
    }

    private async Task<(FileCodingGoalStore Store, CodingTurnRequest Request, ContinualObjectiveSnapshot Goal, ContinualToolReceipt Target)> Seed(
        string userMessage = "Verify the saved result and finish remaining work.", CodingWorkspace? workspace = null)
    {
        var store = new FileCodingGoalStore(Paths);
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest
        { ConversationId = "synthetic-goal", UserMessage = userMessage, Workspace = workspace },
            CodingPermissionPolicy.AutoNoRules, "synthetic-local-user");
        var goals = new CodingGoalOrchestrator(new Runner(), store, new Acceptance());
        var goal = await goals.StartAsync(request);
        var now = DateTimeOffset.UtcNow;
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "previous-worker", now, now.AddMinutes(1)))!;
        var target = new ContinualToolReceipt(goal.Id, 1, 1, "write-once", "synthetic.external-send",
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ExternalSend, "{\"key\":\"once\"}",
            "Transport disconnected after dispatch.", true, now, now);
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, target));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.Uncertain,
            now, now, null, "Retained artifact", "Inspect prior outcome", null, null, 0)));
        goal = (await store.GetAsync(goal.Id))!;
        Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
        return (store, request, (await store.GetAsync(goal.Id))!, target);
    }

    [Fact]
    public async Task Trusted_host_inspection_settles_exact_outcome_then_continues_same_goal_without_replaying()
    {
        var h = await Seed(); var runner = new Runner();
        var inspector = new Inspector((goal, target) =>
        {
            Assert.NotNull(goal.LeaseToken); Assert.Equal(h.Target.ToolCallId, target.ToolCallId);
            return Task.FromResult<CodingGoalOutcomeInspection?>(new(ContinualReceiptResolutionOutcome.KnownOutcome,
                "Synthetic host read found the exact once-only operation; no dispatch performed."));
        });
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [inspector]);
        var events = new List<TurnEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) events.Add(item);
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status); Assert.Equal(h.Goal.Id, saved.Id);
        Assert.Equal(1, inspector.Calls); Assert.Equal(1, runner.Calls);
        var prior = saved.Receipts.Single(x => x.ToolCallId == h.Target.ToolCallId);
        Assert.Equal(JsonSerializer.Serialize(h.Target), JsonSerializer.Serialize(prior with { HostResolution = null }));
        Assert.True(ContinualReceiptResolutionPolicy.IsResolved(prior, saved.Receipts));
        Assert.Single(saved.Receipts, x => x.ToolName == "host.goal-outcome-inspection");
        Assert.Contains(events.OfType<CodingGoalRecoveryProgress>(), x => x.Total == 1 && x.Resolved == 1);
        var firstInspection = events.FindIndex(x => x is CodingGoalRecoveryProgress);
        Assert.True(firstInspection > 0);
        Assert.Equal(ContinualObjectiveStatus.InProgress,
            events.Take(firstInspection).OfType<CodingGoalChanged>().Last().Goal.Status);
        Assert.Null(saved.LeaseToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_inspector_does_not_hide_an_independent_host_outcome_contract(bool supported)
    {
        var h = await Seed(); var runner = new Runner();
        var unavailable = new Inspector((_, _) => throw new InvalidOperationException("Synthetic inspector unavailable."));
        var independent = new Inspector((_, _) => Task.FromResult<CodingGoalOutcomeInspection?>(supported
            ? new(ContinualReceiptResolutionOutcome.KnownNoEffect, "Independent host readback established no effect.") : null));
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [unavailable, independent]);
        var events = new List<TurnEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) events.Add(item);
        var saved = (await new FileCodingGoalStore(Paths).GetAsync(h.Goal.Id))!;
        Assert.Equal(1, unavailable.Calls); Assert.Equal(1, independent.Calls);
        Assert.Equal(supported ? ContinualObjectiveStatus.Completed : ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Equal(h.Goal.ObjectiveText, saved.ObjectiveText); Assert.Equal(h.Goal.Id, saved.Id);
        var prior = saved.Receipts.Single(x => x.ToolCallId == h.Target.ToolCallId);
        Assert.Equal(JsonSerializer.Serialize(h.Target), JsonSerializer.Serialize(prior with { HostResolution = null }));
        Assert.Equal(supported, ContinualReceiptResolutionPolicy.IsResolved(prior, saved.Receipts));
        Assert.Equal(supported ? 1 : 0, saved.Receipts.Count(x => x.ToolName == "host.goal-outcome-inspection"));
        Assert.Contains(events.OfType<CodingGoalRecoveryProgress>(), x => x.Summary.Contains("InvalidOperationException"));
        Assert.DoesNotContain(events.OfType<CodingGoalRecoveryProgress>(), x => x.Summary.Contains("Synthetic inspector unavailable."));
        Assert.Null(saved.LeaseToken);
    }

    [Fact]
    public async Task A_resolved_outcome_cannot_release_another_unresolved_effect_hold()
    {
        var h = await Seed(); var runner = new Runner();
        var now = DateTimeOffset.UtcNow;
        var lease = (await h.Store.TryClaimAsync(h.Goal.Id, h.Goal.Version, "seed-second", now, now.AddMinutes(1)))!;
        var other = h.Target with { ToolCallId = "other-effect", Sequence = 2 };
        Assert.True(await h.Store.UpsertReceiptAsync(h.Goal.Id, lease.Token, other));
        Assert.True(await h.Store.ApplyTransitionAsync(h.Goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled,
            now, now, now, "Retained artifact", "Inspect both outcomes", null, null, 0)));
        var inspector = new Inspector((_, target) => Task.FromResult<CodingGoalOutcomeInspection?>(target.ToolCallId == h.Target.ToolCallId
            ? new(ContinualReceiptResolutionOutcome.KnownOutcome, "Exact first outcome independently read back.") : null));
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [inspector]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) { }
        var saved = (await new FileCodingGoalStore(Paths).GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.True(ContinualReceiptResolutionPolicy.IsResolved(saved.Receipts.Single(x => x.ToolCallId == h.Target.ToolCallId), saved.Receipts));
        Assert.True(ContinualReceiptResolutionPolicy.NeedsInspection(saved.Receipts.Single(x => x.ToolCallId == other.ToolCallId), saved.Receipts));
        Assert.Equal(JsonSerializer.Serialize(other), JsonSerializer.Serialize(saved.Receipts.Single(x => x.ToolCallId == other.ToolCallId)));
        var inspection = Assert.Single(runner.Requests);
        Assert.True(inspection.PlanMode); Assert.True(inspection.ReviewOnly);
        Assert.Equal(h.Goal.ObjectiveText, saved.ObjectiveText); Assert.Null(saved.LeaseToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspector_cancellation_or_lease_loss_never_invokes_another_contract(bool leaseLost)
    {
        var h = await Seed(); var runner = new Runner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = new Inspector((_, _) =>
        {
            if (leaseLost) throw new ContinualObjectiveLeaseLostException(h.Goal.Id);
            timeout.Cancel();
            throw new OperationCanceledException(timeout.Token);
        });
        var other = new Inspector((_, _) => Task.FromResult<CodingGoalOutcomeInspection?>(
            new(ContinualReceiptResolutionOutcome.KnownOutcome, "Must never run after recovery stops.")));
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [stopped, other]);
        async Task Run() { await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) { } }
        if (leaseLost) await Run();
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(1, stopped.Calls); Assert.Equal(0, other.Calls); Assert.Equal(0, runner.Calls);
        Assert.Equal(JsonSerializer.Serialize(h.Target), JsonSerializer.Serialize(Assert.Single(saved.Receipts)));
    }

    [Fact]
    public async Task Pause_during_readback_prevents_proof_and_execution()
    {
        var h = await Seed(); var runner = new Runner();
        var inspector = new Inspector(async (goal, _) =>
        {
            Assert.True(await h.Store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
            return new(ContinualReceiptResolutionOutcome.KnownOutcome, "Synthetic late readback.");
        });
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [inspector]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) { }
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Paused, saved.Status); Assert.Equal(0, runner.Calls);
        Assert.Null(saved.Receipts.Single(x => x.ToolCallId == h.Target.ToolCallId).HostResolution);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("policy")]
    [InlineData("literal")]
    [InlineData("authority")]
    public async Task Current_tool_withholding_or_revoked_authority_prevents_even_trusted_inspection(string restriction)
    {
        var h = await Seed(restriction == "literal" ? "Do not use any tools. Explain the saved result." : "Verify the saved result.");
        var runner = new Runner();
        var inspector = new Inspector((_, _) => Task.FromResult<CodingGoalOutcomeInspection?>(
            new(ContinualReceiptResolutionOutcome.KnownOutcome, "This inspector must not be called.")));
        ICodingGoalRequestPolicy? policy = restriction switch
        {
            "policy" => new Policy(request => request with { HostToolsWithheld = true }),
            "authority" => new Policy(request => request with { CommunicationScope = AgentCommunicationScope.None }),
            _ => null,
        };
        var request = h.Request with { HostToolsWithheld = restriction == "host" };
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), policy: policy, outcomeInspectors: [inspector]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in goals.RunAsync(request, h.Goal.Id, timeout.Token)) { }
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(0, inspector.Calls); Assert.Equal(0, runner.Calls);
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Single(saved.Receipts);
        Assert.Equal(JsonSerializer.Serialize(h.Target), JsonSerializer.Serialize(saved.Receipts.Single()));
        Assert.Null(saved.LeaseToken);
    }

    [Fact]
    public async Task Changed_workspace_prevents_private_inspection_and_execution()
    {
        var originalRoot = Path.Combine(Tmp, "original-repository");
        var foreignRoot = Path.Combine(Tmp, "foreign-repository");
        Directory.CreateDirectory(originalRoot); Directory.CreateDirectory(foreignRoot);
        var h = await Seed(workspace: new CodingWorkspace { RepositoryRoot = originalRoot });
        var runner = new Runner();
        var inspector = new Inspector((_, _) => Task.FromResult<CodingGoalOutcomeInspection?>(
            new(ContinualReceiptResolutionOutcome.KnownOutcome, "This inspector must not be called.")));
        var policy = new Policy(request => request with { Workspace = new CodingWorkspace { RepositoryRoot = foreignRoot } });
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), policy: policy, outcomeInspectors: [inspector]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) { }
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(0, inspector.Calls); Assert.Equal(0, runner.Calls);
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Single(saved.Receipts); Assert.Null(saved.Receipts.Single().HostResolution);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Absent_or_declining_inspector_never_promotes_model_output_to_outcome_proof(bool decliningInspector)
    {
        var h = await Seed(); var runner = new Runner();
        var inspector = new Inspector((_, _) => Task.FromResult<CodingGoalOutcomeInspection?>(null));
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(),
            outcomeInspectors: decliningInspector ? [inspector] : []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) { }
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Equal(decliningInspector ? 1 : 0, inspector.Calls);
        Assert.Equal(1, runner.Calls); // A read-only model report cannot substitute for host proof.
        Assert.Equal(JsonSerializer.Serialize(h.Target),
            JsonSerializer.Serialize(saved.Receipts.Single(x => x.ToolCallId == h.Target.ToolCallId)));
        Assert.DoesNotContain(saved.Receipts, x => x.HostResolution is not null || x.ToolName == "host.goal-outcome-inspection");
        Assert.Null(saved.LeaseToken);
    }

    [Fact]
    public async Task Deletion_during_inspection_wins_over_late_proof_and_cannot_resurrect_goal()
    {
        var h = await Seed(); var runner = new Runner();
        var inspector = new Inspector(async (_, _) =>
        {
            await h.Store.DeleteConversationAsync(h.Request.ConversationId);
            return new(ContinualReceiptResolutionOutcome.KnownOutcome, "Late result after deletion.");
        });
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [inspector]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, timeout.Token)) { }
        Assert.Null(await h.Store.GetAsync(h.Goal.Id));
        Assert.Equal(1, inspector.Calls); Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Cancellation_during_inspection_preserves_original_and_prevents_proof_or_execution()
    {
        var h = await Seed(); var runner = new Runner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inspector = new Inspector((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<CodingGoalOutcomeInspection?>(new(ContinualReceiptResolutionOutcome.KnownOutcome, "Late cancelled result."));
        });
        var goals = new CodingGoalOrchestrator(runner, h.Store, new Acceptance(), outcomeInspectors: [inspector]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in goals.RunAsync(h.Request, h.Goal.Id, cancellation.Token)) { }
        });
        var saved = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.Equal(1, inspector.Calls); Assert.Equal(0, runner.Calls);
        Assert.Single(saved.Receipts);
        Assert.Equal(JsonSerializer.Serialize(h.Target), JsonSerializer.Serialize(saved.Receipts.Single()));
    }
}
