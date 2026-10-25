using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.Skills.Models;

namespace SwarmCode.Hosting.Tests;

public class ConversationRunnerTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private sealed class FakeOrchestrator(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? LastRequest { get; private set; }
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return script(request, ct);
        }
    }

    private sealed record Harness(ServiceProvider Provider, UiPermissionBroker Broker, FileConversationStore Store, ConversationRunner Runner, FakeOrchestrator Orchestrator) : IDisposable
    {
        public void Dispose() { Runner.Dispose(); Provider.Dispose(); }
    }

    private Harness Build(Func<IServiceProvider, Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>>> script, string conversationId = "conv", bool withJournal = false, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (withJournal) services.AddSingleton(new FileTranscriptJournal(Paths));
        var store = new FileConversationStore(Paths);
        var broker = new UiPermissionBroker();
        services.AddSingleton<IConversationStore>(store);
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(broker);
        FakeOrchestrator? orch = null;
        services.AddSingleton<ICodingTurnOrchestrator>(sp => orch ??= new FakeOrchestrator(script(sp)));
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var orchestrator = (FakeOrchestrator)provider.GetRequiredService<ICodingTurnOrchestrator>();
        return new Harness(provider, broker, store, new ConversationRunner(provider, conversationId), orchestrator);
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => tcs.TrySetResult(r);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async IAsyncEnumerable<TurnEvent> Yield(params TurnEvent[] events)
    {
        foreach (var e in events) { await Task.Yield(); yield return e; }
    }

    [Fact]
    public async Task Queued_message_survives_restart_admission_and_drains_once_when_helper_exits()
    {
        var admission = new ConversationRunAdmission();
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "done", TimeSpan.Zero)),
            configure: services => services.AddSingleton(admission));
        var restart = admission.TryQuiesce(); Assert.NotNull(restart);
        try
        {
            Assert.False(h.Runner.TrySend("saved follow-up", "claude"));
            Assert.True(await h.Runner.EnqueueAsync("saved follow-up", "claude", modelOverride: "selected"));
            var queued = Assert.Single(h.Runner.QueuedMessages);
            Assert.Equal("saved follow-up", queued.Text);
            Assert.Equal("selected", queued.ModelOverride);
            Assert.Equal(queued.Id, Assert.Single(h.Provider.GetRequiredService<FileConversationJobStore>().Load("conv")!.Queue).Id);
            Assert.Null(h.Orchestrator.LastRequest);
            var finish = NextFinish(h.Runner);
            restart!.Dispose();
            await finish;
            Assert.Equal("saved follow-up", h.Orchestrator.LastRequest!.UserMessage);
            Assert.Empty(h.Runner.QueuedMessages);
        }
        finally { restart!.Dispose(); }
    }

    [Fact]
    public async Task Failed_restart_keeps_a_newer_explicit_queue_stop()
    {
        var admission = new ConversationRunAdmission();
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "should not run", TimeSpan.Zero)),
            configure: services => services.AddSingleton(admission));
        using (var restart = admission.TryQuiesce())
        {
            Assert.NotNull(restart);
            Assert.True(await h.Runner.EnqueueAsync("retained", "claude"));
            h.Runner.Cancel();
        }
        Assert.True(h.Runner.QueuePaused);
        Assert.Single(h.Runner.QueuedMessages);
        Assert.Null(h.Orchestrator.LastRequest);
    }

    [Fact]
    public async Task Interrupted_work_retains_its_review_requirement_and_does_not_repeat_obtained_opinions()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp);
        var options = new SwarmCore.Abstractions.Collaboration.TurnAssistanceOptions
        {
            GetOpinionsFirst = true, ReviewBeforeFinishing = true, RecommendReviewer = false,
            Reviewer = new("review", "ReviewProvider", "pinned-review-model", "review")
        };
        var jobs = new FileConversationJobStore(Paths);
        jobs.Save(session.Id, new() { State = "Running", Request = "Original task", Assistance = options, OpinionsObtained = true });
        using var h = Build(_ => (request, ct) =>
        {
            Assert.True(jobs.Load(session.Id)!.Assistance!.ReviewBeforeFinishing);
            return Yield(new TurnCompleted("t", "Continued safely.", TimeSpan.Zero));
        }, session.Id, configure: services => services.AddSingleton(catalog));
        await h.Runner.LoadHistoryAsync();
        var finish = NextFinish(h.Runner);
        Assert.True(h.Runner.ContinueInterruptedWork(null, SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, false));
        await finish;
        var captured = h.Orchestrator.LastRequest!;
        Assert.True(captured.Assistance.ReviewBeforeFinishing);
        Assert.Equal("pinned-review-model", captured.Assistance.Reviewer!.Model);
        Assert.False(captured.Assistance.GetOpinionsFirst);
        Assert.False((await catalog.GetAsync(session.Id))!.Assistance.Enabled);
    }

    [Fact]
    public async Task Missing_conversation_settings_cannot_silently_skip_required_options_and_start_work()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "Should not run", TimeSpan.Zero)),
            configure: services => services.AddSingleton(new WorkspaceSessionCatalog(Paths)));
        var finish = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("Do some work", null));
        var result = await finish;
        Assert.Null(h.Orchestrator.LastRequest);
        Assert.Contains("settings could not be loaded", result.Error);
    }

    [Fact]
    public async Task SuccessfulTurn_BuildsTranscript_AndResult()
    {
        using var h = Build(_ => (_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new TextChunk("t", "Hello "),
            new TextChunk("t", "there"),
            new ToolCallStarted("t", "tc1", "code.edit_file", new JsonObject { ["path"] = "a.cs" }),
            new ToolCallCompleted("t", "tc1", "code.edit_file", "edited", false, TimeSpan.FromMilliseconds(42)),
            new TextChunk("t", " done"),
            new TurnCompleted("t", "Hello there done", TimeSpan.FromSeconds(1))));

        var changed = 0;
        h.Runner.Changed += () => Interlocked.Increment(ref changed);
        var finished = NextFinish(h.Runner);

        Assert.True(h.Runner.TrySend("do it", "claude", permissionMode: SwarmCore.Coding.Permissions.CodingPermissionMode.Ask, allowHandOff: false));
        Assert.False(h.Runner.TrySend("second", "claude")); // still running (producer yields before first event)
        var result = await finished;

        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.False(result.Cancelled);
        Assert.True(result.WorkspaceChanged);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(["claude"], result.LanesUsed.ToArray());
        Assert.Equal("Hello there done", result.FinalText);
        Assert.True(changed >= 1);
        Assert.Equal("claude", h.Runner.ActiveLane);

        Assert.Equal("do it", h.Orchestrator.LastRequest!.UserMessage);
        Assert.Equal("conv", h.Orchestrator.LastRequest.ConversationId);
        Assert.Equal("claude", h.Orchestrator.LastRequest.LaneName);
        // Ask mode: the direct request keeps user-requested authority and carries the policy that asks before every side effect.
        Assert.True(h.Orchestrator.LastRequest.CommunicationScope!.UserInitiated);
        Assert.Equal(SwarmCore.Coding.Permissions.CodingPermissionMode.Ask, h.Orchestrator.LastRequest.Permissions!.Mode);
        Assert.NotNull(h.Orchestrator.LastRequest.ValidateWorkingContextSourceCallsAsync);
        Assert.False(h.Orchestrator.LastRequest.AllowHandOff);

        var entries = h.Runner.Entries;
        Assert.IsType<UserEntry>(entries[0]);
        Assert.Equal("do it", ((UserEntry)entries[0]).Text);
        var notice = Assert.IsType<NoticeEntry>(entries[1]);
        Assert.Equal(NoticeKind.LaneSelected, notice.Kind);
        var first = Assert.IsType<AssistantEntry>(entries[2]);
        Assert.Equal("Hello there", first.Text);
        var tool = Assert.IsType<ToolCallEntry>(entries[3]);
        Assert.Equal(ToolCallState.Completed, tool.State);
        Assert.Equal(TimeSpan.FromMilliseconds(42), tool.Elapsed);
        Assert.Equal("edited", tool.Result);
        Assert.Contains("a.cs", tool.InputJson);
        var last = Assert.IsType<AssistantEntry>(entries[4]);
        Assert.False(last.IsStreaming);
        Assert.Equal(" done", last.Text); // text after a tool call starts a new bubble
        Assert.Equal("claude", last.LaneName);

        Assert.False(h.Runner.IsRunning);
        Assert.True(h.Runner.TrySend("again", "claude")); // another turn can start after finish
    }

    [Fact]
    public async Task Consumer_cancellation_releases_a_producer_blocked_on_the_full_relay()
    {
        // The producer writes with CancellationToken.None into a bounded(512) relay. When the consumer leaves on an
        // OperationCanceledException (as a cancelled decision-index read does after Stop), the turn must still finish:
        // the consumer stops the turn and drains, so the blocked producer completes and FinishAsync runs.
        static async IAsyncEnumerable<TurnEvent> Flood()
        {
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            for (var i = 0; i < 2000; i++) yield return new TextChunk("t", "x"); // ignores cancellation on purpose
            yield return new TurnCompleted("t", "done", TimeSpan.Zero);
        }

        using var h = Build(_ => (_, _) => Flood());
        var thrown = 0;
        h.Runner.Changed += () =>
        {
            if (h.Runner.Entries.OfType<AssistantEntry>().Any() && Interlocked.Exchange(ref thrown, 1) == 0)
                throw new OperationCanceledException("consumer observed cancellation");
        };
        var finished = NextFinish(h.Runner);

        Assert.True(h.Runner.TrySend("go", "claude"));
        var result = await finished;

        Assert.Equal(1, thrown);
        Assert.True(result.Cancelled);
        Assert.False(h.Runner.IsRunning);
    }

    [Fact]
    public async Task ErroredTurn_AddsErrorNotice_AndResultError()
    {
        using var h = Build(_ => (_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new TurnErrored("t", "boom", "details")));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("x", null));
        var result = await finished;

        Assert.Equal("boom", result.Error);
        Assert.False(result.Completed);
        var err = h.Runner.Entries.OfType<NoticeEntry>().Single(n => n.Kind == NoticeKind.Error);
        Assert.Equal("boom", err.Text);
        Assert.Equal("details", err.Details);
    }

    [Fact]
    public async Task HandOff_ShowsDestinationWithoutRepeatingDiagnostics()
    {
        var next = new CodingLane("codex", "CodexNative", "codex-cli:default");
        var reason = "Provider unavailable.\n" + new string('x', 1000);
        using var h = Build(_ => (_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new CodingLaneHandOff("t", Lane, next, reason),
            new CodingLaneSelected("t", next, 2),
            new TurnCompleted("t", "done", TimeSpan.Zero)));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("x", null));
        await finished;
        var notice = h.Runner.Entries.OfType<NoticeEntry>().Single(n => n.Kind == NoticeKind.HandOff);
        Assert.Equal("Switching from claude to codex.", notice.Text);
        Assert.Equal(reason, notice.Details);
    }

    [Fact]
    public async Task Attachments_FlowToOrchestratorAndSurviveReopeningHistory()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "done", TimeSpan.Zero)));
        var file = new SwarmCore.Coding.Files.CodingAttachment("id", "image.png", "managed/path", "image/png", 10, "hash");
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("inspect", "claude", attachments: [file]));
        await finished;
        Assert.Equal(file, Assert.Single(h.Orchestrator.LastRequest!.Attachments));
        await h.Runner.DisposeAsync();
        await using var reopened = new ConversationRunner(h.Provider, "conv");
        await reopened.LoadHistoryAsync();
        Assert.Equal(file, Assert.Single(Assert.Single(reopened.Entries.OfType<UserEntry>()).Attachments));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_attempt_owns_final_result_while_prior_errors_stay_in_history(bool recovers)
    {
        var next = new CodingLane("codex", "CodexNative", "codex-cli:default");
        using var h = Build(_ => (_, _) => Yield(
            new CodingLaneSelected("t1", Lane, 1),
            new TurnCompleted("t1", "Candidate from the unsuccessful attempt", TimeSpan.Zero),
            new TurnErrored("t1", "earlier provider failed"),
            new CodingLaneHandOff("t1", Lane, next, "retrying unfinished work"),
            new CodingLaneSelected("t2", next, 2),
            recovers ? new TurnCompleted("t2", "Recovered result", TimeSpan.Zero)
                : new TurnErrored("t2", "last provider failed")));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("Build this app", "claude", allowHandOff: true));
        var result = await finished;
        Assert.Equal(recovers, result.Completed);
        Assert.Equal(recovers ? null : "last provider failed", result.Error);
        Assert.Equal(recovers ? "Recovered result" : null, result.FinalText);
        Assert.Contains(h.Runner.Entries.OfType<NoticeEntry>(), e => e.Kind == NoticeKind.Error && e.Text == "earlier provider failed");
        var saved = h.Provider.GetRequiredService<FileConversationJobStore>().Load("conv")!;
        Assert.Equal(recovers ? "Completed" : "Failed", saved.State);
    }

    [Fact]
    public async Task QueueEdits_PreserveIdentityAndModel_AcrossRestart_AndDeletion()
    {
        using var h = Build(_ => (_, _) => Yield());
        var journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        journal.Save("conv", new ConversationJob { State = "Stopped", Queue = [new("old", "claude", false, false) { Id = "stable", ModelOverride = "chosen-model" }] });
        await h.Runner.LoadHistoryAsync();
        Assert.True(await h.Runner.UpdateQueuedAsync("stable", "edited"));
        Assert.False(await h.Runner.UpdateQueuedAsync("missing", "wrong"));
        await using var reopened = new ConversationRunner(h.Provider, "conv");
        await reopened.LoadHistoryAsync();
        var item = Assert.Single(reopened.QueuedMessages);
        Assert.Equal("stable", item.Id);
        Assert.Equal("edited", item.Text);
        Assert.Equal("chosen-model", item.ModelOverride);
        Assert.Null(h.Orchestrator.LastRequest);
        Assert.True(await reopened.RemoveQueuedAsync("stable"));
        Assert.Empty(journal.Load("conv")!.Queue);
    }

    [Fact]
    public async Task Steering_SavesCorrectionBeforeCancellation_AndWaitsForWorkerExit()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var corrected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = false;
        FileConversationJobStore? journal = null;
        var calls = 0;
        CodingTurnRequest? resumed = null;
        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                yield return new ToolCallStarted("first", "unknown", "shell.exec", new JsonObject());
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    Assert.Contains(journal!.Load("conv")!.Queue, q => q.IsSteering && q.Text == "use the corrected approach");
                    await Task.Delay(100);
                    workerExited = true;
                }
            }
            else
            {
                Assert.True(workerExited);
                Assert.True(request.IsContinuation);
                Assert.Equal("selected-model", request.ModelOverride);
                Assert.Contains("original task", request.UserMessage);
                Assert.Contains("use the corrected approach", request.UserMessage);
                resumed = request;
                corrected.SetResult();
                yield return new TurnCompleted("corrected", "done", TimeSpan.Zero);
            }
        }
        using var h = Build(_ => Script);
        journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        Assert.True(h.Runner.TrySend("original task", "claude"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await h.Runner.EnqueueAsync("later task", "claude"));
        Assert.True(await h.Runner.SteerAsync("use the corrected approach", "claude", modelOverride: "selected-model"));
        await corrected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Change of direction:", resumed!.UserMessage);
        Assert.Equal("original task\n\nuse the corrected approach", resumed.AuthenticatedOriginalUserMessage);
        Assert.Equal(ToolCallState.Unknown, Assert.Single(h.Runner.Entries.OfType<ToolCallEntry>()).State);
        Assert.Equal("later task", Assert.Single(h.Runner.QueuedMessages).Text);
        Assert.True(h.Runner.QueuePaused);
    }

    [Fact]
    public async Task Supporting_key_and_repeated_steers_preserve_protected_objective_through_restart()
    {
        const string original = "Write the report file output.txt.";
        const string key = "BSA7synthetic9Key4ForSteering2Probe8";
        var starts = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        var requests = new List<CodingTurnRequest>();
        var final = new TaskCompletionSource();
        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            requests.Add(request);
            var index = requests.Count - 1;
            if (index < 2)
            {
                starts[index].SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            yield return new TurnCompleted("t", "done", TimeSpan.Zero);
            final.TrySetResult();
        }
        using var h = Build(_ => Script, configure: services =>
        {
            services.AddSingleton<SwarmCore.Secrets.ISecretVault>(new SwarmCore.Secrets.SqliteSecretVault(Paths.SecretsDbPath,
                new SwarmCore.Secrets.EnvelopeSecretProtector(new EncryptedTestKeyStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SwarmCore.Secrets.EnvelopeSecretProtector>.Instance)));
            services.AddSingleton(Web.SwarmCodeWebSearch.IntakeBinding);
            services.AddSingleton<Secrets.SecretIntake>();
        });
        Assert.True(h.Runner.TrySend(original, "claude"));
        await starts[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await h.Runner.SteerAsync("here is the brave key\n" + key, "claude"));
        await starts[1].Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await h.Runner.SteerAsync("Use the supplied documentation for the report.", "claude"));
        await final.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; h.Runner.IsRunning && i < 100; i++) await Task.Delay(10);
        Assert.False(h.Runner.IsRunning);
        var request = requests[2];
        Assert.Contains(original, request.UserMessage);
        Assert.Contains("{{secret:brave-search-api-key}}", request.UserMessage);
        Assert.DoesNotContain(key, request.UserMessage);
        Assert.DoesNotContain("Change of direction:", request.UserMessage);
        Assert.Equal(1, request.UserMessage.Split("Original task:").Length - 1);
        Assert.Contains("Use the supplied documentation", request.AuthenticatedOriginalUserMessage);
        Assert.DoesNotContain("saved tool receipts", request.AuthenticatedOriginalUserMessage);
        Assert.True(request.RequireWorkspaceChange);
        var jobs = h.Provider.GetRequiredService<FileConversationJobStore>();
        var saved = jobs.Load("conv")!;
        Assert.Equal(original, saved.Request);
        Assert.Equal(2, saved.Continuation!.Updates.Count);
        Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.DoesNotContain(key, string.Join("\n", h.Runner.Entries.OfType<UserEntry>().Select(e => e.Text)));
        await h.Runner.DisposeAsync();
        jobs.Save("conv", saved with { State = "Running" }); // simulate an interrupted process, after the previous owner exited
        await using var restarted = new ConversationRunner(h.Provider, "conv");
        await restarted.LoadHistoryAsync();
        var finish = NextFinish(restarted);
        Assert.True(restarted.ContinueInterruptedWork("claude", SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, false));
        await finish;
        Assert.Equal(request.AuthenticatedOriginalUserMessage, requests[3].AuthenticatedOriginalUserMessage);
        Assert.Equal(original, jobs.Load("conv")!.Continuation!.OriginalRequest);
        finish = NextFinish(restarted);
        Assert.True(restarted.TrySend("Explain this new task", "claude"));
        await finish;
        Assert.Empty(jobs.Load("conv")!.Continuation!.Updates);
    }

    [Fact]
    public async Task Legacy_continuation_protects_intent_without_replacing_a_newer_search_key()
    {
        const string oldKey = "BSA8Historical7Key4FromLegacy2Job6"; // gitleaks:allow -- synthetic test fixture, never a live credential
        const string currentKey = "BSA9Current4Key7FromSettings2Probe5";
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "done", TimeSpan.Zero)), configure: services =>
        {
            services.AddSingleton<SwarmCore.Secrets.ISecretVault>(new SwarmCore.Secrets.SqliteSecretVault(Paths.SecretsDbPath,
                new SwarmCore.Secrets.EnvelopeSecretProtector(new EncryptedTestKeyStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SwarmCore.Secrets.EnvelopeSecretProtector>.Instance)));
            services.AddSingleton(Web.SwarmCodeWebSearch.IntakeBinding);
            services.AddSingleton<Secrets.SecretIntake>();
        });
        var vault = h.Provider.GetRequiredService<SwarmCore.Secrets.ISecretVault>();
        await Web.SwarmCodeWebSearch.SaveKeyAsync(vault, currentKey);
        var jobs = h.Provider.GetRequiredService<FileConversationJobStore>();
        jobs.Save("conv", new ConversationJob { State = "Running", Request = "Explain the result. Here is the brave key\n" + oldKey });
        await h.Runner.LoadHistoryAsync();
        var finish = NextFinish(h.Runner);
        Assert.True(h.Runner.ContinueInterruptedWork("claude", SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, false));
        await finish;
        Assert.Equal(currentKey, await vault.GetValueAsync(Web.SwarmCodeWebSearch.SecretName));
        Assert.DoesNotContain(oldKey, h.Orchestrator.LastRequest!.UserMessage);
        Assert.DoesNotContain(oldKey, h.Orchestrator.LastRequest.AuthenticatedOriginalUserMessage);
        Assert.DoesNotContain(oldKey, System.Text.Json.JsonSerializer.Serialize(jobs.Load("conv")));
        Assert.Contains("{{secret:brave-search-api-key-2}}", h.Orchestrator.LastRequest.AuthenticatedOriginalUserMessage);
    }

    [Fact]
    public async Task OrchestratorThrows_ReportsError()
    {
        using var h = Build(_ => (_, _) => Throwing());
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("x", null));
        var result = await finished;
        Assert.Equal("kaput", result.Error);
        Assert.Contains(h.Runner.Entries.OfType<NoticeEntry>(), n => n.Kind == NoticeKind.Error && n.Text == "kaput");

        static async IAsyncEnumerable<TurnEvent> Throwing()
        {
            await Task.Yield();
            throw new InvalidOperationException("kaput");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task EmptyOrWhitespaceText_IsRejected()
    {
        using var h = Build(_ => (_, _) => Yield());
        Assert.False(h.Runner.TrySend("   ", null));
        Assert.Empty(h.Runner.Entries);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeAsync_WaitsForCancelledTurnCleanup_AndRejectsNewTurns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Build(_ => (_, ct) => Slow(ct));
        Assert.True(h.Runner.TrySend("slow", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = h.Runner.DisposeAsync().AsTask();
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(disposal.IsCompleted);
        Assert.False(h.Runner.TrySend("new turn", null));
        release.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(h.Runner.IsRunning);
        Assert.False(h.Runner.TrySend("after disposal", null));

        async IAsyncEnumerable<TurnEvent> Slow([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextChunk("t", "partial");
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaning.SetResult(); await release.Task; }
        }
    }

    [Fact]
    public async Task ConcurrentSend_OnlyStartsOneTurn()
    {
        using var h = Build(_ => (_, ct) => Slow(ct));
        var finished = NextFinish(h.Runner);
        using var barrier = new Barrier(2);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return h.Runner.TrySend("work", null);
        })));
        Assert.Single(accepted, x => x);
        h.Runner.Cancel();
        await finished;

        static async IAsyncEnumerable<TurnEvent> Slow([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextChunk("t", "partial");
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task Queue_ContinuesWithoutPageSubscriber_AndRemovesRequestedItem()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource<CodingTurnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Build(_ => (request, _) => Script(request));
        Assert.True(h.Runner.TrySend("first", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await h.Runner.EnqueueAsync("remove this", "claude"));
        Assert.True(await h.Runner.EnqueueAsync("second", "claude", permissionMode: SwarmCore.Coding.Permissions.CodingPermissionMode.Ask, allowHandOff: false));
        Assert.Equal(2, h.Runner.QueuedMessages.Count);
        await h.Runner.RemoveQueued(0);
        Assert.Equal("second", Assert.Single(h.Runner.QueuedMessages).Text);
        release.SetResult();
        var next = await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("second", next.UserMessage);
        Assert.Equal("claude", next.LaneName);
        Assert.Equal(SwarmCore.Coding.Permissions.CodingPermissionMode.Ask, next.Permissions!.Mode);
        Assert.False(next.AllowHandOff);
        await h.Runner.DisposeAsync();

        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest request)
        {
            if (request.UserMessage == "first") { entered.SetResult(); await release.Task; }
            else nextStarted.SetResult(request);
            yield return new TurnCompleted("t", "ok", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Queue_AfterFreshRequest_DoesNotInheritAnEmptyQueuesPause()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource<CodingTurnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Build(_ => (request, _) => Script(request));
        var journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        // Stops, failures and steering can leave an empty queue paused, including across restarts.
        journal.Save("conv", new ConversationJob { State = "Completed", QueuePaused = true });
        await h.Runner.LoadHistoryAsync();
        Assert.True(h.Runner.QueuePaused);
        Assert.True(h.Runner.TrySend("fresh request", "claude"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await h.Runner.EnqueueAsync("follow up", "claude", modelOverride: "chosen-model"));
        release.SetResult();
        var next = await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("follow up", next.UserMessage);
        Assert.Equal("chosen-model", next.ModelOverride);
        Assert.False(h.Runner.QueuePaused);
        await h.Runner.DisposeAsync();
        Assert.Empty(journal.Load("conv")!.Queue);

        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest request)
        {
            if (request.UserMessage == "fresh request") { entered.SetResult(); await release.Task; }
            else nextStarted.SetResult(request);
            yield return new TurnCompleted("t", "ok", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Queue_FreshRequest_DoesNotResumePreviouslyPausedPrompts()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "ok", TimeSpan.Zero)));
        var journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        journal.Save("conv", new ConversationJob { State = "Stopped", QueuePaused = true, Queue = [new("older task", null, false, null)] });
        await h.Runner.LoadHistoryAsync();
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("fresh request", "claude"));
        Assert.True((await finished).Completed);
        await h.Runner.DisposeAsync();
        Assert.True(h.Runner.QueuePaused);
        Assert.Equal("older task", Assert.Single(journal.Load("conv")!.Queue).Text);
        Assert.Equal("fresh request", h.Orchestrator.LastRequest!.UserMessage);
    }

    [Fact]
    public async Task Queue_StaysPendingAfterStop()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = Build(_ => (_, ct) => Slow(ct));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("first", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await h.Runner.EnqueueAsync("second", null));
        h.Runner.Cancel();
        Assert.True((await finished).Cancelled);
        Assert.False(h.Runner.IsRunning);
        Assert.Single(h.Runner.QueuedMessages);

        async IAsyncEnumerable<TurnEvent> Slow([EnumeratorCancellation] CancellationToken ct)
        {
            entered.SetResult();
            yield return new TextChunk("t", "partial");
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task PermissionRequest_AppearsAsEntry_AndRespondResolves()
    {
        PermissionConsentResult? outcome = null;
        UiPermissionBroker? broker = null;
        using var h = Build(sp =>
        {
            broker = (UiPermissionBroker)sp.GetRequiredService<IPermissionBroker>();
            return (_, ct) => Script(ct);
        });

        async IAsyncEnumerable<TurnEvent> Script([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new CodingLaneSelected("t", Lane, 1);
            outcome = await broker!.RequestAsync(UiPermissionBrokerTests.Request(), ct);
            yield return new TurnCompleted("t", "ok", TimeSpan.Zero);
        }

        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("needs approval", null, permissionMode: SwarmCore.Coding.Permissions.CodingPermissionMode.Ask));

        PermissionEntry? entry = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (entry is null && DateTime.UtcNow < deadline)
        {
            entry = h.Runner.Entries.OfType<PermissionEntry>().FirstOrDefault();
            if (entry is null) await Task.Delay(10);
        }
        Assert.NotNull(entry);
        Assert.True(entry!.IsPending);
        Assert.Equal("Skill / fn", entry.Title);
        Assert.Equal(["Perm"], entry.Permissions.ToArray());

        h.Runner.Respond(entry.RequestId, true);
        var result = await finished;
        Assert.True(result.Completed);
        Assert.True(outcome!.Approved);
        Assert.True(entry.Approved);
        Assert.False(entry.IsPending);
    }

    [Fact]
    public async Task Cancel_DuringSlowTurn_MarksCancelled_WithNotice()
    {
        using var h = Build(_ => (_, ct) => Slow(ct));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("slow", null));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!h.Runner.Entries.OfType<AssistantEntry>().Any() && DateTime.UtcNow < deadline) await Task.Delay(10);
        h.Runner.Cancel();
        var result = await finished;

        Assert.True(result.Cancelled);
        Assert.False(result.Completed);
        Assert.Contains(h.Runner.Entries.OfType<NoticeEntry>(), n => n.Kind == NoticeKind.Cancelled);
        var assistant = h.Runner.Entries.OfType<AssistantEntry>().Single();
        Assert.False(assistant.IsStreaming);

        static async IAsyncEnumerable<TurnEvent> Slow([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextChunk("t", "partial");
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Fact]
    public async Task AssistantBubbleBeforeToolCall_StopsStreaming()
    {
        using var h = Build(_ => (_, _) => Yield(
            new TextChunk("t", "before"),
            new ToolCallStarted("t", "tc", "code.edit_file", new JsonObject()),
            new ToolCallCompleted("t", "tc", "code.edit_file", "ok", false, TimeSpan.Zero),
            new TextChunk("t", "after"),
            new TurnCompleted("t", "after", TimeSpan.Zero)));
        var finished = NextFinish(h.Runner);
        h.Runner.TrySend("go", null);
        await finished;
        Assert.All(h.Runner.Entries.OfType<AssistantEntry>(), a => Assert.False(a.IsStreaming));
    }

    [Fact]
    public async Task OpenToolsAtTurnEnd_HaveUnknownOutcomes()
    {
        using var h = Build(_ => (_, _) => Yield(
            new ToolCallStarted("t", "tc", "shell.exec", new JsonObject()),
            new TurnCompleted("t", "x", TimeSpan.Zero)));
        var finished = NextFinish(h.Runner);
        h.Runner.TrySend("go", null);
        var result = await finished;
        Assert.False(result.WorkspaceChanged);
        var tool = h.Runner.Entries.OfType<ToolCallEntry>().Single();
        Assert.Equal(ToolCallState.Unknown, tool.State);
        Assert.False(tool.IsError);
    }

    [Fact]
    public async Task FailedMutatingTool_DoesNotMarkWorkspaceChanged()
    {
        using var h = Build(_ => (_, _) => Yield(
            new ToolCallStarted("t", "tc", "code.edit_file", new JsonObject()),
            new ToolCallCompleted("t", "tc", "code.edit_file", "err", true, TimeSpan.Zero),
            new TurnCompleted("t", "x", TimeSpan.Zero)));
        var finished = NextFinish(h.Runner);
        h.Runner.TrySend("go", null);
        var result = await finished;
        Assert.False(result.WorkspaceChanged);
        Assert.Equal(ToolCallState.Failed, h.Runner.Entries.OfType<ToolCallEntry>().Single().State);
    }

    [Fact]
    public async Task LoadHistoryAsync_ReproducesPersistedTurns()
    {
        using var h = Build(_ => (_, _) => Yield());
        await h.Store.GetOrCreateAsync("conv", "t");
        var t1 = await h.Store.AppendUserTurnAsync("conv", "question one");
        await h.Store.CompleteAssistantTurnAsync(t1.Id, "answer one", null, TimeSpan.Zero, "reasoning one");
        await h.Store.AppendUserTurnAsync("conv", "question two"); // unanswered

        var changed = 0;
        h.Runner.Changed += () => changed++;
        await h.Runner.LoadHistoryAsync();

        var entries = h.Runner.Entries;
        // The unanswered second turn is followed by the interrupted-turn notice.
        Assert.Equal(4, entries.Count);
        Assert.Equal("question one", Assert.IsType<UserEntry>(entries[0]).Text);
        var a = Assert.IsType<AssistantEntry>(entries[1]);
        Assert.Equal("answer one", a.Text);
        Assert.Equal("reasoning one", a.Reasoning);
        Assert.False(a.IsStreaming);
        Assert.Equal("question two", Assert.IsType<UserEntry>(entries[2]).Text);
        Assert.Equal(NoticeKind.Info, Assert.IsType<NoticeEntry>(entries[3]).Kind);
        Assert.Equal(1, changed);

        h.Runner.ClearTranscript();
        Assert.Empty(h.Runner.Entries);
    }

    [Fact]
    public async Task Restart_PreservesReceiptsAndQueue_WithoutDispatchingUnknownWork()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "ok", TimeSpan.Zero)));
        var journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        journal.Save("conv", new ConversationJob
        {
            State = "Running", Request = "publish the artifact", ActiveLane = "claude",
            Queue = [new("next task", "claude", false, false)],
            Entries = [
                new() { Kind = "user", Text = "publish the artifact" },
                new() { Kind = "tool", Text = "shell.exec", ToolCallId = "done", ToolState = ToolCallState.Completed, Result = "artifact built" },
                new() { Kind = "tool", Text = "shell.exec", ToolCallId = "unknown", ToolState = ToolCallState.Running },
            ],
        });
        await h.Runner.LoadHistoryAsync();
        Assert.Equal("Interrupted", h.Runner.JobState);
        Assert.True(h.Runner.HasInterruptedWork);
        Assert.True(h.Runner.QueuePaused);
        Assert.False(h.Runner.IsRunning);
        Assert.Null(h.Orchestrator.LastRequest);
        Assert.Equal("next task", Assert.Single(h.Runner.QueuedMessages).Text);
        var tools = h.Runner.Entries.OfType<ToolCallEntry>().ToArray();
        Assert.Equal(ToolCallState.Completed, tools[0].State);
        Assert.Equal("artifact built", tools[0].Result);
        Assert.Equal(ToolCallState.Unknown, tools[1].State);
        Assert.Contains("Outcome unknown", tools[1].Result);
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.ContinueInterruptedWork("claude", SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, false));
        await finished;
        Assert.Contains("unknown outcome", h.Orchestrator.LastRequest!.UserMessage);
        Assert.Contains("verify it through a read-only check", h.Orchestrator.LastRequest.UserMessage);
        Assert.Contains("Do not repeat a potentially completed external action", h.Orchestrator.LastRequest.UserMessage);
        Assert.Single(h.Runner.QueuedMessages); // A recovery action does not silently authorize the queued work.
    }

    [Fact]
    public async Task CompletedTurn_ReopensWithToolCardsAndNotices()
    {
        using var h = Build(_ => (_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new ToolCallStarted("t", "tc", "shell.exec", new JsonObject { ["command"] = "dotnet test" }),
            new ToolCallCompleted("t", "tc", "shell.exec", "Passed: 12", false, TimeSpan.FromSeconds(2)),
            new TurnCompleted("t", "verified", TimeSpan.FromSeconds(3))));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("test", null));
        await finished;
        await h.Runner.DisposeAsync();
        await using var reopened = new ConversationRunner(h.Provider, "conv");
        await reopened.LoadHistoryAsync();
        Assert.Equal("Completed", reopened.JobState);
        Assert.Equal("Passed: 12", Assert.Single(reopened.Entries.OfType<ToolCallEntry>()).Result);
        Assert.Contains(reopened.Entries.OfType<NoticeEntry>(), n => n.Kind == NoticeKind.LaneSelected);
        Assert.Equal("verified", Assert.Single(reopened.Entries.OfType<AssistantEntry>()).Text);
    }

    [Fact]
    public async Task RestoredQueue_RunsOnlyAfterExplicitResume_AndRemovalIsDurable()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "ok", TimeSpan.Zero)));
        var journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        journal.Save("conv", new ConversationJob { State = "Stopped", Queue = [new("remove", null, false, null), new("retain", null, false, null)] });
        await h.Runner.LoadHistoryAsync();
        await h.Runner.RemoveQueuedAsync(0);
        Assert.Equal("retain", Assert.Single(journal.Load("conv")!.Queue).Text);
        Assert.Null(h.Orchestrator.LastRequest);
        var finished = NextFinish(h.Runner);
        await h.Runner.ResumeQueueAsync();
        await finished;
        Assert.Equal("retain", h.Orchestrator.LastRequest!.UserMessage);
        Assert.Empty(journal.Load("conv")!.Queue);
    }

    [Fact]
    public async Task JobWriteFailure_DoesNotDispatchAgent()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "must not run", TimeSpan.Zero)));
        File.WriteAllText(Path.Combine(Paths.SessionsDirectory, "jobs"), "blocks journal directory");
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("change files", null));
        var result = await finished;
        Assert.Contains("No agent action was started", result.Error);
        Assert.Null(h.Orchestrator.LastRequest);
        Assert.True(h.Runner.QueuePaused);
    }

    [Fact]
    public async Task LegacyProviderTranscript_RestoresToolReceipts()
    {
        using var h = Build(_ => (_, _) => Yield());
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConversationStore>(h.Store);
        var transcript = new FileAgentSessionStore(Paths);
        services.AddSingleton<SwarmCore.Agent.ToolUseEngine.IAgentSessionStore>(transcript);
        await transcript.AppendRangeAsync("conv", [
            new(SwarmCore.Abstractions.LlmRouting.ChatRole.User, [new SwarmCore.Abstractions.LlmRouting.TextPart("check")]),
            new(SwarmCore.Abstractions.LlmRouting.ChatRole.Assistant, [new SwarmCore.Abstractions.LlmRouting.ToolUsePart("call", "git.status", new JsonObject())]),
            new(SwarmCore.Abstractions.LlmRouting.ChatRole.Tool, [new SwarmCore.Abstractions.LlmRouting.ToolResultPart("call", "clean")]),
        ]);
        using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, "conv");
        await runner.LoadHistoryAsync();
        Assert.Equal("check", Assert.Single(runner.Entries.OfType<UserEntry>()).Text);
        Assert.Equal("clean", Assert.Single(runner.Entries.OfType<ToolCallEntry>()).Result);
        Assert.Equal(ToolCallState.Completed, Assert.Single(runner.Entries.OfType<ToolCallEntry>()).State);
    }

    [Fact]
    public async Task UsageAndAcceptance_SurviveRestart_WithoutInferringVerificationFromAnAnswer()
    {
        using var h = Build(_ => (_, _) => Yield(
            new SwarmCore.Agent.Runtime.TurnUsageReported("t", "test", "model", 10, 5, CostUsd: 0.002m),
            new CodingTurnProgress("t", "claude", TimeSpan.FromSeconds(2), 0, 15, "verifying"),
            new CodingAcceptanceEvaluated("t", CodingAcceptanceResult.Unverified),
            new TurnCompleted("t", "done", TimeSpan.FromSeconds(2))));
        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("task", null));
        await finished;
        Assert.Equal(15, h.Runner.ReportedTokens);
        Assert.Equal(0.002m, h.Runner.ReportedCostUsd);
        Assert.Equal(CodingAcceptanceStatus.Unverified, h.Runner.Acceptance!.Status);
        await h.Runner.DisposeAsync();
        await using var reopened = new ConversationRunner(h.Provider, "conv");
        await reopened.LoadHistoryAsync();
        Assert.Equal(15, reopened.ReportedTokens);
        Assert.Equal(0.002m, reopened.ReportedCostUsd);
        Assert.Equal(CodingAcceptanceStatus.Unverified, reopened.Acceptance!.Status);
    }

    [Fact]
    public async Task Recovery_ReconcilesProviderReceiptAheadOfUiJournal()
    {
        using var h = Build(_ => (_, _) => Yield());
        var journal = h.Provider.GetRequiredService<FileConversationJobStore>();
        journal.Save("conv", new ConversationJob { State = "Running", Request = "write file", Entries = [new() { Kind = "tool", ToolCallId = "call", Text = "fs.write_file", ToolState = ToolCallState.Running }] });
        var transcript = new FileAgentSessionStore(Paths);
        await transcript.AppendRangeAsync("conv", [
            new(SwarmCore.Abstractions.LlmRouting.ChatRole.Assistant, [new SwarmCore.Abstractions.LlmRouting.ToolUsePart("call", "fs.write_file", new JsonObject())]),
            new(SwarmCore.Abstractions.LlmRouting.ChatRole.Tool, [new SwarmCore.Abstractions.LlmRouting.ToolResultPart("call", "file written")]),
        ]);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConversationStore>(h.Store);
        services.AddSingleton(journal);
        services.AddSingleton<SwarmCore.Agent.ToolUseEngine.IAgentSessionStore>(transcript);
        using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, "conv");
        await runner.LoadHistoryAsync();
        var tool = Assert.Single(runner.Entries.OfType<ToolCallEntry>());
        Assert.Equal(ToolCallState.Completed, tool.State);
        Assert.Equal("file written", tool.Result);
        Assert.Equal("Interrupted", runner.JobState);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task ReopeningInterruptedWork_DoesNotDuplicateRecoveryNotices()
    {
        using var h = Build(_ => (_, _) => Yield());
        h.Provider.GetRequiredService<FileConversationJobStore>().Save("conv", new ConversationJob { State = "Running", Request = "work" });
        await h.Runner.LoadHistoryAsync();
        var first = Assert.Single(h.Runner.Entries.OfType<NoticeEntry>());
        await h.Runner.LoadHistoryAsync();
        Assert.Equal(first.Text, Assert.Single(h.Runner.Entries.OfType<NoticeEntry>()).Text);
    }

    [Fact]
    public async Task AssistantStreaming_ContentReadsRemainWholeAcrossBackgroundWrites()
    {
        var assistant = new AssistantEntry();
        using var start = new Barrier(2);
        var writer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 10_000; i++) { assistant.AppendText("abcdefg"); assistant.AppendReasoning("1234567"); }
        });
        var reader = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 1000; i++)
            {
                Assert.Equal(0, assistant.Text.Length % 7);
                Assert.Equal(0, assistant.Reasoning.Length % 7);
            }
        });
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(70_000, assistant.Text.Length);
        Assert.Equal(70_000, assistant.Reasoning.Length);
    }

    [Fact]
    public async Task ConversationGrant_IsHonouredForTheConversation_RevocableByChange_AndReopensWithItsScope()
    {
        UiPermissionBroker? broker = null;
        var outcomes = new List<PermissionConsentResult>();
        using var h = Build(sp =>
        {
            broker = (UiPermissionBroker)sp.GetRequiredService<IPermissionBroker>();
            return (_, ct) => Script(ct);
        }, withJournal: true);

        async IAsyncEnumerable<TurnEvent> Script([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new CodingLaneSelected("t", Lane, 1);
            outcomes.Add(await broker!.RequestAsync(UiPermissionBrokerTests.Request("conv"), ct));
            outcomes.Add(await broker!.RequestAsync(UiPermissionBrokerTests.Request("conv"), ct));
            yield return new TurnCompleted("t", "ok", TimeSpan.Zero);
        }

        var finished = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("needs approval", null, permissionMode: SwarmCore.Coding.Permissions.CodingPermissionMode.Ask));
        PermissionEntry? entry = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (entry is null && DateTime.UtcNow < deadline)
        {
            entry = h.Runner.Entries.OfType<PermissionEntry>().FirstOrDefault();
            if (entry is null) await Task.Delay(10);
        }
        Assert.NotNull(entry);
        Assert.Equal("Skill", entry!.SkillName);
        Assert.Equal("fn", entry.FunctionName);
        Assert.Equal([PermissionLevel.Elevated], entry.Levels.ToArray());

        h.Runner.Respond(entry.RequestId, true, PermissionGrantScope.Conversation);
        Assert.True((await finished).Completed);

        // The second identical request in the conversation was approved without another card.
        Assert.Single(h.Runner.Entries.OfType<PermissionEntry>());
        Assert.True(outcomes[1].AlreadyGranted);
        Assert.Equal(PermissionGrantScope.Conversation, entry.Scope);

        // Change withdraws the grant: the next request asks again.
        Assert.True(h.Runner.RevokePermission(entry.RequestId));
        Assert.True(entry.GrantRevoked);
        Assert.False(broker!.IsGranted(UiPermissionBrokerTests.Request("conv")));

        await h.Runner.DisposeAsync();
        await using var reopened = new ConversationRunner(h.Provider, "conv");
        await reopened.LoadHistoryAsync();
        var restored = Assert.Single(reopened.Entries.OfType<PermissionEntry>());
        Assert.True(restored.Approved);
        Assert.Equal(PermissionGrantScope.Conversation, restored.Scope);
        Assert.True(restored.GrantRevoked);
        Assert.Equal("fn", restored.FunctionName);
        Assert.Equal([PermissionLevel.Elevated], restored.Levels.ToArray());
    }

    [Fact]
    public async Task ClearTranscript_ForgetsTheConversationsGrants()
    {
        using var h = Build(_ => (_, _) => Yield(new TurnCompleted("t", "ok", TimeSpan.Zero)));
        using (h.Broker.Subscribe(r => h.Broker.Respond(r.RequestId, true, PermissionGrantScope.Conversation)))
            await h.Broker.RequestAsync(UiPermissionBrokerTests.Request("conv")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Broker.IsGranted(UiPermissionBrokerTests.Request("conv")));

        h.Runner.ClearTranscript();

        Assert.False(h.Broker.IsGranted(UiPermissionBrokerTests.Request("conv")));
    }
}
