using Bunit;
using SwarmCode.App.Components.Chat;
using SwarmCore.Agent.Communication;
using SwarmCode.Hosting.Goals;
using SwarmCore.Coding.Files;

namespace SwarmCode.App.Tests;

public sealed class GoalInboxAttachmentTests : BunitContext
{
    public GoalInboxAttachmentTests() => JSInterop.Mode = JSRuntimeMode.Loose;
    private static readonly Guid Goal = Guid.NewGuid();
    private static GoalMessageAttachment File(string id) => new(id, id + ".txt", "/local/" + id, "text/plain", 12, new string('a', 64));

    [Theory]
    [InlineData("list")]
    [InlineData("prepare")]
    [InlineData("send")]
    public async Task Navigation_during_each_async_stage_preserves_captured_recipient_profile_and_equal_looking_new_composer(string stage)
    {
        var mailbox = new Mailbox { BlockStage = stage };
        var original = GoalInboxDraft.For(mailbox, "session:A");
        var file = File("same-id");
        var scope = new GoalInboxSendScope("A", mailbox, original, Goal, true, "Same text", GoalMessageKind.Idea,
            [new(file.Id, file.FileName, file.LocalPath, file.MediaType, file.Length, file.Sha256)], []);
        var sending = scope.SendAsync("Same text");
        await mailbox.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Same visible text and file IDs do not make another conversation/profile the owner.
        var otherMailbox = new Mailbox();
        var newer = GoalInboxDraft.For(otherMailbox, "session:B");
        newer.Text = "Same text"; newer.Attachments = [file]; newer.SourceAttachmentIds = [file.Id];
        mailbox.Release.TrySetResult();
        var saved = await sending;
        Assert.NotNull(saved.Saved);
        Assert.False(scope.IsCurrent("B", mailbox));
        Assert.False(scope.IsCurrent("A", otherMailbox));
        Assert.Equal("Same text", newer.Text);
        Assert.Equal(file, Assert.Single(newer.Attachments));
        Assert.Empty(otherMailbox.Calls);
        Assert.Equal(Goal, Assert.Single(mailbox.Targets));
        Assert.Equal(file, Assert.Single(Assert.Single(mailbox.Calls).Attachments!));
    }

    [Fact]
    public async Task Attachment_only_message_is_explicit_host_description_and_keeps_refs_until_confirmed_retry()
    {
        var mailbox = new Mailbox { LoseAck = true };
        var file = File("only-file");
        var draft = new GoalInboxDraft { Attachments = [file], SourceAttachmentIds = [file.Id] };
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        Assert.Equal(GoalMessaging.AttachmentOnlyText, draft.Pending!.Text);
        Assert.Equal(file, Assert.Single(draft.Attachments));
        var key = draft.Pending.IdempotencyKey;
        Assert.NotNull(await draft.RetryAsync(mailbox));
        Assert.Empty(draft.Attachments); Assert.Empty(draft.SourceAttachmentIds);
        Assert.Equal(key, mailbox.Calls[^1].IdempotencyKey);
        Assert.Equal(file, Assert.Single(mailbox.Calls[^1].Attachments!));
        Assert.Equal(Goal, GoalMessageRouting.Resolve("@agent", [new(Goal, "conversation", "Agent", "agent", null, "Paused", 0)], [], true).Contact!.GoalId);
    }

    [Fact]
    public async Task Lost_ack_retry_keeps_exact_references_and_preserves_newer_files_and_text()
    {
        var mailbox = new Mailbox { LoseAck = true };
        var draft = new GoalInboxDraft { Text = "Original idea", SourceText = "@goal Original idea", Attachments = [File("first")], SourceAttachmentIds = ["source-first"] };
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        var command = draft.Pending!;
        Assert.Equal(File("first"), Assert.Single(command.Attachments!));
        Assert.Equal(new[] { "source-first" }, draft.PendingSourceAttachmentIds);
        draft.Text = "Newer idea"; draft.Attachments = [File("new")]; draft.SourceAttachmentIds = ["source-new"];
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        Assert.Single(mailbox.Calls);
        var receipt = await draft.RetryAsync(mailbox);
        Assert.NotNull(receipt);
        Assert.Same(command, mailbox.Calls[1]);
        Assert.Equal("Newer idea", draft.Text);
        Assert.Equal(File("new"), Assert.Single(draft.Attachments));
        Assert.Equal(new[] { "source-new" }, draft.SourceAttachmentIds);
        Assert.Single(mailbox.Messages);
    }

    [Fact]
    public async Task Same_text_with_changed_files_cannot_retarget_pending_command_and_success_clears_only_captured_draft()
    {
        var mailbox = new Mailbox { LoseAck = true };
        var draft = new GoalInboxDraft { Text = "Same text", Attachments = [File("first")], SourceAttachmentIds = ["first"] };
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        draft.Attachments = [File("second")]; draft.SourceAttachmentIds = ["second"];
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        Assert.Single(mailbox.Calls);
        Assert.NotNull(await draft.RetryAsync(mailbox));
        Assert.Equal(File("second"), Assert.Single(draft.Attachments));
        Assert.Equal("Same text", draft.Text);
        draft.Text = "Next idea";
        Assert.NotNull(await draft.SubmitAsync(mailbox, Goal));
        Assert.Empty(draft.Attachments); Assert.Empty(draft.SourceAttachmentIds); Assert.Equal("", draft.Text);
        Assert.Equal(File("second"), Assert.Single(mailbox.Calls[^1].Attachments!));
    }

    [Fact]
    public void Inbox_renders_attachment_name_and_host_unavailable_notice_as_text_without_file_link_or_false_read()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(new("saved", Goal, 1, GoalMessageKind.Correction, "Full original text", new("ui", "source", "user"),
            DateTimeOffset.UtcNow, GoalDeliveryState.Delivered, "delivery", "Transport accepted")
            { Attachments = [File("missing")], AttachmentIssue = "File missing. Reattach the original." });
        var cut = Render<GoalInboxPanel>(p => p.Add(c => c.Mailbox, mailbox).Add(c => c.InitialGoalId, Goal).Add(c => c.Poll, false));
        cut.WaitForAssertion(() => Assert.Contains("missing.txt", cut.Markup));
        Assert.Contains("Full original text", cut.Markup);
        Assert.Contains("File missing. Reattach the original.", cut.Markup);
        Assert.Contains("local to Code", cut.Markup);
        Assert.DoesNotContain("href=\"/local/", cut.Markup);
        Assert.Equal(GoalDeliveryState.Delivered, Assert.Single(mailbox.Messages).State);
    }

    [Fact]
    public async Task Positive_preadmission_rejection_keeps_editable_draft_instead_of_forcing_an_impossible_retry()
    {
        var mailbox = new Mailbox { RejectBeforeCommit = true };
        var draft = new GoalInboxDraft { Text = "Keep text", Attachments = [File("invalid")] };
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        Assert.Null(draft.Pending); Assert.Empty(mailbox.Messages);
        Assert.Equal("Keep text", draft.Text); Assert.Single(draft.Attachments);
        draft.Attachments = [File("replacement")];
        Assert.NotNull(await draft.SubmitAsync(mailbox, Goal));
        Assert.NotEqual(mailbox.Calls[0].IdempotencyKey, mailbox.Calls[1].IdempotencyKey);
        Assert.Single(mailbox.Messages);
    }

    [Fact]
    public async Task Rejected_retry_cannot_erase_an_earlier_unconfirmed_delivery_identity()
    {
        var mailbox = new Mailbox { LoseAck = true };
        var draft = new GoalInboxDraft { Text = "Same message", Attachments = [File("source")] };
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        var pending = draft.Pending;
        mailbox.RejectBeforeCommit = true;
        Assert.Null(await draft.RetryAsync(mailbox));
        Assert.Same(pending, draft.Pending);
        Assert.Contains("earlier delivery is still unconfirmed", draft.Error);
        Assert.NotNull(await draft.RetryAsync(mailbox));
        Assert.Single(mailbox.Messages);
        Assert.All(mailbox.Calls, command => Assert.Same(pending, command));
    }

    private sealed class Mailbox : IGoalMailbox, IGoalAttachmentMailbox
    {
        public bool LoseAck, RejectBeforeCommit;
        public List<GoalMessageCommand> Calls = [];
        public List<GoalInboxMessage> Messages = [];
        public List<Guid> Targets = [];
        public string? BlockStage;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Block(string stage)
        {
            if (BlockStage != stage) return;
            Entered.TrySetResult(); await Release.Task;
        }
        private static GoalContact Contact => new(Goal, "conversation", "Agent", "agent", null, "Paused", 0);
        public Task<GoalMailboxIdentity> GetIdentityAsync(CancellationToken ct = default) => Task.FromResult(new GoalMailboxIdentity("profile", "Code"));
        public async Task<IReadOnlyList<GoalContact>> ListAsync(CancellationToken ct = default) { await Block("list"); return [Contact]; }
        public async Task<IReadOnlyList<GoalMessageAttachment>> PrepareAttachmentsAsync(Guid goalId, IReadOnlyList<CodingAttachment> files, CancellationToken ct = default)
        {
            await Block("prepare");
            return files.Select(f => new GoalMessageAttachment(f.Id, f.FileName, f.LocalPath, f.MediaType, f.Length, f.Sha256)).ToArray();
        }
        public Task<GoalContact> RenameAsync(Guid goalId, string name, string handle, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalMessageThread> ReadAsync(Guid goalId, long afterSequence = 0, int take = 200, CancellationToken ct = default)
            => Task.FromResult(new GoalMessageThread(Contact, Messages.Where(m => m.Sequence > afterSequence).ToArray(), [], Messages.Select(m => m.Sequence).DefaultIfEmpty().Max(), false));
        public async Task<GoalInboxMessage> SendAsync(Guid goalId, GoalMessageCommand message, CancellationToken ct = default)
        {
            await Block("send"); Calls.Add(message); Targets.Add(goalId);
            if (RejectBeforeCommit) { RejectBeforeCommit = false; throw new GoalMessageRejectedException("Invalid files, not admitted"); }
            if (Messages.FirstOrDefault(m => m.Id == message.IdempotencyKey) is { } old) return old;
            var saved = new GoalInboxMessage(message.IdempotencyKey, goalId, Messages.Count + 1, message.Kind, message.Text,
                message.Origin, DateTimeOffset.UtcNow, GoalDeliveryState.Received) { Attachments = message.Attachments };
            Messages.Add(saved);
            if (LoseAck) { LoseAck = false; throw new IOException("Lost ACK"); }
            return saved;
        }
        public Task<GoalDelivery?> BeginDeliveryAsync(Guid goalId, string conversationId, string workerId, int maxCharacters = 16000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordDeliveryAsync(string deliveryId, GoalDeliveryState state, string? detail = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalReply> ReplyAsync(Guid goalId, string conversationId, string deliveryId, string messageId, string text, GoalDeliveryState disposition, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
