using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Files;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalMailboxAttachmentTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Store, FileGoalMailbox Inbox, ContinualObjectiveSnapshot Goal)> Seed()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await store.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "target",
            ConversationId = "target", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Preserve existing work", ObjectiveSummary = "Existing work",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        return (store, new(Paths, store), goal);
    }

    private Task<CodingAttachment> Import(string conversation = "target", string name = "source.txt", string content = "file source")
        => new AttachmentStore(Paths).ImportAsync(conversation, name, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
    private static GoalMessageCommand Command(string id, IReadOnlyList<GoalMessageAttachment>? files = null, string text = "Use the attached source")
        => new(id, text, GoalMessageKind.Correction, new("code-ui", id, "local-user")) { Attachments = files };
    private static async Task<ContinualObjectiveLease> Claim(FileCodingGoalStore store, ContinualObjectiveSnapshot goal)
        => (await store.TryClaimAsync(goal.Id, goal.Version, "worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;

    [Fact]
    public async Task Paused_named_recipient_import_is_target_owned_and_restart_idempotent_without_changing_goal()
    {
        var h = await Seed();
        Assert.True(await h.Store.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var baseline = JsonSerializer.Serialize(await h.Store.GetAsync(h.Goal.Id));
        var source = await Import("sender", "AGENTS.md");
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [source]);
        var file = Assert.Single(refs);
        Assert.NotEqual(source.Id, file.Id);
        Assert.StartsWith(Path.Combine(Paths.SessionsDirectory, "attachments", "target"), file.LocalPath);
        Assert.Equal("file-AGENTS.md", Path.GetFileName(file.LocalPath));
        var command = Command("same", refs);
        var saved = await h.Inbox.SendAsync(h.Goal.Id, command);
        var restored = new FileGoalMailbox(Paths, new(Paths));
        var retried = JsonSerializer.Deserialize<GoalMessageCommand>(JsonSerializer.Serialize(command))!;
        Assert.Equal(saved.Id, (await restored.SendAsync(h.Goal.Id, retried)).Id);
        File.Delete(file.LocalPath);
        Assert.Equal(saved.Id, (await restored.SendAsync(h.Goal.Id, retried)).Id); // Lost ACK is still recoverable.
        Assert.Single((await restored.ReadAsync(h.Goal.Id)).Messages);
        Assert.Equal(baseline, JsonSerializer.Serialize(await h.Store.GetAsync(h.Goal.Id)));
    }

    [Fact]
    public async Task Different_attachment_identity_cannot_reuse_a_saved_message_key_and_old_plaintext_fingerprint_remains_valid()
    {
        var h = await Seed();
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import()]);
        await h.Inbox.SendAsync(h.Goal.Id, Command("key", refs));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Inbox.SendAsync(h.Goal.Id,
            Command("key", [refs[0] with { Sha256 = new string('a', 64) }])));
        var plain = Command("old");
        var saved = await h.Inbox.SendAsync(h.Goal.Id, plain);
        var json = JsonSerializer.Serialize(plain, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("attachments", json);
        Assert.Equal(saved.Id, (await new FileGoalMailbox(Paths, new(Paths)).SendAsync(h.Goal.Id, plain)).Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("linked")]
    public async Task Invalid_file_keeps_text_and_original_audit_but_delivers_unavailable_notice_without_path_and_allows_replacement(string fault)
    {
        var h = await Seed();
        var lease = await Claim(h.Store, h.Goal);
        var file = await Import();
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [file]);
        var first = await h.Inbox.SendAsync(h.Goal.Id, Command("A", refs));
        File.Delete(file.LocalPath);
        if (fault == "changed") await File.WriteAllTextAsync(file.LocalPath, "different bytes");
        if (fault == "linked")
        {
            var other = Path.Combine(Tmp, "outside.txt"); await File.WriteAllTextAsync(other, "file source");
            File.CreateSymbolicLink(file.LocalPath, other);
        }
        var replacement = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import(content: "replacement")]);
        var readBeforeClaim = await h.Inbox.VerifyAttachmentsForWorkerAsync(await h.Inbox.ReadAsync(h.Goal.Id), default);
        Assert.NotNull(Assert.Single(readBeforeClaim.Messages).AttachmentIssue);
        Assert.DoesNotContain(JsonSerializer.Serialize(file.LocalPath), JsonSerializer.Serialize(GoalMessaging.AttachmentEvidence(
            readBeforeClaim.Messages[0].Attachments, readBeforeClaim.Messages[0].AttachmentIssue)));
        Assert.Null(Assert.Single((await h.Inbox.ReadAsync(h.Goal.Id)).Messages).AttachmentIssue); // Read projection owns no persistence.
        var second = await h.Inbox.SendAsync(h.Goal.Id, Command("B", replacement, "Use this replacement instead"));
        var question = await h.Inbox.SendAsync(h.Goal.Id, Command("C", text: "What is next?") with { Kind = GoalMessageKind.Question });
        var delivery = (await GoalMailboxBoundary.BeginAsync(h.Inbox, h.Goal.Id, "target", "worker", expectedLeaseToken: lease.Token))!;
        Assert.Equal(new[] { first.Id, second.Id, question.Id }, delivery.Messages.Select(m => m.Id));
        var unavailable = delivery.Messages[0];
        Assert.NotNull(unavailable.AttachmentIssue);
        Assert.Contains("Do not open or use", delivery.ContextText);
        Assert.DoesNotContain(file.LocalPath, delivery.ContextText);
        Assert.DoesNotContain(JsonSerializer.Serialize(file.LocalPath), delivery.ContextText);
        Assert.Contains(file.Sha256, delivery.ContextText);
        Assert.Contains(JsonSerializer.Serialize(replacement[0].LocalPath), delivery.ContextText);
        Assert.All(delivery.Messages, m => Assert.Equal(GoalDeliveryState.Presenting, m.State));
        var stored = (await h.Inbox.ReadAsync(h.Goal.Id)).Messages.Single(m => m.Id == first.Id);
        Assert.Equal(file.LocalPath, Assert.Single(stored.Attachments!).LocalPath);
        var source = (await h.Inbox.GetInstructionSnapshotAsync(h.Goal.Id)).Sources.Single(s => s.Id == first.Id);
        Assert.Equal(refs[0], Assert.Single(source.Attachments!));
        Assert.Equal("Use the attached source", source.Text);
        var held = (await h.Inbox.GetHeldInputAsync(h.Goal.Id, "target", lease.Token))!;
        Assert.Contains("ATTACHMENT UNAVAILABLE", held.ContextText);
        Assert.DoesNotContain(file.LocalPath, held.ContextText);
        Assert.DoesNotContain(JsonSerializer.Serialize(file.LocalPath), held.ContextText);
        await h.Inbox.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Delivered);
        Assert.All((await h.Inbox.ReadAsync(h.Goal.Id)).Messages, m => Assert.Equal(GoalDeliveryState.Delivered, m.State));
        var later = await h.Inbox.SendAsync(h.Goal.Id, Command("D", text: "Later question") with { Kind = GoalMessageKind.Question });
        Assert.Equal(later.Id, Assert.Single((await h.Inbox.BeginDeliveryForWorkerAsync(h.Goal.Id, "target", "worker", lease.Token))!.Messages).Id);
    }

    [Fact]
    public async Task Send_rejects_arbitrary_crossconversation_symlink_and_hash_drift_before_persisting()
    {
        var h = await Seed();
        var other = await Import("other");
        var forged = new GoalMessageAttachment(other.Id, other.FileName, other.LocalPath, other.MediaType, other.Length, other.Sha256);
        await Assert.ThrowsAsync<GoalMessageRejectedException>(() => h.Inbox.SendAsync(h.Goal.Id, Command("cross", [forged])));
        var owned = await Import();
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [owned]);
        await File.WriteAllTextAsync(owned.LocalPath, "mutated source");
        await Assert.ThrowsAsync<GoalMessageRejectedException>(() => h.Inbox.SendAsync(h.Goal.Id, Command("changed", refs)));
        File.Delete(owned.LocalPath); File.CreateSymbolicLink(owned.LocalPath, other.LocalPath);
        await Assert.ThrowsAsync<GoalMessageRejectedException>(() => h.Inbox.SendAsync(h.Goal.Id, Command("link", refs)));
        Assert.Empty((await h.Inbox.ReadAsync(h.Goal.Id)).Messages);
    }

    [Fact]
    public async Task Staged_import_cannot_resurrect_deleted_recipient_and_competing_messages_do_not_overwrite_pause_or_rename()
    {
        var h = await Seed();
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import("sender")]);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (Task)new FileGoalMailbox(Paths, new(Paths)).SendAsync(h.Goal.Id, Command("concurrent-" + i, refs)))
            .Append(h.Inbox.RenameAsync(h.Goal.Id, "Renamed", "renamed", 0))
            .Append(h.Store.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow)));
        Assert.Equal(ContinualObjectiveStatus.Paused, (await h.Store.GetAsync(h.Goal.Id))!.Status);
        var before = JsonSerializer.Serialize(await h.Store.GetAsync(h.Goal.Id));
        await h.Inbox.SendAsync(h.Goal.Id, Command("late", refs));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Store.GetAsync(h.Goal.Id)));
        Assert.Equal(13, (await h.Inbox.ReadAsync(h.Goal.Id)).Messages.Count);
        Assert.Equal("renamed", (await h.Inbox.ReadAsync(h.Goal.Id)).Contact.Handle);
        await h.Store.DeleteConversationAsync("target");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Inbox.SendAsync(h.Goal.Id, Command("after-delete", refs)));
        Assert.Null(await h.Store.GetAsync(h.Goal.Id));
    }

    [Fact]
    public async Task Current_attachment_limits_and_complete_context_budget_are_enforced_and_source_hash_binds_files()
    {
        var h = await Seed();
        var file = await Import();
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [file]);
        new UserSettingsFile(Paths).WriteCodingSection("AttachmentLimits", new CodingAttachmentOptions { MaxFileBytes = 1 });
        await Assert.ThrowsAsync<GoalMessageRejectedException>(() => h.Inbox.SendAsync(h.Goal.Id, Command("limited", refs)));
        new UserSettingsFile(Paths).WriteCodingSection("AttachmentLimits", new CodingAttachmentOptions());
        var plain = new GoalInstructionSource("id", 0, "Idea", "same text", DateTimeOffset.UtcNow);
        Assert.Equal(ContinualDeliveryEvidence.OutputDigest("same text"), plain.Sha256);
        Assert.NotEqual(plain.Sha256, (plain with { Attachments = refs }).Sha256);
        Assert.NotEqual((plain with { Attachments = refs }).Sha256, (plain with { Attachments = [refs[0] with { Sha256 = new string('b', 64) }] }).Sha256);
        var many = new List<GoalMessageAttachment>();
        for (var i = 0; i < 35; i++) many.AddRange(await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import(name: new string('n', 150) + ".txt")]));
        await Assert.ThrowsAsync<GoalMessageRejectedException>(() => h.Inbox.SendAsync(h.Goal.Id, Command("too-large", many)));
        Assert.Empty((await h.Inbox.ReadAsync(h.Goal.Id)).Messages);
    }

    [Fact]
    public async Task Empty_text_with_a_valid_file_is_saved_as_an_explicit_host_description()
    {
        var h = await Seed();
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import()]);
        var saved = await h.Inbox.SendAsync(h.Goal.Id, Command("file-only", refs, ""));
        Assert.Equal(GoalMessaging.AttachmentOnlyText, saved.Text);
        Assert.Equal(refs[0], Assert.Single(saved.Attachments!));
        Assert.Equal(GoalDeliveryState.Received, saved.State);
        Assert.Equal(saved.Id, (await h.Inbox.SendAsync(h.Goal.Id, Command("file-only", refs, ""))).Id);
    }

    [Fact]
    public async Task Companion_path_is_explicitly_not_a_file_upload_but_plain_messages_still_work()
    {
        var h = await Seed();
        var server = new GoalMailboxServer(h.Inbox, Paths, NullLogger<GoalMailboxServer>.Instance);
        var instance = (await h.Inbox.GetIdentityAsync()).InstanceId;
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import()]);
        JsonObject Request(GoalMessageCommand command) => new() { ["operation"] = "send", ["instanceId"] = instance,
            ["goalId"] = h.Goal.Id.ToString(), ["message"] = JsonSerializer.SerializeToNode(command, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var rejected = await server.HandleAsync(Request(Command("remote", refs)), default);
        Assert.Contains("not an upload", JsonSerializer.Serialize(rejected));
        Assert.Empty((await h.Inbox.ReadAsync(h.Goal.Id)).Messages);
        await server.HandleAsync(Request(Command("plain")), default);
        Assert.Single((await h.Inbox.ReadAsync(h.Goal.Id)).Messages);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truthful_bound_Read_settles_only_attachment_reference_without_inventing_incorporation_or_changing_goal(bool hasInstruction)
    {
        var h = await Seed(); var lease = await Claim(h.Store, h.Goal);
        var refs = await h.Inbox.PrepareAttachmentsAsync(h.Goal.Id, [await Import()]);
        var message = await h.Inbox.SendAsync(h.Goal.Id, Command("reference", refs, hasInstruction ? "Write the real pitch and caption using this source" : ""));
        Assert.True(await h.Inbox.HasPendingAsync(h.Goal.Id));
        var delivery = (await h.Inbox.BeginDeliveryForWorkerAsync(h.Goal.Id, "target", "worker", lease.Token))!;
        await h.Inbox.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Uncertain);
        Assert.True(await h.Inbox.HasPendingAsync(h.Goal.Id));
        var baseline = JsonSerializer.Serialize(await h.Store.GetAsync(h.Goal.Id));
        await h.Inbox.ReplyForWorkerAsync(h.Goal.Id, "target", delivery.Id, message.Id,
            "Received the reference. This acknowledges supplied metadata without claiming file inspection.", GoalDeliveryState.Read, lease.Token);
        var restarted = new FileGoalMailbox(Paths, new(Paths));
        Assert.Equal(hasInstruction, await restarted.HasPendingAsync(h.Goal.Id));
        Assert.Equal(hasInstruction, (await restarted.GetHeldInputAsync(h.Goal.Id, "target", lease.Token)) is not null);
        var saved = Assert.Single((await restarted.ReadAsync(h.Goal.Id)).Messages);
        Assert.Equal(GoalDeliveryState.Read, saved.State); Assert.Equal(refs[0], Assert.Single(saved.Attachments!));
        var source = (await restarted.GetInstructionSnapshotAsync(h.Goal.Id)).Sources.Single(s => s.Id == message.Id);
        Assert.Equal(!hasInstruction, source.IsReferenceOnly); Assert.Equal(refs[0], Assert.Single(source.Attachments!));
        Assert.Equal(baseline, JsonSerializer.Serialize(await h.Store.GetAsync(h.Goal.Id)));
    }

}
