using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Agents;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class AgentGoalReportDeliveryTests : TempDirTest
{
    private static readonly CodingLane Lane = new("test", "TestProvider", "test-model");

    [Fact]
    public async Task Paused_goal_retains_finished_child_reports_until_explicit_goal_resume_collects_them_once()
    {
        Script? script = null;
        await using var workspace = await WorkspaceHost.CreateAsync(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths,
            ConfigureServices = services =>
            {
                services.AddSingleton<ICodingTurnOrchestrator>(sp => script ??= new(() => sp.GetRequiredService<AgentTeam>()));
                services.AddSingleton(sp => new CodingGoalOrchestrator(sp.GetRequiredService<ICodingTurnOrchestrator>(),
                    sp.GetRequiredService<ICodingGoalStore>(), new Delivered()));
            },
        });
        var services = workspace.Services;
        var team = services.GetRequiredService<AgentTeam>();
        var actualHost = services.GetRequiredService<SwarmCodeAgentHost>();
        var delivery = new DeliveryProbe(actualHost);
        team.AttachHost(delivery);
        var session = await services.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(Tmp, "Goal", "test");
        var runner = (await services.GetRequiredService<IConversationRunnerSource>().GetRunnerAsync(session.Id))!;
        actualHost.Observe(runner);
        services.GetRequiredService<ICodingTurnOrchestrator>();
        script!.ParentId = session.Id;

        var stopped = NextFinish(runner);
        Assert.True(await runner.StartGoalAsync("Review both delegated receipt checks.", "test"));
        await script.ChildrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var goalId = runner.Goal!.Id;
        await runner.PauseGoalAsync();
        Assert.True((await stopped).Cancelled);
        Assert.Equal(ContinualObjectiveStatus.Paused, runner.Goal!.Status);
        var priorJob = services.GetRequiredService<FileConversationJobStore>().Load(session.Id)!;

        script.ReleaseChildren.TrySetResult();
        Assert.False(await delivery.Result.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        await Until(() => team.Children(session.Id).Count == 2
            && team.Children(session.Id).All(child => child.State == AgentState.Idle && !child.ResultDelivered));
        await team.FlushAsync();

        Assert.False(runner.IsRunning);
        Assert.Equal(1, script.ParentCalls);
        Assert.Equal(goalId, runner.Goal!.Id);
        Assert.Equal(ContinualObjectiveStatus.Paused, runner.Goal.Status);
        Assert.True(runner.IsGoalJob);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(priorJob),
            System.Text.Json.JsonSerializer.Serialize(services.GetRequiredService<FileConversationJobStore>().Load(session.Id)));
        var savedChildren = await new JsonFileAgentTeamStore(Paths.AgentsDirectory).LoadAsync(default);
        Assert.Equal(2, savedChildren.Count);
        Assert.All(savedChildren, child => { Assert.False(child.ResultDelivered); Assert.Equal("Checked receipt.", child.Result); });
        Assert.DoesNotContain(runner.Entries.OfType<UserEntry>(), entry => entry.Text.Contains("has finished", StringComparison.Ordinal));

        var resumed = NextFinish(runner);
        Assert.True(runner.ResumeGoal("test"));
        Assert.True((await resumed).Completed);
        Assert.Equal(goalId, runner.Goal!.Id);
        Assert.Equal(ContinualObjectiveStatus.Completed, runner.Goal.Status);
        Assert.Equal(2, script.ParentCalls);
        Assert.Equal(2, script.Collected.Count);
        Assert.Equal(2, script.Collected.Distinct().Count());
        Assert.Equal(0, script.SecondCollectionCount);
        Assert.Equal(2, team.Children(session.Id).Count);
        Assert.All(team.Children(session.Id), child => Assert.True(child.ResultDelivered));
        Assert.All(script.Requests.Where(request => request.ConversationId == session.Id),
            request => Assert.NotNull(request.Metadata?.GetValueOrDefault("coding.goal")));
        team.AttachHost(actualHost);
    }

    [Fact]
    public async Task Stop_between_idle_check_and_report_delivery_does_not_restart_the_parent()
    {
        Script? script = null;
        await using var workspace = await WorkspaceHost.CreateAsync(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths,
            ConfigureServices = services => services.AddSingleton<ICodingTurnOrchestrator>(sp => script ??= new(() => sp.GetRequiredService<AgentTeam>())),
        });
        var services = workspace.Services;
        var team = services.GetRequiredService<AgentTeam>();
        var actualHost = services.GetRequiredService<SwarmCodeAgentHost>();
        var delivery = new DeliveryProbe(actualHost) { Hold = true };
        team.AttachHost(delivery);
        var session = await services.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(Tmp, "Stopped parent", "test");
        var runner = (await services.GetRequiredService<IConversationRunnerSource>().GetRunnerAsync(session.Id))!;
        actualHost.Observe(runner);
        services.GetRequiredService<ICodingTurnOrchestrator>();
        script!.ParentId = session.Id;
        var child = await team.SpawnAsync(session.Id, new("Check one saved receipt.", "Receipt check"));

        script.ReleaseChildren.TrySetResult();
        await delivery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        runner.Cancel(); // The team's idle check already passed, but automatic delivery has not dispatched.
        delivery.Release.TrySetResult();
        Assert.False(await delivery.Result.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        await Until(() => team.Get(child.Id) is { State: AgentState.Idle, ResultDelivered: false });
        Assert.True(runner.QueuePaused);
        Assert.False(runner.IsRunning);
        Assert.Equal(0, script.ParentCalls);
        Assert.Equal("Checked receipt.", team.Get(child.Id)!.Result);
        Assert.Single(await team.WaitAsync(session.Id, null, true, TimeSpan.FromSeconds(1)));
        Assert.Empty(await team.WaitAsync(session.Id, null, true, TimeSpan.FromSeconds(1)));
        team.AttachHost(actualHost);
    }

    private sealed class Delivered : IContinualObjectiveAcceptanceService
    {
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => Task.FromResult(ContinualAcceptanceVerdict.Delivered);
    }

    private sealed class Script(Func<AgentTeam> team) : ICodingTurnOrchestrator
    {
        public string ParentId { get; set; } = "";
        public int ParentCalls;
        public int SecondCollectionCount { get; private set; }
        public ConcurrentQueue<string> Collected { get; } = new();
        public ConcurrentQueue<CodingTurnRequest> Requests { get; } = new();
        public TaskCompletionSource ChildrenStarted { get; } = Signal();
        public TaskCompletionSource ReleaseChildren { get; } = Signal();
        public IReadOnlyList<CodingLane> Lanes => [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            yield return new CodingLaneSelected("turn", Lane, 1);
            if (request.ConversationId == ParentId)
            {
                if (Interlocked.Increment(ref ParentCalls) == 1)
                {
                    await team().SpawnAsync(ParentId, new("Check the first saved receipt.", "First check"), ct);
                    await team().SpawnAsync(ParentId, new("Check the second saved receipt.", "Second check"), ct);
                    ChildrenStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                foreach (var child in await team().WaitAsync(ParentId, null, true, TimeSpan.FromSeconds(1), ct)) Collected.Enqueue(child.Id);
                SecondCollectionCount = (await team().WaitAsync(ParentId, null, true, TimeSpan.FromSeconds(1), ct)).Count;
                yield return new TurnCompleted("turn", "Reviewed both checked receipts.", TimeSpan.Zero);
            }
            else
            {
                await ReleaseChildren.Task.WaitAsync(ct);
                yield return new TurnCompleted("turn", "Checked receipt.", TimeSpan.Zero);
            }
        }
    }

    private sealed class DeliveryProbe(IAgentTeamHost inner) : IAgentTeamHost
    {
        public bool Hold { get; init; }
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
            => inner.CreateConversationAsync(parentConversationId, title, lane, model, ct);
        public Task<AgentConversation> CreateAgentConversationAsync(AgentConversationRequest request, CancellationToken ct)
            => inner.CreateAgentConversationAsync(request, ct);
        public Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
            => inner.StartTurnAsync(conversationId, message, lane, model, ct);
        public void Cancel(string conversationId) => inner.Cancel(conversationId);
        public bool IsBusy(string conversationId) => inner.IsBusy(conversationId);
        public async Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct)
        {
            Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(ct);
            var result = await inner.DeliverAsync(conversationId, message, ct);
            Result.TrySetResult(result);
            return result;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var signal = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(TurnResult result) { runner.TurnFinished -= Finished; signal.TrySetResult(result); }
        runner.TurnFinished += Finished;
        return signal.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
