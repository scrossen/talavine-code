using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalMailboxTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Goals, FileGoalMailbox Mailbox, ContinualObjectiveSnapshot Goal)> Seed(string conversation = "conversation")
    {
        var goals = new FileCodingGoalStore(Paths);
        var goal = await goals.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = conversation,
            ConversationId = conversation, ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Finish the existing job", ObjectiveSummary = "Existing work",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        return (goals, new FileGoalMailbox(Paths, goals), goal);
    }

    private static GoalMessageCommand Command(string key = "message", string text = "An idea for the existing job", GoalMessageKind kind = GoalMessageKind.Idea)
        => new(key, text, kind, new("code-ui", key, "local-user"));

    private static async Task<GoalDelivery?> WorkerDelivery(FileCodingGoalStore goals, FileGoalMailbox mailbox,
        Guid goalId, string conversationId, string workerId, int maxCharacters = 16000)
    {
        var current = await goals.GetAsync(goalId);
        return await mailbox.BeginDeliveryForWorkerAsync(goalId, conversationId, workerId,
            current?.LeaseToken ?? "inactive-worker", maxCharacters);
    }

    private static async Task<GoalReply> WorkerReply(FileCodingGoalStore goals, FileGoalMailbox mailbox,
        Guid goalId, string conversationId, string deliveryId, string messageId, string text, GoalDeliveryState disposition)
    {
        var current = await goals.GetAsync(goalId);
        return await mailbox.ReplyForWorkerAsync(goalId, conversationId, deliveryId, messageId, text, disposition,
            current?.LeaseToken ?? "retired-worker");
    }

    private static async Task<ContinualObjectiveLease> Claim(FileCodingGoalStore goals, ContinualObjectiveSnapshot goal)
        => (await goals.TryClaimAsync(goal.Id, goal.Version, "worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;

    [Fact]
    public async Task Authenticated_nonquestion_tool_ban_survives_acknowledgement_restart_and_new_attempt()
    {
        var h = await Seed();
        var lease = await Claim(h.Goals, h.Goal);
        await h.Mailbox.SendAsync(h.Goal.Id, Command("ban", "Do not use tools.", GoalMessageKind.Correction));
        Assert.True(await h.Mailbox.ToolsWithheldAsync(h.Goal.Id, "conversation", lease.Token));
        var delivery = (await h.Mailbox.BeginDeliveryForWorkerAsync(h.Goal.Id, "conversation", "worker", lease.Token))!;
        foreach (var message in delivery.Messages)
            await h.Mailbox.ReplyForWorkerAsync(h.Goal.Id, "conversation", delivery.Id, message.Id, "I incorporated the restriction.", GoalDeliveryState.Incorporated, lease.Token);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await h.Goals.ApplyTransitionAsync(h.Goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled,
            now, now, now, "Existing artifact retained", null, null, "Continue", 0)));
        var next = await Claim(h.Goals, (await h.Goals.GetAsync(h.Goal.Id))!);
        var restarted = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.True(await restarted.ToolsWithheldAsync(h.Goal.Id, "conversation", next.Token));
        await h.Mailbox.SendAsync(h.Goal.Id, Command("ordinary-widening", "You may use tools now.", GoalMessageKind.Answer));
        Assert.True(await restarted.ToolsWithheldAsync(h.Goal.Id, "conversation", next.Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.ToolsWithheldAsync(h.Goal.Id, "conversation", lease.Token));
    }

    [Fact]
    public async Task A_question_does_not_persistently_restrict_the_goal_and_explicit_objective_replacement_sets_a_new_baseline()
    {
        var h = await Seed();
        var lease = await Claim(h.Goals, h.Goal);
        await h.Mailbox.SendAsync(h.Goal.Id, Command("question", "Do not use tools; answer this question in plain text.", GoalMessageKind.Question));
        Assert.False(await h.Mailbox.ToolsWithheldAsync(h.Goal.Id, "conversation", lease.Token));
        await h.Mailbox.SendAsync(h.Goal.Id, Command("ban", "Do not use tools.", GoalMessageKind.Correction));
        Assert.True(await h.Mailbox.ToolsWithheldAsync(h.Goal.Id, "conversation", lease.Token));
        var current = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Goals.UpdateAsync(current.Id, current.Version, "Implement the revised feature using the current permission rules.", DateTimeOffset.UtcNow));
        current = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
        var resumed = await Claim(h.Goals, (await h.Goals.GetAsync(h.Goal.Id))!);
        Assert.False(await h.Mailbox.ToolsWithheldAsync(h.Goal.Id, "conversation", resumed.Token));
    }

    [Fact]
    public async Task Retired_worker_cannot_claim_new_input_after_pause_resume_and_a_new_lease()
    {
        var h = await Seed();
        var firstLease = await Claim(h.Goals, h.Goal);
        var current = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        current = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
        var secondLease = await Claim(h.Goals, (await h.Goals.GetAsync(h.Goal.Id))!);
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command("after-resume"));
        Assert.Null(await h.Mailbox.BeginDeliveryForWorkerAsync(h.Goal.Id, "conversation", "retired-worker-A", firstLease.Token));
        Assert.Equal(GoalDeliveryState.Received, Assert.Single((await h.Mailbox.ReadAsync(h.Goal.Id)).Messages).State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Mailbox.BeginDeliveryAsync(h.Goal.Id, "conversation", "unbound-worker"));
        var delivery = (await h.Mailbox.BeginDeliveryForWorkerAsync(h.Goal.Id, "conversation", "current-worker-B", secondLease.Token))!;
        Assert.Equal(message.Id, Assert.Single(delivery.Messages).Id);
        Assert.Equal("current-worker-B", delivery.WorkerId);
    }

    [Fact]
    public async Task Name_and_message_do_not_replace_goal_progress_authority_or_pause()
    {
        var h = await Seed();
        Assert.True(await h.Goals.ControlAsync(h.Goal.Id, h.Goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var before = JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id));
        var contact = await h.Mailbox.RenameAsync(h.Goal.Id, "Pressroom", "pressroom", 0);
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        Assert.Equal("pressroom", contact.Handle);
        Assert.Equal(GoalDeliveryState.Received, message.State);
        Assert.Null(await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Goals.GetAsync(h.Goal.Id)));
    }

    [Fact]
    public async Task Profile_identity_name_and_message_survive_independent_store_restart()
    {
        var h = await Seed();
        var identity = await h.Mailbox.GetIdentityAsync();
        await h.Mailbox.RenameAsync(h.Goal.Id, "Pressroom", "pressroom", 0);
        var original = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        var restored = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.Equal(identity, await restored.GetIdentityAsync());
        Assert.Equal("Pressroom", Assert.Single(await restored.ListAsync()).Name);
        Assert.Equal(original, Assert.Single((await restored.ReadAsync(h.Goal.Id)).Messages));
    }

    [Fact]
    public async Task Duplicate_command_returns_same_receipt_but_changed_body_or_target_is_rejected()
    {
        var h = await Seed();
        var first = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        Assert.Equal(first, await h.Mailbox.SendAsync(h.Goal.Id, Command()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Mailbox.SendAsync(h.Goal.Id, Command(text: "A different body")));
        var other = await Seed("other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Mailbox.SendAsync(other.Goal.Id, Command()));
        Assert.Single((await h.Mailbox.ReadAsync(h.Goal.Id)).Messages);
    }

    [Fact]
    public async Task Closed_goal_retains_history_and_duplicate_receipt_but_rejects_new_stranded_input()
    {
        var h = await Seed();
        var saved = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        var current = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Cancel, DateTimeOffset.UtcNow));
        Assert.Equal(saved, await h.Mailbox.SendAsync(h.Goal.Id, Command()));
        await Assert.ThrowsAsync<GoalMailboxClosedException>(() => h.Mailbox.SendAsync(h.Goal.Id, Command("new", "New work")));
        var thread = await h.Mailbox.ReadAsync(h.Goal.Id);
        Assert.True(thread.Contact.IsClosed);
        Assert.Equal(saved, Assert.Single(thread.Messages));
    }

    [Fact]
    public async Task Competing_writers_preserve_all_messages_and_only_one_name_revision_wins()
    {
        var h = await Seed();
        var other = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? h.Mailbox : other).SendAsync(h.Goal.Id, Command("message-" + index))));
        Assert.Equal(20, (await h.Mailbox.ReadAsync(h.Goal.Id)).Messages.Count);
        var name = await h.Mailbox.RenameAsync(h.Goal.Id, "First", "first", 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.RenameAsync(h.Goal.Id, "Stale", "stale", 0));
        Assert.Equal(name, Assert.Single(await other.ListAsync()));
    }

    [Fact]
    public async Task Handle_collision_never_readdresses_an_existing_goal()
    {
        var h = await Seed();
        var other = await Seed("other");
        await h.Mailbox.RenameAsync(h.Goal.Id, "First", "pressroom", 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Mailbox.RenameAsync(other.Goal.Id, "Second", "PRESSROOM", 0));
        Assert.NotEqual("pressroom", (await other.Mailbox.ListAsync()).Single(contact => contact.GoalId == other.Goal.Id).Handle);
    }

    [Fact]
    public async Task Renamed_handle_cannot_be_reused_to_misdirect_an_old_email_or_mention()
    {
        var h = await Seed();
        var other = await Seed("other");
        var contact = await h.Mailbox.RenameAsync(h.Goal.Id, "Pressroom", "pressroom", 0);
        await h.Mailbox.RenameAsync(h.Goal.Id, "Awareness", "awareness", contact.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Mailbox.RenameAsync(other.Goal.Id, "Different job", "pressroom", 0));
    }

    [Fact]
    public async Task Delete_wins_over_a_late_message_name_and_worker_reply()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        await h.Goals.DeleteConversationAsync("conversation");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Mailbox.SendAsync(h.Goal.Id, Command("late")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Mailbox.RenameAsync(h.Goal.Id, "Late", "late", 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "conversation", delivery.Id, message.Id, "Late reply", GoalDeliveryState.Incorporated));
        Assert.Empty(await h.Mailbox.ListAsync());
    }

    [Fact]
    public async Task Transport_acceptance_is_not_read_and_a_restart_never_redelivers_it()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "native-thread-and-turn"))!;
        Assert.All(delivery.Messages, value => { Assert.Equal(delivery.Id, value.DeliveryId); Assert.Equal(GoalDeliveryState.Presenting, value.State); });
        await h.Mailbox.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Delivered);
        var restarted = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.Null(await WorkerDelivery(h.Goals, restarted, h.Goal.Id, "conversation", "new-worker"));
        Assert.Equal(GoalDeliveryState.Delivered, Assert.Single((await restarted.ReadAsync(h.Goal.Id)).Messages).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Received));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Read));
        Assert.Contains(message.Text, delivery.ContextText);
    }

    [Fact]
    public async Task Crash_after_claim_remains_uncertain_instead_of_silently_replaying()
    {
        var h = await Seed();
        await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        var restarted = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.Null(await WorkerDelivery(h.Goals, restarted, h.Goal.Id, "conversation", "worker-2"));
        await restarted.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Uncertain, "The transport's acknowledgement was lost.");
        Assert.Equal(GoalDeliveryState.Uncertain, Assert.Single((await restarted.ReadAsync(h.Goal.Id)).Messages).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Received));
    }

    [Fact]
    public async Task Pause_rejects_a_late_semantic_ack_inside_the_goal_lock()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        var current = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "conversation", delivery.Id, message.Id, "Too late", GoalDeliveryState.Incorporated));
        Assert.Empty((await h.Mailbox.ReadAsync(h.Goal.Id)).Replies);
        Assert.Equal(ContinualObjectiveStatus.Paused, (await h.Goals.GetAsync(h.Goal.Id))!.Status);
    }

    [Fact]
    public async Task Retired_worker_cannot_ack_after_pause_resume_and_a_new_worker_claim()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        var oldLease = await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker-A"))!;
        var current = (await h.Goals.GetAsync(h.Goal.Id))!;
        await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow);
        current = (await h.Goals.GetAsync(current.Id))!;
        await h.Goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow);
        current = (await h.Goals.GetAsync(current.Id))!;
        var newLease = await Claim(h.Goals, current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Mailbox.ReplyForWorkerAsync(current.Id, "conversation", delivery.Id,
            message.Id, "Stale acknowledgement", GoalDeliveryState.Incorporated, oldLease.Token));
        Assert.Empty((await h.Mailbox.ReadAsync(current.Id)).Replies);
        // The actual new worker may reconcile the existing thread and acknowledge the original input without replaying it.
        await h.Mailbox.ReplyForWorkerAsync(current.Id, "conversation", delivery.Id, message.Id,
            "I read the retained input in the saved thread.", GoalDeliveryState.Incorporated, newLease.Token);
        Assert.Single((await h.Mailbox.ReadAsync(current.Id)).Replies);
    }

    [Fact]
    public async Task An_ordinary_chat_cannot_impersonate_the_goal_worker()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Mailbox.ReplyAsync(h.Goal.Id, "conversation", delivery.Id,
            message.Id, "A normal chat reply", GoalDeliveryState.Incorporated));
    }

    [Fact]
    public async Task Positive_pre_dispatch_failure_can_release_the_same_message()
    {
        var h = await Seed();
        await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        await h.Mailbox.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Received, "No bytes were written.");
        var retried = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        Assert.Equal(delivery.Messages.Single().Id, retried.Messages.Single().Id);
        Assert.NotEqual(delivery.Id, retried.Id);
    }

    [Fact]
    public async Task Bound_reply_survives_ack_race_and_cannot_cross_conversations()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "other", delivery.Id, message.Id, "Reply", GoalDeliveryState.Incorporated));
        var reply = await WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "conversation", delivery.Id, message.Id, "I incorporated the premise.", GoalDeliveryState.Incorporated);
        Assert.Equal(reply, await WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "conversation", delivery.Id, message.Id, "I incorporated the premise.", GoalDeliveryState.Incorporated));
        await h.Mailbox.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Delivered);
        var thread = await h.Mailbox.ReadAsync(h.Goal.Id);
        Assert.Equal(GoalDeliveryState.Incorporated, Assert.Single(thread.Messages).State);
        Assert.Single(thread.Replies);
    }

    [Fact]
    public async Task State_updates_are_visible_after_a_previous_cursor_without_replacing_input()
    {
        var h = await Seed();
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        var cursor = (await h.Mailbox.ReadAsync(h.Goal.Id)).NextSequence;
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        await h.Mailbox.RecordDeliveryAsync(delivery.Id, GoalDeliveryState.Delivered);
        var changed = Assert.Single((await h.Mailbox.ReadAsync(h.Goal.Id, cursor)).Messages);
        Assert.Equal(message.Id, changed.Id);
        Assert.Equal(message.Text, changed.Text);
        Assert.Equal(message.CreatedUtc, changed.CreatedUtc);
        Assert.True(changed.Sequence > cursor);
    }

    [Fact]
    public async Task Goal_cannot_finish_between_message_acceptance_and_incorporation()
    {
        var h = await Seed();
        var lease = await Claim(h.Goals, h.Goal);
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command());
        var now = DateTimeOffset.UtcNow;
        var completed = new ContinualObjectiveTransition(ContinualObjectiveStatus.Completed, now, now, null, "Done", null, "proof", null, 0);
        Assert.False(await h.Goals.ApplyTransitionAsync(h.Goal.Id, lease.Token, completed));
        var continued = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.RetryScheduled, continued.Status);
        Assert.Null(continued.LeaseToken);
        lease = await Claim(h.Goals, continued);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        await WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "conversation", delivery.Id, message.Id, "Included in the work and verified.", GoalDeliveryState.Incorporated);
        // A worker acknowledgement establishes consumption, never independent completion proof.
        var baseline = (await h.Goals.GetAsync(h.Goal.Id))!;
        Assert.False(FileGoalMailbox.CanComplete(Paths, baseline, "Done"));
        Assert.True(await h.Mailbox.TryRecordAcceptanceAsync(await h.Mailbox.GetInstructionSnapshotAsync(h.Goal.Id), lease.Token,
            FileGoalMailbox.AcceptanceEvidenceDigest(baseline), ContinualDeliveryEvidence.OutputDigest("Done")));
        Assert.True(await h.Goals.ApplyTransitionAsync(h.Goal.Id, lease.Token, completed));
        Assert.Equal(h.Goal.ObjectiveText, (await h.Goals.GetAsync(h.Goal.Id))!.ObjectiveText);
    }

    [Fact]
    public async Task Credentials_are_redacted_before_inbox_context_and_reply_persistence()
    {
        var h = await Seed();
        var secret = "A-private-fixture-password";
        var message = await h.Mailbox.SendAsync(h.Goal.Id, Command(text: "An idea; password=" + secret));
        Assert.DoesNotContain(secret, message.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Mailbox.SendAsync(h.Goal.Id, Command(text: "An idea; password=Changed-private-value")));
        await Claim(h.Goals, h.Goal);
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker"))!;
        Assert.DoesNotContain(secret, delivery.ContextText);
        var reply = await WorkerReply(h.Goals, h.Mailbox, h.Goal.Id, "conversation", delivery.Id, message.Id, "Incorporated; password=" + secret, GoalDeliveryState.Incorporated);
        Assert.DoesNotContain(secret, reply.Text);
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(Path.Combine(Paths.RootDirectory, "goals", "messages.json")));
    }

    [Fact]
    public async Task Oversized_input_is_rejected_and_delivery_budget_never_truncates_a_message()
    {
        var h = await Seed();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Mailbox.SendAsync(h.Goal.Id, Command(text: new string('a', GoalMessaging.MaximumMessageCharacters + 1))));
        var text = new string('x', 6000);
        await h.Mailbox.SendAsync(h.Goal.Id, Command(text: text));
        await Claim(h.Goals, h.Goal);
        Assert.Null(await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker", 1500));
        var delivery = (await WorkerDelivery(h.Goals, h.Mailbox, h.Goal.Id, "conversation", "worker", 16000))!;
        Assert.Equal(text, Assert.Single(delivery.Messages).Text);
        Assert.Contains(text, delivery.ContextText);
    }
}
