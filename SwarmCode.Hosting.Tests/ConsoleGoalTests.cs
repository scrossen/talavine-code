using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Goals;
using SwarmCode.Ui;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

/// <summary>Exercises the console adapter over the durable goal runner, without provider or global-console mutation.</summary>
public sealed class ConsoleGoalTests : TempDirTest
{
    private const string Conversation = "console-goal";
    private static readonly CodingLane Lane = new("test", "TestProvider", "test-model");

    private sealed class ScriptedOrchestrator(Func<CodingTurnRequest, int, CancellationToken, IAsyncEnumerable<TurnEvent>> script)
        : ICodingTurnOrchestrator
    {
        private int _calls;
        public ConcurrentQueue<CodingTurnRequest> Requests { get; } = new();
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Count physical execution, rather than a stream allocated but never enumerated by the adapter.
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _calls);
            await foreach (var evt in script(request, call, ct).ConfigureAwait(false)) yield return evt;
        }
    }

    private sealed class Acceptance(Func<ContinualAcceptanceRequest, int, ContinualAcceptanceVerdict> evaluate)
        : IContinualObjectiveAcceptanceService
    {
        private int _calls;
        public int Calls => _calls;
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => Task.FromResult(evaluate(request, Interlocked.Increment(ref _calls)));
    }

    private sealed record Harness(ServiceProvider Services, ScriptedOrchestrator Inner,
        CodingGoalOrchestrator Goals, Acceptance Acceptance) : IAsyncDisposable
    {
        public (TurnRunner Runner, ConsoleRenderer Renderer, StringWriter Output) NewRunner()
        {
            var output = new StringWriter();
            var renderer = new ConsoleRenderer(new() { Quiet = true }, output, new StringWriter());
            return (new TurnRunner(Inner, renderer, allowHandOff: true) { Services = Services }, renderer, output);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private Harness Build(Func<CodingTurnRequest, int, CancellationToken, IAsyncEnumerable<TurnEvent>> script,
        Func<ContinualAcceptanceRequest, int, ContinualAcceptanceVerdict>? evaluate = null)
    {
        var inner = new ScriptedOrchestrator(script);
        var acceptance = new Acceptance(evaluate ?? ((_, _) => ContinualAcceptanceVerdict.Delivered));
        var store = new FileCodingGoalStore(Paths);
        var goals = new CodingGoalOrchestrator(inner, store, acceptance, options: new()
        {
            BaseRetryDelay = TimeSpan.FromMilliseconds(1), MaximumRetryDelay = TimeSpan.FromMilliseconds(5),
            VerificationRetryDelay = TimeSpan.FromMilliseconds(1), NoProgressAttentionThreshold = 2,
            AttentionRetryDelay = TimeSpan.FromMilliseconds(1), MaximumAttentionRetryDelay = TimeSpan.FromMilliseconds(5),
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICodingTurnOrchestrator>(inner);
        services.AddSingleton<ICodingGoalStore>(store);
        services.AddSingleton(goals);
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), inner, goals, acceptance);
    }

    [Fact]
    public async Task Start_retries_a_rejected_candidate_and_finishes_only_after_independent_acceptance()
    {
        await using var h = Build((_, call, _) => Answer("Candidate " + call), (_, call) => call == 1
            ? new(ContinualAcceptanceOutcome.Retry, "The behavior still needs one correction.")
            : ContinualAcceptanceVerdict.Delivered);
        var (runner, renderer, output) = h.NewRunner();

        await runner.RunGoalAsync(CodingGoalCommand.Parse("Make every requirement pass"), Conversation,
            Lane.Name, CodingPermissionMode.AskRisky, false, "chosen-model").WaitAsync(TimeSpan.FromSeconds(20));

        var saved = (await h.Goals.GetAsync(Conversation))!;
        Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status);
        Assert.Equal(2, h.Acceptance.Calls);
        Assert.Equal(TurnOutcome.Completed, renderer.Outcome);
        Assert.Equal("Candidate 2", renderer.FinalText);
        Assert.Equal("Candidate 2" + Environment.NewLine, output.ToString());
        Assert.DoesNotContain("Candidate 1", output.ToString());
        var requests = h.Inner.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.False(requests[0].IsContinuation);
        Assert.True(requests[1].IsContinuation);
        Assert.All(requests, request =>
        {
            Assert.Equal("Make every requirement pass", request.AuthenticatedOriginalUserMessage);
            Assert.True(request.CommunicationScope!.UserInitiated);
            Assert.Equal("swarmcode-cli", request.CommunicationScope.Authority);
            Assert.Equal(CodingPermissionMode.AskRisky, request.Permissions!.Mode);
            Assert.Equal(Lane.Name, request.LaneName);
            Assert.Equal("chosen-model", request.ModelOverride);
            Assert.False(request.AllowHandOff);
        });
    }

    [Fact]
    public async Task Status_does_not_call_a_model_or_change_the_saved_goal()
    {
        await using var h = Build((_, _, _) => Answer("Verified"));
        var (runner, _, _) = h.NewRunner();
        await runner.RunGoalAsync(CodingGoalCommand.Parse(null), Conversation, null, CodingPermissionMode.Auto, null, null);
        Assert.Empty(h.Inner.Requests);
        Assert.Null(await h.Goals.GetAsync(Conversation));

        await runner.RunGoalAsync(CodingGoalCommand.Parse("Finish the task"), Conversation,
            null, CodingPermissionMode.Auto, null, null).WaitAsync(TimeSpan.FromSeconds(20));
        var before = (await h.Goals.GetAsync(Conversation))!;
        await runner.RunGoalAsync(CodingGoalCommand.Parse("status"), Conversation, null, CodingPermissionMode.Auto, null, null);
        var after = (await h.Goals.GetAsync(Conversation))!;

        Assert.Single(h.Inner.Requests);
        Assert.Equal(1, h.Acceptance.Calls);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(ContinualObjectiveStatus.Completed, after.Status);
    }

    [Fact]
    public async Task Ctrl_C_token_pauses_and_a_new_console_wrapper_resumes_saved_goal_after_an_unrelated_prompt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = Build((_, call, ct) => call == 1 ? WaitForCancellation(started, ct) : Answer("Verified"));
        var (runner, _, _) = h.NewRunner();
        using var interrupt = new CancellationTokenSource();
        var running = runner.RunGoalAsync(CodingGoalCommand.Parse("Implement the original complete feature"), Conversation,
            Lane.Name, CodingPermissionMode.Auto, null, null, interrupt.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        interrupt.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var paused = (await h.Goals.GetAsync(Conversation))!;
        Assert.Equal(ContinualObjectiveStatus.Paused, paused.Status);
        Assert.Single(h.Inner.Requests);
        Assert.Equal(TurnOutcome.Completed,
            await runner.RunAsync(Conversation, "Explain one unrelated detail", Lane.Name, CancellationToken.None));
        var stillPaused = (await h.Goals.GetAsync(Conversation))!;
        Assert.Equal(paused.Id, stillPaused.Id);
        Assert.Equal(ContinualObjectiveStatus.Paused, stillPaused.Status);
        Assert.Equal("Implement the original complete feature", stillPaused.ObjectiveText);

        var (reopened, renderer, output) = h.NewRunner();
        await reopened.RunGoalAsync(CodingGoalCommand.Parse("resume"), Conversation,
            Lane.Name, CodingPermissionMode.Ask, false, "resume-model").WaitAsync(TimeSpan.FromSeconds(20));

        var completed = (await h.Goals.GetAsync(Conversation))!;
        Assert.Equal(paused.Id, completed.Id);
        Assert.Equal(ContinualObjectiveStatus.Completed, completed.Status);
        Assert.Equal(TurnOutcome.Completed, renderer.Outcome);
        Assert.Equal("Verified" + Environment.NewLine, output.ToString());
        Assert.Equal(3, h.Inner.Requests.Count);
        var resumed = h.Inner.Requests.Last();
        Assert.True(resumed.IsContinuation);
        Assert.Equal("Implement the original complete feature", resumed.AuthenticatedOriginalUserMessage);
        Assert.DoesNotContain("Explain one unrelated detail", resumed.UserMessage);
        Assert.Equal(CodingPermissionMode.Ask, resumed.Permissions!.Mode);
        Assert.Equal("resume-model", resumed.ModelOverride);
    }

    [Fact]
    public async Task Plan_mode_refuses_start_and_resume_without_dispatching_or_mutating_the_goal()
    {
        await using var h = Build((_, _, _) => Answer("Must not run"));
        var (runner, _, _) = h.NewRunner();
        runner.PlanMode = true;

        await runner.RunGoalAsync(CodingGoalCommand.Parse("Implement the feature"), Conversation,
            null, CodingPermissionMode.Auto, null, null);
        Assert.Null(await h.Goals.GetAsync(Conversation));
        var original = await h.Goals.StartAsync(UserRequest("Saved goal"));
        var paused = (await h.Goals.ControlAsync(Conversation, CodingGoalControl.Pause))!;
        await runner.RunGoalAsync(CodingGoalCommand.Parse("resume"), Conversation,
            null, CodingPermissionMode.Auto, null, null);

        var after = (await h.Goals.GetAsync(Conversation))!;
        Assert.Empty(h.Inner.Requests);
        Assert.Equal(0, h.Acceptance.Calls);
        Assert.Equal(original.Id, after.Id);
        Assert.Equal(paused.Version, after.Version);
        Assert.Equal(ContinualObjectiveStatus.Paused, after.Status);
        Assert.True(runner.PlanMode);
    }

    [Fact]
    public async Task Pause_and_cancel_commands_change_the_saved_goal_without_dispatching_work()
    {
        await using var h = Build((_, _, _) => Answer("Must not run"));
        var (runner, _, _) = h.NewRunner();
        var original = await h.Goals.StartAsync(UserRequest("Saved goal"));

        await runner.RunGoalAsync(CodingGoalCommand.Parse("pause"), Conversation, null, CodingPermissionMode.Auto, null, null);
        Assert.Equal(ContinualObjectiveStatus.Paused, (await h.Goals.GetAsync(Conversation))!.Status);
        await runner.RunGoalAsync(CodingGoalCommand.Parse("cancel"), Conversation, null, CodingPermissionMode.Auto, null, null);

        var cancelled = (await h.Goals.GetAsync(Conversation))!;
        Assert.Equal(original.Id, cancelled.Id);
        Assert.Equal(ContinualObjectiveStatus.Cancelled, cancelled.Status);
        Assert.Empty(h.Inner.Requests);
        Assert.Equal(0, h.Acceptance.Calls);
    }

    private static CodingTurnRequest UserRequest(string objective)
        => SwarmCodeTurnPolicy.Apply(new() { ConversationId = Conversation, UserMessage = objective },
            CodingPermissionPolicy.AutoNoRules, "swarmcode-cli");

    private static async IAsyncEnumerable<TurnEvent> Answer(string output)
    {
        await Task.Yield();
        yield return new CodingLaneSelected("turn", Lane, 1);
        yield return new TextChunk("turn", output);
        // Goal execution forwards host acceptance while buffering the final answer until independent verification.
        yield return new CodingAcceptanceEvaluated("turn", CodingAcceptanceResult.Unverified);
        yield return new TurnCompleted("turn", output, TimeSpan.Zero);
    }

    private static async IAsyncEnumerable<TurnEvent> WaitForCancellation(TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new CodingLaneSelected("turn", Lane, 1);
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
