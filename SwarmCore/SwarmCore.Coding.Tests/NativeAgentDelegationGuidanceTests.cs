using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class NativeAgentDelegationGuidanceTests
{
    private static readonly McpServerLaunch Bridge = new() { Command = "host-bridge", Arguments = ["--mcp-server"] };
    private static NativeLaneTurn Turn() => new("conversation", "Verify the receipt totals.",
        new("codex", CodexNativeLaneRunner.ProviderId, "default"), Path.GetTempPath(), "host", false)
        { AgentTeamEnabled = true };

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Fresh_and_resumed_native_prompts_carry_the_same_shared_delegation_contract(
        bool first, bool continuation, bool history)
    {
        var prompt = NativeLaneSupport.BuildPrompt(Turn() with { IsContinuation = continuation }, first, Bridge, "AGENTS.md",
            history ? [ChatMessage.Text(ChatRole.User, "Earlier scope.")] : null);

        var shared = AgentTeamPrompts.Guidance.Replace("agents.", "agents_", StringComparison.Ordinal);
        Assert.Contains(shared, prompt);
        Assert.Equal(prompt.IndexOf(shared, StringComparison.Ordinal), prompt.LastIndexOf(shared, StringComparison.Ordinal));
        Assert.Contains("agents_spawn without lane or model", prompt);
        Assert.Contains("Keep the current lead model for this conversation", prompt);
        Assert.Contains("automatic delegation policy can assess the task when enabled", prompt);
        Assert.Contains("built-in subagents do not pass through Talavine Code's local-model routing", prompt);
        Assert.Contains("remain available for clearly difficult work", prompt);
        Assert.Contains("only when the person has separately authorized that work", prompt);
        Assert.Contains("Do not start unsolicited outside-scope jobs", prompt);
        Assert.Contains("For authorized follow-up work, use agents_spawn { independent: true }", prompt);
        Assert.Contains("its result is not handed back to you and agents_wait does not wait for it", prompt);
        Assert.DoesNotContain("When you notice separate work outside", prompt);
        Assert.DoesNotContain("agents_spawn starts Talavine Code agents the person can watch", prompt);
        Assert.EndsWith("Current user request:\nVerify the receipt totals.", prompt);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void Disabled_agents_readonly_confined_or_missing_bridge_never_advertise_delegation(
        bool enabled, bool plan, bool confined, bool bridge)
    {
        var turn = Turn() with { AgentTeamEnabled = enabled, PlanMode = plan,
            Containment = confined ? NativeLaneContainment.WorkspaceConfined : NativeLaneContainment.Bypass };
        var prompt = NativeLaneSupport.BuildPrompt(turn, true, bridge ? Bridge : null, "AGENTS.md");

        Assert.DoesNotContain("When automatic routine delegation is enabled", prompt);
        Assert.DoesNotContain("agents_spawn", prompt);
        Assert.DoesNotContain("built-in subagents", prompt);
        if (plan) Assert.Contains("This turn is in plan mode", prompt);
        if (bridge && !plan) Assert.Contains("shell_background_start", prompt);
    }

    [Fact]
    public void Resumed_agent_guidance_does_not_resend_unchanged_reference_context()
    {
        var turn = Turn() with
        {
            ConversationId = "native-guidance-context-" + Guid.NewGuid().ToString("N"),
            HostContext = [new("repository-memory", "Stable reference context for this repository.")],
        };
        var first = NativeLaneSupport.BuildPrompt(turn, true, Bridge, "AGENTS.md");
        var resumed = NativeLaneSupport.BuildPrompt(turn with { UserMessage = "Continue checking." }, false, Bridge, "AGENTS.md");

        Assert.Contains("agents_spawn without lane or model", first);
        Assert.Contains("Stable reference context for this repository.", first);
        Assert.Contains("agents_spawn without lane or model", resumed);
        Assert.DoesNotContain("Stable reference context for this repository.", resumed);
        Assert.DoesNotContain(NativeHostContext.Preamble, resumed);

        var changed = NativeLaneSupport.BuildPrompt(turn with
            { HostContext = [new("repository-memory", "Changed reference context.")] }, false, Bridge, "AGENTS.md");
        Assert.Contains("agents_spawn without lane or model", changed);
        Assert.Contains(NativeHostContext.UpdatedPreamble, changed);
        Assert.Contains("Changed reference context.", changed);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Orchestrator_advertises_only_a_registered_enabled_host_team(bool registered, bool enabled, bool expected)
    {
        var seen = new List<NativeLaneTurn>();
        var services = new ServiceCollection();
        services.Configure<AgentTeamOptions>(options => options.Enabled = enabled);
        if (registered) services.AddSingleton(new AgentTeam(new InMemoryAgentTeamStore(), () => new AgentTeamOptions { Enabled = enabled }));
        services.AddScoped<INativeLaneRunner>(_ => new RecordingNative(seen));
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
            { Lanes = [new("codex", CodexNativeLaneRunner.ProviderId, "default")], HandOffOnRefusal = false })));
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        using var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CodingOptions()), NullLogger<CodingTurnOrchestrator>.Instance);

        await foreach (var _ in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "native-guidance-" + Guid.NewGuid().ToString("N"), UserMessage = "Verify the receipt totals.",
            LaneName = "codex", CommunicationScope = AgentCommunicationScope.UserRequested("test"),
        })) { }

        Assert.Equal(expected, Assert.Single(seen).AgentTeamEnabled);
    }

    private sealed class RecordingNative(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("native", "Verified totals.", TimeSpan.Zero);
        }
    }
}
