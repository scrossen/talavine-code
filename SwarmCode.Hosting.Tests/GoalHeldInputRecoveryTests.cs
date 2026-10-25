using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalHeldInputRecoveryTests : TempDirTest
{
    private const string Artifact = "The previously saved complete artifact must remain intact.";
    private sealed record Seeded(Guid Id, IReadOnlyList<GoalInboxMessage> Messages, IReadOnlyList<string> InitialContexts);
    private static GoalMessageCommand Command(int i, string body) => new("source-" + i, body, GoalMessageKind.Correction, new("code-ui", "source-" + i, "owner"));

    private async Task<Seeded> Seed(GoalDeliveryState state, int count = 1, bool large = false, bool steer = false, bool referenceOnly = false)
    {
        var store = new FileCodingGoalStore(Paths); var mailbox = new FileGoalMailbox(Paths, store);
        var now = DateTimeOffset.UtcNow;
        var goal = await store.CreateOrJoinAsync(new()
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "held-test", ConversationId = "conversation",
            ExecutorKey = CodingGoalOrchestrator.ExecutorKey, ObjectiveText = "Finish only the remaining work", ObjectiveSummary = "Remaining work",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, now);
        var messages = new List<GoalInboxMessage>();
        for (var i = 0; i < count; i++)
        {
            var command = Command(i, "SOURCE_START_" + i + " PRIOR_EFFECT_ACTION: retain the prior action and its outcome. "
                + (large ? new string((char)('a' + i), 6800) : "Inspect this saved instruction.") + " SOURCE_END_" + i);
            if (referenceOnly)
            {
                var file = await new AttachmentStore(Paths).ImportAsync("conversation", "reference.png",
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("synthetic reference bytes")));
                command = command with { Text = "", Attachments = await mailbox.PrepareAttachmentsAsync(goal.Id, [file]) };
            }
            messages.Add(await mailbox.SendAsync(goal.Id, command));
        }
        if (steer)
        {
            Assert.True(await store.UpdateAsync(goal.Id, goal.Version, "New explicit direction after the queued input", DateTimeOffset.UtcNow));
            goal = (await store.GetAsync(goal.Id))!;
            Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
            goal = (await store.GetAsync(goal.Id))!;
        }
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "old-worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;
        var contexts = new List<string>();
        while (await mailbox.BeginDeliveryForWorkerAsync(goal.Id, "conversation", "old-bound-worker", lease.Token) is { } delivery)
        {
            contexts.Add(delivery.ContextText);
            if (state is GoalDeliveryState.Uncertain or GoalDeliveryState.Delivered) await mailbox.RecordDeliveryAsync(delivery.Id, state);
            if (state == GoalDeliveryState.Read)
                foreach (var message in delivery.Messages) await mailbox.ReplyForWorkerAsync(goal.Id, "conversation", delivery.Id, message.Id, "I read this, but have not incorporated it.", state, lease.Token);
        }
        Assert.NotEmpty(contexts);
        // One already-completed physical operation exists before restart. Neither inspection nor
        // remaining-work execution may replay that operation to manufacture fresh proof.
        now = DateTimeOffset.UtcNow;
        var claimed = (await store.GetAsync(goal.Id))!;
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, new(goal.Id, claimed.AttemptCount, 1, "original-effect", "fixture.physical",
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.WritesLocal, "{\"operation\":\"fixture-original\"}",
            "The existing physical result was completed.", false, now, now) { LocalProcessCompleted = true }));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled, now, now, now, Artifact, null,
            ContinualDeliveryEvidence.OutputDigest(Artifact), "Continue from the saved result.", 0)));
        return new(goal.Id, messages, contexts);
    }

    private sealed class Inner(FileGoalMailbox mailbox, string mode, bool allowNative, FileCodingGoalStore store) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [new("test", "test", "test")];
        public CodingLane? ResolveLane(string? name) => Lanes[0];
        public List<CodingTurnRequest> Inspections = [];
        public List<GoalHeldInputBatch> Batches = [];
        public int OrdinaryCalls, PhysicalActionCounter = 1;
        public string? BaselineAtOrdinaryStart;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!request.ReviewOnly && request.UserMessage.Contains("PRIOR_EFFECT_ACTION")) PhysicalActionCounter++;
            if (request.ReviewOnly)
            {
                Assert.True(request.PlanMode); Assert.True(request.ReadOnlyInspection); Assert.Equal(allowNative, request.AllowNativeLanes); Assert.Null(request.GoalMailbox);
                var batch = (await mailbox.GetHeldInputAsync(request.GoalId!.Value, request.ConversationId, request.GoalMessageLeaseToken!, ct: ct))!;
                Inspections.Add(request); Batches.Add(batch);
                foreach (var message in batch.Messages) Assert.Contains(message.Text, request.UserMessage);
                var selected = mode == "partial" ? batch.Messages.Take(1) : batch.Messages;
                var replies = selected.Select(m => new { deliveryId = m.DeliveryId, messageId = m.Id,
                    text = mode == "read-reference" ? "Received the supplied reference; no file inspection or new instruction is inferred."
                        : "I inspected and incorporated the retained instruction without replaying its action.",
                    disposition = mode == "read-reference" ? "Read" : "Incorporated" });
                var output = mode == "none" ? "I looked at the messages, but supplied no explicit acknowledgement."
                    : "Inspection finished.\n" + GoalFinalReplies.OpenMarker + JsonSerializer.Serialize(new { goalId = request.GoalId, replies }) + GoalFinalReplies.CloseMarker;
                if (mode == "malformed") output += "Invalid trailing material";
                yield return new TurnCompleted("conversation", output, TimeSpan.Zero);
            }
            else
            {
                OrdinaryCalls++; Assert.False(request.PlanMode);
                BaselineAtOrdinaryStart = (await store.GetAsync(request.GoalId!.Value, ct))!.LastOutput;
                yield return new ToolCallStarted("turn", "fresh-proof", "fixture.read", new JsonObject()) { SideEffect = ToolSideEffect.ReadOnly };
                yield return new ToolCallCompleted("turn", "fresh-proof", "fixture.read", "Fresh readback confirms the existing physical result and remaining work.", false, TimeSpan.Zero)
                { SideEffect = ToolSideEffect.ReadOnly };
                yield return new TurnCompleted("conversation", "The remaining work is complete and independently checked.", TimeSpan.Zero);
            }
        }
    }
    private sealed class Audit(FileGoalMailbox mailbox) : IContinualObjectiveAcceptanceService
    {
        public int Calls;
        public async Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
        {
            Calls++;
            Assert.True(await mailbox.TryRecordAcceptanceAsync(await mailbox.GetInstructionSnapshotAsync(request.ObjectiveId, ct), request.Objective!.LeaseToken!,
                FileGoalMailbox.AcceptanceEvidenceDigest(request.Objective), ContinualDeliveryEvidence.OutputDigest(request.FinalOutput), ct));
            return ContinualAcceptanceVerdict.Delivered;
        }
    }
    private async Task<(ContinualObjectiveSnapshot Goal, Inner Inner, Audit Audit)> Run(Seeded seeded, string mode, bool allowNative = true)
    {
        // New owning stores reproduce a process restart; no in-memory delivery ledger is reused.
        var store = new FileCodingGoalStore(Paths); var mailbox = new FileGoalMailbox(Paths, store);
        var inner = new Inner(mailbox, mode, allowNative, store); var audit = new Audit(mailbox);
        var goals = new CodingGoalOrchestrator(inner, store, audit, mailbox: mailbox, options: new()
        { BaseRetryDelay = TimeSpan.FromMilliseconds(1), MaximumRetryDelay = TimeSpan.FromMilliseconds(2), PollInterval = TimeSpan.FromMilliseconds(1) });
        var original = (await store.GetAsync(seeded.Id))!;
        var request = SwarmCodeTurnPolicy.Apply(new() { ConversationId = "conversation", UserMessage = original.ObjectiveText, AllowNativeLanes = allowNative }, CodingPermissionPolicy.AutoNoRules, "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var _ in goals.RunAsync(request, seeded.Id, timeout.Token)) { }
        return ((await store.GetAsync(seeded.Id))!, inner, audit);
    }

    [Theory]
    [InlineData(GoalDeliveryState.Presenting)]
    [InlineData(GoalDeliveryState.Uncertain)]
    [InlineData(GoalDeliveryState.Delivered)]
    [InlineData(GoalDeliveryState.Read)]
    public async Task Restart_inspects_claimed_input_before_remaining_work_and_never_replays_the_prior_physical_operation(GoalDeliveryState state)
    {
        var seeded = await Seed(state); var result = await Run(seeded, "valid");
        Assert.Equal(ContinualObjectiveStatus.Completed, result.Goal.Status); Assert.Single(result.Inner.Inspections);
        Assert.Equal(1, result.Inner.OrdinaryCalls); Assert.Equal(1, result.Inner.PhysicalActionCounter); Assert.Equal(1, result.Audit.Calls);
        Assert.Equal(1, result.Goal.Receipts.Count(r => r.ToolCallId == "original-effect"));
        Assert.All(result.Inner.Batches.SelectMany(b => b.Messages), m => Assert.Equal(seeded.Messages.Single().Text, m.Text));
    }
    [Fact]
    public async Task Held_inspection_does_not_override_the_hosts_explicit_native_lane_prohibition()
    {
        var seeded = await Seed(GoalDeliveryState.Uncertain);
        var result = await Run(seeded, "valid", allowNative: false);
        Assert.Equal(ContinualObjectiveStatus.Completed, result.Goal.Status);
        Assert.False(Assert.Single(result.Inner.Inspections).AllowNativeLanes);
        Assert.Equal(1, result.Inner.PhysicalActionCounter);
    }

    [Fact]
    public async Task More_than_16k_of_held_bodies_progresses_in_complete_exact_batches_without_truncation()
    {
        var seeded = await Seed(GoalDeliveryState.Uncertain, count: 12, large: true); Assert.True(seeded.Messages.Sum(m => m.Text.Length) > 16000);
        var result = await Run(seeded, "valid");
        Assert.Equal(ContinualObjectiveStatus.Completed, result.Goal.Status);
        Assert.InRange(result.Inner.Inspections.Count, 2, seeded.Messages.Count);
        Assert.Equal(1, result.Inner.OrdinaryCalls); Assert.Equal(1, result.Inner.PhysicalActionCounter);
        Assert.All(result.Inner.Batches, b => Assert.True(b.ContextText.Length <= 16000));
        Assert.Equal(seeded.Messages.Select(m => m.Id), result.Inner.Batches.SelectMany(b => b.Messages).Select(m => m.Id));
        foreach (var original in seeded.Messages) Assert.Equal(original.Text, result.Inner.Batches.SelectMany(b => b.Messages).Single(m => m.Id == original.Id).Text);
    }
    [Theory]
    [InlineData("none")]
    [InlineData("malformed")]
    [InlineData("partial")]
    public async Task Missing_malformed_or_partial_acknowledgement_holds_durably_without_running_work_or_replacing_the_artifact(string mode)
    {
        var seeded = await Seed(GoalDeliveryState.Presenting, count: 2); var result = await Run(seeded, mode);
        Assert.Equal(ContinualObjectiveStatus.WaitingForInput, result.Goal.Status); Assert.Equal(Artifact, result.Goal.LastOutput);
        Assert.Equal(0, result.Inner.OrdinaryCalls); Assert.Equal(0, result.Audit.Calls); Assert.Equal(1, result.Inner.PhysicalActionCounter);
        var mailbox = new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths)); var thread = await mailbox.ReadAsync(seeded.Id);
        Assert.True(await mailbox.HasPendingAsync(seeded.Id));
        foreach (var original in seeded.Messages) Assert.Equal(original.Text, thread.Messages.Single(m => m.Id == original.Id).Text);
        Assert.Equal(mode == "partial" ? 1 : 0, thread.Replies.Count);
    }
    [Fact]
    public async Task Input_queued_before_explicit_steer_has_earlier_authority_in_initial_delivery_and_held_recovery()
    {
        var seeded = await Seed(GoalDeliveryState.Presenting, steer: true);
        var normal = Assert.Single(seeded.InitialContexts); Assert.Contains("Current objective authority position: 1;", normal);
        Assert.Contains("Message " + seeded.Messages[0].Id, normal); Assert.Contains("authority position 0", normal);
        var result = await Run(seeded, "valid"); var recovery = Assert.Single(result.Inner.Inspections);
        Assert.Contains("Current objective authority position: 1;", recovery.UserMessage); Assert.Contains("authority position 0", recovery.UserMessage);
        Assert.Contains("New explicit direction after the queued input", recovery.AuthenticatedOriginalUserMessage);
        Assert.Equal(ContinualObjectiveStatus.Completed, result.Goal.Status);
    }
    [Theory]
    [InlineData(GoalDeliveryState.Read, 0)]
    [InlineData(GoalDeliveryState.Uncertain, 1)]
    public async Task Attachment_only_reference_Read_allows_natural_resume_from_saved_artifact_without_another_user_ack_or_effect_dispatch(GoalDeliveryState initial, int inspections)
    {
        var seeded = await Seed(initial, referenceOnly: true);
        var result = await Run(seeded, "read-reference");
        Assert.Equal(ContinualObjectiveStatus.Completed, result.Goal.Status);
        Assert.Equal(inspections, result.Inner.Inspections.Count); Assert.Equal(1, result.Inner.OrdinaryCalls);
        Assert.Equal(Artifact, result.Inner.BaselineAtOrdinaryStart); Assert.Equal(1, result.Inner.PhysicalActionCounter);
        Assert.Single(result.Goal.Receipts.Where(r => r.SideEffect == ContinualSideEffectClass.WritesLocal));
        var thread = await new FileGoalMailbox(Paths, new(Paths)).ReadAsync(seeded.Id);
        var message = Assert.Single(thread.Messages); Assert.Equal(GoalDeliveryState.Read, message.State);
        Assert.Equal(seeded.Messages[0].Attachments, message.Attachments);
        Assert.Single(thread.Replies); Assert.All(thread.Replies, reply => Assert.Equal(GoalDeliveryState.Read, reply.Disposition));
    }

}
