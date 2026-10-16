using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Planning;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

/// <summary>The session task list on disk, plan mode on the runner, and the composed workspace's plan bridge.</summary>
public sealed class PlanHostingTests : TempDirTest
{
    [Fact]
    public async Task Plan_store_round_trips_through_an_atomic_file_per_session()
    {
        var store = new FileConversationPlanStore(Paths);
        var changed = new List<string>();
        store.Changed += (id, _) => changed.Add(id);
        await store.SetAsync("session-1", [new("Read", PlanItemStatus.Completed), new("Edit", PlanItemStatus.InProgress), new("Test", PlanItemStatus.Pending)]);

        var path = Path.Combine(Paths.SessionsDirectory, "plans", "session-1.json");
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Contains("\"in_progress\"", await File.ReadAllTextAsync(path));
        Assert.Equal(["session-1"], changed);

        var reloaded = await new FileConversationPlanStore(Paths).GetAsync("session-1");
        Assert.Equal(["Read", "Edit", "Test"], reloaded.Items.Select(i => i.Content));
        Assert.Equal(PlanItemStatus.InProgress, reloaded.Items[1].Status);
        Assert.Equal(0, (await store.GetAsync("session-2")).Total);

        await store.DeleteAsync("session-1");
        Assert.False(File.Exists(path));
        Assert.Equal(0, (await new FileConversationPlanStore(Paths).GetAsync("session-1")).Total);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task Plan_store_rejects_ids_that_are_not_storage_ids(string id)
    {
        var store = new FileConversationPlanStore(Paths);
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SetAsync(id, [new("x", PlanItemStatus.Pending)]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.GetAsync(id));
    }

    [Fact]
    public async Task An_unreadable_plan_file_loads_as_an_empty_list_and_is_replaced_by_the_next_write()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Paths.SessionsDirectory, "plans"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "s.json"), "{ not json");
        var store = new FileConversationPlanStore(Paths);
        Assert.Equal(0, (await store.GetAsync("s")).Total);
        await store.SetAsync("s", [new("fresh", PlanItemStatus.Pending)]);
        Assert.Equal("fresh", Assert.Single((await new FileConversationPlanStore(Paths).GetAsync("s")).Items).Content);
    }

    /// <summary>Records each request; a plan-mode turn writes a task list the way plan.update would.</summary>
    private sealed class PlanningOrchestrator(IConversationPlanStore plans) : ICodingTurnOrchestrator
    {
        private static readonly CodingLane Lane = new("api", "Api", "m");
        public List<CodingTurnRequest> Requests { get; } = [];
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            if (request.PlanMode)
                await plans.SetAsync(request.ConversationId, [new("Inspect", PlanItemStatus.Completed), new("Change", PlanItemStatus.Pending)], ct);
            yield return new TurnCompleted("t", request.PlanMode ? "Plan: inspect, then change." : "Implemented.", TimeSpan.Zero);
        }
    }

    private static async Task<TurnResult> RunToEnd(ConversationRunner runner, Func<bool> start)
    {
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(TurnResult r) => finished.TrySetResult(r);
        runner.TurnFinished += Done;
        try
        {
            Assert.True(start());
            return await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { runner.TurnFinished -= Done; }
    }

    [Fact]
    public async Task Plan_mode_is_host_state_on_the_request_and_approval_sends_an_implementation_turn()
    {
        var plans = new FileConversationPlanStore(Paths);
        var orchestrator = new PlanningOrchestrator(plans);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<IConversationPlanStore>(plans);
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, "conv");
        await runner.LoadHistoryAsync();

        Assert.False(runner.PlanMode);
        runner.SetPlanMode(true);
        Assert.True(runner.PlanMode);
        Assert.False(runner.PlanAwaitingApproval);

        var planned = await RunToEnd(runner, () => runner.TrySend("Refactor the parser code", "api"));
        Assert.True(planned.Completed);
        Assert.True(orchestrator.Requests[0].PlanMode);
        Assert.True(runner.PlanAwaitingApproval);
        Assert.Equal((1, 2), (runner.Plan.CompletedCount, runner.Plan.Total)); // live from the store's change event

        var approved = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => approved.TrySetResult(r);
        Assert.True(await runner.ApprovePlanAsync("api"));
        await approved.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(runner.PlanMode);
        Assert.False(runner.PlanAwaitingApproval);
        var implement = orchestrator.Requests[1];
        Assert.False(implement.PlanMode);
        Assert.Equal(CodingPlanMode.ApprovalMessage, implement.UserMessage);

        // A reopened runner shows the saved list (plan mode itself is per runner and starts off).
        await using var reopened = new ConversationRunner(provider, "conv");
        await reopened.LoadHistoryAsync();
        Assert.Equal(2, reopened.Plan.Total);
        Assert.False(reopened.PlanMode);
    }

    [Fact]
    public async Task Turning_plan_mode_off_before_approval_withdraws_the_pending_approval()
    {
        var plans = new InMemoryConversationPlanStore();
        var orchestrator = new PlanningOrchestrator(plans);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<IConversationPlanStore>(plans);
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, "conv2");
        runner.SetPlanMode(true);
        await RunToEnd(runner, () => runner.TrySend("Plan the cache change", "api"));
        Assert.True(runner.PlanAwaitingApproval);
        runner.SetPlanMode(false);
        Assert.False(runner.PlanAwaitingApproval);
        await RunToEnd(runner, () => runner.TrySend("Just do it", "api"));
        Assert.False(orchestrator.Requests[1].PlanMode);
    }

    [Fact]
    public async Task Workspace_composition_persists_plans_registers_the_tools_and_hands_the_bridge_its_endpoint()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        await using var workspace = await Task.Run(() => WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
            {
                ["Coding:NativeAgents:McpServer:Command"] = "swarmcode",
                ["Coding:NativeAgents:McpServer:Arguments:0"] = "--mcp-server",
            }),
        })).WaitAsync(TimeSpan.FromSeconds(60));
        var sp = workspace.Services;
        Assert.IsType<FileConversationPlanStore>(sp.GetRequiredService<IConversationPlanStore>());
        var coding = await Task.Run(() => sp.GetRequiredService<IOptions<CodingOptions>>().Value).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(coding.NativeAgents.McpServer!.Environment.ContainsKey(SwarmCore.Coding.Native.HostToolBridgeBroker.PipeEnvironmentVariable));
        var catalog = sp.GetRequiredService<ICapabilityCatalog>();
        foreach (var id in PlanTools.AllToolIds) Assert.NotNull(catalog.All().FirstOrDefault(t => t.ToolId == id));
        Assert.Contains(sp.GetServices<IAgentTurnToolFilter>(), f => f is CodingPlanModeToolFilter);
    }

    [Fact]
    public void Task_list_tools_are_read_only_to_a_plan_mode_cli()
    {
        // Claude Code's plan mode refuses MCP tools without the read-only hint; the task list changes nothing the CLI could act on.
        var store = new InMemoryConversationPlanStore();
        foreach (var tool in PlanTools.Create(store, () => "c"))
            Assert.True(SwarmCore.Coding.Native.HostToolBridgeCatalog.Manifest(SwarmCore.Coding.Native.HostToolBridgeCatalog.McpName(tool.ToolId), tool).ReadOnly, tool.ToolId);
    }
}
