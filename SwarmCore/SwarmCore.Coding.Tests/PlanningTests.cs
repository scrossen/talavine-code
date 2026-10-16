using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Planning;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>The task list (plan.update / plan.read, native todo mirroring) and plan mode (read-only investigation first).</summary>
public sealed class PlanningTests
{
    private static async Task<PlanResult> Invoke(ToolDescriptor tool, object input)
        => (PlanResult)(await tool.Invoke(input, default))!;

    private static ToolDescriptor Tool(IReadOnlyList<ToolDescriptor> tools, string id) => tools.Single(t => t.ToolId == id);

    // ── task list state and tools ─────────────────────────────────

    [Fact]
    public async Task Plan_update_replaces_the_calling_conversations_list_and_plan_read_returns_it()
    {
        var store = new InMemoryConversationPlanStore();
        var changes = new List<(string, ConversationPlan)>();
        store.Changed += (id, plan) => changes.Add((id, plan));
        var tools = PlanTools.Create(store, () => "conv-a");

        var saved = await Invoke(Tool(tools, PlanTools.UpdateToolId), new PlanUpdateInput(
        [
            new("Read the parser", "completed"), new("Fix the bug", "in_progress"), new("Run the tests", "pending"),
        ]));
        Assert.True(saved.Success);
        Assert.Equal((1, 3), (saved.Completed, saved.Total));
        Assert.Equal(["completed", "in_progress", "pending"], saved.Items.Select(i => i.Status));

        // A second update replaces the list whole.
        await Invoke(Tool(tools, PlanTools.UpdateToolId), new PlanUpdateInput([new("Only step", "in_progress")]));
        var read = await Invoke(Tool(tools, PlanTools.ReadToolId), new PlanReadInput());
        Assert.Equal("Only step", Assert.Single(read.Items).Content);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal("conv-a", c.Item1));
        Assert.Equal(0, (await store.GetAsync("conv-b")).Total); // another conversation is untouched
    }

    [Fact]
    public async Task Plan_update_rejects_invalid_lists_without_changing_the_saved_one()
    {
        var store = new InMemoryConversationPlanStore();
        var update = Tool(PlanTools.Create(store, () => "c"), PlanTools.UpdateToolId);
        await Invoke(update, new PlanUpdateInput([new("Keep me", "pending")]));

        var twoActive = await Invoke(update, new PlanUpdateInput([new("a", "in_progress"), new("b", "in_progress")]));
        Assert.False(twoActive.Success);
        Assert.Contains("one item in_progress", twoActive.Message);
        Assert.False((await Invoke(update, new PlanUpdateInput([new("a", "someday")]))).Success);
        Assert.False((await Invoke(update, new PlanUpdateInput([new("  ", "pending")]))).Success);
        Assert.False((await Invoke(update, new PlanUpdateInput(
            Enumerable.Range(0, ConversationPlanRules.MaxItems + 1).Select(i => new PlanItemInput("step " + i, "pending")).ToList()))).Success);
        Assert.Equal("Keep me", Assert.Single((await store.GetAsync("c")).Items).Content);

        // An empty list clears it.
        var cleared = await Invoke(update, new PlanUpdateInput([]));
        Assert.True(cleared.Success);
        Assert.Equal(0, (await store.GetAsync("c")).Total);
    }

    [Fact]
    public async Task Plan_tools_outside_a_turn_have_no_conversation_and_fail_safely()
    {
        var store = new InMemoryConversationPlanStore();
        var result = await Invoke(Tool(PlanTools.Create(store, () => null), PlanTools.UpdateToolId), new PlanUpdateInput([new("x", "pending")]));
        Assert.False(result.Success);
    }

    [Fact]
    public void Plan_tools_are_classified_and_described_in_the_coding_prompt()
    {
        var tools = PlanTools.Create(new InMemoryConversationPlanStore(), () => "c");
        Assert.Equal(ToolSideEffect.WritesLocal, Tool(tools, PlanTools.UpdateToolId).SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, Tool(tools, PlanTools.ReadToolId).SideEffect);
        // Conversation state, not workspace-bound coding tools: hosts' workspace policies are unchanged.
        Assert.DoesNotContain(PlanTools.UpdateToolId, CodingToolRegistrar.CodingToolIds);
        Assert.Contains("plan.update", Prompting.CodingSystemPrompt.Build(new CodingOptions()));
    }

    [Fact]
    public void Status_values_serialize_as_snake_case()
    {
        var json = JsonSerializer.Serialize(new ConversationPlan([new("a", PlanItemStatus.InProgress)], DateTime.UnixEpoch), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"in_progress\"", json);
        var back = JsonSerializer.Deserialize<ConversationPlan>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(PlanItemStatus.InProgress, back.Items[0].Status);
    }

    [Fact]
    public void Claude_code_todo_write_input_is_mirrored_leniently_and_other_tools_are_ignored()
    {
        var input = JsonNode.Parse("""
            {"todos":[{"content":"Explore","status":"completed","activeForm":"Exploring"},
                      {"content":"Edit","status":"in_progress","activeForm":"Editing"},
                      {"content":"Also active","status":"in_progress"},
                      {"content":"","status":"pending"}]}
            """)!.AsObject();
        var items = PlanTools.FromNativeTodoCall("TodoWrite", input)!;
        Assert.Equal(["Explore", "Edit", "Also active"], items.Select(i => i.Content));
        Assert.Equal([PlanItemStatus.Completed, PlanItemStatus.InProgress, PlanItemStatus.Pending], items.Select(i => i.Status));
        Assert.Null(PlanTools.FromNativeTodoCall("Write", input));
        Assert.Null(PlanTools.FromNativeTodoCall("TodoWrite", JsonNode.Parse("""{"todos":"nope"}""")!.AsObject()));
    }

    // ── plan mode: engine tool filter ─────────────────────────────

    private static ToolDescriptor Fake(string id, ToolSideEffect effect, Func<object, ToolSideEffect?>? resolve = null)
        => ToolDescriptor.Create<object, object>(id, "test", "test", ToolCostClass.Cheap, effect, (_, _) => Task.FromResult<object>(new()),
            resolveSideEffect: resolve is null ? null : input => resolve(input));

    private static AgentProfileSnapshot Profile() => new() { Id = Guid.NewGuid(), Name = "test", SystemPrompt = "test", EnabledToolIds = [] };

    [Fact]
    public async Task Plan_mode_filter_keeps_read_only_tools_and_the_task_list_and_blocks_side_effects()
    {
        var plan = PlanTools.Create(new InMemoryConversationPlanStore(), () => "c");
        ToolDescriptor[] tools =
        [
            Fake("code.grep", ToolSideEffect.ReadOnly), Fake("fs.read_file", ToolSideEffect.ReadOnly),
            Fake("code.edit_file", ToolSideEffect.WritesLocal), Fake("fs.write_file", ToolSideEffect.WritesLocal),
            Fake("shell.exec", ToolSideEffect.Exec), Fake("git.commit", ToolSideEffect.WritesLocal),
            Fake("http.request", ToolSideEffect.ExternalSend), Fake("git.reset", ToolSideEffect.Destructive),
            // Statically read-only but input-dependent: not admitted in plan mode.
            Fake("maybe.write", ToolSideEffect.ReadOnly, _ => ToolSideEffect.WritesLocal),
            .. plan,
        ];
        var filter = new CodingPlanModeToolFilter();
        var planRequest = new AgentTurnRequest { ConversationId = "c", UserMessage = "plan", CommunicationScope = AgentCommunicationScope.UserRequested("test"), Metadata = CodingPlanMode.Metadata(null) };
        var kept = await filter.FilterAsync(planRequest, Profile(), tools);
        Assert.Equal(["code.grep", "fs.read_file", PlanTools.UpdateToolId, PlanTools.ReadToolId], kept.Select(t => t.ToolId));

        var normal = await filter.FilterAsync(new AgentTurnRequest { ConversationId = "c", UserMessage = "go", CommunicationScope = AgentCommunicationScope.UserRequested("test") }, Profile(), tools);
        Assert.Equal(tools.Length, normal.Count);
    }

    [Fact]
    public void The_harness_registers_the_plan_mode_filter_and_an_in_memory_plan_store()
    {
        var services = new ServiceCollection();
        services.AddSwarmCoreCodingHarness();
        Assert.Contains(services, d => d.ServiceType == typeof(IAgentTurnToolFilter) && d.ImplementationType == typeof(CodingPlanModeToolFilter));
        Assert.Contains(services, d => d.ServiceType == typeof(IConversationPlanStore) && d.ImplementationType == typeof(InMemoryConversationPlanStore));
    }

    // ── plan mode: native lanes ───────────────────────────────────

    private static NativeLaneTurn ClaudeTurn(NativeLaneContainment containment, bool planMode)
        => new("conv", "plan it", new CodingLane("claude", ClaudeCodeNativeLaneRunner.ProviderId, "claude-code:default"), Path.GetTempPath(), "swarmcode", false)
            { Containment = containment, PlanMode = planMode };

    private static string? After(IList<string> args, string flag) => args.IndexOf(flag) is var i and >= 0 && i + 1 < args.Count ? args[i + 1] : null;

    [Fact]
    public void A_claude_plan_turn_prompt_names_only_the_task_list_from_the_bridge_and_mounts_no_external_server()
    {
        // Live run (2026-10-14): a plan turn needs the task list; the host serves only tools that change nothing for it.
        var bridge = new McpServerLaunch { Command = "swarmcode", Arguments = ["--mcp-server"] };
        var plan = NativeLaneSupport.BuildPrompt(ClaudeTurn(NativeLaneContainment.Bypass, planMode: true), true, bridge, "CLAUDE.md");
        Assert.Contains("plan_update", plan);
        Assert.Contains("only tools that change nothing", plan);
        Assert.DoesNotContain("shell_exec", plan);
        Assert.DoesNotContain("browser_act", plan);

        var path = ClaudeCodeNativeLaneRunner.WriteMcpConfigFor(NativeLaneContainment.Bypass, bridge, Path.GetTempPath(), null, "conv")!;
        try
        {
            var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();
            Assert.Equal(["swarmcode"], servers.Select(s => s.Key));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Claude_code_runs_a_plan_turn_in_its_plan_permission_mode_without_edit_shell_or_unreduced_mcp_tools()
    {
        var args = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", ClaudeTurn(NativeLaneContainment.Bypass, planMode: true),
            null, "mcp.json", NativeLaneContainment.Bypass).ArgumentList.ToList();
        Assert.Equal("plan", After(args, "--permission-mode"));
        Assert.DoesNotContain("bypassPermissions", args);
        // Only this launch's config loads: the bridge reduced to the task-list tools (written by the runner for a plan turn).
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("mcp.json", After(args, "--mcp-config"));
        var disallowed = After(args, "--disallowedTools")!.Split(',');
        Assert.Contains("Bash", disallowed);
        Assert.Contains("Edit", disallowed);
        Assert.Contains("Write", disallowed);

        // Without a bridge every MCP tool stays unavailable.
        var noBridge = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", ClaudeTurn(NativeLaneContainment.Bypass, planMode: true),
            null, null, NativeLaneContainment.Bypass).ArgumentList.ToList();
        Assert.DoesNotContain("--mcp-config", noBridge);
        Assert.Contains("mcp__*", After(noBridge, "--disallowedTools")!.Split(','));

        var confined = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", ClaudeTurn(NativeLaneContainment.WorkspaceConfined, planMode: true),
            null, null, NativeLaneContainment.WorkspaceConfined, restrictedSupported: true).ArgumentList.ToList();
        Assert.Equal("plan", After(confined, "--permission-mode"));
        Assert.Contains("--restricted", confined);
        Assert.Contains("Write", After(confined, "--disallowedTools")!.Split(','));

        // Without plan mode nothing changes.
        var normal = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", ClaudeTurn(NativeLaneContainment.Bypass, planMode: false),
            null, "mcp.json", NativeLaneContainment.Bypass).ArgumentList.ToList();
        Assert.Equal("bypassPermissions", After(normal, "--permission-mode"));
        Assert.Equal("mcp.json", After(normal, "--mcp-config"));
    }

    [Fact]
    public void Only_claude_code_claims_a_verified_plan_mode()
    {
        var options = Options.Create(new CodingOptions());
        Assert.True(((INativeLaneRunner)new ClaudeCodeNativeLaneRunner(null!, null!, options, NullLogger<ClaudeCodeNativeLaneRunner>.Instance)).SupportsPlanMode);
        Assert.False(((INativeLaneRunner)new GeminiCliNativeLaneRunner(null!, null!, options, NullLogger<GeminiCliNativeLaneRunner>.Instance)).SupportsPlanMode);
        Assert.False(((INativeLaneRunner)new CodexNativeLaneRunner(null!, null!, options, NullLogger<CodexNativeLaneRunner>.Instance)).SupportsPlanMode);
    }

    [Fact]
    public void Native_prompt_tells_the_agent_it_is_in_plan_mode()
    {
        var prompt = NativeLaneSupport.BuildPrompt(ClaudeTurn(NativeLaneContainment.Bypass, planMode: true), isFirstTurn: true, null, "CLAUDE.md");
        Assert.Contains("plan mode", prompt);
    }

    // ── plan mode: orchestrator ───────────────────────────────────

    private sealed class Lanes : ICodingLaneSource
    {
        public CodingLaneSet GetLanes() => new()
        {
            Lanes = [new("native", "SomeNative", "n:default") { Priority = 0 }, new("api", "Api", "m") { Priority = 1 }],
        };
    }

    private sealed class Runner(List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            await Task.Yield();
            yield return new TurnCompleted("t", "1. Read the code\n2. Change it", TimeSpan.Zero);
        }
    }

    private sealed class Native(List<NativeLaneTurn> seen, bool plans) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public bool SupportsPlanMode => plans;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("n", "plan", TimeSpan.Zero);
        }
    }

    private sealed class CountingVerifier : ICodingAcceptanceVerifier
    {
        public int Calls;
        public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(CodingAcceptanceResult.Unverified);
        }
    }

    private static async Task<(List<TurnEvent> Events, List<AgentTurnRequest> Api, List<NativeLaneTurn> Native, CountingVerifier Verifier)> Run(
        bool planMode, bool nativePlans)
    {
        var api = new List<AgentTurnRequest>();
        var native = new List<NativeLaneTurn>();
        var verifier = new CountingVerifier();
        var services = new ServiceCollection();
        services.AddScoped<IAgentTurnRunner>(_ => new Runner(api));
        services.AddScoped<INativeLaneRunner>(_ => new Native(native, nativePlans));
        services.AddSingleton<ICodingLaneSource>(new Lanes());
        services.AddSingleton<ICodingAcceptanceVerifier>(verifier);
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<CodingTurnOrchestrator>.Instance);
        var events = new List<TurnEvent>();
        await foreach (var e in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "Fix the login bug in the code", PlanMode = planMode, RequireWorkspaceChange = planMode, // a plan turn ignores it
            CommunicationScope = AgentCommunicationScope.UserRequested("chat"),
        })) events.Add(e);
        return (events, api, native, verifier);
    }

    [Fact]
    public async Task A_plan_turn_skips_native_lanes_without_plan_mode_and_runs_the_engine_read_only()
    {
        var (events, api, native, verifier) = await Run(planMode: true, nativePlans: false);
        Assert.Empty(native);
        Assert.Contains(events.OfType<CodingLaneSkipped>(), s => s.Lane.Name == "native" && s.Reason == CodingPlanMode.NativeLaneCannotPlan);
        var engine = Assert.Single(api);
        Assert.True(CodingPlanMode.IsActive(engine.Metadata));
        Assert.StartsWith("[Plan mode]", engine.UserMessage);
        Assert.Equal("Fix the login bug in the code", engine.AuthenticatedOriginalUserMessage);
        // The plan is the answer: no workspace-change repair, no acceptance checks.
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.DoesNotContain(events, e => e is TurnErrored);
        Assert.DoesNotContain(events, e => e is CodingAcceptanceEvaluated);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task A_plan_turn_runs_a_native_lane_that_supports_plan_mode_with_the_flag()
    {
        var (_, api, native, _) = await Run(planMode: true, nativePlans: true);
        Assert.Empty(api);
        Assert.True(Assert.Single(native).PlanMode);
    }

    [Fact]
    public async Task Without_plan_mode_native_lanes_and_metadata_are_unchanged()
    {
        var (_, api, native, _) = await Run(planMode: false, nativePlans: false);
        Assert.Empty(api);
        Assert.False(Assert.Single(native).PlanMode);
        Assert.Equal("Fix the login bug in the code", native[0].UserMessage);
    }
}
