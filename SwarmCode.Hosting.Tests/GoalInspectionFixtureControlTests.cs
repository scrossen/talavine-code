using System.Runtime.CompilerServices;
using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalInspectionFixtureControlTests : TempDirTest
{
    [Fact]
    public async Task Working_thread_identity_comparison_uses_the_real_file_store_string_representation()
    {
        var conversations = new FileConversationStore(Paths);
        var conversation = await conversations.CreateAsync();
        await conversations.UpdateStateAsync(conversation.Id, new Dictionary<string, object> { ["codex.threadId"] = "unchanged-thread" });
        var state = await conversations.GetStateAsync(conversation.Id);
        var value = state["codex.threadId"];
        Assert.IsType<JsonElement>(value);
        Assert.Equal("unchanged-thread", InstalledGoalInspectionTests.ReadThreadIdentity(value));
        Assert.Null(InstalledGoalInspectionTests.ReadThreadIdentity(JsonSerializer.SerializeToElement(123)));
        Assert.Null(InstalledGoalInspectionTests.ReadThreadIdentity(JsonSerializer.SerializeToElement(new { value = "unchanged-thread" })));
    }
    private sealed class Actual(FileGoalMailbox mailbox, bool fail) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [new("native-inspection", "CodexNative", "codex-cli:fixture")];
        public CodingLane? ResolveLane(string? name) => Lanes[0];
        public int Calls;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            if (fail)
            {
                yield return new TurnErrored("fixture", "A deliberately failed native inspection.") { FailureCategory = LlmFailureCategory.ModelNotFound };
                yield break;
            }
            var batch = (await mailbox.GetHeldInputAsync(request.GoalId!.Value, request.ConversationId, request.GoalMessageLeaseToken!, ct: ct))!;
            yield return new TurnCompleted("fixture", GoalFinalReplies.OpenMarker + JsonSerializer.Serialize(new
            {
                goalId = request.GoalId,
                replies = batch.Messages.Select(message => new { deliveryId = message.DeliveryId, messageId = message.Id,
                    text = "I incorporated this instruction without replaying actions.", disposition = "Incorporated" }),
            }) + GoalFinalReplies.CloseMarker, TimeSpan.Zero);
        }
    }
    private sealed class NoAudit : IContinualObjectiveAcceptanceService
    {
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("No inference or completion audit is allowed in this fixture-control regression.");
    }
    private sealed class LegacyQuota(ICodingTurnOrchestrator inspection) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => inspection.Lanes;
        public CodingLane? ResolveLane(string? name) => inspection.ResolveLane(name);
        public int OrdinaryCalls;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.ReadOnlyInspection) { await foreach (var item in inspection.RunAsync(request, ct)) yield return item; }
            else { OrdinaryCalls++; yield return new TurnErrored("fixture", "Legacy qualification quota hold.") { FailureCategory = LlmFailureCategory.QuotaExhausted }; }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Fixture_stops_after_one_inspection_even_when_native_fails_and_goal_requests_a_retry(bool fail, bool legacyQuota)
    {
        var store = new FileCodingGoalStore(Paths); var mailbox = new FileGoalMailbox(Paths, store);
        var actual = new Actual(mailbox, fail);
        var fixture = new InstalledGoalInspectionTests.InspectionOnly(actual, store);
        var legacy = new LegacyQuota(fixture);
        var goals = new CodingGoalOrchestrator(legacyQuota ? legacy : fixture, store, new NoAudit(), mailbox: mailbox, options: new()
        { BaseRetryDelay = TimeSpan.FromMilliseconds(1), MaximumRetryDelay = TimeSpan.FromMilliseconds(2), PollInterval = TimeSpan.FromMilliseconds(1) });
        var request = new CodingTurnRequest { ConversationId = "fixture-control", UserMessage = "Preserve the existing artifact.",
            LaneName = "native-inspection", ModelOverride = "codex-cli:fixture", SelectionPinned = true,
            CommunicationScope = AgentCommunicationScope.UserRequested("fixture") };
        var goal = await goals.StartAsync(request);
        var now = DateTimeOffset.UtcNow;
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "old", now, now.AddMinutes(5)))!;
        await mailbox.SendAsync(goal.Id, new("held", "Keep the prior action intact. FULL_HELD_SOURCE_END", GoalMessageKind.Correction, new("ui", "fixture", "owner")));
        var delivered = (await mailbox.BeginDeliveryForWorkerAsync(goal.Id, request.ConversationId, "old", lease.Token))!;
        await mailbox.RecordDeliveryAsync(delivered.Id, GoalDeliveryState.Delivered);
        const string artifact = "Original complete artifact.";
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled, now, now, now, artifact, null, null, "Inspect held input", 0)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in goals.RunAsync(request, goal.Id, timeout.Token)) { }
        var saved = (await store.GetAsync(goal.Id))!;
        Assert.Equal(1, actual.Calls); Assert.Equal(1, fixture.Inspections);
        Assert.InRange(fixture.InspectionRequests, 1, 2);
        Assert.Null(saved.LeaseToken);
        if (legacyQuota)
        {
            // Reproduce the original fixture stub without an inference call: it terminates promptly.
            // Its synthetic error text replaces LastOutput, so it was also unsuitable for artifact-retention proof.
            Assert.Equal(1, legacy.OrdinaryCalls); Assert.Equal(ContinualObjectiveStatus.WaitingForInput, saved.Status);
            Assert.Equal("Legacy qualification quota hold.", saved.LastOutput);
            Assert.Equal(GoalDeliveryState.Incorporated, Assert.Single((await mailbox.ReadAsync(goal.Id)).Messages).State);
        }
        else if (!fail)
        {
            Assert.Equal(artifact, saved.LastOutput);
            Assert.Equal(1, fixture.OrdinaryCalls); Assert.Equal(ContinualObjectiveStatus.Paused, saved.Status);
            Assert.Equal(GoalDeliveryState.Incorporated, Assert.Single((await mailbox.ReadAsync(goal.Id)).Messages).State);
        }
        else
        {
            Assert.Equal(artifact, saved.LastOutput);
            Assert.Equal(0, fixture.OrdinaryCalls);
            Assert.Contains(saved.Status, new[] { ContinualObjectiveStatus.Paused, ContinualObjectiveStatus.WaitingForInput, ContinualObjectiveStatus.AttentionRequired });
            Assert.Equal(GoalDeliveryState.Delivered, Assert.Single((await mailbox.ReadAsync(goal.Id)).Messages).State);
        }
    }
}
