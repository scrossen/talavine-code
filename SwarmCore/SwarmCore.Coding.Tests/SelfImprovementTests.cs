using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCore.Coding.Tests;

public sealed class SelfImprovementTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "swarmcore-self-improvement", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static SelfImprovementOptions Options(Action<SelfImprovementOptions>? configure = null)
    {
        var options = new SelfImprovementOptions { StartupDelay = TimeSpan.Zero, PollInterval = TimeSpan.FromMilliseconds(50) };
        configure?.Invoke(options);
        return options;
    }

    private static Exception Thrown(Exception exception)
    {
        try { throw exception; } catch (Exception caught) { return caught; }
    }

    // ---- fingerprints and classification ----------------------------------------------------------

    [Fact]
    public void Fingerprint_folds_line_numbers_paths_guids_and_numbers()
    {
        var a = SelfImprovementFingerprint.Compute(SelfImprovementKind.Defect, "NullReferenceException",
            "Object reference not set at C:\\Code\\SwarmMarshal\\SwarmCode.Hosting\\ConversationRunner.cs:line 912 (id 8f1c2d3e-aaaa-bbbb-cccc-1234567890ab, 42 items)");
        var b = SelfImprovementFingerprint.Compute(SelfImprovementKind.Defect, "NullReferenceException",
            "Object reference not set at D:\\other\\place\\ConversationRunner.cs:line 7 (id 00000000-1111-2222-3333-444444444444, 7 items)");
        var c = SelfImprovementFingerprint.Compute(SelfImprovementKind.Defect, "InvalidOperationException", "Object reference not set");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(SelfImprovementFingerprint.IsValid(a));
        Assert.NotEqual(a, SelfImprovementFingerprint.Compute(SelfImprovementKind.FeatureRequest, "NullReferenceException", "Object reference not set"));
    }

    [Fact]
    public void Classifier_keeps_product_exceptions_and_drops_environmental_ones()
    {
        Assert.True(DefectClassifier.IsProductDefect(Thrown(new NullReferenceException("boom"))));
        Assert.True(DefectClassifier.IsProductDefect(new AggregateException(new InvalidOperationException("inner"))));
        Assert.False(DefectClassifier.IsProductDefect(new OperationCanceledException()));
        Assert.False(DefectClassifier.IsProductDefect(new HttpRequestException("503")));
        Assert.False(DefectClassifier.IsProductDefect(new FileNotFoundException("missing")));
        Assert.False(DefectClassifier.IsProductDefect(new UnauthorizedAccessException("denied")));
        Assert.False(DefectClassifier.IsProductDefect(new TimeoutException("slow")));

        var candidate = DefectClassifier.FromException(Thrown(new NullReferenceException("Object reference not set")), "While handling: fix the tests");
        Assert.NotNull(candidate);
        Assert.StartsWith("NullReferenceException: Object reference not set", candidate!.Title);
        Assert.Contains("Context: While handling: fix the tests", candidate.Details);
        Assert.Contains("SwarmCore.Coding.Tests", candidate.Details); // the product frame from this test assembly
        Assert.Equal(candidate.Fingerprint, DefectClassifier.FromException(Thrown(new NullReferenceException("Object reference not set")))!.Fingerprint);
    }

    [Fact]
    public void Classifier_reads_typed_error_text_and_ignores_provider_failures()
    {
        var typed = DefectClassifier.FromErrorText("InvalidOperationException: Sequence contains no elements", "   at SwarmCore.Coding.Routing.CodingTurnOrchestrator.RunAsync() in x.cs:line 12");
        Assert.NotNull(typed);
        Assert.StartsWith("InvalidOperationException: Sequence contains no elements", typed!.Title);
        Assert.Null(DefectClassifier.FromErrorText("HttpRequestException: 503 Service Unavailable"));
        Assert.Null(DefectClassifier.FromErrorText("The model declined the request."));
        Assert.Null(DefectClassifier.FromErrorText("VendorQuotaException: out of credits")); // unknown type, no product frame
        Assert.NotNull(DefectClassifier.FromErrorText("CustomThingException: bad state\n   at SwarmCode.Hosting.ConversationRunner.Apply()"));
    }

    [Fact]
    public void Capability_gap_heuristics_separate_missing_tools_from_policy_and_typos()
    {
        Assert.True(CapabilityGapHeuristics.IsCapabilityGap("I don't have a tool to send email from SwarmCode."));
        Assert.True(CapabilityGapHeuristics.IsCapabilityGap("SwarmCode has no way to open a pull request on GitLab; that integration is not supported."));
        Assert.True(CapabilityGapHeuristics.IsCapabilityGap("That is not available in Talavine Code."));
        Assert.True(CapabilityGapHeuristics.IsCapabilityGap("That is not available in SwarmCode."));
        Assert.False(CapabilityGapHeuristics.IsCapabilityGap("I can't help with that: it would be harmful."));
        Assert.False(CapabilityGapHeuristics.IsCapabilityGap("This violates policy."));
        Assert.False(CapabilityGapHeuristics.IsCapabilityGap(""));

        string[] known = ["help", "lane", "schedules", "export"];
        Assert.False(CapabilityGapHeuristics.IsUnknownCommandGap("/hepl", known));
        Assert.False(CapabilityGapHeuristics.IsUnknownCommandGap("/lanes", known));
        Assert.False(CapabilityGapHeuristics.IsUnknownCommandGap("/ex", known));
        Assert.True(CapabilityGapHeuristics.IsUnknownCommandGap("/deploy", known));
        Assert.True(CapabilityGapHeuristics.IsUnknownCommandGap("/screenshot", known));
    }

    // ---- backlog store -----------------------------------------------------------------------------

    [Fact]
    public async Task File_backlog_folds_sightings_keeps_dismissed_items_closed_and_checks_revisions()
    {
        var backlog = new JsonFileSelfImprovementBacklog(Path.Combine(_tmp, "backlog"));
        var reporter = new SelfImprovementReporter(backlog, () => Options());
        var first = await reporter.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Crash", "details", Source: "turn-error", ConversationId: "c1"));
        var second = await reporter.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Crash", "details", ConversationId: "c2"));
        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, second.Occurrences);
        Assert.Equal("c2", second.ConversationId);
        Assert.Equal("turn-error", second.Source);
        Assert.Equal(2, second.Revision);
        Assert.Single(await backlog.ListAsync());

        var dismissed = await backlog.UpdateAsync(first.Id, i => i with { Stage = SelfImprovementStage.Dismissed }, second.Revision);
        Assert.Equal(SelfImprovementStage.Dismissed, dismissed!.Stage);
        var again = await reporter.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Crash", "details"));
        Assert.Equal(SelfImprovementStage.Dismissed, again!.Stage);
        Assert.Equal(3, again.Occurrences);

        await Assert.ThrowsAsync<SelfImprovementConflictException>(() => backlog.UpdateAsync(first.Id, i => i with { Note = "x" }, expectedRevision: 1));
        Assert.True(await backlog.DeleteAsync(first.Id));
        Assert.False(await backlog.DeleteAsync(first.Id));
        Assert.Empty(await backlog.ListAsync());
    }

    [Fact]
    public async Task Reporter_respects_the_switches_and_the_cap()
    {
        var backlog = new InMemorySelfImprovementBacklog();
        var off = new SelfImprovementReporter(backlog, () => Options(o => o.Enabled = false));
        Assert.Null(await off.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "a", "b")));
        var noFeatures = new SelfImprovementReporter(backlog, () => Options(o => o.CaptureFeatureRequests = false));
        Assert.Null(await noFeatures.ReportFeatureRequestAsync("send email", "no tool", null, "test"));
        Assert.NotNull(await noFeatures.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "a", "b")));

        var capped = new SelfImprovementReporter(backlog, () => Options(o => o.MaxQueued = 2));
        Assert.NotNull(await capped.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "second", "b")));
        Assert.Null(await capped.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "third", "b")));
        // A repeat of an open item still folds in when the backlog is full.
        Assert.Equal(2, (await capped.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "second", "b")))!.Occurrences);

        var feature = await new SelfImprovementReporter(new InMemorySelfImprovementBacklog(), () => Options())
            .ReportFeatureRequestAsync("open a pull request on GitLab", "There is no GitLab integration.", "Opened it in the browser.", "request_feature", "conv");
        Assert.Equal(SelfImprovementKind.FeatureRequest, feature!.Kind);
        Assert.StartsWith("Feature request: open a pull request on GitLab", feature.Title);
        Assert.Contains("GitLab integration", feature.Details);
        Assert.Contains("Workaround used meanwhile", feature.Details);
        Assert.Null(await new SelfImprovementReporter(backlog, () => Options()).ReportDefectAsync(new HttpRequestException("down"), "turn-error"));
    }

    // ---- idle gate ---------------------------------------------------------------------------------

    private sealed class BusySource(Func<IReadOnlyList<string>> reasons) : ISelfImprovementActivitySource
    {
        public IReadOnlyList<string> BusyReasons() => reasons();
    }

    [Fact]
    public void Idle_gate_waits_for_the_quiet_period_and_for_activity_sources()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        var gate = new SelfImprovementIdleGate(() => Options(o => o.UserIdleAfter = TimeSpan.FromMinutes(10)), time);
        Assert.False(gate.IsIdle(out var reasons)); // startup counts as activity
        Assert.Contains(reasons, r => r.Contains("waiting for 10 min of quiet"));

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.True(gate.IsIdle(out _));

        var busy = new List<string> { "An agent is working." };
        var source = new BusySource(() => busy);
        gate.AddSource(source);
        Assert.False(gate.IsIdle(out reasons));
        Assert.Equal(["An agent is working."], reasons);
        busy.Clear();
        Assert.True(gate.IsIdle(out _));

        gate.ReportUserActivity();
        Assert.False(gate.IsIdle(out _));
        Assert.True(gate.RemoveSource(source));
        gate.AddSource(new BusySource(() => throw new InvalidOperationException("broken")));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(gate.IsIdle(out reasons));
        Assert.Contains(reasons, r => r.Contains("activity check failed"));
    }

    // ---- worker ------------------------------------------------------------------------------------

    private sealed class FakeCycleHost : ISelfImprovementCycleHost
    {
        public bool Allowed { get; set; } = true;
        public Func<SelfImprovementItem, bool>? Allows { get; set; }
        public Task<bool> CanRunAsync(SelfImprovementItem item, CancellationToken ct = default) => Task.FromResult(Allowed && (Allows?.Invoke(item) ?? true));
        public string? Root { get; set; } = "C:/fake/repo";
        public Func<SelfImprovementItem, SelfImprovementCycleContext, CancellationToken, Task<SelfImprovementCycleResult>> Run { get; set; }
            = (_, _, _) => Task.FromResult(new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Verified, "ok", "evidence", "swarmcode/self-improvement-1", new string('a', 40), "wt", "sess"));
        public List<SelfImprovementItem> Calls { get; } = [];
        public string? ResolveRepositoryRoot() => Root;
        public Task<SelfImprovementCycleResult> RunAsync(SelfImprovementItem item, SelfImprovementCycleContext context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(item);
            return Run(item, context, ct);
        }
    }

    private static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Worker_runs_a_queued_item_only_when_idle_and_records_a_verified_fix()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, () => Options(), time);
        var gate = new SelfImprovementIdleGate(() => Options(o => o.UserIdleAfter = TimeSpan.FromMinutes(10)), time);
        var host = new FakeCycleHost();
        await using var worker = new SelfImprovementWorker(backlog, host, gate, () => Options(), time: time);
        var item = (await reporter.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Crash", "details")))!;

        await worker.TickAsync();
        Assert.Empty(host.Calls);
        Assert.Contains("waiting for Talavine Code to be idle", worker.Status);
        Assert.NotEmpty(worker.WaitingReasons);

        time.Advance(TimeSpan.FromMinutes(11));
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        var stored = (await backlog.GetAsync(item.Id))!;
        Assert.Equal(SelfImprovementStage.Verified, stored.Stage);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal("swarmcode/self-improvement-1", stored.Branch);
        Assert.Equal(new string('a', 40), stored.Commit);
        Assert.Equal("sess", stored.SessionId);
        Assert.Equal("evidence", stored.Evidence);
        Assert.Null(stored.Owner);
        Assert.Equal(worker.Instance, host.Calls[0].Owner);

        await worker.TickAsync();
        Assert.Single(host.Calls); // a verified item is not run again
        Assert.Equal("Nothing to do.", worker.Status);
    }

    [Fact]
    public async Task Worker_counts_failed_attempts_honours_the_cooldown_and_hands_the_item_to_a_person()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        var backlog = new InMemorySelfImprovementBacklog();
        var gate = new SelfImprovementIdleGate(() => Options(o => o.UserIdleAfter = TimeSpan.Zero), time);
        var host = new FakeCycleHost { Run = (_, _, _) => Task.FromResult(new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Failed, "checks failed", "ev")) };
        SelfImprovementOptions Opts() => Options(o => { o.MaxAttemptsPerItem = 2; o.RetryCooldown = TimeSpan.FromHours(1); });
        await using var worker = new SelfImprovementWorker(backlog, host, gate, Opts, time: time);
        var item = (await new SelfImprovementReporter(backlog, Opts, time).ReportAsync(new SelfImprovementReport(SelfImprovementKind.FeatureRequest, "Feature", "details")))!;

        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        var afterFirst = (await backlog.GetAsync(item.Id))!;
        Assert.Equal(SelfImprovementStage.Queued, afterFirst.Stage);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal("checks failed", afterFirst.Note);

        await worker.TickAsync(); // inside the cooldown
        Assert.Single(host.Calls);
        Assert.Contains("retry time", worker.Status);

        time.Advance(TimeSpan.FromHours(2));
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 2);
        var afterSecond = (await backlog.GetAsync(item.Id))!;
        Assert.Equal(SelfImprovementStage.NeedsHuman, afterSecond.Stage);
        Assert.Equal(2, afterSecond.Attempts);

        time.Advance(TimeSpan.FromHours(2));
        await worker.TickAsync();
        Assert.Equal(2, host.Calls.Count);

        // Run now requeues a handed-off item and bypasses the gate.
        gate.ReportUserActivity();
        var manualGate = new SelfImprovementIdleGate(() => Options(o => o.UserIdleAfter = TimeSpan.FromHours(1)), time);
        await using var manual = new SelfImprovementWorker(backlog, host, manualGate, Opts, time: time);
        var start = await manual.RunNowAsync(item.Id);
        Assert.True(start.Started, start.Message);
        await Until(() => !manual.IsRunning && host.Calls.Count == 3);
        Assert.Equal(SelfImprovementStage.NeedsHuman, (await backlog.GetAsync(item.Id))!.Stage);
    }

    [Fact]
    public async Task Eligible_goal_repair_takes_precedence_over_old_frequent_unrelated_backlog()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        SelfImprovementOptions Opts() => Options(o => { o.AutoApplyGoalRepairs = true; o.UserIdleAfter = TimeSpan.Zero; });
        var backlog = new InMemorySelfImprovementBacklog(); var reporter = new SelfImprovementReporter(backlog, Opts, time);
        var unrelated = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Old unrelated defect", "Unrelated synthetic defect")))!;
        await backlog.UpdateAsync(unrelated.Id, current => current with { Occurrences = 10000 });
        time.Advance(TimeSpan.FromMinutes(1));
        var linked = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Current goal defect", "Current synthetic goal failure")))!;
        await backlog.UpdateAsync(linked.Id, current => current with { GoalRepair = new(Guid.NewGuid(), 2, "intent", "failure",
            SwarmCore.Agent.Continual.ContinualObjectiveStatus.Uncertain, time.GetUtcNow()) });
        var host = new FakeCycleHost();
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts, time: time);
        await worker.TickAsync(); await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        Assert.Equal(linked.Id, Assert.Single(host.Calls).Id);
        Assert.Equal(SelfImprovementStage.Queued, (await backlog.GetAsync(unrelated.Id))!.Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finished_goal_repair_yields_idle_checkpoints_until_host_observes_it_no_longer_applicable(bool merged)
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        SelfImprovementOptions Opts() => Options(o => { o.AutoApplyGoalRepairs = true; o.UserIdleAfter = TimeSpan.Zero; });
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, Opts, time);
        var unrelated = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Ordinary defect", "Unrelated work")))!;
        var linked = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Goal defect", "Goal work")))!;
        await backlog.UpdateAsync(linked.Id, current => current with { GoalRepair = new(Guid.NewGuid(), 2, "intent", "failure",
            SwarmCore.Agent.Continual.ContinualObjectiveStatus.Uncertain, time.GetUtcNow()) });
        var applicable = true;
        var host = new FakeCycleHost
        {
            Allows = item => item.GoalRepair is null || applicable,
            Run = (_, _, _) => Task.FromResult(new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Verified, "Checked",
                "evidence", "repair", new string('a', 40), Merged: merged))
        };
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts, time: time);
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        Assert.Equal(linked.Id, Assert.Single(host.Calls).Id);
        var finished = (await backlog.GetAsync(linked.Id))!;
        Assert.Equal(merged ? SelfImprovementStage.Merged : SelfImprovementStage.Verified, finished.Stage);
        var idleCheckpoints = 0;
        worker.Checkpoint += () => { if (!worker.IsRunning) idleCheckpoints++; };

        // A delayed host activator gets an idle boundary even when the next loop checks immediately and repeatedly.
        await worker.TickAsync();
        await worker.TickAsync();
        Assert.False(worker.IsRunning);
        Assert.Single(host.Calls);
        Assert.Equal(2, idleCheckpoints);
        Assert.Contains("waiting for installation", worker.Status);
        Assert.Equal(finished, await backlog.GetAsync(linked.Id)); // No implicit install, retry or backlog mutation.
        Assert.Equal(SelfImprovementStage.Queued, (await backlog.GetAsync(unrelated.Id))!.Stage);

        applicable = false; // Host saw activation/resume, a newer goal edit, or a pause. Old repair cannot hold other work.
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 2);
        Assert.Equal(unrelated.Id, host.Calls[1].Id);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Verified_repairs_without_current_automatic_goal_authority_do_not_hold_ordinary_work(bool linked, bool autoApply)
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        SelfImprovementOptions Opts() => Options(o => { o.AutoApplyGoalRepairs = autoApply; o.UserIdleAfter = TimeSpan.Zero; });
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, Opts, time);
        var ready = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Verified defect", "Completed work")))!;
        await backlog.UpdateAsync(ready.Id, current => current with { Stage = SelfImprovementStage.Verified,
            GoalRepair = linked ? new(Guid.NewGuid(), 2, "intent", "failure",
                SwarmCore.Agent.Continual.ContinualObjectiveStatus.Uncertain, time.GetUtcNow()) : null });
        var ordinary = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Ordinary defect", "Waiting work")))!;
        var host = new FakeCycleHost();
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts, time: time);

        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        Assert.Equal(ordinary.Id, Assert.Single(host.Calls).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_cycle_status_remains_truthful_when_future_cycles_are_disabled_or_paused(bool disable)
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var enabled = true;
        var paused = false;
        SelfImprovementOptions Opts() => Options(o => { o.Enabled = enabled; o.UserIdleAfter = TimeSpan.Zero; });
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, Opts, time);
        var current = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Current cycle", "Already started")))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var host = new FakeCycleHost { Run = async (_, _, ct) =>
        {
            using var registration = ct.Register(() => cancelled = true);
            entered.TrySetResult();
            await finish.Task.WaitAsync(ct);
            return new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Verified, "Finished naturally");
        } };
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts,
            paused: () => paused, time: time);
        await worker.TickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = (await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Next cycle", "Not yet started")))!;
        if (disable) enabled = false; else paused = true;
        try
        {
            await worker.TickAsync();
            Assert.True(worker.IsRunning);
            Assert.Equal(current.Id, worker.ActiveItemId);
            Assert.Equal("Working on \"Current cycle\".", worker.Status);
            Assert.False(cancelled);
            Assert.Single(host.Calls);
        }
        finally { finish.TrySetResult(); }

        await Until(() => !worker.IsRunning);
        await worker.TickAsync();
        Assert.Equal(disable ? "Off. Turn self-improvement on in Settings." : "Paused.", worker.Status);
        Assert.Single(host.Calls);
        Assert.False(cancelled);
        Assert.Equal(SelfImprovementStage.Verified, (await backlog.GetAsync(current.Id))!.Stage);
        Assert.Equal(SelfImprovementStage.Queued, (await backlog.GetAsync(waiting.Id))!.Stage);
    }

    [Fact]
    public async Task Goal_linked_repairs_continue_after_the_normal_attempt_allowance_with_backoff_and_host_authority_checks()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        SelfImprovementOptions Opts() => Options(o => { o.AutoApplyGoalRepairs = true; o.MaxAttemptsPerItem = 1; o.UserIdleAfter = TimeSpan.Zero; });
        var backlog = new InMemorySelfImprovementBacklog();
        var item = (await new SelfImprovementReporter(backlog, Opts, time).ReportAsync(new(SelfImprovementKind.Defect, "Harness failure", "Synthetic only")))!;
        await backlog.UpdateAsync(item.Id, current => current with { GoalRepair = new(Guid.NewGuid(), 2, "intent", "failure",
            SwarmCore.Agent.Continual.ContinualObjectiveStatus.Uncertain, time.GetUtcNow()) });
        var host = new FakeCycleHost { Run = (_, _, _) => Task.FromResult(new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Failed, "Regression still fails.")) };
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts, time: time);
        var checkpoints = 0; worker.Checkpoint += () => checkpoints++;
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        Assert.Equal(SelfImprovementStage.Queued, (await backlog.GetAsync(item.Id))!.Stage);
        await worker.TickAsync(); Assert.Single(host.Calls); // No tight retry loop.
        time.Advance(TimeSpan.FromMinutes(3));
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 2);
        Assert.Equal(2, (await backlog.GetAsync(item.Id))!.Attempts);
        Assert.Equal(SelfImprovementStage.Queued, (await backlog.GetAsync(item.Id))!.Stage);
        host.Allowed = false; // The owning host observed a new pause/cancel or a changed original goal.
        time.Advance(TimeSpan.FromHours(2));
        await worker.TickAsync(); Assert.Equal(2, host.Calls.Count);
        Assert.False((await worker.RunNowAsync(item.Id)).Started);
        Assert.Equal(4, checkpoints); // Unchanged idle checks still offer a safe installation boundary.
    }

    [Fact]
    public async Task Worker_stays_quiet_when_paused_or_without_a_repository_and_requeues_dead_owners()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        var backlog = new InMemorySelfImprovementBacklog();
        var gate = new SelfImprovementIdleGate(() => Options(o => o.UserIdleAfter = TimeSpan.Zero), time);
        var host = new FakeCycleHost { Root = null };
        var item = (await new SelfImprovementReporter(backlog, () => Options(), time).ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Crash", "d")))!;
        var paused = true;
        await using var worker = new SelfImprovementWorker(backlog, host, gate, () => Options(), paused: () => paused, time: time);

        await worker.TickAsync();
        Assert.Equal("Paused.", worker.Status);
        paused = false;
        await worker.TickAsync();
        Assert.Contains("source repository was not found", worker.Status);
        Assert.Empty(host.Calls);
        Assert.Equal(SelfImprovementStage.Queued, (await backlog.GetAsync(item.Id))!.Stage);

        // An item a dead SwarmCode left in progress goes back to the queue at the next initialization.
        await backlog.UpdateAsync(item.Id, i => i with { Stage = SelfImprovementStage.InProgress, Owner = "999999:1:dead" });
        await using var fresh = new SelfImprovementWorker(backlog, host, gate, () => Options(), time: time);
        await fresh.InitializeAsync();
        var requeued = (await backlog.GetAsync(item.Id))!;
        Assert.Equal(SelfImprovementStage.Queued, requeued.Stage);
        Assert.Null(requeued.Owner);
        Assert.Contains("closed while this cycle", requeued.Note);
    }

    [Fact]
    public async Task Worker_yields_to_the_person_when_asked_and_does_not_count_the_attempt()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        var backlog = new InMemorySelfImprovementBacklog();
        var gate = new SelfImprovementIdleGate(() => Options(o => o.UserIdleAfter = TimeSpan.Zero), time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeCycleHost
        {
            Run = async (_, _, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Verified, "never");
            },
        };
        await using var worker = new SelfImprovementWorker(backlog, host, gate, () => Options(o => o.StopWhenUserReturns = true), time: time);
        var item = (await new SelfImprovementReporter(backlog, () => Options(), time).ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Crash", "d")))!;
        time.Advance(TimeSpan.FromMinutes(1));
        await worker.TickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(worker.IsRunning);
        Assert.Equal(item.Id, worker.ActiveItemId);

        gate.ReportUserActivity();
        time.Advance(TimeSpan.FromSeconds(6)); // the watcher's next check sees the person back
        await Until(() => !worker.IsRunning);
        var stored = (await backlog.GetAsync(item.Id))!;
        Assert.Equal(SelfImprovementStage.Queued, stored.Stage);
        Assert.Equal(0, stored.Attempts);
        Assert.Contains("came back", stored.Note);
    }

    [Fact]
    public async Task Cancelled_cycle_persists_checkpoint_metadata_without_counting_or_claiming_a_verified_repair()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        SelfImprovementOptions Opts() => Options(o => o.UserIdleAfter = TimeSpan.Zero);
        var backlog = new InMemorySelfImprovementBacklog();
        var item = (await new SelfImprovementReporter(backlog, Opts, time).ReportAsync(new(SelfImprovementKind.Defect,
            "Interrupted repair", "Synthetic work")))!;
        // Failed activation can requeue an earlier verified item while retaining its old commit.
        await backlog.UpdateAsync(item.Id, current => current with { Branch = "swarmcode/old-repair",
            Commit = new string('a', 40), WorktreePath = "old/source" });
        var host = new FakeCycleHost { Run = (_, _, _) => Task.FromResult(new SelfImprovementCycleResult(
            SelfImprovementCycleOutcome.Cancelled, "Unverified checkpoint retained", "Checkpoint evidence",
            Branch: "swarmcode/checkpoint", WorktreePath: "retained/source", SessionId: "stopped-session")) };
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts, time: time);
        await worker.TickAsync();
        await Until(() => !worker.IsRunning && host.Calls.Count == 1);
        var saved = (await backlog.GetAsync(item.Id))!;
        Assert.Equal(SelfImprovementStage.Queued, saved.Stage);
        Assert.Equal(0, saved.Attempts);
        Assert.Null(saved.LastAttemptUtc);
        Assert.Null(saved.Commit);
        Assert.Null(saved.Owner);
        Assert.Equal("swarmcode/checkpoint", saved.Branch);
        Assert.Equal("retained/source", saved.WorktreePath);
        Assert.Equal("stopped-session", saved.SessionId);
        Assert.Equal("Checkpoint evidence", saved.Evidence);
        var prompt = SelfImprovementPrompts.Cycle(saved, []);
        Assert.Contains(saved.WorktreePath, prompt);
        Assert.Contains(saved.Branch, prompt);
        Assert.Contains(saved.Evidence, prompt);
        Assert.Contains("Inspect that checkpoint read-only before reimplementing", prompt);
        Assert.Contains("Do not automatically copy or apply its code, replay its commands, or commit it", prompt);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("dismiss")]
    [InlineData("delete")]
    public async Task Cancelled_checkpoint_writer_preserves_competing_file_backlog_edits_and_does_not_resurrect_items(string competingAction)
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        SelfImprovementOptions Opts() => Options(o => o.UserIdleAfter = TimeSpan.Zero);
        var directory = Path.Combine(_tmp, "cancel-backlog");
        var backlog = new JsonFileSelfImprovementBacklog(directory);
        var competing = new JsonFileSelfImprovementBacklog(directory);
        var item = (await new SelfImprovementReporter(backlog, Opts, time).ReportAsync(new(SelfImprovementKind.Defect,
            "Interrupted repair", "Original details", Source: "original")))!;
        var finish = new TaskCompletionSource<SelfImprovementCycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeCycleHost { Run = (_, _, ct) => finish.Task.WaitAsync(ct) };
        await using var worker = new SelfImprovementWorker(backlog, host, new SelfImprovementIdleGate(Opts, time), Opts, time: time);
        await worker.TickAsync();
        await Until(() => host.Calls.Count == 1);
        SelfImprovementItem? changed = null;
        if (competingAction == "delete") Assert.True(await competing.DeleteAsync(item.Id));
        else changed = await competing.UpdateAsync(item.Id, current => current with
        {
            Details = "Newer details", Source = null,
            Stage = competingAction == "dismiss" ? SelfImprovementStage.Dismissed : current.Stage,
            Owner = competingAction == "dismiss" ? null : current.Owner,
        });
        finish.TrySetResult(new(SelfImprovementCycleOutcome.Cancelled, "Checkpoint retained", "Unverified",
            Branch: "swarmcode/checkpoint", WorktreePath: "retained/source"));
        await Until(() => !worker.IsRunning);
        var saved = await competing.GetAsync(item.Id);
        if (competingAction == "delete") Assert.Null(saved);
        else if (competingAction == "dismiss") Assert.Equal(changed, saved);
        else
        {
            Assert.NotNull(saved);
            Assert.Equal("Newer details", saved.Details);
            Assert.Null(saved.Source);
            Assert.Equal("retained/source", saved.WorktreePath);
            Assert.Equal("swarmcode/checkpoint", saved.Branch);
            Assert.Equal(SelfImprovementStage.Queued, saved.Stage);
            Assert.Equal(0, saved.Attempts);
        }
    }

    // ---- tool and registration ---------------------------------------------------------------------

    [Fact]
    public async Task Feature_request_tool_needs_a_turn_and_queues_once()
    {
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, () => Options());
        string? conversation = null;
        var tool = FeatureRequestTool.Create(reporter, () => conversation);
        var outside = (FeatureRequestOutput)(await tool.Invoke(new FeatureRequestInput("send email", "no email tool"), default))!;
        Assert.False(outside.Queued);
        Assert.Empty(await backlog.ListAsync());

        conversation = "conv-1";
        var queued = (FeatureRequestOutput)(await tool.Invoke(new FeatureRequestInput("send email", "no email tool", "used the browser"), default))!;
        Assert.True(queued.Queued);
        var item = Assert.Single(await backlog.ListAsync());
        Assert.Equal(queued.ItemId, item.Id);
        Assert.Equal("conv-1", item.ConversationId);
        Assert.Equal(FeatureRequestTool.Source, item.Source);
        var repeat = (FeatureRequestOutput)(await tool.Invoke(new FeatureRequestInput("send email", "no email tool"), default))!;
        Assert.True(repeat.Queued);
        Assert.Contains("Already queued", repeat.Message);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
    }

    [Fact]
    public async Task Registration_adds_the_reporter_the_tool_and_the_guidance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCoreCoding(c => c.RepositoryRoot = Path.GetTempPath(), _ => { });
        services.AddSwarmCoreSelfImprovement(o => o.MaxQueued = 7);
        await using var provider = services.BuildServiceProvider();
        var reporter = provider.GetRequiredService<SelfImprovementReporter>();
        Assert.Equal(7, reporter.Options.MaxQueued);
        Assert.IsType<InMemorySelfImprovementBacklog>(provider.GetRequiredService<ISelfImprovementBacklog>());
        foreach (var hosted in provider.GetServices<IHostedService>()) await hosted.StartAsync(default);
        Assert.NotNull(provider.GetRequiredService<ICapabilityCatalog>().Get(FeatureRequestTool.ToolId));
        Assert.Contains(FeatureRequestTool.ToolId, provider.GetRequiredService<IOptions<CodingOptions>>().Value.AdditionalInstructions);
    }

    [Fact]
    public void Checks_are_discovered_for_the_SwarmCode_layout_and_a_single_solution()
    {
        var swarmCode = Path.Combine(_tmp, "swarmcode");
        Directory.CreateDirectory(Path.Combine(swarmCode, "SwarmCode"));
        File.WriteAllText(Path.Combine(swarmCode, "SwarmCode", "SwarmCode.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(swarmCode, "SwarmCode.Hosting.Tests"));
        File.WriteAllText(Path.Combine(swarmCode, "SwarmCode.Hosting.Tests", "SwarmCode.Hosting.Tests.csproj"), "<Project />");
        var checks = SelfImprovementChecks.Discover(swarmCode);
        Assert.Equal(["build Talavine Code", "test SwarmCode.Hosting.Tests"], checks.Select(c => c.Name));
        Assert.All(checks, c => Assert.Equal("dotnet", c.FileName));
        Assert.Contains("SwarmCode/SwarmCode.csproj", checks[0].Arguments);

        var other = Path.Combine(_tmp, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "App.sln"), "");
        Assert.Equal(["build", "test"], SelfImprovementChecks.Discover(other).Select(c => c.Name));
        Assert.Empty(SelfImprovementChecks.Discover(Path.Combine(_tmp, "missing")));

        var prompt = SelfImprovementPrompts.Cycle(new SelfImprovementItem
        {
            Id = "abc", Kind = SelfImprovementKind.Defect, Title = "Crash", Fingerprint = "f", Details = "It broke.", Occurrences = 3,
            FirstSeenUtc = new DateTime(2026, 10, 15), LastSeenUtc = new DateTime(2026, 10, 15),
        }, checks);
        Assert.Contains("Fix a defect", prompt);
        Assert.Contains("Seen: 3 times", prompt);
        Assert.Contains("Do not commit", prompt);
        Assert.Contains("build Talavine Code: dotnet build", prompt);
    }
}
