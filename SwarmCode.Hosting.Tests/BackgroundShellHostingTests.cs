using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Tools.BackgroundShell;

namespace SwarmCode.Hosting.Tests;

public class BackgroundShellHostingTests : TempDirTest
{
    [Fact]
    public async Task Workspace_offers_background_shells_to_api_lanes_and_the_native_bridge()
    {
        var repo = SwarmCore.Missions.SandboxPathResolver.ResolvePhysicalPath(Path.Combine(Tmp, "repo"));
        Directory.CreateDirectory(repo);
        // Agents off: the bridge endpoint must still be handed to native lanes because host tools are served.
        var workspace = await Task.Run(() => WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
            {
                ["Coding:Agents:Enabled"] = "false",
                ["Coding:BackgroundShells:MaxRunningPerConversation"] = "2",
                ["Coding:NativeAgents:McpServer:Command"] = "swarmcode",
                ["Coding:NativeAgents:McpServer:Arguments:0"] = "--mcp-server",
            }),
        })).WaitAsync(TimeSpan.FromSeconds(60));
        BackgroundShellManager manager;
        try
        {
            var sp = workspace.Services;
            var catalog = sp.GetRequiredService<ICapabilityCatalog>();
            foreach (var id in BackgroundShellTools.AllToolIds) Assert.NotNull(catalog.All().FirstOrDefault(t => t.ToolId == id));
            var coding = await Task.Run(() => sp.GetRequiredService<IOptions<CodingOptions>>().Value).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(coding.NativeAgents.McpServer!.Environment.ContainsKey(SwarmCore.Coding.Native.HostToolBridgeBroker.PipeEnvironmentVariable));
            manager = sp.GetRequiredService<BackgroundShellManager>();
            Assert.Equal(2, sp.GetRequiredService<IOptionsMonitor<BackgroundShellOptions>>().CurrentValue.MaxRunningPerConversation);

            // A native agent's bridge call reaches the workspace's shells for the conversation it was launched for.
            var active = sp.GetRequiredService<SwarmCore.Coding.ActiveCodingWorkspaces>();
            using var turn = active.Enter("session-1", new SwarmCore.Coding.CodingWorkspace { RepositoryRoot = repo });
            var proxy = new SwarmCore.Coding.Native.HostToolBridgeProxy(sp.GetRequiredService<SwarmCore.Coding.Native.HostToolBridgeBroker>().GetEndpoint(), "session-1", active.BridgeCapability("session-1"));
            static System.Text.Json.JsonElement Args(object value) => System.Text.Json.JsonSerializer.SerializeToElement(value);
            var started = await proxy.CallAsync("shell_background_start",
                Args(new { command = "echo from-bridge" + (OperatingSystem.IsWindows() ? " & " : "; ") + Sleep(30), name = "bridge" }), default);
            Assert.False(started.IsError, started.Text);
            var shell = Assert.Single(manager.List("session-1"));
            Assert.Equal(Path.GetFullPath(repo), shell.WorkingDirectory, ignoreCase: OperatingSystem.IsWindows());
            var output = (await proxy.CallAsync("shell_background_output",
                Args(new { id = shell.Id, waitForPattern = "from-bridge", timeoutSeconds = 20 }), default)).Text;
            Assert.Contains("from-bridge", output);
            Assert.Contains("patternmatched\":true", output.ToLowerInvariant());
            Assert.Equal(1, manager.RunningCount);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
        // Closing the workspace kills its shells; nothing resumes them.
        Assert.Equal(0, manager.RunningCount);
    }

    private static string Sleep(int seconds) => OperatingSystem.IsWindows()
        ? $"\"{Path.Combine(Environment.SystemDirectory, "PING.EXE")}\" -n {seconds + 1} 127.0.0.1 >nul"
        : $"sleep {seconds}";
}
