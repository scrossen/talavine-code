using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Peers;

namespace SwarmCode.Hosting.Tests;

public sealed class ConversationGoalTests : TempDirTest
{
    private static readonly CodingLane Lane = new("test", "TestProvider", "test-model");

    private sealed class ScriptedOrchestrator(Func<CodingTurnRequest, int, CancellationToken, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        private int _calls;
        public ConcurrentQueue<CodingTurnRequest> Requests { get; } = new();
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            return script(request, Interlocked.Increment(ref _calls), ct);
        }
    }

    private sealed class Acceptance(Func<ContinualAcceptanceRequest, int, ContinualAcceptanceVerdict> verdict) : IContinualObjectiveAcceptanceService
    {
        private int _calls;
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => Task.FromResult(verdict(request, Interlocked.Increment(ref _calls)));
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RefreshPolicy(Func<CodingTurnRequest, int, CodingTurnRequest> apply) : ICodingGoalRequestPolicy
    {
        private int _calls;
        public int Calls => _calls;
        public Task<CodingTurnRequest> PrepareAsync(CodingTurnRequest request, CancellationToken ct = default)
            => Task.FromResult(apply(request, Interlocked.Increment(ref _calls)));
    }

    private sealed record Harness(ServiceProvider Services, ConversationRunner Runner, ScriptedOrchestrator Inner, CodingGoalOrchestrator Goals) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Runner.DisposeAsync(); await Services.DisposeAsync(); }
    }

    private Harness Build(Func<CodingTurnRequest, int, CancellationToken, IAsyncEnumerable<TurnEvent>> script,
        Func<ContinualAcceptanceRequest, int, ContinualAcceptanceVerdict>? acceptance = null, ICodingGoalRequestPolicy? policy = null,
        ICapabilityCatalog? tools = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var inner = new ScriptedOrchestrator(script);
        var store = new FileCodingGoalStore(Paths);
        var goals = new CodingGoalOrchestrator(inner, store,
            new Acceptance(acceptance ?? ((_, _) => ContinualAcceptanceVerdict.Delivered)), options: new()
            {
                BaseRetryDelay = TimeSpan.FromMilliseconds(1), MaximumRetryDelay = TimeSpan.FromMilliseconds(5),
                VerificationRetryDelay = TimeSpan.FromMilliseconds(1),
                NoProgressAttentionThreshold = 2, AttentionRetryDelay = TimeSpan.FromMilliseconds(1),
                MaximumAttentionRetryDelay = TimeSpan.FromMilliseconds(5),
            }, policy: policy, tools: tools);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<ICodingTurnOrchestrator>(inner);
        services.AddSingleton(goals);
        var provider = services.BuildServiceProvider();
        return new(provider, new ConversationRunner(provider, "conversation"), inner, goals);
    }

    [Fact]
    public async Task Ordinary_request_does_not_create_a_goal_or_enter_the_retry_loop()
    {
        await using var h = Build((_, _, _) => Answer("One answer"), (_, _) =>
            throw new InvalidOperationException("An ordinary request must not reach goal acceptance."));
        var finish = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("Explain the code", null));
        Assert.True((await finish).Completed);
        Assert.Single(h.Inner.Requests);
        Assert.Null(await h.Goals.GetAsync("conversation"));
        Assert.Null(h.Runner.Goal);
    }

    [Fact]
    public async Task Actual_host_can_open_an_ordinary_session_and_resolve_goals_from_its_validated_root_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths, ConfigureServices = FixedLanes.Register,
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.NotNull(provider.GetRequiredService<CodingGoalOrchestrator>());
        var session = await provider.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(Tmp);
        await using var runner = new ConversationRunner(provider, session.Id);

        await runner.LoadHistoryAsync();

        Assert.False(runner.IsRunning);
        Assert.Null(runner.Goal);
    }

    [Fact]
    public async Task Goal_retries_unfinished_work_then_finishes_once_after_independent_acceptance()
    {
        await using var h = Build((_, call, _) => Answer("Candidate " + call), (_, call) => call == 1
            ? new(ContinualAcceptanceOutcome.Retry, "One requirement remains.") : ContinualAcceptanceVerdict.Delivered);
        var finishes = new ConcurrentQueue<TurnResult>();
        h.Runner.TurnFinished += finishes.Enqueue;
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Complete all requirements", null));
        var result = await finish;

        Assert.True(result.Completed);
        Assert.Single(finishes);
        Assert.Equal(2, h.Inner.Requests.Count);
        Assert.Equal(ContinualObjectiveStatus.Completed, h.Runner.Goal!.Status);
        Assert.False(h.Runner.HasUnfinishedGoal);
        var requests = h.Inner.Requests.ToArray();
        Assert.True(requests[1].IsContinuation);
        Assert.Equal("Complete all requirements", requests[1].AuthenticatedOriginalUserMessage);
        Assert.True(requests[1].CommunicationScope!.UserInitiated);
    }

    [Fact]
    public async Task Verified_goal_has_one_answer_and_its_job_marker_survives_restart_until_an_ordinary_send()
    {
        await using var h = Build((_, _, _) => Answer("Verified goal answer"));
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Complete the feature", null));
        Assert.True((await finish).Completed);

        Assert.Equal("Verified goal answer", Assert.Single(h.Runner.Entries.OfType<AssistantEntry>()).Text);
        Assert.True(h.Runner.IsGoalJob);
        Assert.True(h.Services.GetRequiredService<FileConversationJobStore>().Load("conversation")!.IsGoal);
        await h.Runner.DisposeAsync();

        await using var reopened = new ConversationRunner(h.Services, "conversation");
        await reopened.LoadHistoryAsync();
        Assert.True(reopened.IsGoalJob);
        Assert.Equal("Verified goal answer", Assert.Single(reopened.Entries.OfType<AssistantEntry>()).Text);

        finish = NextFinish(reopened);
        Assert.True(reopened.TrySend("Explain a separate detail", null));
        Assert.True((await finish).Completed);
        Assert.False(reopened.IsGoalJob);
        Assert.False(h.Services.GetRequiredService<FileConversationJobStore>().Load("conversation")!.IsGoal);
    }

    [Theory]
    [InlineData(SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.AuthInvalid)]
    [InlineData(SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.QuotaExhausted)]
    public async Task A_successful_lane_handoff_clears_the_previous_lanes_failure_before_goal_acceptance(
        SwarmCore.Abstractions.LlmRouting.LlmFailureCategory failure)
    {
        await using var h = Build((_, _, _) => RecoveredAnswer(failure));
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Complete the requested feature", null));

        var result = await finish;

        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.Equal("Recovered result", result.FinalText);
        Assert.Single(h.Inner.Requests);
        Assert.Equal(1, h.Runner.Goal!.AttemptCount);
        Assert.Equal(ContinualObjectiveStatus.Completed, h.Runner.Goal.Status);
    }

    [Fact]
    public async Task Repeated_no_progress_keeps_retrying_past_the_attention_threshold_until_verified()
    {
        await using var h = Build((_, _, _) => Answer("Still incomplete"), (_, call) => call < 5
            ? new(ContinualAcceptanceOutcome.Retry, "One requirement still remains.") : ContinualAcceptanceVerdict.Delivered);
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Finish the complete feature", null));

        Assert.True((await finish).Completed);
        Assert.Equal(5, h.Inner.Requests.Count);
        Assert.Equal(ContinualObjectiveStatus.Completed, h.Runner.Goal!.Status);
    }

    [Fact]
    public async Task Obtained_opinions_are_not_requested_again_for_the_next_goal_attempt()
    {
        await using var h = Build((_, call, _) => OpinionsAnswer(call), (_, call) => call == 1
            ? new(ContinualAcceptanceOutcome.Retry, "One requirement remains.") : ContinualAcceptanceVerdict.Delivered);
        var request = SwarmCodeTurnPolicy.Apply(new()
        {
            ConversationId = "conversation", UserMessage = "Implement and review the complete feature",
            Assistance = new() { GetOpinionsFirst = true, ReviewBeforeFinishing = true },
        }, SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        var goal = await h.Goals.StartAsync(request);

        await foreach (var _ in h.Goals.RunAsync(request, goal.Id)) { }

        var requests = h.Inner.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.True(requests[0].Assistance.GetOpinionsFirst);
        Assert.False(requests[1].Assistance.GetOpinionsFirst);
        Assert.True(requests[1].Assistance.ReviewBeforeFinishing);
        Assert.Equal(ContinualObjectiveStatus.Completed, (await h.Goals.GetAsync("conversation"))!.Status);
    }

    [Fact]
    public async Task A_restored_dispatched_action_without_an_outcome_allows_only_read_only_inspection()
    {
        var time = new MutableTime(new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var inner = new ScriptedOrchestrator((_, _, _) => Answer("Must not execute"));
        var store = new FileCodingGoalStore(Paths, time);
        var goals = new CodingGoalOrchestrator(inner, store, new Acceptance((_, _) =>
            throw new InvalidOperationException("An uncertain dispatch must be inspected before verification.")), time: time);
        var request = SwarmCodeTurnPolicy.Apply(new() { ConversationId = "conversation", UserMessage = "Send the requested report" },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.From(SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, null), "test");
        var goal = await goals.StartAsync(request);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "previous-process", time.Now, time.Now.AddSeconds(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, new(goal.Id, 1, 1, "send-1", "email.send",
            ContinualToolReceiptState.Started, ContinualSideEffectClass.ExternalSend, "{}", null, false, time.Now)));
        time.Now += TimeSpan.FromSeconds(2);

        var events = new List<TurnEvent>();
        await foreach (var evt in goals.RunAsync(request, goal.Id)) events.Add(evt);

        var inspection = Assert.Single(inner.Requests);
        Assert.True(inspection.PlanMode);
        Assert.True(inspection.ReviewOnly);
        Assert.False(inspection.AllowNativeLanes);
        Assert.Equal("Send the requested report", inspection.AuthenticatedOriginalUserMessage);
        Assert.Equal(ContinualObjectiveStatus.Uncertain, (await goals.GetAsync("conversation"))!.Status);
        Assert.DoesNotContain(events, evt => evt is TurnCompleted);
    }

    [Fact]
    public async Task Failed_host_acceptance_cannot_be_overruled_by_an_independent_delivered_verdict()
    {
        await using var h = Build((_, call, _) => CheckedAnswer(call), (_, _) => ContinualAcceptanceVerdict.Delivered);
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Make all checks pass", null));

        Assert.True((await finish).Completed);
        Assert.Equal(2, h.Inner.Requests.Count);
        Assert.Equal(ContinualObjectiveStatus.Completed, h.Runner.Goal!.Status);
    }

    [Theory]
    [InlineData("swarmcode", "peers_wait")]
    [InlineData("swarmcode", "peers_cancel")]
    [InlineData("swarmcode", "unknown_tool")]
    [InlineData("external", "peers_wait")]
    public async Task Restored_unknown_native_calls_remain_fenced_despite_current_read_only_metadata(
        string server, string tool)
    {
        var time = new MutableTime(new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var inner = new ScriptedOrchestrator((_, _, _) => Answer("Current observed result"));
        var store = new FileCodingGoalStore(Paths, time);
        var catalog = new InMemoryCapabilityCatalog();
        foreach (var descriptor in PeerTools.Create(null!, () => "conversation", () => true)) catalog.Register(descriptor);
        ContinualAcceptanceRequest? audit = null;
        var goals = new CodingGoalOrchestrator(inner, store, new Acceptance((request, _) =>
        {
            audit = request;
            return ContinualAcceptanceVerdict.Delivered;
        }), time: time, tools: catalog);
        var request = SwarmCodeTurnPolicy.Apply(new() { ConversationId = "conversation", UserMessage = "Continue the existing work" },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        var goal = await goals.StartAsync(request);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "previous-process", time.Now, time.Now.AddSeconds(1)))!;
        var prior = new ContinualToolReceipt(goal.Id, 1, 1, "interrupted", CodexNativeLaneRunner.McpToolName,
            ContinualToolReceiptState.Started, ContinualSideEffectClass.Unknown,
            new JsonObject { ["server"] = server, ["tool"] = tool,
                ["arguments"] = new JsonObject { ["workId"] = "existing" } }.ToJsonString(), null, false, time.Now);
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, prior));
        time.Now += TimeSpan.FromSeconds(2);

        await foreach (var _ in goals.RunAsync(request, goal.Id)) { }

        var resumed = Assert.Single(inner.Requests);
        Assert.True(resumed.PlanMode);
        Assert.True(resumed.ReviewOnly);
        Assert.False(resumed.AllowNativeLanes);
        var saved = (await goals.GetAsync("conversation"))!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(prior),
            System.Text.Json.JsonSerializer.Serialize(Assert.Single(saved.Receipts)));
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Null(audit);
    }

    [Fact]
    public async Task Cancelling_a_new_metadata_classified_read_only_wait_preserves_it_and_resumes_native_capable_work()
    {
        var started = Signal();
        var catalog = new InMemoryCapabilityCatalog();
        foreach (var descriptor in PeerTools.Create(null!, () => "conversation", () => true)) catalog.Register(descriptor);
        await using var h = Build((_, call, ct) => call == 1 ? NativeWaitForCancellation(started, ct) : Answer("Verified remaining work"),
            tools: catalog);
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Complete existing work", null));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Runner.PauseGoalAsync();
        Assert.True((await finish).Cancelled);
        var prior = Assert.Single((await h.Goals.GetAsync("conversation"))!.Receipts);
        Assert.Equal(ContinualSideEffectClass.ReadOnly, prior.SideEffect);
        Assert.Equal(ContinualToolReceiptState.Started, prior.State);
        Assert.Null(prior.Result);

        finish = NextFinish(h.Runner);
        Assert.True(h.Runner.ResumeGoal(null));
        Assert.True((await finish).Completed);

        var resumed = h.Inner.Requests.Last();
        Assert.False(resumed.PlanMode);
        Assert.False(resumed.ReviewOnly);
        Assert.True(resumed.AllowNativeLanes);
        var retained = Assert.Single((await h.Goals.GetAsync("conversation"))!.Receipts);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(prior), System.Text.Json.JsonSerializer.Serialize(retained));
    }

    [Fact]
    public async Task A_genuine_input_requirement_parks_the_goal_without_claiming_completion_or_retrying()
    {
        await using var h = Build((_, _, _) => Answer("Which account should receive the report?"), (_, _) =>
            new(ContinualAcceptanceOutcome.NeedsUserHelp, "The destination account is required."));
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Prepare the account report", null));

        Assert.False((await finish).Completed);
        Assert.Single(h.Inner.Requests);
        Assert.Equal(ContinualObjectiveStatus.WaitingForInput, h.Runner.Goal!.Status);
        Assert.True(h.Runner.HasUnfinishedGoal);
    }

    [Fact]
    public async Task Revoked_authority_is_rechecked_before_an_automatic_retry_can_execute()
    {
        // Registration and the first attempt both revalidate; revoke before the automatic second attempt.
        var policy = new RefreshPolicy((request, call) => call <= 2 ? request
            : request with { CommunicationScope = AgentCommunicationScope.None });
        await using var h = Build((_, _, _) => Answer("Candidate"), (_, _) =>
            new(ContinualAcceptanceOutcome.Retry, "More work remains."), policy);
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Implement everything", null));

        Assert.False((await finish).Completed);
        Assert.Single(h.Inner.Requests);
        Assert.Equal(3, policy.Calls);
        Assert.Equal(ContinualObjectiveStatus.WaitingForInput, h.Runner.Goal!.Status);
    }

    [Fact]
    public async Task Stop_pauses_goal_and_a_later_ordinary_request_does_not_resume_it()
    {
        var started = Signal();
        await using var h = Build((_, call, ct) => call == 1 ? WaitForCancellation(started, ct) : Answer("Ordinary reply"));
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Keep implementing feature", null));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Runner.Cancel();
        var stopped = await finish;
        Assert.True(stopped.Cancelled);
        Assert.Equal(ContinualObjectiveStatus.Paused, (await h.Goals.GetAsync("conversation"))!.Status);
        Assert.Equal(ContinualObjectiveStatus.Paused, h.Runner.Goal!.Status);

        finish = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("Explain one detail", null));
        Assert.True((await finish).Completed);
        Assert.Equal(2, h.Inner.Requests.Count);
        var goal = (await h.Goals.GetAsync("conversation"))!;
        Assert.Equal(ContinualObjectiveStatus.Paused, goal.Status);
        Assert.Equal("Keep implementing feature", goal.ObjectiveText);
    }

    [Fact]
    public async Task Reopening_a_saved_goal_loads_status_without_dispatch_and_resume_uses_its_saved_intent()
    {
        var started = Signal();
        await using var h = Build((_, call, ct) => call == 1 ? WaitForCancellation(started, ct) : Answer("Verified"));
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Original goal request", null));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Runner.PauseGoalAsync();
        await finish;
        await h.Runner.DisposeAsync();

        await using var reopened = new ConversationRunner(h.Services, "conversation");
        await reopened.LoadHistoryAsync();
        Assert.False(reopened.IsRunning);
        Assert.Equal(ContinualObjectiveStatus.Paused, reopened.Goal!.Status);
        Assert.Single(h.Inner.Requests);
        finish = NextFinish(reopened);
        Assert.True(reopened.ResumeGoal(null));
        Assert.True((await finish).Completed);
        Assert.Equal("Original goal request", h.Inner.Requests.Last().AuthenticatedOriginalUserMessage);
    }

    [Fact]
    public async Task A_paused_goals_attachments_survive_an_unrelated_message_and_restart()
    {
        var started = Signal();
        await using var h = Build((_, call, ct) => call == 1 ? WaitForCancellation(started, ct) : Answer("Verified"));
        var attachment = new SwarmCore.Coding.Files.CodingAttachment("goal-file", "spec.txt", "managed/spec.txt", "text/plain", 12, "goal-hash");
        var unrelated = new SwarmCore.Coding.Files.CodingAttachment("other-file", "other.txt", "managed/other.txt", "text/plain", 15, "other-hash");
        var finish = NextFinish(h.Runner);
        Assert.True(await h.Runner.StartGoalAsync("Implement the attached specification", null, attachments: [attachment]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Runner.PauseGoalAsync();
        await finish;
        finish = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("Explain this unrelated file", null, attachments: [unrelated]));
        await finish;
        await h.Runner.DisposeAsync();

        await using var reopened = new ConversationRunner(h.Services, "conversation");
        await reopened.LoadHistoryAsync();
        finish = NextFinish(reopened);
        Assert.True(reopened.ResumeGoal(null));
        Assert.True((await finish).Completed);
        Assert.Equal(attachment, Assert.Single(h.Inner.Requests.Last().Attachments));
    }

    [Fact]
    public async Task Steering_a_running_goal_preserves_original_and_correction_across_the_next_attempt()
    {
        var started = Signal();
        await using var h = Build((_, call, ct) => call == 1 ? WaitForCancellation(started, ct) : Answer("Corrected and verified"));
        var completed = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runner.TurnFinished += result => { if (result.Completed) completed.TrySetResult(result); };
        Assert.True(await h.Runner.StartGoalAsync("Implement the feature", null));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await h.Runner.SteerAsync("Also handle empty input", null));
        Assert.True((await completed.Task.WaitAsync(TimeSpan.FromSeconds(20))).Completed);
        var saved = (await h.Goals.GetAsync("conversation"))!;
        Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status);
        Assert.Contains("Implement the feature", saved.ObjectiveText);
        Assert.Contains("Also handle empty input", saved.ObjectiveText);
        Assert.Equal(saved.ObjectiveText, h.Inner.Requests.Last().AuthenticatedOriginalUserMessage);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += result => finished.TrySetResult(result);
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async IAsyncEnumerable<TurnEvent> Answer(string output)
    {
        await Task.Yield();
        yield return new CodingLaneSelected("turn", Lane, 1);
        yield return new TextChunk("turn", output);
        yield return new TurnCompleted("turn", output, TimeSpan.Zero);
        yield return new CodingAcceptanceEvaluated("turn", CodingAcceptanceResult.Unverified);
    }

    private static async IAsyncEnumerable<TurnEvent> CheckedAnswer(int call)
    {
        await foreach (var evt in Answer("Candidate " + call))
            if (evt is not CodingAcceptanceEvaluated) yield return evt;
        yield return new CodingAcceptanceEvaluated("turn", new(call == 1 ? CodingAcceptanceStatus.Failed : CodingAcceptanceStatus.Passed,
            call == 1 ? "Feature check failed" : "Feature check passed", [new("feature", call > 1, call == 1 ? 1 : 0, false, "Observed check result")]));
    }

    private static async IAsyncEnumerable<TurnEvent> OpinionsAnswer(int call)
    {
        if (call == 1) yield return new CodingOpinionsObtained("turn");
        await foreach (var evt in Answer("Candidate " + call)) yield return evt;
    }

    private static async IAsyncEnumerable<TurnEvent> RecoveredAnswer(SwarmCore.Abstractions.LlmRouting.LlmFailureCategory failure)
    {
        await Task.Yield();
        var alternate = new CodingLane("alternate", "OtherProvider", "other-model");
        yield return new CodingLaneSelected("first", Lane, 1);
        yield return new TurnErrored("first", "The first provider could not run") { FailureCategory = failure };
        yield return new CodingLaneHandOff("first", Lane, alternate, "Trying another configured provider");
        yield return new CodingLaneSelected("second", alternate, 2);
        yield return new TextChunk("second", "Recovered result");
        yield return new TurnCompleted("second", "Recovered result", TimeSpan.Zero);
        yield return new CodingAcceptanceEvaluated("second", CodingAcceptanceResult.Unverified);
    }

    private static async IAsyncEnumerable<TurnEvent> WaitForCancellation(TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new CodingLaneSelected("turn", Lane, 1);
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static async IAsyncEnumerable<TurnEvent> NativeWaitForCancellation(TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new CodingLaneSelected("turn", Lane, 1);
        yield return new ToolCallStarted("turn", "wait", CodexNativeLaneRunner.McpToolName,
            JsonNode.Parse("""{"server":"swarmcode","tool":"peers_wait","arguments":{"workId":"existing"}}""")!.AsObject());
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
