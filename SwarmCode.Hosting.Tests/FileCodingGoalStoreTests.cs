using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class FileCodingGoalStoreTests : TempDirTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private FileCodingGoalStore Store() => new(Paths, new FixedTime());

    [Fact]
    public async Task Resume_clears_only_generated_pause_feedback_and_initializes_legacy_reference_defaults()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Pause, Now));
        var paused = (await store.GetAsync(goal.Id))!;
        Assert.Equal("Paused by the user.", paused.ContinuationFeedback);
        Assert.True(await store.ControlAsync(goal.Id, paused.Version, CodingGoalControl.Resume, Now));
        var resumed = (await store.GetAsync(goal.Id))!;
        Assert.Null(resumed.ContinuationFeedback);
        Assert.Empty(CodingGoalPayload.Read(resumed).Attachments);
    }

    [Fact]
    public async Task Explicit_resume_records_fresh_control_without_replacing_content_or_unknown_payload_fields()
    {
        var first = Store(); var other = Store();
        var record = await first.CreateOrJoinAsync(Start() with { ExecutorPayloadJson =
            "{\"Attachments\":[],\"RepositoryRoot\":\"/retained/project\",\"AuthorityAfterMessageCount\":7,\"FutureNull\":null,\"FutureFalse\":false}" }, Now);
        Assert.True(await first.ControlAsync(record.Id, record.Version, CodingGoalControl.Pause, Now));
        var paused = (await other.GetAsync(record.Id))!;
        Assert.Equal("Paused by the user.", paused.ContinuationFeedback);
        Assert.True(await first.UpdateAsync(record.Id, paused.Version, "Preserve the corrected full objective", Now));
        Assert.False(await other.ControlAsync(record.Id, paused.Version, CodingGoalControl.Resume, Now));
        var corrected = (await other.GetAsync(record.Id))!;
        Assert.True(await other.ControlAsync(record.Id, corrected.Version, CodingGoalControl.Resume, Now));
        var resumed = (await Store().GetAsync(record.Id))!;
        var payload = CodingGoalPayload.Read(resumed); var control = Assert.IsType<CodingGoalUserControl>(payload.LastUserControl);
        Assert.Equal(resumed.Id, control.GoalId); Assert.Equal(resumed.Version, control.GoalVersion);
        Assert.Equal(CodingGoalControl.Resume, control.Control); Assert.Equal(Now, control.AtUtc);
        Assert.Equal(ContinualObjectiveStatus.Paused, control.PreviousStatus);
        Assert.Equal(ContinualObjectiveStatus.RetryScheduled, control.ResultingStatus);
        Assert.Equal("Preserve the corrected full objective", resumed.ObjectiveText);
        Assert.Equal(CodingGoalPayload.Read(corrected).AuthorityUpdatedAtUtc, payload.AuthorityUpdatedAtUtc);
        Assert.Equal(CodingGoalPayload.Read(corrected).AuthorityAfterMessageCount, payload.AuthorityAfterMessageCount);
        Assert.Equal("/retained/project", payload.RepositoryRoot);
        using var json = System.Text.Json.JsonDocument.Parse(resumed.ExecutorPayloadJson!);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("FutureNull").ValueKind);
        Assert.False(json.RootElement.GetProperty("FutureFalse").GetBoolean());
        var lease = (await first.TryClaimAsync(resumed.Id, resumed.Version, "worker", Now, Now.AddMinutes(1)))!;
        Assert.True(await first.HeartbeatAsync(resumed.Id, lease.Token, Now, Now.AddMinutes(2)));
        var active = (await Store().GetAsync(resumed.Id))!;
        Assert.Equal(control, CodingGoalPayload.Read(active).LastUserControl);
        Assert.Contains("explicit Resume above supersedes older pause/setup instructions", CodingGoalControlAuthority.Format(active, payload));
        await first.DeleteConversationAsync(active.ConversationId);
        Assert.False(await other.ControlAsync(active.Id, active.Version, CodingGoalControl.Resume, Now));
        Assert.Null(await Store().GetAsync(active.Id));
    }

    [Fact]
    public async Task Resume_preserves_real_recovery_feedback_and_labels_control_authority_separately()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        Assert.True(await store.HoldAsync(goal.Id, goal.Version, "Inspect the prior uncertain operation; do not replay.", Now));
        var held = (await store.GetAsync(goal.Id))!;
        Assert.True(await store.ControlAsync(held.Id, held.Version, CodingGoalControl.Resume, Now));
        var resumed = (await store.GetAsync(goal.Id))!; var payload = CodingGoalPayload.Read(resumed);
        Assert.Equal(held.ContinuationFeedback, resumed.ContinuationFeedback);
        var text = CodingGoalControlAuthority.Format(resumed, payload, inspectionOnly: true);
        Assert.Contains("inspection only; remaining work runs separately", text);
        Assert.Contains("does not replace content corrections", text);
        Assert.DoesNotContain("Continue this same saved goal and remaining work.", text);
        var wrong = payload with { LastUserControl = payload.LastUserControl! with { GoalId = Guid.NewGuid() } };
        Assert.DoesNotContain("explicit Resume above supersedes", CodingGoalControlAuthority.Format(resumed, wrong));
        wrong = payload with { LastUserControl = payload.LastUserControl! with { GoalVersion = resumed.Version + 1 } };
        Assert.DoesNotContain("explicit Resume above supersedes", CodingGoalControlAuthority.Format(resumed, wrong));
    }

    [Fact]
    public async Task Independent_writers_join_duplicate_trigger_and_only_one_claims_execution()
    {
        var first = Store();
        var second = Store();
        var starts = Enumerable.Range(0, 12).Select(_ => Start()).ToArray();
        var records = await Task.WhenAll(starts.Select((start, index) =>
            (index % 2 == 0 ? first : second).CreateOrJoinAsync(start, Now)));
        var id = Assert.Single(records.Select(record => record.Id).Distinct());
        var record = (await first.GetAsync(id))!;
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            (index % 2 == 0 ? first : second).TryClaimAsync(id, record.Version,
                "worker-" + index, Now, Now.AddMinutes(1))));

        Assert.Single(claims, claim => claim is not null);
        Assert.Equal(id, (await Store().GetLatestForConversationAsync("workspace", "conversation"))!.Id);
    }

    [Theory]
    [InlineData(CodingGoalControl.Pause, ContinualObjectiveStatus.Paused)]
    [InlineData(CodingGoalControl.Cancel, ContinualObjectiveStatus.Cancelled)]
    public async Task Operator_control_revokes_claim_and_rejects_late_completion(CodingGoalControl control, ContinualObjectiveStatus expected)
    {
        var executing = Store();
        var controlling = Store();
        var record = await executing.CreateOrJoinAsync(Start(), Now);
        var lease = (await executing.TryClaimAsync(record.Id, record.Version, "worker", Now, Now.AddMinutes(1)))!;
        var active = (await controlling.GetAsync(record.Id))!;

        Assert.False(await controlling.ControlAsync(record.Id, record.Version, control, Now));
        Assert.True(await controlling.ControlAsync(record.Id, active.Version, control, Now));
        Assert.False(await executing.ApplyTransitionAsync(record.Id, lease.Token, Completed(), default));
        var saved = (await Store().GetAsync(record.Id))!;
        Assert.Equal(expected, saved.Status);
        Assert.Null(saved.LeaseToken);
    }

    [Fact]
    public async Task Competing_corrections_preserve_checked_version_and_current_objective()
    {
        var first = Store();
        var second = Store();
        var record = await first.CreateOrJoinAsync(Start(), Now);

        Assert.True(await first.UpdateAsync(record.Id, record.Version, "Implement the first correction", Now));
        Assert.False(await second.UpdateAsync(record.Id, record.Version, "Stale correction", Now));
        var saved = (await second.GetAsync(record.Id))!;
        Assert.Equal("Implement the first correction", saved.ObjectiveText);
        Assert.True(saved.Version > record.Version);
    }

    [Fact]
    public async Task A_correction_cannot_be_completed_by_the_previous_objectives_active_attempt()
    {
        var executing = Store();
        var correcting = Store();
        var record = await executing.CreateOrJoinAsync(Start(), Now);
        var lease = (await executing.TryClaimAsync(record.Id, record.Version, "worker", Now, Now.AddMinutes(1)))!;
        var baseline = (await correcting.GetAsync(record.Id))!;

        Assert.True(await correcting.UpdateAsync(record.Id, baseline.Version, "Also implement the corrected requirement", Now));
        Assert.False(await executing.ApplyTransitionAsync(record.Id, lease.Token, Completed()));
        var saved = (await Store().GetAsync(record.Id))!;
        Assert.Equal("Also implement the corrected requirement", saved.ObjectiveText);
        Assert.NotEqual(ContinualObjectiveStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task Resume_from_another_host_cannot_revoke_a_live_execution_lease()
    {
        var executing = Store();
        var resuming = Store();
        var record = await executing.CreateOrJoinAsync(Start(), Now);
        var lease = (await executing.TryClaimAsync(record.Id, record.Version, "live-process", Now, Now.AddMinutes(1)))!;
        var baseline = (await resuming.GetAsync(record.Id))!;

        Assert.False(await resuming.ControlAsync(record.Id, baseline.Version, CodingGoalControl.Resume, Now));
        var saved = (await Store().GetAsync(record.Id))!;
        Assert.Equal(lease.Token, saved.LeaseToken);
        Assert.Equal(lease.ClaimedVersion, saved.Version);
        Assert.Null(await resuming.TryClaimAsync(record.Id, saved.Version, "other-process", Now, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task Deleted_conversation_cannot_be_resurrected_by_a_late_receipt_or_completion()
    {
        var executing = Store();
        var deleting = Store();
        var record = await executing.CreateOrJoinAsync(Start(), Now);
        var lease = (await executing.TryClaimAsync(record.Id, record.Version, "worker", Now, Now.AddMinutes(1)))!;
        await deleting.DeleteConversationAsync(record.ConversationId);

        Assert.False(await executing.UpsertReceiptAsync(record.Id, lease.Token, Receipt(record.Id, 1, ContinualToolReceiptState.Completed)));
        Assert.False(await executing.ApplyTransitionAsync(record.Id, lease.Token, Completed()));
        Assert.False(await executing.UpdateAsync(record.Id, lease.ClaimedVersion, "Stale task", Now));
        Assert.Null(await Store().GetAsync(record.Id));
        Assert.Null(await Store().GetLatestForConversationAsync("workspace", "conversation"));
    }

    [Fact]
    public async Task Delayed_start_cannot_replace_a_completed_receipt_from_another_writer()
    {
        var first = Store();
        var second = Store();
        var record = await first.CreateOrJoinAsync(Start(), Now);
        var lease = (await first.TryClaimAsync(record.Id, record.Version, "worker", Now, Now.AddMinutes(1)))!;
        Assert.True(await first.UpsertReceiptAsync(record.Id, lease.Token, Receipt(record.Id, 1, ContinualToolReceiptState.Started)));
        Assert.True(await second.UpsertReceiptAsync(record.Id, lease.Token, Receipt(record.Id, 2, ContinualToolReceiptState.Completed)));

        Assert.False(await first.UpsertReceiptAsync(record.Id, lease.Token, Receipt(record.Id, 1, ContinualToolReceiptState.Started)));
        var receipt = Assert.Single((await Store().GetAsync(record.Id))!.Receipts);
        Assert.Equal(ContinualToolReceiptState.Completed, receipt.State);
        Assert.Equal("written", receipt.Result);
    }

    [Fact]
    public async Task Long_receipt_texts_live_in_the_body_log_and_round_trip_exactly()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", Now, Now.AddMinutes(1)))!;
        var result = new string('r', 50_000) + " ✓ fin — end"; var input = "{\"text\":\"" + new string('i', 20_000) + "\"}";
        var receipt = Receipt(goal.Id, 1, ContinualToolReceiptState.Completed) with { Result = result, InputJson = input };
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, receipt));
        var inventory = File.ReadAllText(Inventory(Paths));
        Assert.True(new FileInfo(Inventory(Paths)).Length < 20_000, "The inventory kept the long receipt texts inline.");
        Assert.DoesNotContain(new string('r', 2_000), inventory);
        Assert.StartsWith("{\"stamp\":\"", inventory);
        Assert.True(File.Exists(BodyLog(Paths, goal.Id)));
        foreach (var reader in new[] { Store(), ColdStore() })
        {
            var saved = Assert.Single((await reader.GetAsync(goal.Id))!.Receipts);
            Assert.Equal(result, saved.Result); Assert.Equal(input, saved.InputJson);
            Assert.Equal(ContinualReceiptResolutionPolicy.Fingerprint(receipt), ContinualReceiptResolutionPolicy.Fingerprint(saved));
        }
        // A heartbeat rewrites the inventory without appending the unchanged body again.
        var length = new FileInfo(BodyLog(Paths, goal.Id)).Length;
        Assert.True(await store.HeartbeatAsync(goal.Id, lease.Token, Now, Now.AddMinutes(2)));
        Assert.Equal(length, new FileInfo(BodyLog(Paths, goal.Id)).Length);
        Assert.Equal(result, Assert.Single((await ColdStore().GetAsync(goal.Id))!.Receipts).Result);
    }

    [Fact]
    public async Task A_legacy_inline_inventory_loads_and_moves_its_long_texts_on_the_first_write()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", Now, Now.AddMinutes(1)))!;
        var result = "{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('a', 30_000) + "\"}]}";
        var receipt = Receipt(goal.Id, 1, ContinualToolReceiptState.Completed) with { Result = result };
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, receipt));
        var legacy = ColdPaths(); InlineLegacyInventory(legacy, goal.Id);
        Assert.False(Directory.Exists(Path.Combine(legacy.RootDirectory, "goals", "receipt-bodies")));
        Assert.Contains(new string('a', 30_000), File.ReadAllText(Inventory(legacy)));
        var reader = new FileCodingGoalStore(legacy, new FixedTime());
        var loaded = Assert.Single((await reader.GetAsync(goal.Id))!.Receipts);
        Assert.Equal(result, loaded.Result);
        Assert.True(await reader.HeartbeatAsync(goal.Id, lease.Token, Now, Now.AddMinutes(2)));
        Assert.DoesNotContain(new string('a', 2_000), File.ReadAllText(Inventory(legacy)));
        Assert.True(File.Exists(BodyLog(legacy, goal.Id)));
        var moved = Assert.Single((await new FileCodingGoalStore(ColdPaths(legacy), new FixedTime()).GetAsync(goal.Id))!.Receipts);
        Assert.Equal(result, moved.Result);
        Assert.Equal(ContinualReceiptResolutionPolicy.Fingerprint(receipt), ContinualReceiptResolutionPolicy.Fingerprint(moved));
    }

    [Fact]
    public async Task A_damaged_body_log_leaves_the_text_null_and_keeps_its_reference_for_a_restored_log()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", Now, Now.AddMinutes(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, ContinualToolReceiptState.Completed) with { Result = new string('b', 10_000) }));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 2, ContinualToolReceiptState.Completed) with { ToolCallId = "write-2", Result = new string('c', 10_000) }));
        var truncated = ColdPaths();
        using (var log = new FileStream(BodyLog(truncated, goal.Id), FileMode.Open, FileAccess.ReadWrite)) log.SetLength(10_200);
        var reader = new FileCodingGoalStore(truncated, new FixedTime());
        var damaged = (await reader.GetAsync(goal.Id))!;
        Assert.Equal(2, damaged.Receipts.Count);
        Assert.Equal(new string('b', 10_000), damaged.Receipts[0].Result);
        Assert.Null(damaged.Receipts[1].Result);
        var key = $"{goal.Id:N}/1/result/write-2";
        Assert.Contains(key, FileCodingGoalStore.UnavailableBodies);
        Assert.True(await reader.HeartbeatAsync(goal.Id, lease.Token, Now, Now.AddMinutes(2)));
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Inventory(truncated)))!.AsObject();
        Assert.True(node["bodies"]!.AsObject().ContainsKey(key), "The reference was dropped; a restored log could no longer repair the text.");
        Assert.DoesNotContain("unavailable", File.ReadAllText(Inventory(truncated)), StringComparison.OrdinalIgnoreCase);
        File.Copy(BodyLog(Paths, goal.Id), BodyLog(truncated, goal.Id), overwrite: true);
        var restored = (await new FileCodingGoalStore(ColdPaths(truncated), new FixedTime()).GetAsync(goal.Id))!;
        Assert.Equal(new string('c', 10_000), restored.Receipts[1].Result);
        var missing = ColdPaths(); Directory.Delete(Path.Combine(missing.RootDirectory, "goals", "receipt-bodies"), recursive: true);
        var orphaned = (await new FileCodingGoalStore(missing, new FixedTime()).GetAsync(goal.Id))!;
        Assert.All(orphaned.Receipts, saved => Assert.Null(saved.Result));
        Assert.Equal(goal.Id, orphaned.Id);
    }

    [Fact]
    public async Task A_shorter_rewrite_of_a_receipt_drops_the_stale_body_reference()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", Now, Now.AddMinutes(1)))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, ContinualToolReceiptState.Completed) with { Result = new string('d', 20_000) }));
        Assert.True(File.Exists(BodyLog(Paths, goal.Id)));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 2, ContinualToolReceiptState.Completed) with { Result = "short" }));
        Assert.Equal("short", Assert.Single((await ColdStore().GetAsync(goal.Id))!.Receipts).Result);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Inventory(Paths)))!.AsObject();
        Assert.False(node["bodies"]!.AsObject().ContainsKey($"{goal.Id:N}/1/result/write-1"));
    }

    [Fact]
    public async Task A_refused_control_or_join_leaves_the_inventory_bytes_unchanged()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var before = File.ReadAllBytes(Inventory(Paths));
        Assert.False(await store.ControlAsync(goal.Id, goal.Version + 5, CodingGoalControl.Pause, Now));
        Assert.Null(await Store().TryClaimAsync(goal.Id, goal.Version + 5, "worker", Now, Now.AddMinutes(1)));
        Assert.Equal(goal.Id, (await store.CreateOrJoinAsync(Start(), Now)).Id);
        Assert.Equal(before, File.ReadAllBytes(Inventory(Paths)));
    }

    [Fact]
    public async Task References_are_rebuilt_from_the_framed_log_after_an_unaware_rewrite()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", Now, Now.AddMinutes(1)))!;
        var input = "{\"text\":\"" + new string('i', 3_000) + "\"}";
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, ContinualToolReceiptState.Completed) with { Result = new string('e', 5_000), InputJson = input }));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 2, ContinualToolReceiptState.Completed) with { ToolCallId = "write-2", Result = new string('f', 7_000) }));
        var rewritten = ColdPaths();
        // What a build that does not know the body log writes back: null texts, no references, no stamp.
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Inventory(rewritten)))!.AsObject();
        node.Remove("bodies"); node.Remove("stamp"); node.Remove("format");
        File.WriteAllText(Inventory(rewritten), node.ToJsonString());
        var reader = new FileCodingGoalStore(rewritten, new FixedTime());
        var recovered = (await reader.GetAsync(goal.Id))!;
        Assert.Equal(new string('e', 5_000), recovered.Receipts[0].Result);
        Assert.Equal(input, recovered.Receipts[0].InputJson);
        Assert.Equal(new string('f', 7_000), recovered.Receipts[1].Result);
        Assert.True(await reader.HeartbeatAsync(goal.Id, lease.Token, Now, Now.AddMinutes(2)));
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Inventory(rewritten)))!.AsObject();
        Assert.Equal(3, saved["bodies"]!.AsObject().Count);
        Assert.Equal(new string('f', 7_000), (await new FileCodingGoalStore(ColdPaths(rewritten), new FixedTime()).GetAsync(goal.Id))!.Receipts[1].Result);
    }

    [Fact]
    public async Task Deleting_a_conversation_removes_its_body_log_and_a_lone_surrogate_stays_inline()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", Now, Now.AddMinutes(1)))!;
        var unpaired = new string('x', 5_000) + "\ud83d";
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 1, ContinualToolReceiptState.Completed) with { Result = unpaired }));
        Assert.False(File.Exists(BodyLog(Paths, goal.Id)));
        Assert.Contains(new string('x', 5_000), File.ReadAllText(Inventory(Paths)));
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, Receipt(goal.Id, 2, ContinualToolReceiptState.Completed) with { ToolCallId = "write-2", Result = new string('y', 5_000) }));
        Assert.True(File.Exists(BodyLog(Paths, goal.Id)));
        await store.DeleteConversationAsync(goal.ConversationId);
        Assert.False(File.Exists(BodyLog(Paths, goal.Id)));
        Assert.DoesNotContain(goal.Id.ToString("N"), File.ReadAllText(Inventory(Paths)));
    }

    [Fact]
    public async Task A_foreign_writer_is_observed_whether_or_not_it_stamps_the_inventory()
    {
        var store = Store(); var goal = await store.CreateOrJoinAsync(Start(), Now);
        Assert.Equal("Implement feature and verify it", (await store.GetAsync(goal.Id))!.ObjectiveText);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Inventory(Paths)))!.AsObject();
        node["rows"]![0]!["objectiveText"] = "Stamped foreign edit"; node["stamp"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(Inventory(Paths), node.ToJsonString());
        Assert.Equal("Stamped foreign edit", (await store.GetAsync(goal.Id))!.ObjectiveText);
        node["rows"]![0]!["objectiveText"] = "Unstamped foreign edit"; node.Remove("stamp");
        File.WriteAllText(Inventory(Paths), node.ToJsonString());
        Assert.Equal("Unstamped foreign edit", (await store.GetAsync(goal.Id))!.ObjectiveText);
        Assert.Equal("Unstamped foreign edit", (await Store().GetAsync(goal.Id))!.ObjectiveText);
    }

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
    private FileCodingGoalStore ColdStore() => new(ColdPaths(), new FixedTime());
    /// <summary>Rewrites a copied inventory in the pre-body-log shape: every text inline, no stamp, no log.</summary>
    private static void InlineLegacyInventory(SwarmCodeDataPaths paths, Guid goal)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Inventory(paths)))!.AsObject();
        var bodies = node["bodies"]!.AsObject(); var log = File.ReadAllBytes(BodyLog(paths, goal));
        foreach (var row in node["rows"]!.AsArray())
            foreach (var receipt in row!["receipts"]!.AsArray())
                foreach (var (field, property) in new[] { ("result", "result"), ("input", "inputJson") })
                {
                    var key = $"{goal:N}/{receipt!["attempt"]}/{field}/{receipt["toolCallId"]}";
                    if (bodies[key] is not { } reference) continue;
                    var offset = (int)reference["offset"]!.GetValue<long>(); var length = reference["length"]!.GetValue<int>();
                    receipt[property] = System.Text.Encoding.UTF8.GetString(log, offset, length);
                }
        node.Remove("bodies"); node.Remove("stamp");
        File.WriteAllText(Inventory(paths), node.ToJsonString());
        Directory.Delete(Path.Combine(paths.RootDirectory, "goals", "receipt-bodies"), recursive: true);
    }

    private static ContinualObjectiveStart Start() => new()
    {
        Surface = "coding-goal", PartitionKey = "workspace", IdempotencyKey = "manual:1",
        ConversationId = "conversation", ExecutorKey = "coding-goal",
        ObjectiveText = "Implement feature and verify it", ObjectiveSummary = "Implement feature and verify it",
        Authority = ContinualObjectiveAuthority.UserRequested("test"),
    };

    private static ContinualObjectiveTransition Completed() => new(ContinualObjectiveStatus.Completed,
        Now, Now, null, "done", null, "fingerprint", null, 0);

    private static ContinualToolReceipt Receipt(Guid id, long sequence, ContinualToolReceiptState state) => new(
        id, 1, sequence, "write-1", "fs.write_file", state, ContinualSideEffectClass.WritesLocal,
        "{}", state == ContinualToolReceiptState.Completed ? "written" : null, false,
        Now, state == ContinualToolReceiptState.Completed ? Now : null);
}
