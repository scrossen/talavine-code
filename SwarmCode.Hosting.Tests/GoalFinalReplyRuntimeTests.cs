using System.Runtime.CompilerServices;
using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalFinalReplyRuntimeTests : TempDirTest
{
    private sealed class Inner(FileGoalMailbox mailbox, string mode) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [new("test", "test", "test")];
        public CodingLane? ResolveLane(string? name) => Lanes[0];
        public int Calls;
        public string? OriginalOutput;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            Assert.Contains("Do not use tools", request.AuthenticatedOriginalUserMessage);
            var delivery = (await mailbox.BeginDeliveryForWorkerAsync(request.GoalId!.Value, request.ConversationId, "synthetic-bound-worker", request.GoalMessageLeaseToken!, ct: ct))!;
            var replies = delivery.Messages.Select((m, i) => new
            {
                deliveryId = delivery.Id, messageId = mode == "partial-failure" && i == 1 ? "unbound-message" : m.Id,
                text = "I incorporated this correction without invoking a tool.", disposition = "Incorporated",
            }).ToArray();
            OriginalOutput = "The complete visible answer.\n" + GoalFinalReplies.OpenMarker
                + JsonSerializer.Serialize(new { goalId = request.GoalId, replies }) + GoalFinalReplies.CloseMarker;
            if (mode == "malformed") OriginalOutput += "Nonterminal text";
            yield return new TurnCompleted(request.ConversationId, OriginalOutput, TimeSpan.Zero);
        }
    }
    private sealed class Audit(FileGoalMailbox mailbox) : IContinualObjectiveAcceptanceService
    {
        public int Calls;
        public async Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal("The complete visible answer.", request.FinalOutput);
            Assert.True(await mailbox.TryRecordAcceptanceAsync(await mailbox.GetInstructionSnapshotAsync(request.ObjectiveId, ct),
                request.Objective!.LeaseToken!, FileGoalMailbox.AcceptanceEvidenceDigest(request.Objective),
                ContinualDeliveryEvidence.OutputDigest(request.FinalOutput), ct));
            return ContinualAcceptanceVerdict.Delivered;
        }
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("malformed")]
    [InlineData("partial-failure")]
    public async Task Explicit_no_tools_acknowledgement_uses_the_real_goal_lease_and_strips_only_after_all_replies_commit(string mode)
    {
        var store = new FileCodingGoalStore(Paths); var mailbox = new FileGoalMailbox(Paths, store);
        var inner = new Inner(mailbox, mode); var audit = new Audit(mailbox);
        var goals = new CodingGoalOrchestrator(inner, store, audit, mailbox: mailbox);
        var request = SwarmCodeTurnPolicy.Apply(new() { ConversationId = "conversation", UserMessage = "Do not use tools. Provide the requested answer in this chat." },
            CodingPermissionPolicy.AutoNoRules, "test");
        var original = await goals.StartAsync(request);
        for (var i = 0; i < (mode == "partial-failure" ? 2 : 1); i++)
            await mailbox.SendAsync(original.Id, new("message" + i, "A correction " + i, GoalMessageKind.Correction, new("code-ui", "message" + i, "owner")));
        var events = new List<TurnEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var evt in goals.RunAsync(request, original.Id, timeout.Token)) events.Add(evt);
        var saved = (await store.GetAsync(original.Id))!; var thread = await mailbox.ReadAsync(original.Id);
        Assert.Equal(1, inner.Calls); Assert.Empty(saved.Receipts); // No agent tool invocation is invented.
        if (mode == "valid")
        {
            Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status); Assert.Equal(1, audit.Calls);
            Assert.Equal("The complete visible answer.", saved.LastOutput);
            Assert.Single(thread.Replies); Assert.Equal(GoalDeliveryState.Incorporated, Assert.Single(thread.Messages).State);
            Assert.DoesNotContain(GoalFinalReplies.OpenMarker, Assert.Single(events.OfType<TurnCompleted>()).FinalText);
        }
        else
        {
            Assert.Equal(ContinualObjectiveStatus.WaitingForInput, saved.Status); Assert.Equal(0, audit.Calls);
            Assert.Equal(inner.OriginalOutput, saved.LastOutput); Assert.Empty(events.OfType<TurnCompleted>());
            Assert.True(await mailbox.HasPendingAsync(original.Id)); Assert.Equal(mode == "partial-failure" ? 1 : 0, thread.Replies.Count);
        }
    }
}
