using System.Collections.Concurrent;
using System.Text.Json;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Native;
using Xunit;

namespace SwarmCore.Coding.Tests;

public class AgentTeamTests
{
    /// <summary>Records what the team asked the host to do; turns finish only when a test reports them.</summary>
    private sealed class FakeHost : IAgentTeamHost
    {
        private int _next;
        public ConcurrentQueue<(string Id, string Message)> Started { get; } = new();
        public ConcurrentQueue<(string Id, string Message)> Delivered { get; } = new();
        public ConcurrentBag<string> Cancelled { get; } = [];
        public ConcurrentDictionary<string, bool> Busy { get; } = new();
        public bool DeliverResult { get; set; } = true;

        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
            => Task.FromResult($"agent{Interlocked.Increment(ref _next)}");

        public Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
        {
            Started.Enqueue((conversationId, message));
            Busy[conversationId] = true;
            return Task.FromResult(true);
        }

        public void Cancel(string conversationId) => Cancelled.Add(conversationId);
        public bool IsBusy(string conversationId) => Busy.GetValueOrDefault(conversationId);

        public Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct)
        {
            Delivered.Enqueue((conversationId, message));
            return Task.FromResult(DeliverResult);
        }
    }

    private static (AgentTeam Team, FakeHost Host) Build(AgentTeamOptions? options = null, IAgentTeamStore? store = null)
    {
        var o = options ?? new AgentTeamOptions();
        var team = new AgentTeam(store ?? new InMemoryAgentTeamStore(), () => o);
        var host = new FakeHost();
        team.AttachHost(host);
        return (team, host);
    }

    private static Task Finish(AgentTeam team, FakeHost host, string id, string text = "done", bool cancelled = false, string? error = null)
    {
        host.Busy[id] = false;
        return team.ReportTurnFinishedAsync(id, new AgentTurnOutcome(!cancelled && error is null, cancelled, error, text, 2));
    }

    [Fact]
    public async Task Spawn_creates_a_conversation_and_starts_it_with_a_self_contained_brief()
    {
        var (team, host) = Build();
        var agent = await team.SpawnAsync("root", new AgentSpawnRequest("Check the build of service A", "Build A"));

        Assert.Equal(AgentState.Running, agent.State);
        Assert.Equal("root", agent.ParentId);
        Assert.Equal("root", agent.RootId);
        Assert.Equal(1, agent.Depth);
        var (id, message) = Assert.Single(host.Started);
        Assert.Equal(agent.Id, id);
        Assert.Contains("Check the build of service A", message);
        Assert.Contains("Build A", message);
        Assert.Contains("may start your own agents", message); // depth 1 of 2
    }

    [Fact]
    public async Task Running_slots_queue_further_agents_and_start_them_as_slots_free()
    {
        var (team, host) = Build(new AgentTeamOptions { MaxRunning = 2 });
        var a = await team.SpawnAsync("root", new("a"));
        var b = await team.SpawnAsync("root", new("b"));
        var c = await team.SpawnAsync("root", new("c"));

        Assert.Equal(AgentState.Queued, c.State);
        Assert.Equal(2, host.Started.Count);

        await Finish(team, host, a.Id);
        Assert.Equal(AgentState.Running, team.Get(c.Id)!.State);
        Assert.Equal(3, host.Started.Count);
        Assert.Equal(AgentState.Idle, team.Get(a.Id)!.State);
        Assert.Equal(AgentState.Running, team.Get(b.Id)!.State);
    }

    [Fact]
    public async Task Depth_and_per_session_limits_bound_fan_out()
    {
        var (team, _) = Build(new AgentTeamOptions { MaxDepth = 2, MaxAgentsPerSession = 3 });
        var child = await team.SpawnAsync("root", new("child"));
        var grandchild = await team.SpawnAsync(child.Id, new("grandchild"));
        Assert.Equal(2, grandchild.Depth);
        Assert.Equal("root", grandchild.RootId);

        var deep = await Assert.ThrowsAsync<InvalidOperationException>(() => team.SpawnAsync(grandchild.Id, new("too deep")));
        Assert.Contains("nest", deep.Message);

        await team.SpawnAsync("root", new("third"));
        var many = await Assert.ThrowsAsync<InvalidOperationException>(() => team.SpawnAsync("root", new("fourth")));
        Assert.Contains("already started", many.Message);
    }

    [Fact]
    public async Task Only_the_starting_conversation_can_see_message_or_stop_an_agent()
    {
        var (team, _) = Build();
        var agent = await team.SpawnAsync("root", new("work"));
        var tools = AgentTeamTools.Create(team, () => "intruder").ToDictionary(t => t.ToolId);

        var status = (AgentsResult)(await tools[AgentTeamTools.StatusToolId].Invoke(new AgentsIdInput(agent.Id), default))!;
        var cancel = (AgentsResult)(await tools[AgentTeamTools.CancelToolId].Invoke(new AgentsCancelInput(agent.Id), default))!;
        var list = (AgentsResult)(await tools[AgentTeamTools.ListToolId].Invoke(new AgentsListInput(), default))!;

        Assert.False(status.Success);
        Assert.False(cancel.Success);
        Assert.Empty(list.Agents);
        Assert.Equal(AgentState.Running, team.Get(agent.Id)!.State);

        var noTurn = AgentTeamTools.Create(team, () => null).Single(t => t.ToolId == AgentTeamTools.SpawnToolId);
        Assert.False(((AgentsResult)(await noTurn.Invoke(new AgentsSpawnInput("x"), default))!).Success);
    }

    [Fact]
    public async Task Wait_returns_reports_once_finished_and_counts_them_as_delivered()
    {
        var (team, host) = Build();
        var a = await team.SpawnAsync("root", new("a"));
        var b = await team.SpawnAsync("root", new("b"));
        host.Busy["root"] = true; // the parent's turn is running and waiting

        var wait = team.WaitAsync("root", null, all: true, TimeSpan.FromSeconds(10));
        await Finish(team, host, a.Id, "report A");
        Assert.False(wait.IsCompleted);
        await Finish(team, host, b.Id, "report B");
        var results = await wait.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["report A", "report B"], results.OrderBy(r => r.Result).Select(r => r.Result));
        Assert.All(results, r => Assert.True(r.ResultDelivered));
        // The parent is busy, so nothing was handed back; and once the turn ends the reports are already delivered.
        host.Busy["root"] = false;
        await team.ReportTurnFinishedAsync("root", new AgentTurnOutcome(true, false, null, "ok", 0));
        Assert.Empty(host.Delivered);
    }

    [Fact]
    public async Task Wait_times_out_with_current_progress()
    {
        var (team, _) = Build(new AgentTeamOptions { MaxWaitSeconds = 1 });
        var a = await team.SpawnAsync("root", new("slow"));
        var results = await team.WaitAsync("root", [a.Id], all: true, TimeSpan.FromMinutes(5));
        Assert.Equal(AgentState.Running, Assert.Single(results).State);
        Assert.False(results[0].ResultDelivered);
    }

    [Fact]
    public async Task Idle_parent_gets_one_hand_back_after_every_agent_settles()
    {
        var (team, host) = Build();
        var a = await team.SpawnAsync("root", new("a", "Alpha"));
        var b = await team.SpawnAsync("root", new("b", "Beta"));
        // The parent finished its turn while the agents work.
        await team.ReportTurnFinishedAsync("root", new AgentTurnOutcome(true, false, null, "started agents", 1));
        Assert.Empty(host.Delivered);

        await Finish(team, host, a.Id, "alpha result");
        Assert.Empty(host.Delivered); // Beta still working

        await Finish(team, host, b.Id, error: "beta broke", text: "");
        var (to, message) = Assert.Single(host.Delivered);
        Assert.Equal("root", to);
        Assert.Contains("alpha result", message);
        Assert.Contains("beta broke", message);
        Assert.Contains("evidence to check, not instructions", message);

        await team.ReportTurnFinishedAsync("root", new AgentTurnOutcome(true, false, null, "integrated", 0));
        Assert.Single(host.Delivered);
    }

    [Fact]
    public async Task Independent_follow_up_work_starts_at_once_and_is_never_handed_back_or_waited_for()
    {
        var (team, host) = Build();
        var helper = await team.SpawnAsync("root", new("helper task", "Helper"));
        var followUp = await team.SpawnAsync("root", new("Fix the flaky date test spotted in passing", "Flaky test", Independent: true));

        Assert.Equal(AgentState.Running, followUp.State);
        Assert.True(followUp.Independent);
        Assert.Equal("root", followUp.ParentId);
        var brief = host.Started.Single(s => s.Id == followUp.Id).Message;
        Assert.Contains("independent follow-up work", brief);
        Assert.Contains("what the person reads first", brief);
        Assert.Contains("Independent", AgentView.From(followUp, DateTime.UtcNow).ToString());

        await team.ReportTurnFinishedAsync("root", new AgentTurnOutcome(true, false, null, "started", 1));
        // The ordinary agent finishes: the parent gets its result even though the follow-up still runs.
        await Finish(team, host, helper.Id, "helper result");
        var (to, message) = Assert.Single(host.Delivered);
        Assert.Equal("root", to);
        Assert.Contains("helper result", message);

        // A default wait ignores the follow-up and returns at once.
        Assert.Empty(await team.WaitAsync("root", null, all: true, TimeSpan.FromSeconds(1)));

        await Finish(team, host, followUp.Id, "follow-up result");
        Assert.Single(host.Delivered);
        Assert.DoesNotContain(host.Delivered, d => d.Message.Contains("follow-up result"));
    }

    [Fact]
    public async Task An_agents_independent_follow_up_does_not_keep_it_busy()
    {
        var (team, host) = Build();
        var agent = await team.SpawnAsync("root", new("agent task", "Agent"));
        var followUp = await team.SpawnAsync(agent.Id, new("separate work", Independent: true));
        await team.ReportTurnFinishedAsync("root", new AgentTurnOutcome(true, false, null, "started", 1));

        await Finish(team, host, agent.Id, "agent result");
        Assert.False(team.IsActive(agent.Id));
        Assert.True(team.IsActive(followUp.Id));
        var (to, message) = Assert.Single(host.Delivered);
        Assert.Equal("root", to);
        Assert.Contains("agent result", message);
    }

    [Fact]
    public async Task A_nested_agent_reports_to_its_parent_before_the_parent_reports_up()
    {
        var (team, host) = Build();
        var child = await team.SpawnAsync("root", new("child"));
        var grandchild = await team.SpawnAsync(child.Id, new("grandchild"));
        await Finish(team, host, child.Id, "child partial");
        Assert.Empty(host.Delivered); // the grandchild still works, so the child has not settled

        await Finish(team, host, grandchild.Id, "grandchild result");
        var (to, message) = Assert.Single(host.Delivered);
        Assert.Equal(child.Id, to);
        Assert.Contains("grandchild result", message);

        // The child continues with that hand-back, then settles; only then does the root hear about it.
        team.ReportTurnStarted(child.Id);
        host.Busy[child.Id] = true;
        await Finish(team, host, child.Id, "child final");
        Assert.Equal(2, host.Delivered.Count);
        Assert.Equal("root", host.Delivered.Last().Id);
        Assert.Contains("child final", host.Delivered.Last().Message);
    }

    [Fact]
    public async Task Failed_hand_back_leaves_results_for_the_parent_to_collect()
    {
        var (team, host) = Build();
        host.DeliverResult = false;
        var a = await team.SpawnAsync("root", new("a"));
        await Finish(team, host, a.Id, "report");
        Assert.Single(host.Delivered);
        Assert.False(team.Get(a.Id)!.ResultDelivered);
        Assert.Single(team.Children("root", includeDelivered: false));
    }

    [Fact]
    public async Task Cancel_stops_the_agent_and_everything_it_started()
    {
        var (team, host) = Build(new AgentTeamOptions { MaxRunning = 2 });
        var child = await team.SpawnAsync("root", new("child"));
        var grandchild = await team.SpawnAsync(child.Id, new("grandchild"));
        var queued = await team.SpawnAsync(child.Id, new("queued"));
        Assert.Equal(AgentState.Queued, queued.State);

        await team.CancelAsync("root", child.Id);
        Assert.Contains(child.Id, host.Cancelled);
        Assert.Contains(grandchild.Id, host.Cancelled);
        Assert.Equal(AgentState.Cancelled, team.Get(queued.Id)!.State);

        await Finish(team, host, grandchild.Id, cancelled: true);
        await Finish(team, host, child.Id, cancelled: true);
        Assert.False(team.IsActive(child.Id));
        Assert.Empty(host.Delivered); // stopped work is not handed back
    }

    [Fact]
    public async Task Send_resumes_a_finished_agent_and_queues_behind_a_running_one()
    {
        var (team, host) = Build();
        var agent = await team.SpawnAsync("root", new("first"));
        await team.SendAsync("root", agent.Id, "while running");
        Assert.Equal(2, host.Started.Count);
        Assert.Equal(AgentState.Running, team.Get(agent.Id)!.State);

        await Finish(team, host, agent.Id);
        var resumed = await team.SendAsync("root", agent.Id, "next step");
        Assert.Equal(AgentState.Running, resumed.State);
        Assert.Equal(2, resumed.Turns);
        Assert.Equal("next step", host.Started.Last().Message);
    }

    [Fact]
    public async Task Reload_marks_work_of_a_stopped_host_interrupted_and_detach_interrupts_running_work()
    {
        var dir = Path.Combine(Path.GetTempPath(), "swarmcore-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonFileAgentTeamStore(dir);
            var (team, host) = Build(store: store);
            var agent = await team.SpawnAsync("root", new("long job"));
            await team.FlushAsync();
            // Simulate a process that died: rewrite the owner to a pid that cannot be alive.
            await store.SaveAsync(team.Get(agent.Id)! with { Owner = $"{int.MaxValue}:1:dead" }, default);

            var (reloaded, _) = Build(store: store);
            await reloaded.InitializeAsync();
            var record = reloaded.Get(agent.Id)!;
            Assert.Equal(AgentState.Interrupted, record.State);
            Assert.Contains("Send it a message", record.Error);

            var (live, liveHost) = Build();
            var running = await live.SpawnAsync("root", new("x"));
            live.DetachHost(liveHost, interruptRunning: true);
            Assert.Equal(AgentState.Interrupted, live.Get(running.Id)!.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => live.SpawnAsync("root", new("no workspace")));
            await team.DisposeAsync();
            await reloaded.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task A_native_lane_starts_agents_through_the_host_tool_bridge_for_its_own_conversation()
    {
        var (team, _) = Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ActiveCodingWorkspaces>(services);
        var turns = new SwarmCore.Agent.Runtime.AsyncLocalCurrentTurnContextAccessor();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<SwarmCore.Agent.Runtime.ICurrentTurnContextAccessor>(services, turns);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<SwarmCore.Agent.Runtime.IProfileResolver>(services,
            new AgentsOnlyResolver(AgentTeamTools.Create(team, () => turns.Current?.ConversationId)));
        services.AddSwarmCoreHostToolBridge();
        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var broker = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<HostToolBridgeBroker>(sp);
        var active = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ActiveCodingWorkspaces>(sp);

        var proxy = new HostToolBridgeProxy(broker.GetEndpoint(), "root");
        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ListAsync(default)); // no turn running yet
        using (active.Enter("root", new CodingWorkspace { RepositoryRoot = Path.GetTempPath() }))
        {
            Assert.Contains(await proxy.ListAsync(default), t => t.Name == "agents_spawn");
            var spawned = await proxy.CallAsync("agents_spawn", JsonSerializer.SerializeToElement(new { task = "over the pipe", title = "Pipe" }), default);
            Assert.False(spawned.IsError, spawned.Text);
            Assert.Equal("root", Assert.Single(team.Children("root")).ParentId);
        }
    }

    private sealed class AgentsOnlyResolver(IReadOnlyList<SwarmCore.Agent.Catalog.ToolDescriptor> tools) : SwarmCore.Agent.Runtime.IProfileResolver
    {
        public Task<SwarmCore.Agent.Runtime.ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
            => Task.FromResult(new SwarmCore.Agent.Runtime.ResolvedProfile(
                new SwarmCore.Agent.ToolUseEngine.AgentProfileSnapshot { Id = Guid.Empty, Name = "t", SystemPrompt = "", EnabledToolIds = [] }, tools));
    }

    [Fact]
    public void Bridge_launch_names_the_conversation_only_for_the_host_bridge()
    {
        var bridge = new McpServerLaunch { Command = "swarmcode", Arguments = ["--mcp-server"] };
        var external = new McpServerLaunch { ServerName = "talavine", Command = "talavine-mcp" };
        var servers = NativeLaneSupport.MountedServers(bridge, [external], ".", "conv-1");
        Assert.Equal("conv-1", servers.Single(s => !s.IsExternal).Environment[NativeLaneSupport.ConversationEnvironmentVariable]);
        Assert.False(servers.Single(s => s.IsExternal).Environment.ContainsKey(NativeLaneSupport.ConversationEnvironmentVariable));
        Assert.False(NativeLaneSupport.MountedServers(bridge, null, ".", "../bad").Single().Environment.ContainsKey(NativeLaneSupport.ConversationEnvironmentVariable));
    }

    /// <summary>A host whose start observes its token first, like SwarmCodeAgentHost (GetRunnerAsync(.., ct)).</summary>
    private sealed class TokenObservingHost(Action? onCreate = null, bool blockStart = false) : IAgentTeamHost
    {
        public ConcurrentQueue<string> Started { get; } = new();
        public TaskCompletionSource Starting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
        {
            onCreate?.Invoke();
            return Task.FromResult("agent1");
        }
        public async Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
        {
            Starting.TrySetResult();
            if (blockStart) await Task.Delay(Timeout.Infinite, ct);
            ct.ThrowIfCancellationRequested();
            Started.Enqueue(conversationId);
            return true;
        }
        public void Cancel(string conversationId) { }
        public bool IsBusy(string conversationId) => false;
        public Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task A_spawn_stopped_while_the_host_creates_the_conversation_still_starts_the_agent_it_recorded()
    {
        using var caller = new CancellationTokenSource();
        // The person presses Stop while the host is creating the agent's conversation (a worktree takes seconds).
        var host = new TokenObservingHost(onCreate: caller.Cancel);
        await using var team = new AgentTeam(new InMemoryAgentTeamStore(), () => new AgentTeamOptions());
        team.AttachHost(host);

        var agent = await team.SpawnAsync("root", new("check the build"), caller.Token);

        Assert.Equal("agent1", Assert.Single(host.Started));
        Assert.Equal(AgentState.Running, agent.State);
    }

    [Fact]
    public async Task Closing_the_team_while_a_start_is_in_flight_interrupts_the_agent_instead_of_leaving_it_running()
    {
        var host = new TokenObservingHost(blockStart: true);
        var team = new AgentTeam(new InMemoryAgentTeamStore(), () => new AgentTeamOptions());
        team.AttachHost(host);

        var spawn = team.SpawnAsync("root", new("long start"));
        await host.Starting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await team.DisposeAsync();
        var agent = await spawn.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentState.Interrupted, team.Get(agent.Id)!.State);
        Assert.False(team.IsActive(agent.Id));
        Assert.Equal(0, team.RunningCount);
    }

    private sealed class LockedDeleteStore : IAgentTeamStore
    {
        private readonly InMemoryAgentTeamStore inner = new();
        public Task<IReadOnlyList<AgentRecord>> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
        public Task SaveAsync(AgentRecord record, CancellationToken ct) => inner.SaveAsync(record, ct);
        public Task DeleteAsync(string id, CancellationToken ct) => throw new IOException("The file is in use by another process.");
    }

    [Fact]
    public async Task A_failed_store_delete_keeps_the_agent_instead_of_reporting_it_forgotten()
    {
        var (team, host) = Build(store: new LockedDeleteStore());
        var agent = await team.SpawnAsync("root", new("a"));
        await Finish(team, host, agent.Id);

        var result = await team.ForgetWithCleanupAsync(agent.Id);

        Assert.False(result.Forgotten);
        Assert.NotNull(team.Get(agent.Id)); // matches the record still on disk, so it does not reappear next session
        await team.FlushAsync(); // the failure was logged, not left as a faulted save chain
        await team.DisposeAsync();
    }
}
