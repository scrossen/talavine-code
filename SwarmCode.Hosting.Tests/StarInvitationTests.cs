using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Checkpoints;
using SwarmCore.Coding.Engagement;
using SwarmCore.Coding.Routing;
using SwarmCore.Skills.Models;
using SwarmCore.Testing;

namespace SwarmCode.Hosting.Tests;

public sealed class StarInvitationTests : TempDirTest
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly CodingLane Lane = new("fixture", "Fixture", "fixture-model");
    private static readonly CodingCompletionEvidence VerifiedSuccess = new(true, false, false, true, true, false, false, false,
        JournalSaved: true, SuccessfulWorkTool: false, AcceptanceFailed: false);

    private sealed class FakeOrchestrator(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default) => script(request, ct);
    }

    private sealed record Harness(ServiceProvider Provider, ConversationRunner Runner, StarInvitationStore Invitations,
        AppPreferencesStore Preferences) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Runner.DisposeAsync().AsTask().WaitAsync(Limit);
            await Provider.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private async Task<Harness> BuildAsync(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script,
        bool registerCatalog = true, bool child = false, bool temporary = false, bool registerInvitations = true,
        string? checkpointRoot = null, bool registerJobStore = true)
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, parentSessionId: child ? "parent-session" : null).WaitAsync(Limit);
        // Independently exercise child identity and temporary state instead of relying on the child default.
        await catalog.SetTemporaryAsync(session.Id, temporary).WaitAsync(Limit);
        var preferences = new AppPreferencesStore(Paths);
        var invitations = new StarInvitationStore(preferences);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        if (registerJobStore) services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<ICodingTurnOrchestrator>(new FakeOrchestrator(script));
        if (registerInvitations) services.AddSingleton(invitations);
        if (registerCatalog) services.AddSingleton(catalog);
        if (checkpointRoot is not null)
        {
            services.AddSingleton<ICodingWorkspaceResolver>(new FixedWorkspace(checkpointRoot));
            services.AddSingleton(new TurnCheckpointService(new TurnCheckpointOptions()));
        }
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new(provider, new ConversationRunner(provider, session.Id), invitations, preferences);
    }

    private sealed class FixedWorkspace(string root) : ICodingWorkspaceResolver
    {
        public Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default)
            => Task.FromResult<CodingWorkspace?>(new() { RepositoryRoot = root });
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += result => finished.TrySetResult(result);
        return finished.Task.WaitAsync(Limit);
    }

    private static async IAsyncEnumerable<TurnEvent> Yield(params TurnEvent[] events)
    {
        foreach (var item in events) { await Task.Yield(); yield return item; }
    }

    private static CodingAcceptanceEvaluated Acceptance(CodingAcceptanceStatus status)
        => new("turn", new(status, "Fixture host acceptance", [new("fixture-check", status == CodingAcceptanceStatus.Passed, 0, false, "fixture evidence")]));

    private static ToolCallStarted StartTool(string name = "fs.write_file")
        => new("turn", "work", name, new JsonObject());

    private static ToolCallCompleted FinishTool(bool failed = false, string name = "fs.write_file")
        => new("turn", "work", name, failed ? "Write failed" : "Tool succeeded", failed, TimeSpan.Zero);

    [Fact]
    public async Task VerifiedSuccessAndExplicitDismissalSurviveIndependentStoreReopen()
    {
        var store = new StarInvitationStore(new AppPreferencesStore(Paths));
        Assert.False((await store.ReadAsync().WaitAsync(Limit)).ShouldShow(false));

        await store.RecordAsync(VerifiedSuccess).WaitAsync(Limit);
        var reopened = new StarInvitationStore(new AppPreferencesStore(Paths));
        var eligible = await reopened.ReadAsync().WaitAsync(Limit);
        Assert.True(eligible.Eligible);
        Assert.True(eligible.ShouldShow(false));
        Assert.False(eligible.ShouldShow(true));

        await reopened.DismissAsync().WaitAsync(Limit);
        await store.RecordAsync(VerifiedSuccess).WaitAsync(Limit);
        var afterReopen = await new StarInvitationStore(new AppPreferencesStore(Paths)).ReadAsync().WaitAsync(Limit);
        Assert.True(afterReopen.Eligible);
        Assert.True(afterReopen.Dismissed);
        Assert.False(afterReopen.ShouldShow(false));
    }

    [Fact]
    public async Task CompetingPreferenceWritersPreserveDismissalExplicitEditsAndUnknownFields()
    {
        await File.WriteAllTextAsync(Paths.PreferencesPath,
            """{"theme":"dark","defaultLane":"old","showReasoning":true,"future":{"enabled":false,"scope":null}}""").WaitAsync(Limit);
        var firstPreferences = new AppPreferencesStore(Paths);
        var secondPreferences = new AppPreferencesStore(Paths);
        var first = new StarInvitationStore(firstPreferences);
        var second = new StarInvitationStore(secondPreferences);
        _ = firstPreferences.Current;
        _ = secondPreferences.Current;

        await Task.WhenAll(first.RecordAsync(VerifiedSuccess), second.DismissAsync(),
            Task.Run(() => new AppPreferencesStore(Paths).Update(p =>
            {
                p.Theme = "light";
                p.DefaultLane = null;
                p.ShowReasoning = false;
            }))).WaitAsync(Limit);

        var saved = new AppPreferencesStore(Paths).Current;
        Assert.True(saved.GitHubStarInvitationDismissed);
        Assert.Equal("light", saved.Theme);
        Assert.Null(saved.DefaultLane);
        Assert.False(saved.ShowReasoning);
        var raw = JsonNode.Parse(await File.ReadAllTextAsync(Paths.PreferencesPath).WaitAsync(Limit))!;
        Assert.False(raw["future"]!["enabled"]!.GetValue<bool>());
        Assert.True(raw["future"]!.AsObject().ContainsKey("scope"));
        Assert.Null(raw["future"]!["scope"]);
        Assert.False((await first.ReadAsync().WaitAsync(Limit)).ShouldShow(false));
        Assert.False((await second.ReadAsync().WaitAsync(Limit)).ShouldShow(false));
    }

    [Fact]
    public async Task DismissalBeforeEligibilityIsPermanentAndNonqualifyingEvidenceDoesNotCreatePreferences()
    {
        var store = new StarInvitationStore(new AppPreferencesStore(Paths));
        await store.RecordAsync(null).WaitAsync(Limit);
        await store.RecordAsync(VerifiedSuccess with { AcceptancePassed = false }).WaitAsync(Limit);
        Assert.False(File.Exists(Paths.PreferencesPath));

        await store.DismissAsync().WaitAsync(Limit);
        await store.RecordAsync(VerifiedSuccess).WaitAsync(Limit);
        var state = await new StarInvitationStore(new AppPreferencesStore(Paths)).ReadAsync().WaitAsync(Limit);
        Assert.False(state.Eligible);
        Assert.True(state.Dismissed);
        Assert.False(state.ShouldShow(false));
    }

    [Fact]
    public async Task MalformedPreferencesSuppressInvitationAndBothWritesPreserveOriginalBytes()
    {
        const string invalid = "{malformed preferences";
        await File.WriteAllTextAsync(Paths.PreferencesPath, invalid).WaitAsync(Limit);
        var original = await File.ReadAllBytesAsync(Paths.PreferencesPath).WaitAsync(Limit);
        var store = new StarInvitationStore(new AppPreferencesStore(Paths));

        Assert.False((await store.ReadAsync().WaitAsync(Limit)).ShouldShow(false));
        await Assert.ThrowsAnyAsync<JsonException>(() => store.RecordAsync(VerifiedSuccess).WaitAsync(Limit));
        Assert.Equal(original, await File.ReadAllBytesAsync(Paths.PreferencesPath).WaitAsync(Limit));
        await Assert.ThrowsAnyAsync<JsonException>(() => store.DismissAsync().WaitAsync(Limit));
        Assert.Equal(original, await File.ReadAllBytesAsync(Paths.PreferencesPath).WaitAsync(Limit));
    }

    [Fact]
    public async Task CancellationWhileEligibilityWaitsForPreferenceLeaseDoesNotQualifyOrPersistSuccess()
    {
        await using var harness = await BuildAsync((_, _) => Yield(Acceptance(CodingAcceptanceStatus.Passed),
            new TurnCompleted("turn", "Verified, finishing persistence", TimeSpan.Zero)));
        var finished = NextFinish(harness.Runner);
        using (var lease = new FileStream(Paths.PreferencesPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(harness.Runner.TrySend("Implement and verify the change", null));
            var jobs = harness.Provider.GetRequiredService<FileConversationJobStore>();
            using var waitDeadline = new CancellationTokenSource(Limit);
            await Task.Run(async () =>
            {
                while (jobs.Load(harness.Runner.ConversationId)?.State != "Completed") await Task.Delay(10, waitDeadline.Token);
            }).WaitAsync(Limit);
            Assert.True(harness.Runner.IsRunning);
            harness.Runner.Cancel();
        }
        var result = await finished;
        Assert.True(result.Cancelled);
        Assert.False(OptionalInvitationPolicy.Qualifies(result.CompletionEvidence));
        Assert.False((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);
        Assert.Equal("Stopped", harness.Provider.GetRequiredService<FileConversationJobStore>().Load(harness.Runner.ConversationId)!.State);
    }

    [Fact]
    public async Task StopFromEligibilityChangedEventCannotRetroactivelyCancelCommittedSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await BuildAsync((_, ct) => Script(ct));
        var changed = 0;
        var stoppedWhileFinishing = false;
        harness.Preferences.Changed += () =>
        {
            Interlocked.Increment(ref changed);
            stoppedWhileFinishing = harness.Runner.IsRunning;
            harness.Runner.Cancel();
        };
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Implement and verify the change", null));
        await entered.Task.WaitAsync(Limit);
        Assert.True(await harness.Runner.EnqueueAsync("queued follow-up", null).WaitAsync(Limit));
        release.TrySetResult();

        var result = await finished;
        Assert.Equal(1, changed);
        Assert.True(stoppedWhileFinishing);
        Assert.True(result.Completed);
        Assert.False(result.Cancelled);
        Assert.Equal(VerifiedSuccess, result.CompletionEvidence);
        Assert.True((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);
        var job = harness.Provider.GetRequiredService<FileConversationJobStore>().Load(harness.Runner.ConversationId)!;
        Assert.Equal("Completed", job.State);
        Assert.True(job.QueuePaused); // Stop still applies to follow-ups after success commits.
        Assert.Equal("queued follow-up", Assert.Single(job.Queue).Text);
        Assert.Equal("queued follow-up", Assert.Single(harness.Runner.QueuedMessages).Text);

        async IAsyncEnumerable<TurnEvent> Script([EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return Acceptance(CodingAcceptanceStatus.Passed);
            yield return new TurnCompleted("turn", "Verified, finishing persistence", TimeSpan.Zero);
        }
    }

    [Theory]
    [InlineData("new")]
    [InlineData("eligible")]
    [InlineData("dismissed")]
    [InlineData("no-store")]
    public async Task CompletionBoundaryResetsBeforeTheNextTurnForEveryInvitationState(string scenario)
    {
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await BuildAsync((request, ct) => Script(request, ct), registerInvitations: scenario != "no-store");
        if (scenario == "eligible") await harness.Invitations.RecordAsync(VerifiedSuccess).WaitAsync(Limit);
        if (scenario == "dismissed") await harness.Invitations.DismissAsync().WaitAsync(Limit);
        var firstFinished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("first", null));
        Assert.Equal(VerifiedSuccess, (await firstFinished).CompletionEvidence);

        var nextFinished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("next", null));
        await nextEntered.Task.WaitAsync(Limit);
        harness.Runner.Cancel();

        var next = await nextFinished;
        Assert.True(next.Cancelled);
        Assert.False(OptionalInvitationPolicy.Qualifies(next.CompletionEvidence));
        Assert.Equal("Stopped", harness.Provider.GetRequiredService<FileConversationJobStore>().Load(harness.Runner.ConversationId)!.State);
        var saved = await harness.Invitations.ReadAsync().WaitAsync(Limit);
        Assert.Equal(scenario is "new" or "eligible", saved.Eligible);
        Assert.Equal(scenario == "dismissed", saved.Dismissed);

        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return Acceptance(CodingAcceptanceStatus.Passed);
            yield return new TurnCompleted("turn", "Verified request", TimeSpan.Zero);
            if (request.UserMessage == "next")
            {
                nextEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        }
    }

    [Fact]
    public async Task RealRunnerPublishesHostVerifiedSnapshotAndPersistsEligibilityBeforeFinishEvent()
    {
        await using var harness = await BuildAsync((_, _) => Yield(Acceptance(CodingAcceptanceStatus.Passed),
            new TurnCompleted("turn", "Task finished", TimeSpan.Zero)));
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Implement and verify the change", null));

        var result = await finished;
        Assert.Equal(VerifiedSuccess, result.CompletionEvidence);
        Assert.Equal("Completed", harness.Provider.GetRequiredService<FileConversationJobStore>().Load(harness.Runner.ConversationId)!.State);
        Assert.True((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulWriteQualifiesWithDefaultNoChecksWithoutClaimingAcceptancePassed(bool reportUnverified)
    {
        Assert.Empty(new CodingAcceptanceOptions().Commands);
        await using var harness = await BuildAsync((_, _) => Script());
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Write the requested file", null));

        var result = await finished;
        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.True(result.CompletionEvidence!.SuccessfulWorkTool);
        Assert.True(result.CompletionEvidence.JournalSaved);
        Assert.False(result.CompletionEvidence.AcceptancePassed);
        Assert.False(result.CompletionEvidence.AcceptanceFailed);
        Assert.Equal("requested content", await File.ReadAllTextAsync(Path.Combine(Tmp, "requested.txt")));
        Assert.True(OptionalInvitationPolicy.Qualifies(result.CompletionEvidence));
        Assert.True((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);
        var saved = harness.Provider.GetRequiredService<FileConversationJobStore>().Load(harness.Runner.ConversationId)!;
        Assert.Equal("Completed", saved.State);
        if (reportUnverified) Assert.Equal(CodingAcceptanceStatus.Unverified, saved.Acceptance!.Status);
        else Assert.Null(saved.Acceptance);

        async IAsyncEnumerable<TurnEvent> Script()
        {
            yield return StartTool();
            await File.WriteAllTextAsync(Path.Combine(Tmp, "requested.txt"), "requested content");
            yield return FinishTool();
            if (reportUnverified) yield return new CodingAcceptanceEvaluated("turn", CodingAcceptanceResult.Unverified);
            yield return new TurnCompleted("turn", "Requested write finished; no checks configured.", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task SuccessfulWorkReceiptCannotQualifyWhenFinalJobJournalCannotBeSaved()
    {
        await using var harness = await BuildAsync((request, _) => Script(request.ConversationId));
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Write the requested file", null));

        var result = await finished;
        Assert.True(result.Completed);
        Assert.True(result.CompletionEvidence!.SuccessfulWorkTool);
        Assert.False(result.CompletionEvidence.JournalSaved);
        Assert.False(OptionalInvitationPolicy.Qualifies(result.CompletionEvidence));
        Assert.False((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);

        async IAsyncEnumerable<TurnEvent> Script(string conversationId)
        {
            yield return StartTool();
            yield return FinishTool();
            yield return new CodingAcceptanceEvaluated("turn", CodingAcceptanceResult.Unverified);
            yield return new TurnCompleted("turn", "Write finished", TimeSpan.Zero);
            // Let the consumer journal every receipt before blocking the terminal save.
            // A producer can otherwise outrun the relay and break an earlier event save.
            var jobsStore = new FileConversationJobStore(Paths);
            using var deadline = new CancellationTokenSource(Limit);
            while (jobsStore.Load(conversationId)?.Entries.Any(e => e.Kind == "assistant" && e.Text == "Write finished") != true)
                await Task.Delay(10, deadline.Token);
            var jobs = Path.Combine(Paths.SessionsDirectory, "jobs");
            Directory.Move(jobs, jobs + "-held");
            await File.WriteAllTextAsync(jobs, "Owned fixture blocks the final job save");
        }
    }

    [GitFact]
    public async Task ConcurrentCheckpointDiffWithoutSuccessfulWorkReceiptDoesNotQualify()
    {
        var repo = Directory.CreateDirectory(Path.Combine(Tmp, "checkpoint-repo")).FullName;
        foreach (var args in new[]
        {
            new[] { "init", "-q" }, ["config", "core.autocrlf", "false"], ["config", "user.name", "Fixture"],
            ["config", "user.email", "fixture@example.com"], ["config", "commit.gpgsign", "false"],
        }) Assert.Equal(0, TestGit.Run(repo, args).ExitCode);
        var file = Path.Combine(repo, "concurrent.txt");
        await File.WriteAllTextAsync(file, "before\n");
        Assert.Equal(0, TestGit.Run(repo, "add", ".").ExitCode);
        Assert.Equal(0, TestGit.Run(repo, "commit", "-q", "-m", "fixture baseline").ExitCode);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await BuildAsync((_, ct) => Script(ct), checkpointRoot: repo);
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Inspect the design", null));
        await entered.Task.WaitAsync(Limit);
        // Another writer changes the actual repository after the pre-turn checkpoint.
        await File.WriteAllTextAsync(file, "concurrent writer's edit\n");
        release.TrySetResult();

        var result = await finished;
        Assert.True(result.Completed);
        Assert.True(result.WorkspaceChanged);
        Assert.Single(harness.Runner.Entries.OfType<TurnChangesEntry>());
        Assert.True(result.CompletionEvidence!.JournalSaved);
        Assert.False(result.CompletionEvidence.SuccessfulWorkTool);
        Assert.False(OptionalInvitationPolicy.Qualifies(result.CompletionEvidence));
        Assert.False((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);

        async IAsyncEnumerable<TurnEvent> Script([EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new CodingAcceptanceEvaluated("turn", CodingAcceptanceResult.Unverified);
            yield return new TurnCompleted("turn", "Inspection finished", TimeSpan.Zero);
        }
    }

    [Theory]
    [InlineData("no-catalog")]
    [InlineData("no-journal")]
    [InlineData("child")]
    [InlineData("temporary")]
    [InlineData("schedule")]
    [InlineData("self-improvement")]
    [InlineData("plan")]
    [InlineData("continuation")]
    [InlineData("unverified")]
    [InlineData("acceptance-failed")]
    [InlineData("turn-failed")]
    [InlineData("prose-only")]
    [InlineData("failed-tool")]
    [InlineData("read-only")]
    [InlineData("started-tool")]
    [InlineData("failed-acceptance-with-work")]
    public async Task RunnerRejectsIneligibleHostContextsEvenWhenModelReportsSuccess(string scenario)
    {
        var events = new List<TurnEvent>();
        if (scenario is "failed-tool" or "read-only" or "started-tool" or "failed-acceptance-with-work")
        {
            var name = scenario == "read-only" ? "fs.read_file" : "fs.write_file";
            events.Add(StartTool(name));
            if (scenario != "started-tool") events.Add(FinishTool(scenario == "failed-tool", name));
        }
        if (scenario is not ("prose-only" or "started-tool"))
            events.Add(Acceptance(scenario is "unverified" or "failed-tool" or "read-only" ? CodingAcceptanceStatus.Unverified
                : scenario is "acceptance-failed" or "failed-acceptance-with-work" ? CodingAcceptanceStatus.Failed : CodingAcceptanceStatus.Passed));
        events.Add(new TurnCompleted("turn", "Everything is implemented, tested, and verified successfully.", TimeSpan.Zero));
        if (scenario == "turn-failed") events.Add(new TurnErrored("turn", "The final check failed"));

        await using var harness = await BuildAsync((_, _) => Yield(events.ToArray()),
            registerCatalog: scenario != "no-catalog", child: scenario == "child", temporary: scenario == "temporary",
            registerJobStore: scenario != "no-journal");
        if (scenario == "plan") harness.Runner.SetPlanMode(true);
        var origin = scenario == "schedule" ? TurnOrigin.Scheduled("schedule", allowNativeLanes: true)
            : scenario == "self-improvement" ? TurnOrigin.SelfImprovement("improvement", allowNativeLanes: true) : null;
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Do the coding work", null,
            isContinuation: scenario == "continuation", origin: origin));

        var result = await finished;
        Assert.NotNull(result.CompletionEvidence);
        Assert.False(OptionalInvitationPolicy.Qualifies(result.CompletionEvidence));
        Assert.False((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);
        if (scenario == "no-journal") Assert.False(result.CompletionEvidence.JournalSaved);
        if (scenario == "failed-acceptance-with-work")
        {
            Assert.True(result.CompletionEvidence.SuccessfulWorkTool);
            Assert.True(result.CompletionEvidence.AcceptanceFailed);
        }
        else Assert.False(result.CompletionEvidence.SuccessfulWorkTool);
        if (scenario is "no-catalog" or "child" or "schedule" or "self-improvement") Assert.False(result.CompletionEvidence.DirectUserRequest);
        if (scenario == "temporary") Assert.True(result.CompletionEvidence.Temporary);
        if (scenario == "plan") Assert.True(result.CompletionEvidence.PlanOnly);
        if (scenario == "continuation") Assert.True(result.CompletionEvidence.Continuation);
    }

    [Fact]
    public async Task CancellationAfterAcceptanceAndCompletionStillPreventsEligibility()
    {
        var reachedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await BuildAsync((_, ct) => Script(ct));
        var finished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("Do the coding work", null));
        await reachedCompletion.Task.WaitAsync(Limit);
        harness.Runner.Cancel();

        var result = await finished;
        Assert.True(result.Cancelled);
        Assert.True(result.CompletionEvidence!.Cancelled);
        Assert.False(OptionalInvitationPolicy.Qualifies(result.CompletionEvidence));
        Assert.False((await harness.Invitations.ReadAsync().WaitAsync(Limit)).Eligible);

        async IAsyncEnumerable<TurnEvent> Script([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Acceptance(CodingAcceptanceStatus.Passed);
            yield return new TurnCompleted("turn", "Completed candidate", TimeSpan.Zero);
            reachedCompletion.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task QueuedTurnResetCannotChangePriorCompletionEvidenceAndBusyStateSuppressesDisplay()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await BuildAsync((request, ct) => Script(request, ct));
        var firstFinished = NextFinish(harness.Runner);
        Assert.True(harness.Runner.TrySend("first", null));
        await firstEntered.Task.WaitAsync(Limit);
        Assert.True(await harness.Runner.EnqueueAsync("queued", null).WaitAsync(Limit));
        releaseFirst.TrySetResult();
        var first = await firstFinished;
        var snapshot = first.CompletionEvidence;
        await nextEntered.Task.WaitAsync(Limit);

        Assert.True(harness.Runner.IsRunning);
        Assert.Null(harness.Runner.Acceptance);
        Assert.Equal(VerifiedSuccess, snapshot);
        Assert.Same(snapshot, first.CompletionEvidence);
        var state = await harness.Invitations.ReadAsync().WaitAsync(Limit);
        Assert.True(state.Eligible);
        Assert.False(state.ShouldShow(harness.Runner.IsRunning || harness.Runner.QueuedMessages.Count > 0));

        var nextFinished = NextFinish(harness.Runner);
        releaseNext.TrySetResult();
        var next = await nextFinished;
        Assert.False(next.CompletionEvidence!.AcceptancePassed);
        Assert.Equal(VerifiedSuccess, first.CompletionEvidence);
        Assert.True((await harness.Invitations.ReadAsync().WaitAsync(Limit)).ShouldShow(false));

        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (request.UserMessage == "first")
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
                yield return Acceptance(CodingAcceptanceStatus.Passed);
                yield return new TurnCompleted("first", "Verified first request", TimeSpan.Zero);
            }
            else
            {
                nextEntered.TrySetResult();
                await releaseNext.Task.WaitAsync(ct);
                yield return new TurnCompleted("next", "Unverified next request", TimeSpan.Zero);
            }
        }
    }
}
