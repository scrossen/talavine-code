using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Procedures;
using SwarmCore.RemoteControl;
using SwarmCore.RemoteControl.Agent;
using SwarmCore.RemoteControl.Tests;

namespace SwarmCode.Hosting.Tests;

/// <summary>desktop.* in the coding catalog follows Coding:Computer:Enabled; an in-memory desktop keeps the real one untouched.</summary>
public class DesktopControlHostingTests : TempDirTest
{
    private async Task<WorkspaceHost> Host(bool computerEnabled)
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        return await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
            {
                ["Coding:Computer:Enabled"] = computerEnabled ? "true" : "false",
                ["Coding:Agents:Enabled"] = "false",
            }),
            ConfigureServices = services => services.AddSingleton<IDesktopAutomation>(new FakeDesktopAutomation()),
        });
    }

    [Fact]
    public async Task Desktop_tools_are_in_the_coding_catalog_when_computer_access_is_on()
    {
        await using var workspace = await Host(computerEnabled: true);
        var tools = workspace.Services.GetRequiredService<ICapabilityCatalog>().All()
            .Where(t => t.ToolId.StartsWith("desktop.", StringComparison.Ordinal)).ToArray();
        foreach (var id in new[] { DesktopControlTools.ListWindowsTool, DesktopControlTools.InspectTool, DesktopControlTools.CaptureTool,
                     DesktopControlTools.ActTool, DesktopControlTools.InputTool, DesktopControlTools.WaitForTool, DesktopControlTools.CommandTool })
        {
            var tool = Assert.Single(tools, t => t.ToolId == id);
            Assert.Equal("coding", tool.OwnerAgentId);
            Assert.Contains("browser", tool.Description, StringComparison.OrdinalIgnoreCase);
        }
        // AddCodingComputer goes through AddDesktopControl, so screenshots are downscaled and listings compact.
        var options = workspace.Services.GetRequiredService<RemoteControlSessionOptions>();
        Assert.NotNull(options.ScreenshotScaler);
        Assert.True(options.EmbedScreenshotBase64);
        Assert.Equal(60, options.ListingMaxRows);
        Assert.Equal(90, options.ListingNameMaxLength);
        Assert.StartsWith(Paths.RootDirectory, options.CaptureDirectory, StringComparison.OrdinalIgnoreCase);
        // The desktop procedure runner exists, and the host's own procedure store is the one it is given.
        Assert.NotNull(workspace.Services.GetRequiredService<DesktopProcedureRunner>());
        Assert.IsType<Projects.FileAppProcedureStore>(workspace.Services.GetRequiredService<IAgentProcedureStore>());
    }

    [Fact]
    public async Task Desktop_tools_are_absent_when_computer_access_is_off()
    {
        await using var workspace = await Host(computerEnabled: false);
        Assert.DoesNotContain(workspace.Services.GetRequiredService<ICapabilityCatalog>().All(),
            t => t.ToolId.StartsWith("desktop.", StringComparison.Ordinal));
    }
}
