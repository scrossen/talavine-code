using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Coding;
using SwarmCore.Coding.Computer;
using SwarmCore.Coding.Native;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// A real SwarmCode workspace with a native bridge configured, one conversation running a coding turn, and a bridge
/// proxy for it: exactly what a native CLI's MCP bridge process reaches.
/// </summary>
internal sealed class BridgedWorkspace : IAsyncDisposable
{
    private readonly List<IDisposable> _turns = [];

    private BridgedWorkspace(WorkspaceHost workspace, string repo)
    {
        Workspace = workspace;
        Repo = repo;
    }

    public WorkspaceHost Workspace { get; }
    public string Repo { get; }
    public IServiceProvider Services => Workspace.Services;
    public HostToolBridgeBroker Broker => Services.GetRequiredService<HostToolBridgeBroker>();

    public static async Task<BridgedWorkspace> StartAsync(SwarmCodeDataPaths paths, string repo, IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configure = null)
    {
        Directory.CreateDirectory(repo);
        var settings = new Dictionary<string, string?>
        {
            ["Coding:NativeAgents:McpServer:Command"] = "swarmcode",
            ["Coding:NativeAgents:McpServer:Arguments:0"] = "--mcp-server",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>()) settings[key] = value;
        // With a bridge configured the coding options post-configure the bridge endpoint: composition must finish promptly.
        var workspace = await Task.Run(() => WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(paths, settings),
            ConfigureServices = services =>
            {
                services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
                configure?.Invoke(services);
            },
        })).WaitAsync(TimeSpan.FromSeconds(60));
        return new BridgedWorkspace(workspace, repo);
    }

    /// <summary>Starts a coding turn for the conversation, as the orchestrator does for the turn's duration.</summary>
    public HostToolBridgeProxy Turn(string conversationId, bool planMode = false)
    {
        _turns.Add(Services.GetRequiredService<ActiveCodingWorkspaces>().Enter(conversationId, new CodingWorkspace { RepositoryRoot = Repo }, planMode));
        return new HostToolBridgeProxy(Broker.GetEndpoint(), conversationId);
    }

    public static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    public static JsonDocument Json(HostToolResult result) => JsonDocument.Parse(result.Text);

    public async ValueTask DisposeAsync()
    {
        foreach (var turn in _turns) turn.Dispose();
        await Workspace.DisposeAsync();
    }
}

public class HostToolBridgeHostingTests : TempDirTest
{
    private string Repo => Path.Combine(Tmp, "repo");

    [Fact]
    public async Task One_bridge_endpoint_serves_every_host_tool_family_and_leaves_cli_equivalents_out()
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var coding = await Task.Run(() => host.Services.GetRequiredService<IOptions<CodingOptions>>().Value).WaitAsync(TimeSpan.FromSeconds(30));
        var environment = coding.NativeAgents.McpServer!.Environment;
        Assert.True(environment.ContainsKey(HostToolBridgeBroker.PipeEnvironmentVariable));
        // The one endpoint replaces the per-family pipes.
        Assert.DoesNotContain(environment.Keys, k => k is "SWARMCODE_AGENTS_PIPE" or "SWARMCODE_PLAN_PIPE" or "SWARMCODE_MEMORY_PIPE" or "SWARMCODE_COMPUTER_PIPE");

        var names = (await host.Turn("session-1").ListAsync(default)).Select(t => t.Name).ToHashSet();
        foreach (var name in new[]
                 {
                     "agents_spawn", "plan_update", "shell_background_start", "credentials_list", "shell_exec", "web_search",
                     "notebook_read", "github_pr_status", "git_push", "apps_list", "browser_observe", "computer_act",
                     "personal_memory_remember", "repo_memory_search",
                 })
            Assert.Contains(name, names);
        foreach (var name in new[] { "code_edit_file", "code_grep", "fs_write_file", "git_commit", "git_status", "harness_decline" })
            Assert.DoesNotContain(name, names);
    }

    [Fact]
    public async Task A_plan_turn_lists_only_tools_that_change_nothing_and_marks_them_read_only()
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var tools = await host.Turn("session-1", planMode: true).ListAsync(default);
        Assert.Contains(tools, t => t.Name == "plan_update");
        Assert.Contains(tools, t => t.Name == "github_pr_status");
        Assert.All(tools, t => Assert.True(t.ReadOnly, t.Name));
        foreach (var acting in new[] { "shell_exec", "agents_spawn", "git_push", "browser_act", "personal_memory_remember" })
            Assert.DoesNotContain(tools, t => t.Name == acting);
    }

    [Fact]
    public async Task Credentials_and_commands_use_the_hosts_vault_and_never_show_values()
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var vault = host.Services.GetRequiredService<ISecretVault>();
        await vault.SetAsync(new SecretWriteRequest { Name = "github-token", Value = "ghp_supersecretvalue1234567890", Kind = SecretKinds.Token, Site = "https://github.com", Username = "octocat" });
        await vault.SetAsync(new SecretWriteRequest { Name = "demo", Value = "value-9f3a1c-demo" });
        var bridge = host.Turn("session-1");

        var listed = await bridge.CallAsync("credentials_list", BridgedWorkspace.Args(new { }), default);
        Assert.False(listed.IsError, listed.Text);
        Assert.Contains("{{secret:github-token}}", listed.Text);
        Assert.DoesNotContain("ghp_supersecret", listed.Text);

        var ran = await bridge.CallAsync("shell_exec", BridgedWorkspace.Args(new { command = "echo start {{secret:demo}} end" }), default);
        Assert.False(ran.IsError, ran.Text);
        Assert.Contains("start {{secret:demo}} end", ran.Text);
        Assert.DoesNotContain("value-9f3a1c-demo", ran.Text);

        // A missing name reads as it would on an API lane: the stored names, never a value.
        var missing = await bridge.CallAsync("shell_exec", BridgedWorkspace.Args(new { command = "echo {{secret:absent}}" }), default);
        Assert.True(missing.IsError);
        Assert.Contains("absent", missing.Text);
        Assert.Contains("demo", missing.Text);
        Assert.DoesNotContain("value-9f3a1c-demo", missing.Text);
    }

    [Fact]
    public async Task Web_search_through_the_bridge_reports_a_missing_key()
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var result = await host.Turn("session-1").CallAsync("web_search", BridgedWorkspace.Args(new { query = "current dotnet version" }), default);
        Assert.Contains("Settings > Web search", result.Text);
    }

    [Theory]
    [InlineData(false, "omitted")]
    [InlineData(false, "null")]
    [InlineData(false, "empty")]
    [InlineData(true, "omitted")]
    [InlineData(true, "null")]
    [InlineData(true, "empty")]
    public async Task Shell_arguments_remain_optional_through_the_native_bridge(bool structured, string arguments)
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var bridge = host.Turn("session-1");
        var shell = Assert.Single(await bridge.ListAsync(default), tool => tool.Name == "shell_exec");
        var required = shell.InputSchema["required"]?.AsArray().Select(value => value!.GetValue<string>()) ?? [];
        Assert.DoesNotContain("argumentList", required);

        // With no arguments the platform shell exits on shell.exec's closed stdin.
        var input = structured
            ? new JsonObject { ["fileName"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh" }
            : new JsonObject { ["command"] = "echo shell-bridge-ok" };
        if (arguments != "omitted") input["argumentList"] = arguments == "empty" ? new JsonArray() : null;
        input["timeoutSeconds"] = 10;

        var result = await bridge.CallAsync("shell_exec", BridgedWorkspace.Args(input), default);

        Assert.False(result.IsError, result.Text);
        using var output = BridgedWorkspace.Json(result);
        Assert.Equal(0, output.RootElement.Prop("exitCode").GetInt32());
        Assert.False(output.RootElement.Prop("timedOut").GetBoolean());
        if (!structured) Assert.Contains("shell-bridge-ok", output.RootElement.Prop("stdout").GetString());
    }

    [Fact]
    public async Task Concurrent_turns_each_reach_their_own_personal_memory_turn()
    {
        // The old memory pipe refused whenever more than one turn ran; the bridge names the conversation.
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var turns = host.Services.GetRequiredService<SwarmCode.Hosting.Memory.SwarmCodeMemoryTurns>();
        using var first = turns.Begin(turns.Create("session-1", false, "Remember that I always run dotnet format before committing.", "t1"));
        using var second = turns.Begin(turns.Create("session-2", true, "What do you know about me?", "t2"));
        var one = host.Turn("session-1");
        var two = host.Turn("session-2");

        var remembered = await one.CallAsync("personal_memory_remember",
            BridgedWorkspace.Args(new { text = "User always runs dotnet format before committing.", category = "routine" }), default);
        Assert.True(BridgedWorkspace.Json(remembered).RootElement.Prop("ok").GetBoolean(), remembered.Text);
        // session-2's turn is temporary: memory is neither read nor written for it.
        var temporary = await two.CallAsync("personal_memory_list", BridgedWorkspace.Args(new { }), default);
        Assert.False(BridgedWorkspace.Json(temporary).RootElement.Prop("ok").GetBoolean(), temporary.Text);
    }

    [Fact]
    public async Task Screenshots_reach_the_cli_as_image_blocks_never_inside_the_text()
    {
        const string pixels = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aMxkAAAAASUVORK5CYII=";
        var observation = new ComputerObservation("session", "surface", "observation", "https://example.test", DateTimeOffset.UtcNow, 1, 1, 1) { PngBase64 = pixels };
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo, configure: services =>
            services.AddSingleton<ICodingComputer>(new FixedComputer(observation)));
        var result = await host.Turn("session-1").CallAsync("computer_observe", BridgedWorkspace.Args(new { windowId = "1" }), default);

        var mcp = SwarmCodeMcpServer.ToMcp(result);
        var text = Assert.Single(mcp.Content.OfType<TextContentBlock>()).Text;
        Assert.DoesNotContain(pixels, text);
        Assert.Contains("observation", text);
        var image = Assert.Single(mcp.Content.OfType<ImageContentBlock>());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(pixels), image.Data.ToArray());
        Assert.Equal("image/png", image.MimeType);
        Assert.False(mcp.IsError);
    }

    [Fact]
    public async Task A_conversation_without_a_running_turn_gets_nothing()
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Repo);
        var idle = new HostToolBridgeProxy(host.Broker.GetEndpoint(), "session-9");
        await Assert.ThrowsAsync<InvalidOperationException>(() => idle.ListAsync(default));
        var refused = await idle.CallAsync("credentials_list", BridgedWorkspace.Args(new { }), default);
        Assert.True(refused.IsError);
    }

    private sealed class FixedComputer(ComputerObservation observation) : ICodingComputer
    {
        public Task<ComputerObservation> DesktopObserveAsync(string windowId, CancellationToken ct = default) => Task.FromResult(observation);
        public Task<IReadOnlyList<BrowserTabInfo>> BrowserTabsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BrowserTabInfo> BrowserOpenAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BrowserObservation> BrowserObserveAsync(string tabId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ComputerActionResult> BrowserActAsync(ComputerActionBatch batch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DesktopWindowInfo>> WindowsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ComputerActionResult> DesktopActAsync(ComputerActionBatch batch, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>The host tool bridge over SwarmCode's memory tools alone: a conversation is admitted while its memory turn runs.</summary>
internal sealed class MemoryBridgeHost(SwarmCode.Hosting.Memory.SwarmCodeMemoryTools tools, SwarmCode.Hosting.Memory.SwarmCodeMemoryTurns turns,
    Func<string, bool> running) : IHostToolBridgeHost
{
    public HostToolProfile? Admit(string conversationId) => running(conversationId) ? HostToolProfile.Full : null;
    public Task<IReadOnlyList<SwarmCore.Agent.Catalog.ToolDescriptor>> ToolsAsync(string conversationId, CancellationToken ct) => Task.FromResult(tools.Descriptors);
    public Task<SwarmCore.Agent.Runtime.ToolGuardDecision> InspectAsync(SwarmCore.Agent.Runtime.ToolInvocationRequest request, CancellationToken ct)
        => Task.FromResult(SwarmCore.Agent.Runtime.ToolGuardDecision.Allow());
    public IDisposable BeginCall(string conversationId) => turns.BeginBridgedCall(conversationId) ?? new Nothing();
    private sealed class Nothing : IDisposable { public void Dispose() { } }

    public static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}

internal static class JsonResult
{
    /// <summary>A property of a bridged tool result, whatever its casing (results use the engine's JSON, as on API lanes).</summary>
    public static JsonElement Prop(this JsonElement element, string name)
        => element.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
