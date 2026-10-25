using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class PausedGoalMailboxStatusTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Goals, FileGoalMailbox Mailbox, ContinualObjectiveSnapshot Goal)> Seed()
    {
        var goals = new FileCodingGoalStore(Paths);
        var goal = await goals.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "conversation",
            ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Finish the existing job", ObjectiveSummary = "Existing work",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        return (goals, new FileGoalMailbox(Paths, goals), goal);
    }

    private static GoalMessageCommand Command(string key, GoalMessageKind kind = GoalMessageKind.Question)
        => new(key, "Why did the work stop?", kind, new("code-ui", key, "local-user"));

    private string MailboxPath => Path.Combine(Paths.RootDirectory, "goals", "messages.json");

    [Fact]
    public async Task Paused_send_gets_a_persistent_host_receipt_without_reading_input_resuming_or_accepting_work()
    {
        var h = await Seed();
        Assert.True(await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var before = JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id));
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command("question"));
        var thread = await h.Mailbox.ReadAsync(h.Goal.Id);
        var notice = Assert.Single(thread.Replies);
        Assert.Equal(GoalReplyOrigin.HostStatus, notice.Origin);
        Assert.Equal(GoalDeliveryState.Received, notice.Disposition);
        Assert.Equal(message.Id, notice.MessageId);
        Assert.Equal(thread.Contact.GoalVersion, notice.ObservedGoalVersion);
        Assert.Contains("had not read", notice.Text);
        Assert.Contains("paused", notice.Text);
        Assert.Equal(message, Assert.Single(thread.Messages));
        Assert.Null(message.DeliveryId);
        Assert.True(await h.Mailbox.HasPendingAsync(h.Goal.Id));
        Assert.False(FileGoalMailbox.CanComplete(Paths, (await h.Goals.GetAsync(h.Goal.Id))!, "Done"));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id)));

        var restarted = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.Equal(notice, Assert.Single((await restarted.ReadAsync(h.Goal.Id)).Replies));
        Assert.Equal(message, await restarted.SendAsync(h.Goal.Id, Command("question")));
        Assert.Single((await restarted.ReadAsync(h.Goal.Id)).Replies);
        Assert.True(await restarted.HasPendingAsync(h.Goal.Id));
        // Older hosts ignore StatusNotices; their old 'any reply answers a question'
        // logic must still see no semantic reply after an application downgrade.
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(MailboxPath));
        Assert.Equal(0, persisted.RootElement.GetProperty("replies").GetArrayLength());
        Assert.Equal(1, persisted.RootElement.GetProperty("statusNotices").GetArrayLength());
    }

    [Fact]
    public async Task Reading_backfills_saved_received_input_once_and_polling_does_not_rewrite_the_mailbox()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command("saved-before-pause"));
        Assert.Empty((await h.Mailbox.ReadAsync(h.Goal.Id)).Replies);
        var cursor = (await h.Mailbox.ReadAsync(h.Goal.Id)).NextSequence;
        Assert.True(await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var before = JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id));
        var changed = await h.Mailbox.ReadAsync(h.Goal.Id, cursor);
        Assert.Empty(changed.Messages);
        Assert.Equal(message.Id, Assert.Single(changed.Replies).MessageId);
        Assert.True(changed.NextSequence > cursor);
        var saved = await File.ReadAllBytesAsync(MailboxPath);
        var oldTime = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(MailboxPath, oldTime);
        var time = File.GetLastWriteTimeUtc(MailboxPath);
        var latest = await h.Mailbox.ReadAsync(h.Goal.Id, changed.NextSequence);
        Assert.Empty(latest.Messages); Assert.Empty(latest.Replies);
        Assert.Equal(changed.NextSequence, latest.NextSequence);
        Assert.Equal(saved, await File.ReadAllBytesAsync(MailboxPath));
        Assert.Equal(time, File.GetLastWriteTimeUtc(MailboxPath));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id)));
    }

    [Fact]
    public async Task Competing_read_backfills_and_sends_preserve_all_messages_and_only_one_receipt_per_message()
    {
        var h = await Seed();
        var existing = await h.Mailbox.SendAsync(h.Goal.Id, Command("original"));
        Assert.True(await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var before = JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id));
        var other = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var box = index % 2 == 0 ? h.Mailbox : other;
            await box.ReadAsync(h.Goal.Id);
            await box.SendAsync(h.Goal.Id, Command("new-" + index));
            await box.ReadAsync(h.Goal.Id);
        }));
        var thread = await h.Mailbox.ReadAsync(h.Goal.Id);
        Assert.Equal(13, thread.Messages.Count); Assert.Equal(13, thread.Replies.Count);
        Assert.Equal(existing, thread.Messages.Single(message => message.Id == existing.Id));
        Assert.All(thread.Messages, message =>
        {
            Assert.Equal(GoalDeliveryState.Received, message.State); Assert.Null(message.DeliveryId);
            Assert.Single(thread.Replies, reply => reply.MessageId == message.Id);
        });
        Assert.All(thread.Replies, reply => Assert.Equal(GoalReplyOrigin.HostStatus, reply.Origin));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id)));
    }

    [Fact]
    public async Task Delete_wins_over_delayed_backfill_and_cannot_resurrect_a_goal_or_reply()
    {
        var h = await Seed();
        await h.Mailbox.SendAsync(h.Goal.Id, Command("original"));
        Assert.True(await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var delayed = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.Single(await delayed.ListAsync()); // Retain a stale contact across deletion.
        await h.Goals.DeleteConversationAsync("conversation");
        var before = await File.ReadAllBytesAsync(MailboxPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => delayed.ReadAsync(h.Goal.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => delayed.SendAsync(h.Goal.Id, Command("late")));
        Assert.Equal(before, await File.ReadAllBytesAsync(MailboxPath));
        Assert.Empty(await delayed.ListAsync());
        Assert.Null(await h.Goals.GetAsync(h.Goal.Id));
    }

    [Fact]
    public async Task Host_receipt_does_not_replace_a_later_real_worker_answer_or_allow_completion_before_it()
    {
        var h = await Seed();
        await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow);
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command("question"));
        var current = (await h.Goals.GetAsync(h.Goal.Id))!;
        await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow);
        current = (await h.Goals.GetAsync(h.Goal.Id))!;
        var lease = (await h.Goals.TryClaimAsync(current.Id, current.Version, "actual-worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;
        Assert.True(await h.Mailbox.HasPendingAsync(current.Id));
        Assert.False(FileGoalMailbox.CanComplete(Paths, (await h.Goals.GetAsync(h.Goal.Id))!, "Done"));
        var delivery = (await h.Mailbox.BeginDeliveryForWorkerAsync(current.Id, "conversation", "actual-worker", lease.Token))!;
        Assert.Equal(message.Id, Assert.Single(delivery.Messages).Id);
        var reply = await h.Mailbox.ReplyForWorkerAsync(current.Id, "conversation", delivery.Id, message.Id,
            "I read your question and this is the answer.", GoalDeliveryState.Replied, lease.Token);
        Assert.Equal(GoalReplyOrigin.Agent, reply.Origin);
        Assert.False(await h.Mailbox.HasPendingAsync(current.Id));
        var thread = await h.Mailbox.ReadAsync(current.Id);
        Assert.Equal(2, thread.Replies.Count);
        Assert.Equal(GoalDeliveryState.Replied, Assert.Single(thread.Messages).State);
    }

    [Fact]
    public async Task Held_status_exposes_the_current_host_blocker_without_changing_evidence_or_claiming_a_model_answer()
    {
        var h = await Seed();
        await h.Goals.HoldAsync(h.Goal.Id, h.Goal.Version, "Inspect a prior uncertain action; do not repeat it.", DateTimeOffset.UtcNow);
        var before = JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id));
        await h.Mailbox.SendAsync(h.Goal.Id, Command("question"));
        var thread = await h.Mailbox.ReadAsync(h.Goal.Id);
        Assert.Contains("outcome inspection", thread.Contact.StatusDetail);
        Assert.Contains("prior uncertain action", thread.Contact.Blocker);
        Assert.Contains("prior uncertain action", Assert.Single(thread.Replies).Text);
        Assert.Equal(before, JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id)));
    }

    [Fact]
    public async Task Held_received_instruction_keeps_its_original_chronology_and_stays_pending()
    {
        var h = await Seed();
        await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow);
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command("correction", GoalMessageKind.Correction));
        var snapshot = await h.Mailbox.GetInstructionSnapshotAsync(h.Goal.Id);
        await h.Mailbox.ReadAsync(h.Goal.Id);
        Assert.True(await h.Mailbox.HasPendingAsync(h.Goal.Id));
        Assert.Equal(snapshot.Revision, (await h.Mailbox.GetInstructionSnapshotAsync(h.Goal.Id)).Revision);
        Assert.Equal(message, Assert.Single((await h.Mailbox.ReadAsync(h.Goal.Id)).Messages));
    }

    [Fact]
    public async Task Contact_exposes_new_receipt_failure_cursor_even_when_goal_version_does_not_change()
    {
        var h = await Seed(); var now = DateTimeOffset.UtcNow;
        var lease = (await h.Goals.TryClaimAsync(h.Goal.Id, h.Goal.Version, "worker", now, now.AddMinutes(5)))!;
        var before = Assert.Single(await h.Mailbox.ListAsync());
        var receipt = new ContinualToolReceipt(h.Goal.Id, (await h.Goals.GetAsync(h.Goal.Id))!.AttemptCount, 1, "call", "fs.read_file",
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ReadOnly, "{}", "New read failure", true, now, now);
        Assert.True(await h.Goals.UpsertReceiptAsync(h.Goal.Id, lease.Token, receipt));
        var current = Assert.Single(await h.Mailbox.ListAsync());
        Assert.Equal(before.GoalVersion, current.GoalVersion);
        Assert.Equal(0, before.GoalEventSequence); Assert.Equal(1, current.GoalEventSequence);
        Assert.Equal("New read failure", current.Blocker);
        Assert.Empty((await h.Mailbox.ReadAsync(h.Goal.Id)).Replies);
    }

    [Fact]
    public void Old_replies_keep_agent_provenance_and_new_status_replies_roundtrip_distinctly()
    {
        var old = JsonSerializer.Deserialize<GoalReply>("{\"Id\":\"old\",\"GoalId\":\"11111111-1111-1111-1111-111111111111\",\"MessageId\":\"message\",\"Sequence\":1,\"Text\":\"Answer\",\"Disposition\":5,\"CreatedUtc\":\"2026-01-01T00:00:00Z\"}")!;
        Assert.True(GoalMailboxStatus.IsAgentReply(old));
        var status = old with { Origin = GoalReplyOrigin.HostStatus, ObservedGoalVersion = 42, Disposition = GoalDeliveryState.Received };
        Assert.False(GoalMailboxStatus.IsAgentReply(JsonSerializer.Deserialize<GoalReply>(JsonSerializer.Serialize(status))!));
    }

    [Theory]
    [InlineData(ContinualObjectiveStatus.Paused, true)]
    [InlineData(ContinualObjectiveStatus.WaitingForInput, true)]
    [InlineData(ContinualObjectiveStatus.Uncertain, true)]
    [InlineData(ContinualObjectiveStatus.DeliveryBlocked, true)]
    [InlineData(ContinualObjectiveStatus.AttentionRequired, true)]
    [InlineData(ContinualObjectiveStatus.InProgress, false)]
    [InlineData(ContinualObjectiveStatus.RetryScheduled, false)]
    [InlineData(ContinualObjectiveStatus.Completed, false)]
    [InlineData(ContinualObjectiveStatus.Cancelled, false)]
    public void Status_policy_distinguishes_a_hold_from_running_scheduled_or_terminal_work(ContinualObjectiveStatus status, bool held)
        => Assert.Equal(held, GoalMailboxStatus.Detail(status, null) is not null);

    [Fact]
    public void An_attention_retry_is_not_presented_as_indefinitely_blocked()
        => Assert.Null(GoalMailboxStatus.Detail(ContinualObjectiveStatus.AttentionRequired, DateTimeOffset.UtcNow.AddMinutes(1)));
}
