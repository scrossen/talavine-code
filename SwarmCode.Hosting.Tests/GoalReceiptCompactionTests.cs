using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Goals;
using SwarmCode.Hosting.Memory;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;
using SwarmCore.PersonalMemory;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalReceiptCompactionTests : TempDirTest
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTime _time = new(T0);
    private FileCodingGoalStore Store() => new(Paths, _time);
    private FileCodingGoalStore Store(SwarmCodeDataPaths paths) => new(paths, _time);
    private static GoalReceiptCompactionOptions Eager() => new() { VacuumMinimumDeadBytes = 0, VacuumMinimumDeadShare = 0 };

    [Fact]
    public async Task A_finished_goal_keeps_full_texts_for_a_week_then_keeps_digests_and_drops_its_log()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        var longResult = "first line ✓\n" + new string('r', 50_000);
        var longInput = "{\"content\":\"" + new string('i', 20_000) + "\"}";
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 1, "write-1", result: longResult)));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 2, "write-2", input: longInput, result: "ok")));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 3, "write-3", result: "short")));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, Transition(ContinualObjectiveStatus.Completed)));
        var before = (await store.GetAsync(goal.Id))!;
        var untouched = ContinualReceiptResolutionPolicy.Fingerprint(before.Receipts[2]);
        var log = BodyLog(Paths, goal.Id);
        Assert.True(File.Exists(log));

        _time.Now = T0.AddDays(6);
        var early = (await store.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.Equal(ContinualReceiptRetentionHold.None, early.GoalHold);
        Assert.Equal(0, early.ReceiptsCompacted);
        Assert.False(early.LogRewritten);
        Assert.Equal(3, early.Held[ContinualReceiptRetentionHold.TooRecent]);
        Assert.Equal(longResult, (await Store(ColdPaths()).GetAsync(goal.Id))!.Receipts[0].Result);

        _time.Now = T0.AddDays(8);
        var inventoryBefore = new FileInfo(Inventory(Paths)).Length;
        var result = (await store.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.Equal(2, result.ReceiptsCompacted);
        Assert.Equal(Encoding.UTF8.GetByteCount(longResult) + Encoding.UTF8.GetByteCount(longInput), result.BytesCompacted);
        Assert.True(result.LogRewritten);
        Assert.Equal(0, result.LogBytesAfter);
        Assert.False(File.Exists(log), "every retained body of the goal was compacted, so its log should be gone");
        Assert.True(new FileInfo(Inventory(Paths)).Length < inventoryBefore + 3_500, $"the inventory should hold only the excerpts and digests ({inventoryBefore} -> {new FileInfo(Inventory(Paths)).Length})");

        foreach (var reader in new[] { store, Store(ColdPaths()) })
        {
            var saved = (await reader.GetAsync(goal.Id))!;
            Assert.Equal(before.Version, saved.Version);
            Assert.Equal(before.UpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status);
            var compacted = saved.Receipts[0];
            Assert.True(ContinualReceiptRetentionPolicy.IsCompactedResult(compacted.Result));
            Assert.Equal(Sha(longResult), compacted.CompactedResult!.Sha256);
            Assert.Equal(Encoding.UTF8.GetByteCount(longResult), compacted.CompactedResult.ByteLength);
            Assert.Equal(_time.Now, compacted.CompactedResult.CompactedAtUtc);
            Assert.EndsWith(ContinualReceiptRetentionPolicy.Excerpt(longResult, 1024), compacted.Result);
            Assert.Equal("{}", compacted.InputJson);
            Assert.Null(compacted.CompactedInput);
            var inputCompacted = saved.Receipts[1];
            Assert.Equal("ok", inputCompacted.Result);
            Assert.Equal(Sha(longInput), inputCompacted.CompactedInput!.Sha256);
            Assert.Contains(ContinualReceiptRetentionPolicy.InputMarkerProperty, inputCompacted.InputJson);
            Assert.Equal(untouched, ContinualReceiptResolutionPolicy.Fingerprint(saved.Receipts[2]));
            Assert.Equal("short", saved.Receipts[2].Result);
        }
        Assert.DoesNotContain(FileCodingGoalStore.UnavailableBodies, key => key.StartsWith(goal.Id.ToString("N"), StringComparison.Ordinal));
        var node = JsonNode.Parse(File.ReadAllText(Inventory(Paths)))!.AsObject();
        Assert.Empty(node["bodies"]!.AsObject());
        var run = Assert.Single(await store.GetCompactionJournalAsync());
        Assert.Equal(goal.Id, run.GoalId); Assert.Equal(2, run.Receipts); Assert.Equal(_time.Now, run.AtUtc);
        Assert.True(run.LogBytesBefore > 70_000); Assert.Equal(0, run.LogBytesAfter);
        Assert.Equal(run, Assert.Single(await Store(ColdPaths()).GetCompactionJournalAsync()));

        // A second pass changes nothing, byte for byte.
        var bytes = File.ReadAllBytes(Inventory(Paths));
        var again = (await store.CompactAsync(goal.Id, Eager(), _time.Now.AddDays(1)))!;
        Assert.False(again.Changed);
        Assert.Equal(bytes, File.ReadAllBytes(Inventory(Paths)));
    }

    [Fact]
    public async Task A_working_or_uncertain_goal_is_refused_whole_whatever_the_age()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 1, "write-1", result: new string('a', 30_000))));
        var inventory = File.ReadAllBytes(Inventory(Paths)); var log = File.ReadAllBytes(BodyLog(Paths, goal.Id));

        _time.Now = T0.AddDays(45);
        var working = (await store.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.Equal(ContinualReceiptRetentionHold.GoalActive, working.GoalHold);
        Assert.False(working.Changed);
        Assert.Equal(inventory, File.ReadAllBytes(Inventory(Paths)));
        Assert.Equal(log, File.ReadAllBytes(BodyLog(Paths, goal.Id)));

        var current = (await store.GetAsync(goal.Id))!;
        Assert.True(await store.HoldAsync(goal.Id, current.Version, "Inspect prior actions.", _time.Now));
        var uncertain = (await store.CompactAsync(goal.Id, Eager(), _time.Now.AddDays(60)))!;
        Assert.Equal(ContinualReceiptRetentionHold.GoalActive, uncertain.GoalHold);
        Assert.Equal(log, File.ReadAllBytes(BodyLog(Paths, goal.Id)));
        Assert.Equal(new string('a', 30_000), (await Store(ColdPaths()).GetAsync(goal.Id))!.Receipts[0].Result);
    }

    [Fact]
    public async Task A_paused_goal_compacts_only_old_closed_attempts_and_keeps_proofs_observations_and_the_current_attempt_intact()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease1 = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        var r1 = Receipt(goal.Id, 1, 1, "write-1", result: new string('1', 40_000));
        var r3 = Receipt(goal.Id, 1, 2, "write-3", result: new string('3', 30_000), isError: true) with { LocalProcessCompleted = false };
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease1.Token, r1));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease1.Token, r3));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease1.Token, Transition(ContinualObjectiveStatus.RetryScheduled)));

        var lease2 = (await store.TryClaimAsync(goal.Id, (await store.GetAsync(goal.Id))!.Version, "worker", T0, T0.AddMinutes(1)))!;
        var r2 = Receipt(goal.Id, 2, 3, "read-2", result: new string('2', 20_000), sideEffect: ContinualSideEffectClass.ReadOnly);
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease2.Token, r2));
        var savedR3 = (await store.GetAsync(goal.Id))!.Receipts.Single(r => r.ToolCallId == "write-3");
        var savedR2 = (await store.GetAsync(goal.Id))!.Receipts.Single(r => r.ToolCallId == "read-2");
        var proof = new ContinualReceiptResolution(goal.Id, 1, "write-3", ContinualReceiptResolutionPolicy.Fingerprint(savedR3),
            ContinualReceiptResolutionOutcome.KnownOutcome, "host-verifier", "read back the written file",
            [new(2, "read-2", ContinualReceiptResolutionPolicy.Fingerprint(savedR2))], T0);
        Assert.True(await store.ResolveReceiptAsync(goal.Id, lease2.Token, proof, T0));
        var r4 = Receipt(goal.Id, 2, 4, "write-4", result: new string('4', 40_000));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease2.Token, r4));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease2.Token, Transition(ContinualObjectiveStatus.RetryScheduled)));

        _time.Now = T0.AddDays(20);
        var lease3 = (await store.TryClaimAsync(goal.Id, (await store.GetAsync(goal.Id))!.Version, "worker", _time.Now, _time.Now.AddMinutes(1)))!;
        var r5 = Receipt(goal.Id, 3, 5, "write-5", result: new string('5', 40_000), at: _time.Now);
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease3.Token, r5));
        var active = (await store.GetAsync(goal.Id))!;
        Assert.True(await store.ControlAsync(goal.Id, active.Version, CodingGoalControl.Pause, _time.Now));
        var before = (await store.GetAsync(goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Paused, before.Status);
        Assert.Equal(3, before.AttemptCount);
        var fingerprints = before.Receipts.ToDictionary(r => r.ToolCallId, ContinualReceiptResolutionPolicy.Fingerprint);
        var logBefore = new FileInfo(BodyLog(Paths, goal.Id)).Length;

        _time.Now = T0.AddDays(31);
        var result = (await store.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.Equal(ContinualReceiptRetentionHold.None, result.GoalHold);
        Assert.Equal(2, result.ReceiptsCompacted);
        Assert.Equal(1, result.Held[ContinualReceiptRetentionHold.ProofObservation]);
        Assert.Equal(1, result.Held[ContinualReceiptRetentionHold.HostProof]);
        Assert.Equal(1, result.Held[ContinualReceiptRetentionHold.AttemptOpen]);
        Assert.True(result.LogRewritten);
        Assert.True(result.LogBytesAfter < logBefore - 70_000, $"log {logBefore} -> {result.LogBytesAfter}");
        Assert.Equal(result.LogBytesAfter, new FileInfo(BodyLog(Paths, goal.Id)).Length);

        foreach (var reader in new[] { store, Store(ColdPaths()) })
        {
            var saved = (await reader.GetAsync(goal.Id))!;
            var byCall = saved.Receipts.ToDictionary(r => r.ToolCallId);
            Assert.True(ContinualReceiptRetentionPolicy.IsCompactedResult(byCall["write-1"].Result));
            Assert.True(ContinualReceiptRetentionPolicy.IsCompactedResult(byCall["write-4"].Result));
            Assert.Equal(Sha(r1.Result!), byCall["write-1"].CompactedResult!.Sha256);
            Assert.Equal(Sha(r4.Result!), byCall["write-4"].CompactedResult!.Sha256);
            foreach (var kept in new[] { "read-2", "write-3", "write-5" })
            {
                Assert.Equal(fingerprints[kept], ContinualReceiptResolutionPolicy.Fingerprint(byCall[kept]));
                Assert.Null(byCall[kept].CompactedResult);
            }
            Assert.Equal(new string('2', 20_000), byCall["read-2"].Result);
            Assert.Equal(new string('3', 30_000), byCall["write-3"].Result);
            Assert.Equal(new string('5', 40_000), byCall["write-5"].Result);
            Assert.True(ContinualReceiptResolutionPolicy.IsResolved(byCall["write-3"], saved.Receipts), "the host proof must still verify after compaction");
            Assert.DoesNotContain(saved.Receipts, r => ContinualReceiptResolutionPolicy.NeedsInspection(r, saved.Receipts));
            Assert.Equal(before.Version, saved.Version);
            Assert.Equal(before.UpdatedAtUtc, saved.UpdatedAtUtc);
        }
        Assert.DoesNotContain(FileCodingGoalStore.UnavailableBodies, key => key.StartsWith(goal.Id.ToString("N"), StringComparison.Ordinal));
        var bodies = JsonNode.Parse(File.ReadAllText(Inventory(Paths)))!["bodies"]!.AsObject();
        Assert.Equal(3, bodies.Count);
        Assert.False(bodies.ContainsKey($"{goal.Id:N}/1/result/write-1"));
        Assert.True(bodies.ContainsKey($"{goal.Id:N}/2/result/read-2"));
        var run = Assert.Single(await store.GetCompactionJournalAsync());
        Assert.Equal(2, run.Receipts); Assert.Equal(logBefore, run.LogBytesBefore); Assert.Equal(result.LogBytesAfter, run.LogBytesAfter);
        Assert.Contains("30 days", run.Policy);

        // Running again is a no-op; the log is not rewritten and the inventory bytes stand.
        var bytes = File.ReadAllBytes(Inventory(Paths));
        var again = (await store.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.False(again.Changed);
        Assert.Equal(bytes, File.ReadAllBytes(Inventory(Paths)));
        Assert.Single(await store.GetCompactionJournalAsync());
    }

    [Fact]
    public async Task Dead_records_from_superseded_texts_are_vacuumed_only_past_the_thresholds()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        // The same call saved twice with different long texts leaves the first record dead in the log.
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 1, "write-1", result: new string('a', 30_000), state: ContinualToolReceiptState.Started) with { Result = new string('a', 30_000) }));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 2, "write-1", result: new string('b', 30_000))));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, Transition(ContinualObjectiveStatus.Completed)));
        var log = BodyLog(Paths, goal.Id);
        var length = new FileInfo(log).Length;
        Assert.True(length > 60_000);

        _time.Now = T0.AddDays(1);
        var conservative = (await store.CompactAsync(goal.Id, new GoalReceiptCompactionOptions(), _time.Now))!;
        Assert.False(conservative.LogRewritten);
        Assert.Equal(length, new FileInfo(log).Length);

        var eager = (await store.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.True(eager.LogRewritten);
        Assert.Equal(0, eager.ReceiptsCompacted);
        Assert.True(new FileInfo(log).Length < 31_000);
        Assert.Equal(new string('b', 30_000), (await Store(ColdPaths()).GetAsync(goal.Id))!.Receipts.Single().Result);
        var run = Assert.Single(await store.GetCompactionJournalAsync());
        Assert.Equal(0, run.Receipts); Assert.Equal(length, run.LogBytesBefore); Assert.True(run.LogBytesAfter < length);
    }

    [Fact]
    public async Task Stale_references_after_an_interrupted_log_rewrite_are_repaired_from_the_framed_log()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        var texts = new[] { new string('a', 5_000), new string('b', 6_000), new string('c', 7_000) };
        for (var index = 0; index < texts.Length; index++)
            Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, index + 1, "write-" + index, result: texts[index])));
        var copy = ColdPaths();
        // What an interrupted vacuum leaves: a rewritten log (here, the records in another order) while the inventory still has the old offsets.
        var node = JsonNode.Parse(File.ReadAllText(Inventory(copy)))!.AsObject();
        var bodies = node["bodies"]!.AsObject();
        var original = File.ReadAllBytes(BodyLog(copy, goal.Id));
        using (var rewritten = new FileStream(BodyLog(copy, goal.Id), FileMode.Create, FileAccess.Write))
            foreach (var key in bodies.Select(pair => pair.Key).Reverse())
            {
                var offset = (int)bodies[key]!["offset"]!.GetValue<long>(); var length = bodies[key]!["length"]!.GetValue<int>();
                rewritten.Write(Encoding.UTF8.GetBytes("SWARMCODE-RECEIPT-BODY\n" + key + "\n" + length + "\n"));
                rewritten.Write(original, offset, length);
                rewritten.Write("\n"u8);
            }
        var reader = Store(copy);
        var recovered = (await reader.GetAsync(goal.Id))!;
        Assert.Equal(texts, recovered.Receipts.Select(r => r.Result).ToArray());
        Assert.DoesNotContain(FileCodingGoalStore.UnavailableBodies, key => key.StartsWith(goal.Id.ToString("N"), StringComparison.Ordinal));
        // The next write persists the repaired offsets: every saved reference now points at bytes with its own hash.
        Assert.True(await reader.HeartbeatAsync(goal.Id, lease.Token, T0, T0.AddMinutes(2)));
        var saved = JsonNode.Parse(File.ReadAllText(Inventory(copy)))!["bodies"]!.AsObject();
        var log = File.ReadAllBytes(BodyLog(copy, goal.Id));
        foreach (var pair in saved)
        {
            var offset = (int)pair.Value!["offset"]!.GetValue<long>(); var length = pair.Value["length"]!.GetValue<int>();
            Assert.Equal(pair.Value["sha256"]!.GetValue<string>(), Convert.ToHexString(SHA256.HashData(log.AsSpan(offset, length))), ignoreCase: true);
        }
        Assert.Equal(texts, (await Store(ColdPaths(copy)).GetAsync(goal.Id))!.Receipts.Select(r => r.Result).ToArray());
    }

    [Fact]
    public async Task An_unavailable_body_blocks_only_the_log_rewrite_until_the_log_is_restored()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 1, "write-1", result: new string('a', 10_000))));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 2, "write-2", result: new string('b', 10_000))));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, Transition(ContinualObjectiveStatus.Completed)));
        var damaged = ColdPaths();
        var intact = File.ReadAllBytes(BodyLog(damaged, goal.Id));
        using (var log = new FileStream(BodyLog(damaged, goal.Id), FileMode.Open, FileAccess.ReadWrite)) log.SetLength(10_200);
        var reader = Store(damaged);
        _time.Now = T0.AddDays(8);
        var result = (await reader.CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.Equal(1, result.ReceiptsCompacted);
        Assert.False(result.LogRewritten);
        Assert.Equal(10_200, new FileInfo(BodyLog(damaged, goal.Id)).Length);
        var saved = (await reader.GetAsync(goal.Id))!;
        Assert.NotNull(saved.Receipts[0].CompactedResult);
        Assert.Null(saved.Receipts[1].Result);
        File.WriteAllBytes(BodyLog(damaged, goal.Id), intact);
        var restored = (await Store(ColdPaths(damaged)).CompactAsync(goal.Id, Eager(), _time.Now))!;
        Assert.Equal(1, restored.ReceiptsCompacted);
        Assert.True(restored.LogRewritten);
        Assert.Equal(0, restored.LogBytesAfter);
    }

    [Fact]
    public void Options_bind_from_configuration_and_fall_back_to_defaults_when_invalid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Goals:ReceiptCompaction:Retention:ClosedAttemptAge"] = "90.00:00:00",
            ["Goals:ReceiptCompaction:Retention:MinimumTextLength"] = "2048",
            ["Goals:ReceiptCompaction:Interval"] = "01:00:00",
        }).Build();
        var options = GoalReceiptCompactionOptions.Read(configuration);
        Assert.Equal(TimeSpan.FromDays(90), options.Retention.ClosedAttemptAge);
        Assert.Equal(2048, options.Retention.MinimumTextLength);
        Assert.Equal(TimeSpan.FromDays(7), options.Retention.TerminalGoalAge);
        Assert.Equal(TimeSpan.FromHours(1), options.Interval);

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Goals:ReceiptCompaction:Interval"] = "00:00:00",
            ["Goals:ReceiptCompaction:VacuumMinimumDeadShare"] = "1.5",
        }).Build();
        var fallback = GoalReceiptCompactionOptions.Read(invalid);
        Assert.Equal(TimeSpan.FromHours(6), fallback.Interval);
        Assert.Equal(0.25, fallback.VacuumMinimumDeadShare);
        Assert.Equal(new GoalReceiptCompactionOptions().Retention, fallback.Retention);
    }

    [Fact]
    public async Task Inspect_reports_the_inventory_the_logs_and_every_file_the_store_does_not_use()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 1, "write-1", result: new string('a', 5_000))));
        var goals = Path.Combine(Paths.RootDirectory, "goals");
        File.WriteAllText(Path.Combine(goals, "objectives 2.json"), new string('x', 10_000));
        File.WriteAllText(Path.Combine(goals, "messages 2.json"), new string('y', 2_000));
        File.WriteAllText(Path.Combine(goals, "messages.json"), "{}");
        File.WriteAllText(Path.Combine(goals, "results.json"), "{\"results\":[]}");
        File.WriteAllText(Path.Combine(goals, "reports.json"), "{\"reports\":[]}");
        File.WriteAllText(Path.Combine(goals, "objectives.json.lock"), "");
        Directory.CreateDirectory(Path.Combine(goals, "old-copy"));
        File.WriteAllText(Path.Combine(goals, "old-copy", "objectives.json"), new string('z', 3_000));
        File.WriteAllText(Path.Combine(goals, "receipt-bodies", "notes.txt"), "stray");
        // A writer's fresh temporary file is in flight, not unused; an old one is listed.
        File.WriteAllText(Path.Combine(goals, "objectives.json." + Guid.NewGuid().ToString("N") + ".tmp"), "in flight");
        var leftover = Path.Combine(goals, "objectives.json." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(leftover, "left behind"); File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));

        var report = store.Inspect();
        Assert.Equal(goals, report.Directory);
        Assert.Equal(new FileInfo(Inventory(Paths)).Length, report.InventoryBytes);
        Assert.Equal(1, report.BodyLogs);
        Assert.Equal(new FileInfo(BodyLog(Paths, goal.Id)).Length, report.BodyLogBytes);
        Assert.Equal(["objectives 2.json", "old-copy", "messages 2.json", Path.GetFileName(leftover), "notes.txt"], report.Stray.Select(file => Path.GetFileName(file.Path)).ToArray());
        Assert.Equal(10_000 + 3_000 + 2_000 + 11 + 5, report.StrayBytes);
        Assert.All(report.Stray, file => Assert.Equal("unused", file.Kind));
    }

    [Fact]
    public async Task The_idle_step_runs_only_when_switched_on_and_once_per_interval_but_the_button_always_runs()
    {
        GoalReceiptCompaction.ResetForTests();
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), T0);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", T0, T0.AddMinutes(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, 1, "write-1", result: new string('a', 30_000))));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, Transition(ContinualObjectiveStatus.Completed)));
        var preferences = new AppPreferencesStore(Paths);
        var options = Eager();
        var step = new GoalReceiptCompaction(store, preferences, options, time: _time);
        _time.Now = T0.AddDays(10);

        Assert.False(step.IsEnabled);
        Assert.True(step.IsDue(_time.Now), "switched off, the step still gets one pass to report unused files");
        await step.RunAsync(default);
        Assert.Empty(await store.GetCompactionJournalAsync());
        Assert.False(step.IsDue(_time.Now));
        Assert.Null(GoalReceiptCompaction.LastReport);

        preferences.Update(p => p.CompactGoalReceipts = true);
        Assert.True(step.IsDue(_time.Now));
        var report = (await step.RunOnceAsync(manual: false))!;
        Assert.Equal(1, report.ReceiptsCompacted);
        Assert.Equal(1, report.GoalsChanged);
        Assert.Null(report.Error);
        Assert.Same(report, GoalReceiptCompaction.LastReport);
        Assert.False(step.IsDue(_time.Now.AddHours(1)));
        Assert.True(step.IsDue(_time.Now + options.Interval));

        preferences.Update(p => p.CompactGoalReceipts = false);
        var manual = (await step.RunOnceAsync(manual: true))!;
        Assert.True(manual.Manual);
        Assert.Equal(0, manual.ReceiptsCompacted);
        Assert.Equal(1, manual.Goals.Count);
    }

    [Fact]
    public void Idle_steps_run_after_the_startup_delay_while_no_turn_is_running_and_a_failing_step_does_not_stop_the_next()
    {
        var time = new ManualTime(T0);
        var turns = new SwarmCodeMemoryTurns(new MemoryScope("local", "m", "swarmcode"), time);
        var services = new ServiceCollection().BuildServiceProvider();
        var dreaming = new SwarmCodeDreamingOptions { StartupDelay = TimeSpan.FromSeconds(30), IdleDelay = TimeSpan.FromMinutes(2) };
        var failing = new FakeStep("failing", due: true, fail: true);
        var step = new FakeStep("compaction", due: true);
        var idle = new FakeStep("idle", due: false);
        var maintenance = new SwarmCodeMemoryMaintenance(services.GetRequiredService<IServiceScopeFactory>(), turns,
            new PersonalMemoryOptions { Enabled = false }, dreaming, time: time, steps: [failing, step, idle]);
        maintenance.StartAsync(default).GetAwaiter().GetResult();
        try
        {
            Assert.False(maintenance.IsIdle(time.Now));
            Assert.Empty(maintenance.DueSteps(time.Now));
            time.Now += TimeSpan.FromMinutes(3);
            Assert.True(maintenance.IsIdle(time.Now));
            Assert.Equal(["failing", "compaction"], maintenance.DueSteps(time.Now).Select(s => s.Name).ToArray());
            using (turns.Begin(turns.Create("c", false, "x", "t")))
                Assert.Empty(maintenance.DueSteps(time.Now));
            // Ending the turn is activity: the idle delay starts over.
            Assert.Empty(maintenance.DueSteps(time.Now));
            time.Now += TimeSpan.FromMinutes(2);
            Assert.Equal(2, maintenance.RunDueStepsAsync(default).GetAwaiter().GetResult() + failing.Failures);
            Assert.Equal(1, step.Runs);
            Assert.Equal(1, failing.Failures);
            Assert.Equal(0, idle.Runs);
        }
        finally { maintenance.StopAsync(default).GetAwaiter().GetResult(); }
    }

    private sealed class FakeStep(string name, bool due, bool fail = false) : IIdleMaintenanceStep
    {
        public int Runs; public int Failures;
        public string Name => name;
        public bool IsDue(DateTimeOffset now) => due;
        public Task RunAsync(CancellationToken ct)
        {
            if (fail) { Failures++; throw new IOException("disk"); }
            Runs++;
            return Task.CompletedTask;
        }
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Inventory(SwarmCodeDataPaths paths) => Path.Combine(paths.RootDirectory, "goals", "objectives.json");
    private static string BodyLog(SwarmCodeDataPaths paths, Guid goal) => Path.Combine(paths.RootDirectory, "goals", "receipt-bodies", goal.ToString("N") + ".log");

    /// <summary>A copy of the saved goals directory under a new root: another process's first, uncached read.</summary>
    private SwarmCodeDataPaths ColdPaths(SwarmCodeDataPaths? source = null)
    {
        source ??= Paths;
        var root = Path.Combine(Tmp, "cold-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source.RootDirectory, "goals"), "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            var target = Path.Combine(root, Path.GetRelativePath(source.RootDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        return new SwarmCodeDataPaths(root);
    }

    private static ContinualObjectiveStart Start() => new()
    {
        Surface = "coding-goal", PartitionKey = "workspace", IdempotencyKey = "manual:1",
        ConversationId = "conversation", ExecutorKey = "coding-goal",
        ObjectiveText = "Implement feature and verify it", ObjectiveSummary = "Implement feature and verify it",
        Authority = ContinualObjectiveAuthority.UserRequested("test"),
    };

    private ContinualObjectiveTransition Transition(ContinualObjectiveStatus status) => new(status,
        _time.Now, _time.Now, status == ContinualObjectiveStatus.RetryScheduled ? _time.Now : null, "done", null, "fingerprint", null, 0);

    private ContinualToolReceipt Receipt(Guid id, int attempt, long sequence, string callId, string? result = null, string? input = null,
        ContinualSideEffectClass sideEffect = ContinualSideEffectClass.WritesLocal, ContinualToolReceiptState state = ContinualToolReceiptState.Completed,
        bool isError = false, DateTimeOffset? at = null)
    {
        var when = at ?? _time.Now;
        return new(id, attempt, sequence, callId, sideEffect == ContinualSideEffectClass.ReadOnly ? "fs.read_file" : "fs.write_file", state, sideEffect,
            input ?? "{}", result, isError, when, state == ContinualToolReceiptState.Completed ? when : null) { LocalProcessCompleted = true };
    }
}
