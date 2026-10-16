using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class AcceptanceReloadTests : TempDirTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrected_or_removed_check_takes_effect_without_rebuilding_the_workspace(bool remove)
    {
        var settings = new UserSettingsFile(Paths);
        WriteCheck(settings, "missing success evidence");
        await using var host = await WorkspaceHost.CreateAsync(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths),
        });
        using var scope = host.Services.CreateScope();
        var verifier = Assert.IsType<ReloadingCodingAcceptanceVerifier>(scope.ServiceProvider.GetRequiredService<ICodingAcceptanceVerifier>());
        var context = new CodingAcceptanceContext("c", Tmp, "request", "answer", new("lane", "provider", "model"));
        var first = await verifier.VerifyAsync(context);
        Assert.Equal(CodingAcceptanceStatus.Failed, first.Status);
        // The check ran and only lacked its success evidence (not a command that could not start).
        Assert.Equal(0, Assert.Single(first.Checks).ExitCode);

        if (remove)
        {
            var root = JsonNode.Parse(File.ReadAllText(Paths.UserConfigPath))!.AsObject();
            root["Coding"]!.AsObject().Remove("Acceptance");
            File.WriteAllText(Paths.UserConfigPath, root.ToJsonString());
        }
        else WriteCheck(settings, ".");

        var result = await verifier.VerifyAsync(context);
        Assert.Equal(remove ? CodingAcceptanceStatus.Unverified : CodingAcceptanceStatus.Passed, result.Status);
        Assert.Equal(remove ? 0 : 1, result.Checks.Count);
        // Other execution settings stay bound to the existing workspace.
        Assert.Equal(37, host.Services.GetRequiredService<IOptions<CodingOptions>>().Value.ExecutionLimits.MaxToolCalls);
    }

    [Fact]
    public async Task Invalid_configuration_does_not_silently_disable_verification()
    {
        WriteCheck(new UserSettingsFile(Paths), ".");
        await using var host = await WorkspaceHost.CreateAsync(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths),
        });
        using var scope = host.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<ICodingAcceptanceVerifier>();
        File.WriteAllText(Paths.UserConfigPath, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.VerifyAsync(
            new("c", Tmp, "request", "answer", new("lane", "provider", "model"))));
    }

    // The platform shell, not a PATH tool: a lane with a pinned SDK and a minimal PATH has no `dotnet` to start, so a
    // PATH-dependent check fails for a reason unrelated to the reload.
    private static void WriteCheck(UserSettingsFile settings, string required)
    {
        var (fileName, arguments) = SwarmCore.Testing.TestShell.Echo("acceptance-ok-1.0");
        settings.WriteRuntimeSettings(new() { MaxToolCalls = 37 },
            new() { Commands = [new() { Name = "Shell available", FileName = fileName, Arguments = arguments, RequiredOutput = required }] }, new());
    }
}
