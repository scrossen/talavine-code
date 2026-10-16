using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Coding.Settings;
using Xunit.Abstractions;

namespace SwarmCode.Hosting.Tests;

public sealed class AgentSettingsLiveFactAttribute : FactAttribute
{
    public AgentSettingsLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWARMCODE_SETTINGS_LIVE") != "1") Skip = "Set SWARMCODE_SETTINGS_LIVE=1 to verify settings through an authenticated native model and host bridge.";
    }
}

[Trait("LlmMode", "Live")]
public sealed class AgentSettingsLiveTests(ITestOutputHelper output) : TempDirTest
{
    private static string ToolName(ToolCallEntry tool)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(tool.InputJson)?["tool"]?.ToString() ?? ""; }
        catch { return ""; }
    }

    [AgentSettingsLiveFact]
    public async Task Natural_language_request_saves_current_defaults_appearance_and_runtime_via_real_agent()
    {
        var repo = Path.Combine(Tmp,"repo"); Directory.CreateDirectory(repo);
        var overrides = new Dictionary<string,string?>
        {
            ["PersonalMemory:Enabled"] = "false", ["Coding:Agents:Enabled"] = "false",
            ["Coding:Computer:Enabled"] = "false", ["Coding:HardTimeout"] = "00:03:00",
            ["Coding:FallbackDiscovery:Enabled"] = "false"
        };
        // The test console is framework-dependent. Launch its DLL with the exact test host,
        // so a native CLI's environment filtering cannot select a different installed runtime.
        var console = Path.Combine(AppContext.BaseDirectory, "swarmcode.dll");
        Assert.True(File.Exists(console));
        overrides["Coding:NativeAgents:McpServer:Command"] = Environment.ProcessPath!;
        overrides["Coding:NativeAgents:McpServer:Arguments:0"] = console;
        overrides["Coding:NativeAgents:McpServer:Arguments:1"] = "--mcp-server";
        overrides["Coding:NativeAgents:McpServer:Environment:SWARMCODE_HOME"] = Paths.RootDirectory;
        overrides["Coding:NativeAgents:McpServer:Required"] = "true";
        await using var host = await WorkspaceHost.CreateAsync(new() { DataPaths = Paths, RepositoryRoot = repo,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, overrides) });
        var laneName = Environment.GetEnvironmentVariable("SWARMCODE_SETTINGS_LIVE_LANE") ?? "claude";
        var lane = host.Lanes.First(l => l.Name == laneName);
        var sessions = host.Services.GetRequiredService<WorkspaceSessionCatalog>();
        var session = await sessions.CreateAsync(repo, "Settings live verification", laneName: lane.Name, model: lane.Model);
        await sessions.SetModelAsync(session.Id,lane.Name,lane.Model);
        await using var runner = host.OpenSession(session.Id);
        var finish = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += result => finish.TrySetResult(result);
        Assert.True(runner.TrySend("Make my current selections the default model, provider and reasoning. Also show reasoning in chat, set my text size to 1.1, and limit jobs to 37 tool calls. Do this now.", lane.Name));
        var result = await finish.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.Null(result.Error); Assert.True(result.Completed);
        var tools = runner.Entries.OfType<ToolCallEntry>().ToArray();
        output.WriteLine("Tools: " + string.Join(", ", tools.Select(t => t.ToolName + ":" + t.State + ":" + ToolName(t))));
        foreach (var tool in tools.Where(t => t.State == ToolCallState.Failed)) output.WriteLine(tool.Result);
        foreach (var assistant in runner.Entries.OfType<AssistantEntry>()) output.WriteLine(assistant.Text);
        var prefs = new AppPreferencesStore(Paths).Current;
        Assert.Equal(lane.Name,prefs.DefaultLane); Assert.Equal(lane.Model,prefs.DefaultModel);
        Assert.True(prefs.ShowReasoning); Assert.Equal(1.1,prefs.Zoom);
        Assert.Equal(37,new UserSettingsFile(Paths).ReadCodingSection<SwarmCore.Coding.CodingExecutionLimits>("ExecutionLimits").MaxToolCalls);
        // The durable host settings, not a model's answer, establish success.
        Assert.Contains(tools,t => t.ToolName.Contains("settings",StringComparison.OrdinalIgnoreCase) || ToolName(t).Contains("settings",StringComparison.OrdinalIgnoreCase));
        output.WriteLine("Live native model saved current defaults, reasoning display, text size and a runtime limit; independent stores verified the persisted values.");
    }
}
