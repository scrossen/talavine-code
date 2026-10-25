using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalInstructionProofTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Store, FileGoalMailbox Mailbox, ContinualObjectiveLease Lease, Guid Id)> Seed()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await store.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "goal",
            ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Preserve existing data and implement the requested feature", ObjectiveSummary = "Feature",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;
        return (store, new(Paths, store), lease, goal.Id);
    }
    private static GoalMessageCommand Command(string id) => new(id, "Correction " + id, GoalMessageKind.Correction, new("code-ui", id, "owner"));
    private static async Task Consume(FileGoalMailbox mailbox, Guid id, string lease)
    {
        var delivery = (await mailbox.BeginDeliveryForWorkerAsync(id, "conversation", "worker", lease))!;
        foreach (var message in delivery.Messages.Reverse())
            await mailbox.ReplyForWorkerAsync(id, "conversation", delivery.Id, message.Id, "I incorporated this source.", GoalDeliveryState.Incorporated, lease);
    }

    [Fact]
    public async Task Instruction_order_is_insertion_order_even_when_acknowledgements_reverse_mutable_cursors()
    {
        var h = await Seed();
        var a = await h.Mailbox.SendAsync(h.Id, Command("first"));
        var b = await h.Mailbox.SendAsync(h.Id, Command("second"));
        var before = await h.Mailbox.GetInstructionSnapshotAsync(h.Id);
        await Consume(h.Mailbox, h.Id, h.Lease.Token);
        var snapshot = await h.Mailbox.GetInstructionSnapshotAsync(h.Id);
        Assert.Equal(new[] { "objective", a.Id, b.Id }, snapshot.Sources.Select(s => s.Id));
        Assert.Equal(before.Revision, snapshot.Revision);
        Assert.Equal(a.CreatedUtc, snapshot.Sources[1].CreatedUtc);
        var messages = (await h.Mailbox.ReadAsync(h.Id)).Messages;
        Assert.True(messages.Single(m => m.Id == a.Id).Sequence > messages.Single(m => m.Id == b.Id).Sequence);
    }

    [Fact]
    public async Task Acceptance_proof_survives_restart_but_binds_exact_current_inputs_output_and_all_receipts()
    {
        var h = await Seed(); await h.Mailbox.SendAsync(h.Id, Command("first")); await Consume(h.Mailbox, h.Id, h.Lease.Token);
        var snapshot = await h.Mailbox.GetInstructionSnapshotAsync(h.Id); var current = (await h.Store.GetAsync(h.Id))!;
        var evidence = FileGoalMailbox.AcceptanceEvidenceDigest(current); var output = "Full verified deliverable";
        Assert.True(await h.Mailbox.TryRecordAcceptanceAsync(snapshot, h.Lease.Token, evidence, ContinualDeliveryEvidence.OutputDigest(output)));
        Assert.True(FileGoalMailbox.CanComplete(Paths, current, output)); Assert.False(FileGoalMailbox.CanComplete(Paths, current, output + " changed"));
        Assert.False(FileGoalMailbox.CanComplete(Paths, current with { AttemptCount = current.AttemptCount + 1 }, output));
        Assert.False(FileGoalMailbox.CanComplete(Paths, current with { LeaseToken = "new-worker" }, output));
        var restored = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths));
        Assert.Equal(snapshot.Revision, (await restored.GetInstructionSnapshotAsync(h.Id)).Revision);
        var now = DateTimeOffset.UtcNow;
        var receipt = new ContinualToolReceipt(h.Id, current.AttemptCount, 1, "new-read", "host.read",
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ReadOnly, "{}", "New exact evidence", false, now, now);
        Assert.True(await h.Store.UpsertReceiptAsync(h.Id, h.Lease.Token, receipt));
        Assert.False(FileGoalMailbox.CanComplete(Paths, (await h.Store.GetAsync(h.Id))!, output));
        Assert.False(await h.Mailbox.TryRecordAcceptanceAsync(snapshot, h.Lease.Token, evidence, ContinualDeliveryEvidence.OutputDigest(output)));
    }

    [Fact]
    public async Task New_message_during_audit_and_deleted_goal_cannot_receive_a_stale_proof()
    {
        var h = await Seed(); await h.Mailbox.SendAsync(h.Id, Command("first")); await Consume(h.Mailbox, h.Id, h.Lease.Token);
        var snapshot = await h.Mailbox.GetInstructionSnapshotAsync(h.Id); var current = (await h.Store.GetAsync(h.Id))!;
        var evidence = FileGoalMailbox.AcceptanceEvidenceDigest(current); var output = ContinualDeliveryEvidence.OutputDigest("Done");
        await new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths)).SendAsync(h.Id, Command("second"));
        await Consume(h.Mailbox, h.Id, h.Lease.Token);
        Assert.False(await h.Mailbox.TryRecordAcceptanceAsync(snapshot, h.Lease.Token, evidence, output));
        Assert.False(FileGoalMailbox.CanComplete(Paths, (await h.Store.GetAsync(h.Id))!, "Done"));
        await h.Store.DeleteConversationAsync("conversation");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Mailbox.TryRecordAcceptanceAsync(snapshot, h.Lease.Token, evidence, output));
        Assert.Empty(await h.Mailbox.ListAsync());
    }

    [Fact]
    public async Task Retired_lease_cannot_save_a_verdict_and_questions_never_become_hidden_completion_criteria()
    {
        var h = await Seed(); await h.Mailbox.SendAsync(h.Id, Command("first")); await Consume(h.Mailbox, h.Id, h.Lease.Token);
        var snapshot = await h.Mailbox.GetInstructionSnapshotAsync(h.Id); var current = (await h.Store.GetAsync(h.Id))!;
        await h.Store.ControlAsync(h.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow);
        Assert.False(await h.Mailbox.TryRecordAcceptanceAsync(snapshot, h.Lease.Token, FileGoalMailbox.AcceptanceEvidenceDigest(current), ContinualDeliveryEvidence.OutputDigest("Done")));
        await h.Mailbox.SendAsync(h.Id, Command("question") with { Kind = GoalMessageKind.Question });
        Assert.Equal(snapshot.Revision, (await h.Mailbox.GetInstructionSnapshotAsync(h.Id)).Revision);
        Assert.True(await h.Mailbox.HasPendingAsync(h.Id));
    }
    [Fact]
    public async Task Explicit_steer_follows_all_existing_inbox_sources_and_invalidates_the_old_completion_proof()
    {
        var h = await Seed(); var first = await h.Mailbox.SendAsync(h.Id, Command("first"));
        await Consume(h.Mailbox, h.Id, h.Lease.Token);
        var snapshot = await h.Mailbox.GetInstructionSnapshotAsync(h.Id); var goal = (await h.Store.GetAsync(h.Id))!;
        Assert.True(await h.Mailbox.TryRecordAcceptanceAsync(snapshot, h.Lease.Token, FileGoalMailbox.AcceptanceEvidenceDigest(goal), ContinualDeliveryEvidence.OutputDigest("Done")));
        Assert.True(FileGoalMailbox.CanComplete(Paths, goal, "Done"));
        var steerTime = DateTimeOffset.UtcNow;
        Assert.True(await h.Store.UpdateAsync(h.Id, goal.Version, "An explicit new direction after the inbox", steerTime));
        var next = await h.Mailbox.GetInstructionSnapshotAsync(h.Id);
        Assert.Equal(new[] { first.Id, "objective" }, next.Sources.Select(s => s.Id));
        Assert.Equal(steerTime, next.Sources[1].CreatedUtc); Assert.NotEqual(snapshot.Revision, next.Revision);
        Assert.False(FileGoalMailbox.CanComplete(Paths, (await h.Store.GetAsync(h.Id))!, "Done"));
        var later = await h.Mailbox.SendAsync(h.Id, Command("after-steer"));
        Assert.Equal(new[] { first.Id, "objective", later.Id }, (await h.Mailbox.GetInstructionSnapshotAsync(h.Id)).Sources.Select(s => s.Id));
    }
}
