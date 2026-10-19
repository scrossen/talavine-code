using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Projects;
using SwarmCore.Coding.Projects;

namespace SwarmCode.Hosting.Tests;

public sealed class MyAppsLiveFactAttribute : FactAttribute
{
    public MyAppsLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWARMCODE_RUN_APP_BUILD_LIVE") != "1")
            Skip = "Opt in with SWARMCODE_RUN_APP_BUILD_LIVE=1; uses the configured native coding CLI and a disposable project.";
    }
}

[Trait("LlmMode", "Live")]
[Trait("DatabaseMode", "Ephemeral")]
public sealed class MyAppsLiveTests : TempDirTest
{
    [MyAppsLiveFact]
    public async Task Real_coding_lane_builds_an_app_and_the_host_verifies_and_replays_its_launch()
    {
        var folder = Path.Combine(Tmp, "apps"); Directory.CreateDirectory(folder);
        var library = new CodingAppLibraryStore(Paths); library.SetFolder(folder, null);
        var app = library.Create("Hello app", "Create a single index.html that displays Hello from My apps. No packages or external services. "
            + "Use the installed Python HTTP server with an available loopback port. Verify the page responds, stop your test server, and save the Run app definition.", folder);
        var lane = Environment.GetEnvironmentVariable("SWARMCODE_APP_BUILD_TEST_LANE") ?? "codex";
        var model = Environment.GetEnvironmentVariable("SWARMCODE_APP_BUILD_TEST_MODEL");
        var configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
        {
            ["Coding:Agents:Enabled"] = "false", ["Coding:FallbackDiscovery:Enabled"] = "false",
            ["Coding:HardTimeout"] = "00:06:00", ["PersonalMemory:Enabled"] = "false"
        });
        await using var host = await WorkspaceHost.CreateAsync(new() { DataPaths = Paths, RepositoryRoot = app.Root, Configuration = configuration });
        Assert.Contains(host.Lanes, l => l.Name == lane);
        var sessions = host.Services.GetRequiredService<WorkspaceSessionCatalog>();
        var session = await sessions.CreateAsync(app.Root, app.Name, lane);
        if (!string.IsNullOrWhiteSpace(model))
        {
            await sessions.SetModelAsync(session.Id, lane, model);
            session = (await sessions.GetAsync(session.Id))!;
        }
        await using var runner = host.OpenSession(session.Id);
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += result => finished.TrySetResult(result);
        Assert.True(runner.TrySend(CodingAppPrompts.Create(app), lane, allowHandOff: false, modelOverride: model));
        TurnResult result;
        try { result = await finished.Task.WaitAsync(TimeSpan.FromMinutes(7)); }
        finally { if (runner.IsRunning) runner.Cancel(); }
        Assert.Null(result.Error); Assert.False(result.Cancelled); Assert.True(result.Completed);
        Assert.Equal(new[] { lane }, result.LanesUsed);
        if (model is not null) Assert.Equal(model, runner.ActiveModel);
        Assert.Contains("Hello", await File.ReadAllTextAsync(Path.Combine(app.Root, "index.html")));
        var plan = CodingAppLaunchFile.Read(app.Root);
        Assert.NotNull(plan); Assert.Equal("http", plan.ReadyWhen);
        var store = new FileAppProcedureStore(Paths);
        await using var runtime = new CodingAppRuntime(store, Paths.RootDirectory, store.AcquireRunLease);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _ = runtime.StartAsync(app);
            var until = DateTime.UtcNow.AddSeconds(90);
            while (runtime.Status(app.Id).State == CodingAppRunState.Starting && DateTime.UtcNow < until) await Task.Delay(100);
            Assert.Equal(CodingAppRunState.Running, runtime.Status(app.Id).State);
            Assert.Equal(attempt == 1, runtime.Status(app.Id).Replayed);
            using var http = new HttpClient();
            Assert.Contains("Hello", await http.GetStringAsync(runtime.Status(app.Id).Url));
            await runtime.StopAsync(app.Id);
        }
        Assert.Equal(app.Root, (await sessions.GetAsync(session.Id))!.RepositoryRoot);
    }
}
