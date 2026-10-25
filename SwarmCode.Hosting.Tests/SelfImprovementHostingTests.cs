using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Improvement;
using SwarmCode.Hosting.Scheduling;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.SelfImprovement;
using SwarmCore.Coding.Tools;

namespace SwarmCode.Hosting.Tests;

public class SelfImprovementHostingTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    /// <summary>Plays an agent turn: writes a file into the conversation's resolved workspace, then reports acceptance as told.</summary>
    private sealed class CycleOrchestrator(IServiceProvider services, bool pass) : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests { get; } = [];
        public List<CodingWorkspace?> Workspaces { get; } = [];
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            CodingWorkspace? workspace;
            using (var scope = services.CreateScope())
                workspace = await scope.ServiceProvider.GetRequiredService<ICodingWorkspaceResolver>().ResolveAsync(request.ConversationId, ct);
            lock (Workspaces) Workspaces.Add(workspace);
            yield return new CodingLaneSelected("t", Lane, 1);
            if (workspace is not null)
                await File.WriteAllTextAsync(Path.Combine(workspace.FullRoot, "fix.txt"), "fixed by the cycle", ct);
            yield return new ToolCallStarted("t", "tc1", "code.edit_file", new JsonObject { ["path"] = "fix.txt" });
            yield return new ToolCallCompleted("t", "tc1", "code.edit_file", "edited", false, TimeSpan.FromMilliseconds(3));
            yield return new TurnCompleted("t", "Fixed it and added a test.", TimeSpan.FromMilliseconds(5));
            yield return new CodingAcceptanceEvaluated("t", pass
                ? new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "Passed all 1 configured acceptance checks.", [new("build", true, 0, false, "ok")])
                : new CodingAcceptanceResult(CodingAcceptanceStatus.Failed, "Failed 1 of 1 configured acceptance checks.", [new("build", false, 1, false, "error CS0001")]));
        }
    }

    private static async Task Git(string repo, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}{stdout}");
    }

    private static async Task<string> GitOut(string repo, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout.Trim();
    }

    private async Task<string> SourceRepositoryAsync()
    {
        var repo = Path.Combine(Tmp, "source");
        Directory.CreateDirectory(Path.Combine(repo, "SwarmCode.Hosting"));
        Directory.CreateDirectory(Path.Combine(repo, "SwarmCore", "SwarmCore.Coding"));
        await File.WriteAllTextAsync(Path.Combine(repo, "SwarmCode.Hosting", "SwarmCode.Hosting.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(repo, "SwarmCore", "SwarmCore.Coding", "SwarmCore.Coding.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "source");
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "add", "-A");
        await Git(repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "initial");
        return repo;
    }

    private (SwarmCodeHostOptions Options, Func<CycleOrchestrator> Orchestrator) HostOptions(string root, WorkspaceSessionCatalog sessions,
        SessionWorkspaceOverrides overrides, ISelfImprovementBacklog backlog, bool pass,
        Func<IServiceProvider, ICodingTurnOrchestrator>? orchestratorOverride = null)
    {
        CycleOrchestrator? created = null;
        var options = new SwarmCodeHostOptions
        {
            RepositoryRoot = root,
            DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?> { ["Coding:Agents:Enabled"] = "false", ["Coding:RepositoryRoot"] = root }),
            SelfImprovement = true,
            ConfigureServices = services =>
            {
                services.AddSingleton(sessions);
                services.AddSingleton(overrides);
                services.AddSingleton(backlog);
                services.AddSingleton<ICodingTurnOrchestrator>(sp => orchestratorOverride?.Invoke(sp) ?? (created = new CycleOrchestrator(sp, pass)));
                services.AddSingleton<IAgentTeamStore>(new InMemoryAgentTeamStore());
            },
        };
        return (options, () => created!);
    }

    private static CodingAcceptanceCommand Check() => new() { Name = "build", FileName = "dotnet", Arguments = ["--version"], TimeoutSeconds = 60 };

    [Fact]
    public async Task A_cycle_works_in_a_worktree_verifies_through_acceptance_and_commits_on_its_own_branch()
    {
        var repo = await SourceRepositoryAsync();
        var sessions = new WorkspaceSessionCatalog(Paths);
        var preferences = new AppPreferencesStore(Paths);
        var overrides = new SessionWorkspaceOverrides();
        var backlog = new InMemorySelfImprovementBacklog();
        Func<CycleOrchestrator>? orchestrator = null;
        await using var workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null), (root, ct) =>
        {
            var (options, get) = HostOptions(root, sessions, overrides, backlog, pass: true);
            orchestrator = get;
            return WorkspaceHost.CreateAsync(options, ct: ct);
        });
        var settings = new SelfImprovementOptions { RepositoryRoot = repo, Checks = [Check()], AllowNativeLanes = false };
        var host = new SwarmCodeSelfImprovementCycleHost(sessions, preferences, workspaces, overrides, Paths, () => settings);
        Assert.Equal(Path.GetFullPath(repo), host.ResolveRepositoryRoot());

        var item = new SelfImprovementItem
        {
            Id = Guid.NewGuid().ToString("N"), Kind = SelfImprovementKind.Defect, Title = "NullReferenceException: boom", Fingerprint = "abc",
            Details = "It crashed.", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
        };
        string? announced = null;
        var result = await host.RunAsync(item, new SelfImprovementCycleContext("owner", repo, settings) { SessionStarted = id => announced = id }, default);

        Assert.Equal(SelfImprovementCycleOutcome.Verified, result.Outcome);
        Assert.StartsWith("swarmcode/self-improvement-", result.Branch);
        Assert.NotNull(result.Commit);
        Assert.Equal(announced, result.SessionId);
        Assert.False(result.Merged);
        Assert.Contains("Merge the branch", result.Note);
        Assert.Contains("PASS build", result.Evidence);

        // The turn ran unattended in the worktree with the cycle's checks as acceptance.
        var request = Assert.Single(orchestrator!().Requests);
        Assert.True(request.UnattendedAgentTurn);
        Assert.Equal(TurnOrigin.SelfImprovementTrigger, request.Metadata!["swarmcode.trigger"]);
        Assert.Equal(item.Id, request.Metadata["swarmcode.self-improvement"]);
        Assert.Contains("Fix a defect", request.UserMessage);
        var workspace = Assert.Single(orchestrator().Workspaces)!;
        Assert.NotEqual(Path.GetFullPath(repo), workspace.FullRoot);
        Assert.Equal(["build"], workspace.AcceptanceChecks!.Select(c => c.Name));
        Assert.Equal(0, overrides.Count);

        // The branch holds the commit, the worktree stays for review, and the source checkout is untouched.
        Assert.Equal(result.Commit, await GitOut(repo, "rev-parse", "refs/heads/" + result.Branch));
        Assert.Contains("Talavine Code self-improvement: NullReferenceException: boom", await GitOut(repo, "log", "-1", "--format=%B", result.Branch));
        Assert.Contains("Self-improvement-Item: " + item.Id, await GitOut(repo, "log", "-1", "--format=%B", result.Branch));
        Assert.True(Directory.Exists(result.WorktreePath));
        Assert.False(File.Exists(Path.Combine(repo, "fix.txt")));
        Assert.Equal("", await GitOut(repo, "status", "--porcelain"));

        var session = (await sessions.GetAsync(result.SessionId!))!;
        Assert.StartsWith("🛠 Fix: ", session.Title);
        Assert.True(session.Temporary);
        Assert.Equal(workspace.FullRoot, session.WorkingRoot, ignoreCase: true);
        Assert.Equal(0, workspaces.BackgroundWorkspaceCount);
    }

    [Fact]
    public async Task A_failed_cycle_keeps_nothing_and_a_merge_setting_fast_forwards_a_clean_checkout()
    {
        var repo = await SourceRepositoryAsync();
        var sessions = new WorkspaceSessionCatalog(Paths);
        var preferences = new AppPreferencesStore(Paths);
        var overrides = new SessionWorkspaceOverrides();
        var backlog = new InMemorySelfImprovementBacklog();
        var pass = false;
        await using var workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null),
            (root, ct) => WorkspaceHost.CreateAsync(HostOptions(root, sessions, overrides, backlog, pass).Options, ct: ct));
        var settings = new SelfImprovementOptions { RepositoryRoot = repo, Checks = [Check()], MergeVerifiedFixes = true };
        var host = new SwarmCodeSelfImprovementCycleHost(sessions, preferences, workspaces, overrides, Paths, () => settings);
        var item = new SelfImprovementItem
        {
            Id = Guid.NewGuid().ToString("N"), Kind = SelfImprovementKind.FeatureRequest, Title = "Feature request: send email", Fingerprint = "def",
            Details = "The person asked: send email", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
        };

        var failed = await host.RunAsync(item, new SelfImprovementCycleContext("owner", repo, settings), default);
        Assert.Equal(SelfImprovementCycleOutcome.Failed, failed.Outcome);
        Assert.Contains("Verification failed", failed.Note);
        Assert.Contains("FAIL build", failed.Evidence);
        Assert.NotNull(failed.SessionId);
        Assert.Null(failed.WorktreePath);
        Assert.DoesNotContain("self-improvement", await GitOut(repo, "branch", "--list", "swarmcode/*"));
        Assert.Empty(Directory.Exists(Paths.WorktreesDirectory) ? Directory.EnumerateDirectories(Paths.WorktreesDirectory, "self-improvement-*", SearchOption.AllDirectories) : []);

        pass = true;
        var merged = await host.RunAsync(item, new SelfImprovementCycleContext("owner", repo, settings), default);
        Assert.Equal(SelfImprovementCycleOutcome.Verified, merged.Outcome);
        Assert.True(merged.Merged, merged.Note);
        Assert.Equal(merged.Commit, await GitOut(repo, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(repo, "fix.txt")));
        Assert.Null(merged.WorktreePath); // merged and clean: the worktree went away

        // Without checks nothing can be verified, so the cycle refuses rather than guessing.
        var noChecks = new SwarmCodeSelfImprovementCycleHost(sessions, preferences, workspaces, overrides, Paths, () => new SelfImprovementOptions { RepositoryRoot = repo });
        var unverifiable = await noChecks.RunAsync(item, new SelfImprovementCycleContext("owner", Path.Combine(Tmp, "plain"), new SelfImprovementOptions()), default);
        Assert.Equal(SelfImprovementCycleOutcome.Unverifiable, unverifiable.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_cycle_retains_unverified_work_and_next_attempt_uses_another_worktree(bool cancelHostToken)
    {
        var repo = await SourceRepositoryAsync();
        var sourceHead = await GitOut(repo, "rev-parse", "HEAD");
        var sessions = new WorkspaceSessionCatalog(Paths);
        var preferences = new AppPreferencesStore(Paths);
        var overrides = new SessionWorkspaceOverrides();
        var backlog = new InMemorySelfImprovementBacklog();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var roots = new List<string>();
        var requests = new List<CodingTurnRequest>();
        async IAsyncEnumerable<TurnEvent> Cycle(IServiceProvider services, CodingTurnRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var workspace = (await scope.ServiceProvider.GetRequiredService<ICodingWorkspaceResolver>().ResolveAsync(request.ConversationId, ct))!;
            roots.Add(workspace.FullRoot);
            requests.Add(request);
            yield return new CodingLaneSelected("t", Lane, 1);
            await File.WriteAllTextAsync(Path.Combine(workspace.FullRoot, "README.md"), "unverified tracked change", ct);
            await File.WriteAllTextAsync(Path.Combine(workspace.FullRoot, "checkpoint.txt"), "unverified new file", ct);
            if (roots.Count == 1)
            {
                entered.TrySetResult();
                if (cancelHostToken) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new OperationCanceledException("Stopped at the runner's checkpoint.");
            }
            yield return new TurnCompleted("t", "Independently implemented and verified", TimeSpan.FromMilliseconds(1));
            yield return new CodingAcceptanceEvaluated("t", new CodingAcceptanceResult(CodingAcceptanceStatus.Passed,
                "Synthetic check passed", [new("build", true, 0, false, "ok")]));
        }
        await using var workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null),
            (root, ct) => WorkspaceHost.CreateAsync(HostOptions(root, sessions, overrides, backlog, pass: true,
                sp => new ScriptedOrchestrator((request, token) => Cycle(sp, request, token))).Options, ct: ct));
        var settings = new SelfImprovementOptions { RepositoryRoot = repo, Checks = [Check()], KeepFailedWorktrees = false };
        var host = new SwarmCodeSelfImprovementCycleHost(sessions, preferences, workspaces, overrides, Paths, () => settings);
        var item = new SelfImprovementItem { Id = Guid.NewGuid().ToString("N"), Kind = SelfImprovementKind.Defect,
            Title = "Interrupted source repair", Fingerprint = "cancelled", Details = "Synthetic test", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        using var stop = new CancellationTokenSource();
        var running = host.RunAsync(item, new("owner", repo, settings), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelHostToken) stop.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(SelfImprovementCycleOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.WorktreePath);
        Assert.NotNull(result.Branch);
        Assert.Null(result.Commit);
        Assert.False(result.Merged);
        Assert.Contains(result.WorktreePath, result.Evidence);
        Assert.Contains("not a verified repair", result.Evidence);
        Assert.Equal("unverified tracked change", await File.ReadAllTextAsync(Path.Combine(result.WorktreePath, "README.md")));
        Assert.Equal("unverified new file", await File.ReadAllTextAsync(Path.Combine(result.WorktreePath, "checkpoint.txt")));
        var checkpointStatus = await GitOut(result.WorktreePath, "status", "--porcelain", "--untracked-files=all");
        Assert.Contains("README.md", checkpointStatus);
        Assert.Contains("checkpoint.txt", checkpointStatus);
        Assert.Equal(sourceHead, await GitOut(repo, "rev-parse", "refs/heads/" + result.Branch));
        Assert.Equal("", await GitOut(repo, "status", "--porcelain"));
        Assert.Equal(0, overrides.Count);
        Assert.Equal(0, workspaces.BackgroundWorkspaceCount);

        // A retry reads the checkpoint guidance but gets an unused branch and folder; the old WIP is untouched.
        var next = await host.RunAsync(item with { Branch = result.Branch, WorktreePath = result.WorktreePath,
            Evidence = result.Evidence, SessionId = result.SessionId }, new("owner", repo, settings), default);
        Assert.Equal(SelfImprovementCycleOutcome.Verified, next.Outcome);
        Assert.NotEqual(result.WorktreePath, next.WorktreePath);
        Assert.NotEqual(result.Branch, next.Branch);
        Assert.Contains(result.WorktreePath, requests[1].UserMessage);
        Assert.Contains("Inspect that checkpoint read-only before reimplementing", requests[1].UserMessage);
        Assert.Equal(checkpointStatus, await GitOut(result.WorktreePath, "status", "--porcelain", "--untracked-files=all"));
        Assert.Equal(sourceHead, await GitOut(result.WorktreePath, "rev-parse", "HEAD"));
        Assert.Equal("unverified tracked change", await File.ReadAllTextAsync(Path.Combine(result.WorktreePath, "README.md")));
        Assert.Equal("unverified new file", await File.ReadAllTextAsync(Path.Combine(result.WorktreePath, "checkpoint.txt")));
        Assert.Equal(sourceHead, await GitOut(repo, "rev-parse", "HEAD"));
        Assert.Equal("", await GitOut(repo, "status", "--porcelain"));
    }

    // ---- capture in the runner ---------------------------------------------------------------------

    private sealed class ScriptedOrchestrator(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default) => script(request, ct);
    }

    private (ServiceProvider Provider, InMemorySelfImprovementBacklog Backlog) RunnerServices(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script)
    {
        var backlog = new InMemorySelfImprovementBacklog();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<ICodingTurnOrchestrator>(new ScriptedOrchestrator(script));
        services.AddSingleton<ISelfImprovementBacklog>(backlog);
        services.AddSingleton(new SelfImprovementReporter(backlog, () => new SelfImprovementOptions()));
        return (services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), backlog);
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => tcs.TrySetResult(r);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }

    private static async IAsyncEnumerable<TurnEvent> Yield(params TurnEvent[] events)
    {
        foreach (var e in events) { await Task.Yield(); yield return e; }
    }

    [Fact]
    public async Task A_product_exception_in_a_turn_becomes_a_defect_and_an_environmental_one_does_not()
    {
        static async IAsyncEnumerable<TurnEvent> Throwing(Exception exception, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            throw exception;
        }

        var (crashing, backlog) = RunnerServices((_, ct) => Throwing(new NullReferenceException("Object reference not set to an instance of an object."), ct));
        using (crashing)
        {
            using var runner = new ConversationRunner(crashing, "conv-crash");
            var finish = NextFinish(runner);
            Assert.True(runner.TrySend("fix the tests", null));
            var result = await finish;
            Assert.NotNull(result.Error);
            await Until(() => backlog.ListAsync().Result.Count == 1);
            var item = Assert.Single(await backlog.ListAsync());
            Assert.Equal(SelfImprovementKind.Defect, item.Kind);
            Assert.StartsWith("NullReferenceException", item.Title);
            Assert.Equal(SelfImprovementCapture.TurnErrorSource, item.Source);
            Assert.Equal("conv-crash", item.ConversationId);
            Assert.Contains("fix the tests", item.Details);
        }

        var (network, quiet) = RunnerServices((_, ct) => Throwing(new HttpRequestException("503"), ct));
        using (network)
        {
            using var runner = new ConversationRunner(network, "conv-net");
            var finish = NextFinish(runner);
            Assert.True(runner.TrySend("fix the tests", null));
            await finish;
            await Task.Delay(300);
            Assert.Empty(await quiet.ListAsync());
        }
    }

    [Fact]
    public async Task Lane_errors_failed_tools_and_capability_declines_are_captured_but_never_from_a_cycle()
    {
        var (provider, backlog) = RunnerServices((_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new ToolCallStarted("t", "d1", HarnessDeclineTool.ToolId, new JsonObject { ["scope"] = "all", ["reason"] = "I don't have a tool to send email from SwarmCode." }),
            new ToolCallCompleted("t", "d1", HarnessDeclineTool.ToolId, "{}", false, TimeSpan.Zero),
            new ToolCallStarted("t", "x1", "code.edit_file", new JsonObject { ["path"] = "a.cs" }),
            new ToolCallCompleted("t", "x1", "code.edit_file", "InvalidOperationException: Sequence contains no elements\n   at SwarmCore.Coding.Tools.EditTool.Apply()", true, TimeSpan.Zero),
            new TurnErrored("t", "KeyNotFoundException: The given key 'lane' was not present in the dictionary.", "   at SwarmCode.Hosting.ConversationRunner.Apply()")));
        using (provider)
        {
            using var runner = new ConversationRunner(provider, "conv-events");
            var finish = NextFinish(runner);
            Assert.True(runner.TrySend("send this report by email", null));
            await finish;
            await Until(() => backlog.ListAsync().Result.Count == 3);
            var items = await backlog.ListAsync();
            var feature = Assert.Single(items, i => i.Kind == SelfImprovementKind.FeatureRequest);
            Assert.Contains("send this report by email", feature.Details);
            Assert.Equal(SelfImprovementCapture.DeclineSource, feature.Source);
            Assert.Contains(items, i => i.Source == SelfImprovementCapture.ToolErrorSource && i.Title.StartsWith("InvalidOperationException"));
            Assert.Contains(items, i => i.Source == SelfImprovementCapture.TurnErrorSource && i.Title.StartsWith("KeyNotFoundException"));
        }

        var (cycle, untouched) = RunnerServices((_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new TurnErrored("t", "KeyNotFoundException: The given key 'lane' was not present.", null)));
        using (cycle)
        {
            using var runner = new ConversationRunner(cycle, "conv-cycle");
            var finish = NextFinish(runner);
            Assert.True(runner.TrySend("[SwarmCode self-improvement] fix", null, origin: TurnOrigin.SelfImprovement("item-1", allowNativeLanes: false)));
            await finish;
            await Task.Delay(300);
            Assert.Empty(await untouched.ListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_is_reported_before_turn_completion_and_never_from_an_RSI_cycle(bool isCycle)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<TurnEvent> Running([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new CodingLaneSelected("t", Lane, 1);
            for (var i = 0; i < 2; i++)
                yield return new ToolCallCompleted("t", "r" + i, "shell.exec",
                    SwarmCore.Coding.Routing.RepeatedToolCallGuard.RecoveryMarker + ": repeated failure", true, TimeSpan.Zero);
            reached.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new TurnCompleted("t", "Worked around the failure.", TimeSpan.Zero);
        }
        var (provider, backlog) = RunnerServices((_, ct) => Running(ct));
        using (provider)
        using (var runner = new ConversationRunner(provider, "recovery-report"))
        {
            var finish = NextFinish(runner);
            Assert.True(runner.TrySend("synthetic recovery probe", null,
                origin: isCycle ? TurnOrigin.SelfImprovement("probe", false) : null));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!isCycle) await Until(() => backlog.ListAsync().GetAwaiter().GetResult().Count == 1);
            Assert.True(runner.IsRunning);
            var items = await backlog.ListAsync();
            if (isCycle) Assert.Empty(items);
            else
            {
                var item = Assert.Single(items);
                Assert.Equal(1, item.Occurrences);
                Assert.Equal(SelfImprovementKind.Defect, item.Kind);
                Assert.Contains("recovery exhausted", item.Title);
            }
            release.TrySetResult();
            await finish;
        }
    }

    [Fact]
    public void Unknown_commands_and_unhandled_exceptions_reach_the_backlog()
    {
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, () => new SelfImprovementOptions());
        string[] known = ["help", "lane", "schedules"];
        SelfImprovementCapture.UnknownCommand(reporter, "/hepl", known, "conv");
        SelfImprovementCapture.UnknownCommand(reporter, "/screenshot of the app", known, "conv");
        SelfImprovementCapture.Unhandled(reporter, new InvalidCastException("bad cast"), "An unobserved task failed.");
        SelfImprovementCapture.Unhandled(reporter, new OperationCanceledException(), "An unobserved task failed.");
        Until(() => backlog.ListAsync().Result.Count == 2).GetAwaiter().GetResult();
        var items = backlog.ListAsync().Result;
        var command = Assert.Single(items, i => i.Kind == SelfImprovementKind.FeatureRequest);
        Assert.Contains("/screenshot of the app", command.Details);
        Assert.Equal(SelfImprovementCapture.UnknownCommandSource, command.Source);
        var crash = Assert.Single(items, i => i.Kind == SelfImprovementKind.Defect);
        Assert.StartsWith("InvalidCastException", crash.Title);
        Assert.Equal(SelfImprovementCapture.UnhandledExceptionSource, crash.Source);
    }

    [Fact]
    public async Task The_workspace_host_composes_the_backlog_the_tool_and_the_guidance()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        await using var host = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo, DataPaths = Paths, SelfImprovement = true,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?> { ["Coding:SelfImprovement:MaxQueued"] = "9" }),
        });
        var backlog = Assert.IsType<JsonFileSelfImprovementBacklog>(host.Services.GetRequiredService<ISelfImprovementBacklog>());
        Assert.Equal(Paths.SelfImprovementDirectory, backlog.Directory);
        Assert.Equal(9, host.Services.GetRequiredService<SelfImprovementReporter>().Options.MaxQueued);
        Assert.NotNull(host.Services.GetRequiredService<ICapabilityCatalog>().Get(FeatureRequestTool.ToolId));
        Assert.Contains(FeatureRequestTool.ToolId, host.CodingOptions.AdditionalInstructions);
        Assert.NotNull(host.Services.GetService<SessionWorkspaceOverrides>());
        Assert.Null(SwarmCodeSourceRepository.Detect(repo));

        // Off by default (a Release host never opts in): no backlog, reporter or tool, and settings cannot turn it on.
        await using var plain = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo, DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?> { ["Coding:SelfImprovement:Enabled"] = "true" }),
        });
        Assert.Null(plain.Services.GetService<SelfImprovementReporter>());
        Assert.Null(plain.Services.GetService<ISelfImprovementBacklog>());
        Assert.Null(plain.Services.GetRequiredService<ICapabilityCatalog>().Get(FeatureRequestTool.ToolId));
        Assert.DoesNotContain(FeatureRequestTool.ToolId, plain.CodingOptions.AdditionalInstructions ?? "");
    }
}
