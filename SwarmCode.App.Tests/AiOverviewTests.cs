using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.App.Services;
using SwarmCode.App.Tests.Components;
using SwarmCode.Hosting;
using SwarmCode.Hosting.Tests;
using SwarmCore.HarnessLearning;
using Talavine.TestSupport;

namespace SwarmCode.App.Tests;

public sealed class AiOverviewTests
{
    [Fact]
    public async Task Overview_reads_profile_evidence_without_opening_a_workspace_or_starting_a_model_job()
    {
        await using var scratch = TestTempDirectory.Create("overview-page");
        var paths = new SwarmCodeDataPaths(scratch.Path);
        await File.WriteAllTextAsync(paths.UserConfigPath, """{"Coding":{"SelfImprovement":{"Enabled":false}}}""");
        var baseline = new HarnessRevision("baseline", "current", new Dictionary<string, string>());
        var report = new HarnessExperimentReport(Guid.NewGuid(), new("test-profile", "repository", HarnessEvidence.Hash("env"), HarnessEvidence.Hash("suite")),
            DateTimeOffset.UtcNow, baseline, [], new(), [], [], null, null);
        await new FileHarnessExperimentHistory(paths.HarnessExperimentsDirectory).AppendAsync(report, default);
        await using var state = new AppState(paths, new(paths), new(paths), new(paths), NullLoggerFactory.Instance, new EncryptedTestKeyStore());
        using var context = new BunitContext();
        context.Services.AddSingleton(state);
        var view = context.Render<AiOverview>();
        view.WaitForAssertion(() => Assert.Single(view.FindAll("details")), TimeSpan.FromSeconds(10));
        Assert.Contains("repository", view.Markup);
        Assert.Contains("No calls were recorded", view.Markup);
        Assert.Contains("Off in Settings.", view.Markup);
        Assert.Null(state.Workspace);
        Assert.False(state.AnyTurnRunning);
        await view.FindAll("button").Single(b => b.TextContent == "Import experiment report").ClickAsync(new());
        Assert.Single(await HarnessReportReader.ReadHistoryAsync(paths.HarnessExperimentsDirectory));
        Assert.Empty(await state.Sessions.ListAsync());
    }
}
