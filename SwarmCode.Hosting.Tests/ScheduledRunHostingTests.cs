using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Scheduling;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Scheduling;

namespace SwarmCode.Hosting.Tests;

public class ScheduledRunHostingTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private sealed class RecordingOrchestrator : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests { get; } = [];
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            yield return new TurnCompleted("t", "All 12 tests passed.", TimeSpan.FromMilliseconds(5));
        }
    }

    private (string Repo, WorkspaceSessionCatalog Sessions, AppPreferencesStore Preferences) Setup()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        return (repo, new WorkspaceSessionCatalog(Paths), new AppPreferencesStore(Paths));
    }

    private SwarmCodeHostOptions Options(string root, WorkspaceSessionCatalog sessions, RecordingOrchestrator orchestrator, bool background,
        CodingScheduler? scheduler = null, IDictionary<string, string?>? overrides = null) => new()
    {
        RepositoryRoot = root,
        DataPaths = Paths,
        Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, overrides ?? (background
            ? new Dictionary<string, string?> { ["Coding:Agents:Enabled"] = "false" } : null)),
        ConfigureServices = services =>
        {
            services.AddSingleton(sessions);
            services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
            if (scheduler is not null) services.AddSingleton(scheduler);
            if (background) services.AddSingleton<IAgentTeamStore>(new InMemoryAgentTeamStore());
        },
    };

    [Fact]
    public async Task A_schedule_for_another_repository_runs_in_a_background_workspace_as_an_unattended_turn()
    {
        var (repo, sessions, preferences) = Setup();
        var orchestrator = new RecordingOrchestrator();
        var created = 0;
        ScheduledRunWorkspaces? workspaces = null;
        workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null), (root, ct) =>
        {
            Interlocked.Increment(ref created);
            return WorkspaceHost.CreateAsync(Options(root, sessions, orchestrator, background: true), ct: ct);
        });
        await using var _ = workspaces;
        await using var scheduler = new CodingScheduler(new JsonFileCodingScheduleStore(Paths.SchedulesDirectory),
            new SwarmCodeScheduledRunHost(sessions, preferences, workspaces));
        var schedule = await scheduler.CreateAsync(new CodingScheduleEdit
        {
            Title = "Morning tests", Prompt = "Run the tests and report.", RepositoryRoot = repo, Recurrence = ScheduleRecurrence.WeekdaysAt(8, 0),
            AllowNativeLanes = false, // native lanes run schedules by default; this one opted out
        });

        var start = await scheduler.RunNowAsync(schedule.Id);
        Assert.True(start.Started, start.Message);
        await Until(() => !scheduler.IsRunning(schedule.Id) && workspaces.BackgroundWorkspaceCount == 0);

        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal("Run the tests and report.", request.UserMessage);
        Assert.False(request.IsContinuation);
        Assert.True(request.UnattendedAgentTurn); // opted out: native lanes are skipped by the harness
        Assert.True(request.CommunicationScope!.UserInitiated); // the person's own (scheduled) request
        Assert.Equal("schedule", request.Metadata!["swarmcode.trigger"]);
        Assert.Equal(schedule.Id, request.Metadata["swarmcode.schedule"]);

        var session = (await sessions.GetAsync(start.SessionId!))!;
        Assert.StartsWith("⏱ Morning tests — ", session.Title);
        Assert.Equal(Path.GetFullPath(repo).TrimEnd('\\', '/'), session.RepositoryRoot, ignoreCase: true);
        Assert.True(session.Temporary);
        Assert.Null(session.ParentSessionId);

        var stored = (await scheduler.GetAsync(schedule.Id))!;
        Assert.Equal(ScheduleRunOutcome.Succeeded, stored.Run.LastOutcome);
        Assert.Equal(start.SessionId, stored.Run.LastSessionId);
        Assert.Equal(1, created);
        Assert.Null(workspaces.TryGetRunner(start.SessionId!)); // released once idle

        // The run's transcript is durable: a window that opens the session later sees the prompt and the report.
        await using var reopened = await WorkspaceHost.CreateAsync(Options(repo, sessions, orchestrator, background: false));
        await using var runner = reopened.OpenSession(start.SessionId!);
        await runner.LoadHistoryAsync();
        Assert.Contains(runner.Entries.OfType<UserEntry>(), e => e.Text == "Run the tests and report.");
        Assert.Contains(runner.Entries.OfType<AssistantEntry>(), e => e.Text.Contains("12 tests passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_schedule_for_the_open_repository_runs_in_that_workspace_and_can_opt_native_lanes_in()
    {
        var (repo, sessions, preferences) = Setup();
        var orchestrator = new RecordingOrchestrator();
        await using var open = await WorkspaceHost.CreateAsync(Options(repo, sessions, orchestrator, background: false));
        var runners = new List<ConversationRunner>();
        var backgroundCreated = false;
        await using var workspaces = new ScheduledRunWorkspaces(async (root, sessionId, ct) =>
        {
            var runner = open.OpenSession(sessionId);
            await runner.LoadHistoryAsync(ct);
            runners.Add(runner);
            return runner;
        }, (_, _) => { backgroundCreated = true; throw new InvalidOperationException("not expected"); });
        await using var scheduler = new CodingScheduler(new JsonFileCodingScheduleStore(Paths.SchedulesDirectory),
            new SwarmCodeScheduledRunHost(sessions, preferences, workspaces));
        var schedule = await scheduler.CreateAsync(new CodingScheduleEdit
        {
            Title = "Lint", Prompt = "Run the linter.", RepositoryRoot = repo, Recurrence = ScheduleRecurrence.DailyAt(9, 0), // native lanes by default
        });

        var start = await scheduler.RunNowAsync(schedule.Id);
        Assert.True(start.Started, start.Message);
        await Until(() => !scheduler.IsRunning(schedule.Id));
        Assert.False(backgroundCreated);
        Assert.Equal(0, workspaces.BackgroundWorkspaceCount);
        var request = Assert.Single(orchestrator.Requests);
        Assert.False(request.UnattendedAgentTurn);
        Assert.Equal(start.SessionId, Assert.Single(runners).ConversationId);
        Assert.False(runners[0].IsRunning);
        foreach (var r in runners) await r.DisposeAsync();
    }

    [Fact]
    public async Task A_run_that_cannot_open_its_repository_is_recorded_as_failed()
    {
        var (repo, sessions, preferences) = Setup();
        await using var workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null),
            (_, _) => Task.FromException<WorkspaceHost>(new IOException("disk unavailable")));
        await using var scheduler = new CodingScheduler(new JsonFileCodingScheduleStore(Paths.SchedulesDirectory),
            new SwarmCodeScheduledRunHost(sessions, preferences, workspaces));
        var schedule = await scheduler.CreateAsync(new CodingScheduleEdit
        {
            Title = "Tests", Prompt = "Run the tests.", RepositoryRoot = repo, Recurrence = ScheduleRecurrence.DailyAt(9, 0),
        });
        var start = await scheduler.RunNowAsync(schedule.Id);
        Assert.False(start.Started);
        var stored = (await scheduler.GetAsync(schedule.Id))!;
        Assert.Equal(ScheduleRunOutcome.Failed, stored.Run.LastOutcome);
        Assert.Contains("disk unavailable", stored.Run.LastNote);
        Assert.False(scheduler.IsRunning(schedule.Id));
        Assert.Equal(0, workspaces.BackgroundWorkspaceCount);
    }

    [Fact]
    public async Task Workspace_composition_offers_schedule_tools_and_the_bridge_only_with_a_scheduler()
    {
        var (repo, sessions, preferences) = Setup();
        var orchestrator = new RecordingOrchestrator();
        await using var workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null),
            (_, _) => Task.FromException<WorkspaceHost>(new InvalidOperationException()));
        await using var scheduler = new CodingScheduler(new JsonFileCodingScheduleStore(Paths.SchedulesDirectory),
            new SwarmCodeScheduledRunHost(sessions, preferences, workspaces));
        var bridge = new Dictionary<string, string?>
        {
            ["Coding:Agents:Enabled"] = "false",
            ["Coding:NativeAgents:McpServer:Command"] = "swarmcode",
            ["Coding:NativeAgents:McpServer:Arguments:0"] = "--mcp-server",
        };

        // With a bridge configured, the coding options post-configure the endpoint; a dependency cycle there deadlocks.
        await using (var with = await Task.Run(() => WorkspaceHost.CreateAsync(Options(repo, sessions, orchestrator, background: false, scheduler, bridge)))
                         .WaitAsync(TimeSpan.FromSeconds(60)))
        {
            var sp = with.Services;
            var coding = await Task.Run(() => sp.GetRequiredService<IOptions<CodingOptions>>().Value).WaitAsync(TimeSpan.FromSeconds(30));
            var catalog = sp.GetRequiredService<ICapabilityCatalog>();
            foreach (var id in ScheduleTools.AllToolIds) Assert.Contains(catalog.All(), t => t.ToolId == id);
            Assert.True(coding.NativeAgents.McpServer!.Environment.ContainsKey(SwarmCore.Coding.Native.HostToolBridgeBroker.PipeEnvironmentVariable));
            // A native agent creates a schedule through the bridge, for its own conversation, in this workspace by default.
            using var turn = sp.GetRequiredService<SwarmCore.Coding.ActiveCodingWorkspaces>().Enter("native-session", new SwarmCore.Coding.CodingWorkspace { RepositoryRoot = repo });
            var bridged = await sp.GetRequiredService<SwarmCore.Coding.Native.HostToolBridgeBroker>().CallAsync("native-session", "schedules_create",
                System.Text.Json.JsonSerializer.SerializeToElement(new { title = "Nightly", prompt = "Run the build.", preset = "daily", time = "02:00" }), default);
            Assert.False(bridged.IsError, bridged.Text);
            Assert.Contains("Nightly", bridged.Text);
        }

        await using var without = await WorkspaceHost.CreateAsync(Options(repo, sessions, orchestrator, background: false, overrides: bridge));
        Assert.DoesNotContain(without.Services.GetRequiredService<ICapabilityCatalog>().All(), t => t.ToolId.StartsWith("schedules.", StringComparison.Ordinal));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }
}
