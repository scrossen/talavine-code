using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Planning;
using SwarmCore.Coding.Computer;
using SwarmCore.Coding.SelfImprovement;
using Talavine.TestSupport;
using Xunit;

namespace SwarmCore.Coding.Tests;

public class HostToolBridgeTests
{
    public sealed record EchoInput(string Text = "");
    public sealed record EchoOutput(string Text, string? Owner);
    public sealed record ShotOutput(string Text, [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ImagePart> Images) : IToolResultImages;
    public sealed record FailOutput(bool Success, string Error);

    /// <summary>A host with a fixed tool list, scripted admission and guard, and a record of ambient calls.</summary>
    private sealed class FakeHost(IReadOnlyList<ToolDescriptor> tools) : IHostToolBridgeHost
    {
        public Dictionary<string, HostToolProfile> Running { get; } = new(StringComparer.Ordinal);
        public Func<ToolInvocationRequest, ToolGuardDecision> Guard { get; set; } = _ => ToolGuardDecision.Allow();
        public List<string> Began { get; } = [];
        public int Ended;
        public static readonly AsyncLocal<string?> Owner = new();

        public HostToolProfile? Admit(string conversationId) => Running.TryGetValue(conversationId, out var p) ? p : null;
        public Task<IReadOnlyList<ToolDescriptor>> ToolsAsync(string conversationId, CancellationToken ct) => Task.FromResult(tools);
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct) => Task.FromResult(Guard(request));

        public IDisposable BeginCall(string conversationId)
        {
            Began.Add(conversationId);
            var prior = Owner.Value;
            Owner.Value = conversationId;
            return new End(() => { Owner.Value = prior; Interlocked.Increment(ref Ended); });
        }

        private sealed class End(Action end) : IDisposable { public void Dispose() => end(); }
    }

    private static ToolDescriptor Echo(string id = "agents.echo", ToolSideEffect effect = ToolSideEffect.WritesLocal)
        => ToolDescriptor.Create<EchoInput, EchoOutput>(id, "test", "Echo " + id, ToolCostClass.Cheap, effect,
            (input, _) => Task.FromResult(new EchoOutput(input.Text, FakeHost.Owner.Value)));

    private static IReadOnlyList<ToolDescriptor> Tools(params ToolDescriptor[] extra) =>
    [
        Echo(),
        Echo("personal-memory.list", ToolSideEffect.ReadOnly),
        Echo("code.edit_file"), Echo("git.commit"), Echo("fs.read_file", ToolSideEffect.ReadOnly),
        Echo("mcp.talavine.search", ToolSideEffect.ReadOnly),
        Echo(PlanTools.UpdateToolId),
        .. extra,
    ];

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Catalog_offers_host_tools_without_cli_equivalents_or_external_servers_under_mcp_names()
    {
        var full = HostToolBridgeCatalog.Select(Tools(), HostToolProfile.Full);
        Assert.Equal(["agents_echo", "personal_memory_list", "plan_update"], full.Keys.Order());

        // A plan turn gets only tools that change nothing, plus the task list.
        var plan = HostToolBridgeCatalog.Select(Tools(), HostToolProfile.Plan);
        Assert.Equal(["personal_memory_list", "plan_update"], plan.Keys.Order());
        Assert.True(HostToolBridgeCatalog.Manifest("plan_update", plan["plan_update"]).ReadOnly);
        Assert.False(HostToolBridgeCatalog.Manifest("agents_echo", full["agents_echo"]).ReadOnly);
        Assert.Equal("object", (string?)HostToolBridgeCatalog.Manifest("agents_echo", full["agents_echo"]).InputSchema["type"]);
    }

    [Fact]
    public async Task Only_a_conversation_running_a_turn_is_served_and_calls_run_under_its_ambient_turn()
    {
        var host = new FakeHost(Tools());
        await using var broker = new HostToolBridgeBroker(host);

        Assert.Null(await broker.ListAsync("other", default));
        var refused = await broker.CallAsync("other", "agents_echo", Args(new { text = "x" }), default);
        Assert.True(refused.IsError);
        Assert.Empty(host.Began);

        host.Running["root"] = HostToolProfile.Full;
        Assert.Equal(3, (await broker.ListAsync("root", default))!.Count);
        var result = await broker.CallAsync("root", "agents_echo", Args(new { text = "hi" }), default);
        Assert.False(result.IsError);
        var echoed = JsonSerializer.Deserialize<EchoOutput>(result.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(("hi", "root"), (echoed.Text, echoed.Owner));
        Assert.Equal(["root"], host.Began);
        Assert.Equal(1, host.Ended);

        // Not offered: a CLI-native equivalent, an external server's projection, an unknown name.
        foreach (var name in new[] { "code_edit_file", "mcp_talavine_search", "nope" })
            Assert.True((await broker.CallAsync("root", name, Args(new { }), default)).IsError);
    }

    [Fact]
    public async Task A_plan_turn_reaches_only_tools_that_change_nothing()
    {
        var host = new FakeHost(Tools());
        host.Running["root"] = HostToolProfile.Plan;
        await using var broker = new HostToolBridgeBroker(host);

        var refused = await broker.CallAsync("root", "agents_echo", Args(new { text = "x" }), default);
        Assert.True(refused.IsError);
        Assert.Contains("plan mode", refused.Text);
        Assert.False((await broker.CallAsync("root", "plan_update", Args(new { text = "x" }), default)).IsError);
    }

    [Fact]
    public async Task The_host_guard_runs_before_dispatch_and_an_approval_need_refuses()
    {
        var ran = 0;
        var counted = ToolDescriptor.Create<EchoInput, EchoOutput>("agents.count", "test", "Count", ToolCostClass.Cheap, ToolSideEffect.Exec,
            (input, _) => { Interlocked.Increment(ref ran); return Task.FromResult(new EchoOutput(input.Text, null)); });
        var host = new FakeHost(Tools(counted));
        host.Running["root"] = HostToolProfile.Full;
        await using var broker = new HostToolBridgeBroker(host);

        host.Guard = request => request.ToolId == "agents.count" && request.EffectiveSideEffect == ToolSideEffect.Exec && request.ConversationId == "root"
            ? ToolGuardDecision.Block("Denied by your settings (Coding:Permissions:Deny agents.*).")
            : ToolGuardDecision.Allow();
        var blocked = await broker.CallAsync("root", "agents_count", Args(new { text = "x" }), default);
        Assert.True(blocked.IsError);
        Assert.Contains("Coding:Permissions:Deny", blocked.Text);

        host.Guard = _ => ToolGuardDecision.Escalate("This needs approval.");
        var escalated = await broker.CallAsync("root", "agents_count", Args(new { text = "x" }), default);
        Assert.True(escalated.IsError);
        Assert.Contains("approval", escalated.Text);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Results_carry_images_failures_and_the_same_exception_text_an_api_lane_reads()
    {
        var shot = ToolDescriptor.Create<EchoInput, ShotOutput>("browser.shot", "test", "Shot", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => Task.FromResult(new ShotOutput("page", [new ImagePart(Convert.ToBase64String([1, 2, 3]), "image/png")])));
        var failing = ToolDescriptor.Create<EchoInput, FailOutput>("agents.fail", "test", "Fail", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => Task.FromResult(new FailOutput(false, "nope")));
        var stated = ToolDescriptor.Create<EchoInput, EchoOutput>("computer.stated", "test", "Stated", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => throw new Computer.ComputerOperationException("Windows refused to activate the window."));
        var leaky = ToolDescriptor.Create<EchoInput, EchoOutput>("apps.leaky", "test", "Leaky", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => throw new InvalidOperationException("no such app"));
        var host = new FakeHost(Tools(shot, failing, stated, leaky));
        host.Running["root"] = HostToolProfile.Full;
        await using var broker = new HostToolBridgeBroker(host);

        var image = await broker.CallAsync("root", "browser_shot", Args(new { }), default);
        Assert.Equal("image/png", Assert.Single(image.Images).MediaType);
        Assert.DoesNotContain("AQID", image.Text); // pixels travel beside the JSON, not inside it

        Assert.True((await broker.CallAsync("root", "agents_fail", Args(new { }), default)).IsError);
        Assert.Contains("refused to activate", (await broker.CallAsync("root", "computer_stated", Args(new { }), default)).Text);

        // Any other failure reads exactly as it would on an API lane (ToolUseAgentEngine): type and message.
        var proxy = new HostToolBridgeProxy(broker.GetEndpoint(), "root");
        var generic = await proxy.CallAsync("apps_leaky", Args(new { }), default);
        Assert.True(generic.IsError);
        Assert.Equal("InvalidOperationException: no such app", generic.Text);
    }

    [Fact]
    public async Task Chrome_setup_reaches_the_native_client_without_becoming_a_product_defect()
    {
        using var root = TestTempDirectory.Reserve("chrome-bridge");
        var services = new ServiceCollection();
        services.AddCodingComputer(o => o.PersonalChromeUserDataDirectory = root.Path);
        await using var provider = services.BuildServiceProvider();
        var host = new FakeHost(provider.GetRequiredService<CodingComputerTools>().Descriptors);
        host.Running["root"] = HostToolProfile.Full;
        await using var broker = new HostToolBridgeBroker(host);
        var proxy = new HostToolBridgeProxy(broker.GetEndpoint(), "root");

        var result = await proxy.CallAsync("browser_tabs", Args(new { }), default);

        Assert.True(result.IsError);
        Assert.Contains(PersonalChromeConnection.SetupUrl, result.Text);
        Assert.Contains("allow Chrome's connection prompt", result.Text);
        Assert.Null(DefectClassifier.FromErrorText(result.Text));
        Assert.False(root.Exists);
    }

    [Fact]
    public async Task PostToolUse_notes_reach_the_cli_without_hiding_the_tools_images_or_failure()
    {
        var shot = ToolDescriptor.Create<EchoInput, Extensions.HookAnnotatedToolResult>("browser.shot", "test", "Shot", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => Task.FromResult(new Extensions.HookAnnotatedToolResult(
                new ShotOutput("page", [new ImagePart(Convert.ToBase64String([1, 2, 3]), "image/png")]), ["checked by a hook"])));
        var failing = ToolDescriptor.Create<EchoInput, Extensions.HookAnnotatedToolResult>("agents.fail", "test", "Fail", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => Task.FromResult(new Extensions.HookAnnotatedToolResult(new FailOutput(false, "nope"), ["formatted by a hook"])));
        var host = new FakeHost(Tools(shot, failing));
        host.Running["root"] = HostToolProfile.Full;
        await using var broker = new HostToolBridgeBroker(host);

        var image = await broker.CallAsync("root", "browser_shot", Args(new { }), default);
        Assert.Equal("image/png", Assert.Single(image.Images).MediaType);
        Assert.Contains("checked by a hook", image.Text);
        Assert.False(image.IsError);

        var failed = await broker.CallAsync("root", "agents_fail", Args(new { }), default);
        Assert.True(failed.IsError);
        Assert.Contains("formatted by a hook", failed.Text);
    }

    [Fact]
    public async Task The_pipe_lists_and_calls_and_a_dropped_call_is_cancelled_in_the_host()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = ToolDescriptor.Create<EchoInput, EchoOutput>("shell.slow", "test", "Slow", ToolCostClass.Cheap, ToolSideEffect.Exec,
            async (_, ct) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return new EchoOutput("never", null);
            });
        var host = new FakeHost(Tools(slow));
        host.Running["root"] = HostToolProfile.Full;
        await using var broker = new HostToolBridgeBroker(host);
        var proxy = new HostToolBridgeProxy(broker.GetEndpoint(), "root");

        var listed = await proxy.ListAsync(default);
        Assert.Contains(listed, t => t.Name == "shell_slow");
        Assert.Contains("over the pipe", (await proxy.CallAsync("agents_echo", Args(new { text = "over the pipe" }), default)).Text);

        using var cts = new CancellationTokenSource();
        var call = proxy.CallAsync("shell_slow", Args(new { }), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The broker keeps serving after a dropped call.
        Assert.False((await proxy.CallAsync("agents_echo", Args(new { text = "again" }), default)).IsError);

        // A conversation that is not running gets a refusal, not tools.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HostToolBridgeProxy(broker.GetEndpoint(), "other").ListAsync(default));
    }

    [Fact]
    public async Task Disposing_the_broker_cancels_running_work()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = ToolDescriptor.Create<EchoInput, EchoOutput>("shell.slow", "test", "Slow", ToolCostClass.Cheap, ToolSideEffect.Exec,
            async (_, ct) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return new EchoOutput("never", null);
            });
        var host = new FakeHost(Tools(slow));
        host.Running["root"] = HostToolProfile.Full;
        var broker = new HostToolBridgeBroker(host);
        var call = new HostToolBridgeProxy(broker.GetEndpoint(), "root").CallAsync("shell_slow", Args(new { }), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await broker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => call);
    }

    [Fact]
    public async Task Service_host_admits_running_coding_turns_and_roots_calls_in_their_workspace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ActiveCodingWorkspaces>();
        services.AddSingleton<ICurrentTurnContextAccessor, AsyncLocalCurrentTurnContextAccessor>();
        var echo = ToolDescriptor.Create<EchoInput, EchoOutput>("agents.where", "test", "Where", ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (_, _) => Task.FromResult(new EchoOutput(AsyncLocalCurrentTurnContextAccessor.Ambient?.WorkspaceRoot ?? "", AsyncLocalCurrentTurnContextAccessor.Ambient?.ConversationId)));
        var scopeSeen = false;
        services.AddSingleton<IProfileResolver>(new FakeResolver([echo], () => scopeSeen = HostToolBridgeScope.Active));
        services.AddSwarmCoreHostToolBridge();
        await using var sp = services.BuildServiceProvider();
        var broker = sp.GetRequiredService<HostToolBridgeBroker>();
        var active = sp.GetRequiredService<ActiveCodingWorkspaces>();

        Assert.Null(await broker.ListAsync("conv", default));
        var root = Path.GetFullPath(Path.GetTempPath());
        using (active.Enter("conv", new CodingWorkspace { RepositoryRoot = root }, planMode: true))
        {
            Assert.True(active.IsPlanTurn("conv"));
            var result = await broker.CallAsync("conv", "agents_where", Args(new { }), default);
            var where = JsonSerializer.Deserialize<EchoOutput>(result.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal((Path.GetFullPath(root), "conv"), (Path.GetFullPath(where.Text), where.Owner));
            Assert.True(scopeSeen); // external MCP servers are not started for a bridge lookup
        }
        Assert.Null(await broker.ListAsync("conv", default));
        Assert.Null(AsyncLocalCurrentTurnContextAccessor.Ambient);
    }

    private sealed class FakeResolver(IReadOnlyList<ToolDescriptor> tools, Action onResolve) : IProfileResolver
    {
        public Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
        {
            onResolve();
            return Task.FromResult(new ResolvedProfile(new SwarmCore.Agent.ToolUseEngine.AgentProfileSnapshot { Id = Guid.Empty, Name = "t", SystemPrompt = "", EnabledToolIds = [] }, tools));
        }
    }
}
