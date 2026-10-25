using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Agents;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Routing;
using SwarmCore.Abstractions.LlmRouting.Decisions;

namespace SwarmCode.Hosting.Tests;

public class AgentHostingTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    /// <summary>Plays the parent and its agent: the parent starts an agent, the agent reports, the parent integrates.</summary>
    private sealed class ScriptedOrchestrator(Func<AgentTeam> team) : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests { get; } = [];
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            string answer;
            if (request.UserMessage.Contains("has finished", StringComparison.Ordinal)) answer = "integrated: " + (request.UserMessage.Contains("42") ? "42" : "?");
            else if (request.UserMessage.Contains("Task:", StringComparison.Ordinal)) answer = "The answer is 42.";
            else
            {
                await team().SpawnAsync(request.ConversationId, new AgentSpawnRequest("Find the answer.", "Answer finder"), ct);
                answer = "Started an agent.";
            }
            yield return new TurnCompleted("t", answer, TimeSpan.FromMilliseconds(5));
        }
    }

    [Fact]
    public async Task A_session_starts_an_agent_session_and_receives_its_report_when_idle()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        ScriptedOrchestrator? orchestrator = null;
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            ConfigureServices = services => services.AddSingleton<ICodingTurnOrchestrator>(sp =>
                orchestrator ??= new ScriptedOrchestrator(() => sp.GetRequiredService<AgentTeam>())),
        });
        var sp = workspace.Services;
        var team = sp.GetRequiredService<AgentTeam>();
        var host = sp.GetRequiredService<SwarmCodeAgentHost>();
        var runners = Assert.IsType<ConversationRunnerPool>(sp.GetRequiredService<IConversationRunnerSource>());
        var sessions = sp.GetRequiredService<WorkspaceSessionCatalog>();
        var parentSession = await sessions.CreateAsync(repo, "Parent", "claude");
        var parent = (await runners.GetRunnerAsync(parentSession.Id))!;
        host.Observe(parent);

        Assert.True(parent.TrySend("Work out the answer with an agent.", "claude"));

        // The parent ends its first turn, the agent works and reports, then the parent continues with the report.
        await Until(() => parent.Entries.OfType<AssistantEntry>().Any(e => e.Text.StartsWith("integrated", StringComparison.Ordinal)));
        Assert.Equal("integrated: 42", parent.Entries.OfType<AssistantEntry>().Last().Text);
        var handBack = parent.Entries.OfType<UserEntry>().Last();
        Assert.True(handBack.IsContinuation);

        var agent = Assert.Single(team.Children(parentSession.Id));
        Assert.Equal(AgentState.Idle, agent.State);
        Assert.Equal("The answer is 42.", agent.Result);
        Assert.True(agent.ResultDelivered);

        var agentSession = (await sessions.GetAsync(agent.Id))!;
        Assert.Equal(parentSession.Id, agentSession.ParentSessionId);
        Assert.True(agentSession.Temporary); // never feeds personal memory
        Assert.Equal("Answer finder", agentSession.Title);
        Assert.Equal("claude", agentSession.LaneName);
        var agentRunner = runners.TryGetRunner(agent.Id)!;
        Assert.True(agentRunner.Entries.OfType<UserEntry>().Single().IsContinuation);

        // Records are durable: one file per agent.
        await team.FlushAsync();
        Assert.True(File.Exists(Path.Combine(Paths.AgentsDirectory, agent.Id + ".json")));
        await runners.DisposeAsync();
    }

    [Fact]
    public async Task Workspace_composition_offers_agent_tools_guidance_and_the_native_bridge()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        // With a bridge configured the coding options post-configure the agents endpoint; a dependency cycle there
        // deadlocks startup, so composition must finish promptly.
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
        var coding = await Task.Run(() => sp.GetRequiredService<IOptions<CodingOptions>>().Value).WaitAsync(TimeSpan.FromSeconds(30));
        var catalog = sp.GetRequiredService<ICapabilityCatalog>();
        foreach (var id in AgentTeamTools.AllToolIds) Assert.NotNull(catalog.All().FirstOrDefault(t => t.ToolId == id));
        Assert.Contains("agents.spawn", coding.AdditionalInstructions);
        Assert.True(coding.NativeAgents.McpServer!.Environment.ContainsKey(SwarmCore.Coding.Native.HostToolBridgeBroker.PipeEnvironmentVariable));
        Assert.True(sp.GetRequiredService<AgentTeam>().HasHost);
    }

    [Fact]
    public async Task Agents_can_be_turned_off()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?> { ["Coding:Agents:Enabled"] = "false" }),
        });
        var sp = workspace.Services;
        Assert.DoesNotContain(sp.GetRequiredService<ICapabilityCatalog>().All(), t => t.ToolId.StartsWith("agents.", StringComparison.Ordinal));
        Assert.DoesNotContain("agents.spawn", sp.GetRequiredService<IOptions<CodingOptions>>().Value.AdditionalInstructions ?? "");
    }

    private sealed class DelegationProvider : IChoiceDecisionProvider
    {
        public string Name => "delegation-fixture";
        public int Calls;
        public Task<ChoiceDecision> ChooseAsync(DecisionConnection connection, ChoiceDecisionRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ChoiceDecision(request.Options.Any(o => o.Id == "routine") ? "routine" : request.Options[0].Id, .9));
        }
    }

    private sealed class LocalInventory : IDelegatedModelInventory
    {
        public int Calls;
        public Task<IReadOnlyList<ModelDecisionCandidate>> GetEligibleAsync(string id, string task,
            IReadOnlyList<ModelDecisionCandidate> configured, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<ModelDecisionCandidate>>([new("local", "local", "small", "Synthetic admitted local worker")]);
        }
    }

    private sealed class DelegationOrchestrator : ICodingTurnOrchestrator
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<CodingTurnRequest> Requests = new();
        public IReadOnlyList<CodingLane> Lanes { get; } = [new("strong", "test", "large"), new("local", "Ollama", "small")];
        public CodingLane? ResolveLane(string? name) => Lanes.FirstOrDefault(l => l.Name == name);
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            yield return new CodingLaneSelected("synthetic", ResolveLane(request.LaneName) ?? Lanes[0], 1);
            yield return new TurnCompleted("synthetic", "Synthetic worker result for host routing verification.", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Automatic_delegation_runs_the_selected_child_as_a_continuation_and_followups_reuse_it()
    {
        var repo = Path.Combine(Tmp, "repo"); Directory.CreateDirectory(repo);
        var decider = new DelegationProvider(); var inventory = new LocalInventory(); var execution = new DelegationOrchestrator();
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo, DataPaths = Paths,
            ConfigureServices = services =>
            {
                services.AddSingleton<IChoiceDecisionProvider>(decider);
                services.AddSingleton<IDelegatedModelInventory>(inventory);
                services.AddSingleton<ICodingTurnOrchestrator>(execution);
            },
        });
        await new DecisionSettingsStore(Paths).SaveAsync(new() { Enabled = true, AutoDelegateRoutineTasks = true,
            Connection = new() { Provider = decider.Name },
            Candidates = [new("local", "local", "small", "Local routine worker"), new("strong", "strong", "large", "Difficult work")] });
        var services = workspace.Services; var team = services.GetRequiredService<AgentTeam>();
        var sessions = services.GetRequiredService<WorkspaceSessionCatalog>();
        var parent = await sessions.CreateAsync(repo, "Lead", "strong", model: "large");
        await sessions.SetModelAsync(parent.Id, "strong", "large");
        var originalLead = (await sessions.GetAsync(parent.Id))!;
        var worker = await team.SpawnAsync(parent.Id, new("Read the declared options from one file.", "Routine worker"));
        await Until(() => team.Get(worker.Id)?.State == AgentState.Idle);
        Assert.Equal("local", worker.Lane); Assert.Equal("small", worker.Model); Assert.NotNull(worker.RoutingNote);
        Assert.True(Assert.Single(execution.Requests).IsContinuation);
        Assert.Equal("local", execution.Requests.Single().LaneName);
        Assert.False(execution.Requests.Single().AllowHandOff);
        Assert.True(execution.Requests.Single().DisableRouteFallback);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.LlmInteractivity.Background, execution.Requests.Single().RoutingInteractivity);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.Policy, execution.Requests.Single().RouteOverrideProvenance);
        Assert.Equal(originalLead.Selection, (await sessions.GetAsync(parent.Id))!.Selection);
        var savedChild = (await sessions.GetAsync(worker.Id))!;
        Assert.True(savedChild.Temporary); Assert.Equal(ModelSelectionOrigin.Decision, savedChild.Selection!.Origin);
        await team.SendAsync(parent.Id, worker.Id, "Check the same result once more.");
        await Until(() => execution.Requests.Count == 2 && team.Get(worker.Id)?.State == AgentState.Idle);
        Assert.All(execution.Requests, request => { Assert.Equal("local", request.LaneName); Assert.True(request.IsContinuation); });
        Assert.Equal(2, decider.Calls); Assert.Equal(1, inventory.Calls);
        await sessions.SetModelAsync(worker.Id, "strong", "manual-strong");
        await team.SendAsync(parent.Id, worker.Id, "Continue after my explicit model change.");
        await Until(() => execution.Requests.Count == 3 && team.Get(worker.Id)?.State == AgentState.Idle);
        Assert.Equal("strong", execution.Requests.Last().LaneName);
        Assert.Equal("manual-strong", execution.Requests.Last().ModelOverride);
        Assert.True(execution.Requests.Last().IsContinuation);
        Assert.Equal(2, decider.Calls);
        await team.FlushAsync();
        var persisted = await new JsonFileAgentTeamStore(Paths.AgentsDirectory).LoadAsync(default);
        Assert.Equal(worker.RoutingNote, Assert.Single(persisted).RoutingNote);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }
}
