using System.Diagnostics;
using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCode.Hosting.Improvement;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Tests;

public sealed class FailedGoalRepairActivationTests : TempDirTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class OldAppVerified : IFailedRepairActivationVerifier
    {
        public bool Ready = true;
        public int Calls;
        public Func<Task>? BeforeReturn;
        public async Task<bool> VerifyAsync(RelaunchStatus status, CancellationToken ct = default)
        { ++Calls; if (BeforeReturn is not null) await BeforeReturn(); return Ready; }
    }
    private sealed class NeverInstalled : ISelfImprovementInstalledFixVerifier
    { public Task<string?> VerifyAsync(SelfImprovementItem item, CancellationToken ct = default) => Task.FromResult<string?>(null); }
    private sealed class RetryHost(string root) : ISelfImprovementCycleHost
    {
        public string? Prompt;
        public string? ResolveRepositoryRoot() => root;
        public Task<SelfImprovementCycleResult> RunAsync(SelfImprovementItem item, SelfImprovementCycleContext context, CancellationToken ct)
        {
            Prompt = SelfImprovementPrompts.Cycle(item, []);
            return Task.FromResult(new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Verified,
                "Installation failure repaired and regression checked.", "Independent checks passed.", "next-repair", new string('b', 40)));
        }
    }

    private async Task<(FileCodingGoalStore Goals, ContinualObjectiveSnapshot Goal, JsonFileSelfImprovementBacklog Backlog,
        SelfImprovementItem Item, SelfImprovementOptions Options, string Source)> SetupAsync()
    {
        var source = Path.Combine(Tmp, "source"); Directory.CreateDirectory(source);
        var options = new SelfImprovementOptions { RepositoryRoot = source, AutoApplyGoalRepairs = true,
            UserIdleAfter = TimeSpan.Zero, StartupDelay = TimeSpan.Zero };
        var goals = new FileCodingGoalStore(Paths); var now = DateTimeOffset.UtcNow;
        var goal = await goals.CreateOrJoinAsync(new()
        {
            Surface = "coding", PartitionKey = CodingGoalOrchestrator.Partition, ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            IdempotencyKey = Guid.NewGuid().ToString("N"), ConversationId = Guid.NewGuid().ToString("N"),
            ObjectiveText = "Finish the remaining synthetic goal.", ObjectiveSummary = "Synthetic goal",
            Authority = ContinualObjectiveAuthority.UserRequested("fixture"),
            ExecutorPayloadJson = JsonSerializer.Serialize(new CodingGoalPayload([], source)),
        }, now);
        var lease = (await goals.TryClaimAsync(goal.Id, goal.Version, "previous-goal-owner", now, now.AddMinutes(1)))!;
        Assert.True(await goals.UpsertReceiptAsync(goal.Id, lease.Token, new(goal.Id, 1, 1, "prior-unknown-send", "fixture.send",
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ExternalSend, "{}", "Unknown prior delivery", true, now, now)));
        Assert.True(await goals.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.Uncertain, now, now, null,
            "Saved goal output", "InvalidOperationException: synthetic harness defect", null, "Inspect exact previous outcomes.", 0)));
        goal = (await goals.GetAsync(goal.Id))!;
        var backlog = new JsonFileSelfImprovementBacklog(Paths.SelfImprovementDirectory);
        var item = (await new GoalSelfImprovementCoordinator(new(backlog, () => options), goals, new NeverInstalled()).ObserveAsync(goal))!;
        item = (await backlog.UpdateAsync(item.Id, fresh => fresh with { Stage = SelfImprovementStage.Merged,
            Commit = new string('a', 40), Evidence = "Independent source checks passed.", Attempts = 2, LastAttemptUtc = DateTime.UtcNow.AddMinutes(-10) }))!;
        return (goals, goal, backlog, item, options, source);
    }

    private RelaunchStatus Failed(SelfImprovementItem item, string source) => new(DateTime.UtcNow.AddMinutes(-5),
        [item.Title], "fixture native build", 9, DateTime.UtcNow.AddMinutes(-3), Path.Combine(Paths.SelfImprovementDirectory, "relaunch.log"))
    {
        AutomaticGoalRepair = true, AttemptId = Guid.NewGuid().ToString("N"), ProfileRoot = Paths.RootDirectory,
        SourceRoot = source, ArtifactRoot = Path.Combine(Tmp, "old.app", "Contents"), ExitedProcessId = int.MaxValue,
        RepairedItems = [RelaunchInstalledFixVerifier.Binding(item)], PreviousArtifactHashes = new Dictionary<string, string> { ["old"] = new string('c', 64) },
        FallbackVerifiedAtUtc = DateTime.UtcNow.AddMinutes(-2), FailurePhase = "build",
    };

    private async Task Save(RelaunchStatus status)
    {
        Directory.CreateDirectory(Paths.SelfImprovementDirectory);
        var path = Path.Combine(Paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(status, Json)); File.Move(temporary, path, overwrite: true);
    }

    [Fact]
    public async Task Failed_activation_requeues_once_across_restart_and_the_existing_worker_repairs_again_beyond_ordinary_attempt_limit()
    {
        var f = await SetupAsync(); var status = Failed(f.Item, f.Source); await Save(status);
        var feedback = new GoalRepairActivationFeedback(Paths, f.Backlog, f.Goals, () => f.Options, verifier: new OldAppVerified());
        var before = JsonSerializer.Serialize(f.Goal);
        Assert.Equal(1, await feedback.RequeueAsync());
        Assert.Equal(0, await new GoalRepairActivationFeedback(Paths, new JsonFileSelfImprovementBacklog(Paths.SelfImprovementDirectory),
            new FileCodingGoalStore(Paths), () => f.Options, verifier: new OldAppVerified()).RequeueAsync());
        var queued = (await f.Backlog.GetAsync(f.Item.Id))!;
        Assert.Equal(SelfImprovementStage.Queued, queued.Stage); Assert.Equal(status.AttemptId, queued.ActivationFailure!.AttemptId);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Goals.GetAsync(f.Goal.Id)));
        Assert.True(MissionTransitions.NeedsInspection(Assert.Single((await f.Goals.GetAsync(f.Goal.Id))!.Receipts)));
        var host = new RetryHost(f.Source);
        await using var worker = new SelfImprovementWorker(f.Backlog, host, new SelfImprovementIdleGate(() => f.Options), () => f.Options);
        await worker.TickAsync();
        for (var i = 0; i < 100 && (await f.Backlog.GetAsync(f.Item.Id))!.Stage != SelfImprovementStage.Verified; ++i) await Task.Delay(20);
        var repaired = (await f.Backlog.GetAsync(f.Item.Id))!;
        Assert.Equal(SelfImprovementStage.Verified, repaired.Stage); Assert.Equal(3, repaired.Attempts);
        Assert.Contains("failed during build (exit 9)", host.Prompt); Assert.Contains("Do not repeat the goal's external actions", host.Prompt);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("resume")]
    [InlineData("edit")]
    [InlineData("cancel")]
    [InlineData("disabled")]
    [InlineData("paused-repairs")]
    [InlineData("dismissed")]
    [InlineData("active")]
    [InlineData("wrong-item")]
    [InlineData("wrong-commit")]
    [InlineData("wrong-evidence")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-time")]
    [InlineData("manual")]
    [InlineData("unverified-old-app")]
    public async Task Failure_feedback_respects_current_controls_and_exact_host_binding(string change)
    {
        var f = await SetupAsync(); var status = Failed(f.Item, f.Source); var paused = false;
        var verifier = new OldAppVerified();
        if (change == "stop") await f.Goals.ControlAsync(f.Goal.Id, f.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow);
        if (change == "resume" || change == "active") await f.Goals.ControlAsync(f.Goal.Id, f.Goal.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow);
        if (change == "active")
        { var current = (await f.Goals.GetAsync(f.Goal.Id))!; Assert.NotNull(await f.Goals.TryClaimAsync(current.Id, current.Version, "other-owner", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))); }
        if (change == "edit") await f.Goals.UpdateAsync(f.Goal.Id, f.Goal.Version, "A newer objective", DateTimeOffset.UtcNow);
        if (change == "cancel") await f.Goals.ControlAsync(f.Goal.Id, f.Goal.Version, CodingGoalControl.Cancel, DateTimeOffset.UtcNow);
        if (change == "disabled") f.Options.AutoApplyGoalRepairs = false;
        if (change == "paused-repairs") paused = true;
        if (change == "dismissed") await f.Backlog.UpdateAsync(f.Item.Id, fresh => fresh with { Stage = SelfImprovementStage.Dismissed });
        if (change == "wrong-item") status = status with { RepairedItems = [status.RepairedItems[0] with { Id = "unrelated-item" }] };
        if (change == "wrong-commit") status = status with { RepairedItems = [status.RepairedItems[0] with { Commit = new string('d', 40) }] };
        if (change == "wrong-evidence") status = status with { RepairedItems = [status.RepairedItems[0] with { EvidenceDigest = new string('d', 64) }] };
        if (change == "wrong-profile") status = status with { ProfileRoot = Path.Combine(Tmp, "another-profile") };
        if (change == "wrong-time") status = status with { StartedUtc = f.Item.LastAttemptUtc!.Value.AddMinutes(-1) };
        if (change == "manual") status = status with { AutomaticGoalRepair = false };
        if (change == "unverified-old-app") verifier.Ready = false;
        await Save(status);
        var beforeGoal = JsonSerializer.Serialize(await f.Goals.GetAsync(f.Goal.Id));
        var beforeItem = JsonSerializer.Serialize(await f.Backlog.GetAsync(f.Item.Id));
        Assert.Equal(0, await new GoalRepairActivationFeedback(Paths, f.Backlog, f.Goals, () => f.Options, () => paused, verifier).RequeueAsync());
        Assert.Equal(beforeGoal, JsonSerializer.Serialize(await f.Goals.GetAsync(f.Goal.Id)));
        Assert.Equal(beforeItem, JsonSerializer.Serialize(await f.Backlog.GetAsync(f.Item.Id)));
    }

    [Fact]
    public async Task Stop_while_independent_old_artifact_verification_waits_wins_without_consuming_the_failure_token()
    {
        var f = await SetupAsync(); var status = Failed(f.Item, f.Source); await Save(status);
        var verifier = new OldAppVerified { BeforeReturn = async () => { Assert.True(await f.Goals.ControlAsync(f.Goal.Id,
            f.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow)); } };
        Assert.Equal(0, await new GoalRepairActivationFeedback(Paths, f.Backlog, f.Goals, () => f.Options, verifier: verifier).RequeueAsync());
        Assert.Equal(SelfImprovementStage.Merged, (await f.Backlog.GetAsync(f.Item.Id))!.Stage);
        Assert.Null((await f.Backlog.GetAsync(f.Item.Id))!.ActivationFailure);
    }

    private static async Task<string> Run(string executable, string cwd, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await output + await error); return (await output).Trim();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_installed_mac_build_failure_restarts_only_the_exact_signed_old_app_and_retains_failed_status(bool tampered)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var f = await SetupAsync(); var bundle = Path.Combine(Tmp, "installed", "Talavine Code.app");
        var contents = Path.Combine(bundle, "Contents"); var executable = Path.Combine(contents, "MacOS", "SwarmCode.App");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        var program = Path.Combine(Tmp, "old-app.c");
        await File.WriteAllTextAsync(program, "#include <stdio.h>\n#include <stdlib.h>\nint main(){char p[4096];const char*h=getenv(\"SWARMCODE_HOME\");if(!h)return 2;snprintf(p,sizeof(p),\"%s/fallback-alive.txt\",h);FILE*f=fopen(p,\"w\");if(!f)return 3;fputs(h,f);fclose(f);return 0;}\n");
        // Match the compiler selected by xcode-select to its own SDK, rather than
        // the independently installed CommandLineTools SDK.
        var sdk = await Run("/usr/bin/xcrun", Tmp, "--sdk", "macosx", "--show-sdk-path");
        await Run("/usr/bin/clang", Tmp, "-isysroot", sdk, program, "-o", executable);
        await File.WriteAllTextAsync(Path.Combine(contents, "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>test.talavine.repair.fixture</string><key>CFBundleExecutable</key><string>SwarmCode.App</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>1.0.7</string></dict></plist>");
        await Run("/usr/bin/codesign", Tmp, "--force", "--deep", "--sign", "-", "--timestamp=none", bundle);
        await Run("/usr/bin/codesign", Tmp, "--verify", "--deep", "--strict", bundle);
        var original = InstalledArtifactSnapshot.Capture(contents);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "build.sh"), "exit 9\n");
        await File.WriteAllTextAsync(Path.Combine(f.Source, "repair.txt"), "before");
        await Run("git", f.Source, "init", "-q", "-b", "main");
        await Run("git", f.Source, "-c", "user.name=Test", "-c", "user.email=test@example.com", "add", "-A");
        await Run("git", f.Source, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "Base");
        await Run("git", f.Source, "checkout", "-q", "-b", "repair"); await File.WriteAllTextAsync(Path.Combine(f.Source, "repair.txt"), "verified source fix");
        await Run("git", f.Source, "add", "repair.txt"); await Run("git", f.Source, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "Verified source fix");
        var commit = await Run("git", f.Source, "rev-parse", "HEAD"); await Run("git", f.Source, "checkout", "-q", "main");
        await f.Backlog.UpdateAsync(f.Item.Id, fresh => fresh with { Stage = SelfImprovementStage.Verified, Commit = commit, Branch = "repair" });
        var rebuild = new SwarmCodeRebuild(Paths, () => f.Options, f.Backlog);
        Assert.True(Assert.Single(await rebuild.MergeVerifiedAsync(f.Source, itemIds: new HashSet<string> { f.Item.Id })).Merged);
        using var departed = Process.Start("/usr/bin/true")!; await departed.WaitForExitAsync();
        var script = rebuild.WriteRelaunchScript(new(true, "fixture", f.Source, executable, "macos-appkit", "/bin/bash", [Path.Combine(f.Source, "build.sh")])
        { InstallBundlePath = bundle, StagingArtifactsPath = Path.Combine(Tmp, "staged") }, departed.Id, [], waitSeconds: 1);
        var generated = await File.ReadAllTextAsync(script);
        var reopenedGuard = generated.IndexOf("target_not_running || { echo 'The app was reopened", StringComparison.Ordinal);
        Assert.True(reopenedGuard >= 0 && reopenedGuard < generated.IndexOf("mv \"$target\" \"$backup\"", StringComparison.Ordinal));
        if (tampered) await File.AppendAllTextAsync(Path.Combine(contents, "Info.plist"), " ");
        using var helper = rebuild.StartRelaunchTracked(script); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(9, helper.ExitCode); var failed = rebuild.ReadStatus()!;
        Assert.Equal(9, failed.BuildExitCode);
        if (tampered)
        {
            Assert.Null(failed.FallbackVerifiedAtUtc); Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "fallback-alive.txt")));
            Assert.Equal(0, await new GoalRepairActivationFeedback(Paths, f.Backlog, f.Goals, () => f.Options, verifier: new OldAppVerified()).RequeueAsync());
            return;
        }
        Assert.NotNull(failed.FallbackVerifiedAtUtc); Assert.Equal("build", failed.FailurePhase);
        var marker = Path.Combine(Paths.RootDirectory, "fallback-alive.txt");
        for (var i = 0; i < 100 && !File.Exists(marker); ++i) await Task.Delay(20);
        Assert.Equal(Paths.RootDirectory, await File.ReadAllTextAsync(marker));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(InstalledArtifactSnapshot.Capture(contents)));
        Assert.Equal(RelaunchInstalledFixVerifier.Binding((await f.Backlog.GetAsync(f.Item.Id))!), Assert.Single(failed.RepairedItems));
        Assert.True(failed.RepairedItems[0].GoalRepair!.CanResume((await f.Goals.GetAsync(f.Goal.Id))!, DateTimeOffset.UtcNow));
        var checkVerifier = new OldAppVerified();
        var requeued = await new GoalRepairActivationFeedback(Paths, f.Backlog, f.Goals, () => f.Options, verifier: checkVerifier).RequeueAsync();
        Assert.Equal(1, checkVerifier.Calls);
        Assert.Equal(1, requeued);
        Assert.Equal(SelfImprovementStage.Queued, (await f.Backlog.GetAsync(f.Item.Id))!.Stage);
        // A failed build can never satisfy the successful installed-fix verifier.
        Assert.Null(await new RelaunchInstalledFixVerifier(Paths).VerifyAsync((await f.Backlog.GetAsync(f.Item.Id))!));
    }
    [Theory]
    [InlineData("crash", 128)]
    [InlineData("wrong-attempt", 128)]
    [InlineData("ready", 0)]
    [InlineData("timeout", 128)]
    public async Task Automatic_startup_supervisor_requires_a_live_child_and_exact_host_acknowledgment(string behavior, int expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var f = await SetupAsync(); var status = Failed(f.Item, f.Source) with
        { BuildExitCode = null, FinishedUtc = null, FallbackVerifiedAtUtc = null, ArtifactHashes = new Dictionary<string, string> { ["fixture"] = new string('e', 64) } };
        await Save(status);
        var executable = Path.Combine(Tmp, "candidate");
        var program = "#!/usr/bin/env python3\nimport datetime,json,os,pathlib,time\np=pathlib.Path(os.environ['SWARMCODE_HOME'])/'self-improvement'/'relaunch.json'\nd=json.loads(p.read_text())\n";
        if (behavior == "timeout") program += "(p.parent/'candidate-pid.txt').write_text(str(os.getpid()))\ntime.sleep(60)\n";
        else if (behavior != "crash")
            program += "a={'attemptId':" + (behavior == "wrong-attempt" ? "'another-attempt'" : "d['attemptId']") +
                ", 'processId':os.getpid(),'artifactHashes':d['artifactHashes'],'acknowledgedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()}\n" +
                "(p.parent/('startup-'+d['attemptId']+'.json')).write_text(json.dumps(a))\ntime.sleep(" + (behavior == "ready" ? "20" : ".3") + ")\n";
        await File.WriteAllTextAsync(executable, program);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var supervisor = Path.Combine(Tmp, "startup.py"); await File.WriteAllTextAsync(supervisor, SwarmCodeRebuild.MacStartupSupervisor);
        var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true };
        start.Environment["SWARMCODE_HOME"] = Paths.RootDirectory;
        foreach (var arg in new[] { supervisor, Path.Combine(Paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName), executable }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); Assert.Equal(expected, process.ExitCode);
        if (behavior == "timeout")
        {
            var childPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(Paths.SelfImprovementDirectory, "candidate-pid.txt")));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(childPid));
        }
        var current = JsonSerializer.Deserialize<RelaunchStatus>(await File.ReadAllTextAsync(Path.Combine(Paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName)), Json)!;
        if (behavior == "ready")
        {
            Assert.Equal(0, current.BuildExitCode); Assert.NotNull(current.StartupAcknowledgedAtUtc);
            using var child = Process.GetProcessById(current.StartedProcessId!.Value);
            Assert.False(child.HasExited); child.Kill(); await child.WaitForExitAsync();
        }
        else { Assert.Null(current.BuildExitCode); Assert.Null(current.StartupAcknowledgedAtUtc); }
        // A synthetic executable/ack cannot make this unrelated testhost claim its modules are the installed artifact.
        Assert.False(await GoalRepairStartupAcknowledgment.TryAcknowledgeAsync(Paths));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("attempt")]
    [InlineData("manifest")]
    public async Task Restored_runner_waits_for_its_exact_pending_startup_then_observes_terminal_status_or_refuses_another_attempt(string replacement)
    {
        var f = await SetupAsync(); var pending = Failed(f.Item, f.Source) with { BuildExitCode = null, FinishedUtc = null, ArtifactHashes = new Dictionary<string, string> { ["fixture"] = new string('f', 64) } };
        await Save(pending);
        var waiting = new RelaunchInstalledFixVerifier(Paths).WaitForStartupCompletionAsync(pending, default, TimeSpan.FromSeconds(3));
        await Task.Delay(150);
        await Save(pending with { BuildExitCode = 0, FinishedUtc = DateTime.UtcNow,
            AttemptId = replacement == "attempt" ? Guid.NewGuid().ToString("N") : pending.AttemptId,
            ArtifactHashes = replacement == "manifest" ? new Dictionary<string, string> { ["fixture"] = new string('a', 64) } : pending.ArtifactHashes });
        var result = await waiting;
        if (replacement != "same") Assert.Null(result);
        else { Assert.NotNull(result); Assert.Equal(0, result.BuildExitCode); Assert.Equal(pending.AttemptId, result.AttemptId); }
    }

    [Fact]
    public async Task Pending_startup_wait_is_cancellable_and_has_a_bounded_timeout()
    {
        var f = await SetupAsync(); var pending = Failed(f.Item, f.Source) with { BuildExitCode = null, FinishedUtc = null, ArtifactHashes = new Dictionary<string, string> { ["fixture"] = new string('f', 64) } };
        await Save(pending); var verifier = new RelaunchInstalledFixVerifier(Paths);
        Assert.Null(await verifier.WaitForStartupCompletionAsync(pending, default, TimeSpan.FromMilliseconds(150)));
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.WaitForStartupCompletionAsync(pending, cancellation.Token));
    }

    private sealed class RacingBacklog(ISelfImprovementBacklog owner) : ISelfImprovementBacklog
    {
        public Task<IReadOnlyList<SelfImprovementItem>> ListAsync(CancellationToken ct = default) => owner.ListAsync(ct);
        public Task<SelfImprovementItem?> GetAsync(string id, CancellationToken ct = default) => owner.GetAsync(id, ct);
        public Task<(SelfImprovementItem Item, bool Created)> UpsertByFingerprintAsync(string fingerprint, Func<SelfImprovementItem> create,
            Func<SelfImprovementItem, SelfImprovementItem?> update, CancellationToken ct = default) => owner.UpsertByFingerprintAsync(fingerprint, create, update, ct);
        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => owner.DeleteAsync(id, ct);
        public async Task<SelfImprovementItem?> UpdateAsync(string id, Func<SelfImprovementItem, SelfImprovementItem?> change, long? expectedRevision = null, CancellationToken ct = default)
        {
            await owner.UpdateAsync(id, fresh => fresh with { Stage = SelfImprovementStage.Dismissed, Note = "Newer human dismissal" }, ct: ct);
            return await owner.UpdateAsync(id, change, expectedRevision, ct);
        }
    }

    [Fact]
    public async Task Competing_backlog_writer_wins_failed_activation_feedback_CAS_without_consuming_attempt_token()
    {
        var f = await SetupAsync(); await Save(Failed(f.Item, f.Source));
        Assert.Equal(0, await new GoalRepairActivationFeedback(Paths, new RacingBacklog(f.Backlog), f.Goals,
            () => f.Options, verifier: new OldAppVerified()).RequeueAsync());
        var current = (await f.Backlog.GetAsync(f.Item.Id))!;
        Assert.Equal(SelfImprovementStage.Dismissed, current.Stage); Assert.Null(current.ActivationFailure);
        Assert.Equal("Newer human dismissal", current.Note);
    }

    [Fact]
    public async Task Successful_host_acknowledgment_survives_a_later_restart_only_with_a_post_install_process_baseline()
    {
        var f = await SetupAsync(); var failed = Failed(f.Item, f.Source);
        var status = failed with { BuildExitCode = 0, StartedProcessId = 1001, ExitedProcessId = 1000,
            StartupAcknowledgedAtUtc = failed.FinishedUtc!.Value.AddSeconds(-1) };
        Assert.Equal(status.StartedUtc.ToUniversalTime(), RelaunchInstalledFixVerifier.StartupProofBaseline(status, 1001));
        Assert.Equal(status.FinishedUtc!.Value.ToUniversalTime(), RelaunchInstalledFixVerifier.StartupProofBaseline(status, 1002));
        Assert.Null(RelaunchInstalledFixVerifier.StartupProofBaseline(status with { StartedProcessId = 1000 }, 1002));
        Assert.Null(RelaunchInstalledFixVerifier.StartupProofBaseline(status with { StartupAcknowledgedAtUtc = status.StartedUtc.AddSeconds(-1) }, 1002));
        Assert.Null(RelaunchInstalledFixVerifier.StartupProofBaseline(status with { StartupAcknowledgedAtUtc = status.FinishedUtc!.Value.AddSeconds(1) }, 1002));
        Assert.Null(RelaunchInstalledFixVerifier.StartupProofBaseline(status with { BuildExitCode = 128 }, 1002));
    }

    [Fact]
    public async Task Reopened_exact_app_is_detected_without_stopping_it_before_automatic_replacement()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var executable = Path.Combine(Tmp, "reopened-app"); File.Copy("/bin/sleep", executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Relocated Apple platform binaries need a fresh signature on current macOS.
        await Run("/usr/bin/codesign", Tmp, "--force", "--sign", "-", executable);
        using var reopened = Process.Start(executable, "30")!;
        try
        {
            await Task.Delay(100);
            Assert.False(reopened.HasExited);
            var probe = Path.Combine(Tmp, "process-inventory.py"); await File.WriteAllTextAsync(probe, SwarmCodeRebuild.TargetProcessInventory);
            var start = new ProcessStartInfo("python3") { UseShellExecute = false };
            start.ArgumentList.Add(probe); start.ArgumentList.Add(executable);
            using var inventory = Process.Start(start)!; await inventory.WaitForExitAsync();
            Assert.Equal(1, inventory.ExitCode); Assert.False(reopened.HasExited);
        }
        finally { if (!reopened.HasExited) { reopened.Kill(); await reopened.WaitForExitAsync(); } }
    }

}
