using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class CodingGoalReceiptRecoveryTests : TempDirTest
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ContinualObjectiveStart Start() => new()
    {
        Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "recovery",
        ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
        ObjectiveText = "Finish the requested work", ObjectiveSummary = "Finish the requested work",
        Authority = ContinualObjectiveAuthority.UserRequested("test"),
    };

    private static ContinualToolReceipt Failed(Guid id, DateTimeOffset now) => new(id, 1, 1, "reused", "native.command",
        ContinualToolReceiptState.Completed, ContinualSideEffectClass.Unknown, "{\"operation\":\"exact\"}",
        "Original terminal error; partial effects need inspection", true, now, now);

    private static ContinualReceiptResolution Proof(ContinualToolReceipt failed, ContinualToolReceipt read, DateTimeOffset now)
        => new(failed.ObjectiveId, failed.Attempt, failed.ToolCallId, ContinualReceiptResolutionPolicy.Fingerprint(failed),
            ContinualReceiptResolutionOutcome.KnownOutcome, "host-test-verifier:sha256-bound-implementation",
            "Host readback independently verified the partial outcome; never replay it.",
            [new(read.Attempt, read.ToolCallId, ContinualReceiptResolutionPolicy.Fingerprint(read))], now);

    private async Task<(FileCodingGoalStore Store, Clock Time, ContinualObjectiveLease Lease, ContinualToolReceipt Failed,
        ContinualToolReceipt Read)> Seed()
    {
        var time = new Clock();
        var store = new FileCodingGoalStore(Paths, time);
        var goal = await store.CreateOrJoinAsync(Start(), time.Now);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "old", time.Now, time.Now.AddMinutes(1)))!;
        var failed = Failed(goal.Id, time.Now);
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, failed));
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled,
            time.Now, time.Now, time.Now, "Original saved output", "Earlier failure", "saved-fingerprint", "Keep constraints", 4)));
        time.Now += TimeSpan.FromSeconds(1);
        goal = (await store.GetAsync(goal.Id))!;
        lease = (await store.TryClaimAsync(goal.Id, goal.Version, "inspector", time.Now, time.Now.AddMinutes(1)))!;
        // Deliberately reuse the tool call ID in a different attempt.
        var read = new ContinualToolReceipt(goal.Id, 2, 2, "reused", "host.inspect", ContinualToolReceiptState.Completed,
            ContinualSideEffectClass.ReadOnly, "{}", "Exact current readback", false, time.Now, time.Now);
        Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, read));
        return (store, time, lease, failed, read);
    }

    [Fact]
    public async Task Exact_host_resolution_survives_reload_preserves_original_facts_and_unowned_fields()
    {
        var h = await Seed();
        var before = (await h.Store.GetAsync(h.Failed.ObjectiveId))!;
        var proof = Proof(h.Failed, h.Read, h.Time.Now);
        Assert.True(await h.Store.ResolveReceiptAsync(before.Id, h.Lease.Token, proof, h.Time.Now));
        var saved = (await new FileCodingGoalStore(Paths, h.Time).GetAsync(before.Id))!;
        var receipt = saved.Receipts.Single(r => r.Attempt == 1);
        Assert.Equal(JsonSerializer.Serialize(h.Failed), JsonSerializer.Serialize(receipt with { HostResolution = null }));
        Assert.True(MissionTransitions.NeedsInspection(receipt)); // Mission policy is unchanged.
        Assert.False(ContinualReceiptResolutionPolicy.NeedsInspection(receipt, saved.Receipts));
        Assert.Equal(before.ObjectiveText, saved.ObjectiveText);
        Assert.Equal(before.LastOutput, saved.LastOutput);
        Assert.Equal(before.LastFailure, saved.LastFailure);
        Assert.Equal(before.ContinuationFeedback, saved.ContinuationFeedback);
        Assert.Equal(before.LeaseToken, saved.LeaseToken);
        Assert.Equal(before.LastEventSequence, saved.LastEventSequence);
        Assert.Equal(JsonSerializer.Serialize(before.Receipts[1]), JsonSerializer.Serialize(saved.Receipts[1]));
        Assert.Equal(0, saved.ConsecutiveNoProgress);
        Assert.True(await h.Store.ResolveReceiptAsync(before.Id, h.Lease.Token, proof, h.Time.Now));
        Assert.Equal(saved.Version, (await h.Store.GetAsync(before.Id))!.Version);
        Assert.False(await h.Store.UpsertReceiptAsync(before.Id, h.Lease.Token, receipt));
    }

    [Theory]
    [InlineData("target-hash")]
    [InlineData("attempt")]
    [InlineData("observation-hash")]
    [InlineData("historical-observation")]
    [InlineData("missing-observation")]
    [InlineData("future-proof")]
    [InlineData("no-verifier")]
    [InlineData("invalid-outcome")]
    [InlineData("changed-target")]
    [InlineData("changed-observation")]
    [InlineData("expired-lease")]
    [InlineData("expired-lease-stale-now")]
    [InlineData("pause")]
    [InlineData("delete")]
    public async Task Resolution_rejects_drift_stale_authority_and_unbound_evidence(string scenario)
    {
        var h = await Seed();
        var id = h.Failed.ObjectiveId;
        var proof = Proof(h.Failed, h.Read, h.Time.Now);
        var suppliedNow = h.Time.Now;
        switch (scenario)
        {
            case "target-hash": proof = proof with { ReceiptSha256 = new string('0', 64) }; break;
            case "attempt": proof = proof with { Attempt = 2 }; break;
            case "observation-hash": proof = proof with { Observations = [new(2, "reused", new string('0', 64))] }; break;
            case "historical-observation": proof = proof with { Observations = [new(1, "reused", proof.ReceiptSha256)] }; break;
            case "missing-observation": proof = proof with { Observations = [] }; break;
            case "future-proof": proof = proof with { VerifiedAtUtc = h.Time.Now.AddHours(1) }; break;
            case "no-verifier": proof = proof with { VerifierIdentity = "" }; break;
            case "invalid-outcome": proof = proof with { Outcome = (ContinualReceiptResolutionOutcome)99 }; break;
            case "changed-target": Assert.True(await h.Store.UpsertReceiptAsync(id, h.Lease.Token, h.Failed with { Sequence = 3, Result = "Changed late result" })); break;
            case "changed-observation": Assert.True(await h.Store.UpsertReceiptAsync(id, h.Lease.Token, h.Read with { Sequence = 3, Result = "Changed readback" })); break;
            case "expired-lease": h.Time.Now += TimeSpan.FromMinutes(2); break;
            case "expired-lease-stale-now": h.Time.Now += TimeSpan.FromMinutes(2); break;
            case "pause": Assert.True(await new FileCodingGoalStore(Paths, h.Time).ControlAsync(id, (await h.Store.GetAsync(id))!.Version, CodingGoalControl.Pause, h.Time.Now)); break;
            case "delete": await new FileCodingGoalStore(Paths, h.Time).DeleteConversationAsync("conversation"); break;
        }
        Assert.False(await h.Store.ResolveReceiptAsync(id, h.Lease.Token, proof,
            scenario == "expired-lease-stale-now" ? suppliedNow : h.Time.Now));
        var saved = await h.Store.GetAsync(id);
        if (saved is not null) Assert.All(saved.Receipts, receipt => Assert.Null(receipt.HostResolution));
    }

    private sealed class Inner(bool sensitiveRead = false, bool inspectionThrows = false) : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests = [];
        public IReadOnlyList<CodingLane> Lanes => [new("test", "test", "test")];
        public CodingLane? ResolveLane(string? name) => Lanes[0];
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            if (request.ReviewOnly)
            {
                if (inspectionThrows) throw new InvalidOperationException("Inspection unavailable");
                yield return new ToolCallStarted("turn", "fresh-read", "host.inspect", new JsonObject()) { SideEffect = ToolSideEffect.ReadOnly };
                yield return new ToolCallCompleted("turn", "fresh-read", "host.inspect", sensitiveRead
                    ? "Exact readback; Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456" : "Exact host-readable observations",
                    false, TimeSpan.Zero) { SideEffect = ToolSideEffect.ReadOnly };
            }
            yield return new TurnCompleted("turn", request.ReviewOnly ? "Model claims everything is fine" : "Remaining work verified", TimeSpan.Zero);
        }
    }

    private sealed class Acceptance : IContinualObjectiveAcceptanceService
    {
        public int Calls;
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
        {
            Calls++;
            Assert.True(request.UseHostReceiptResolutions);
            Assert.DoesNotContain(request.Receipts, receipt => ContinualReceiptResolutionPolicy.NeedsInspection(receipt, request.Receipts));
            return Task.FromResult(ContinualAcceptanceVerdict.Delivered);
        }
    }

    private sealed class Verifier(Clock time) : ICodingGoalReceiptVerifier
    {
        public Task<IReadOnlyList<ContinualReceiptResolution>> VerifyAsync(ContinualObjectiveSnapshot objective,
            IReadOnlyList<ContinualToolReceipt> observations, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ContinualReceiptResolution>>(objective.Receipts
                .Where(r => ContinualReceiptResolutionPolicy.NeedsInspection(r, objective.Receipts))
                .Select(r => Proof(r, observations.Single(), time.Now)).ToArray());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task Completed_error_is_inspected_before_saved_candidate_verification_and_model_claims_cannot_clear_it(
        bool trustedVerifier, bool sensitiveRead, bool inspectionThrows)
    {
        var h = await Seed();
        // Return to verification-pending exactly like the failing restored goal.
        Assert.True(await h.Store.ApplyTransitionAsync(h.Failed.ObjectiveId, h.Lease.Token, new(ContinualObjectiveStatus.VerificationPending,
            h.Time.Now, h.Time.Now, h.Time.Now, "Retained candidate", "Prior outcome uncertain", null, null, 5)));
        var inner = new Inner(sensitiveRead, inspectionThrows); var acceptance = new Acceptance();
        var goals = new CodingGoalOrchestrator(inner, h.Store, acceptance, time: h.Time,
            receiptVerifier: trustedVerifier ? new Verifier(h.Time) : null);
        var request = SwarmCodeTurnPolicy.Apply(new() { ConversationId = "conversation", UserMessage = "Finish the requested work" },
            CodingPermissionPolicy.AutoNoRules, "test");
        await foreach (var _ in goals.RunAsync(request, h.Failed.ObjectiveId)) { }
        var saved = (await h.Store.GetAsync(h.Failed.ObjectiveId))!;
        var inspection = Assert.Single(inner.Requests);
        Assert.True(inspection.PlanMode); Assert.True(inspection.ReviewOnly); Assert.False(inspection.AllowNativeLanes);
        Assert.Contains("Attempt 1; call reused", inspection.UserMessage);
        Assert.Equal(trustedVerifier ? ContinualObjectiveStatus.Completed : ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Equal(trustedVerifier ? 1 : 0, acceptance.Calls);
        Assert.Equal(3, saved.AttemptCount);
        Assert.Null(saved.LeaseToken);
        if (inspectionThrows) Assert.Contains("Host inspection failed (InvalidOperationException)", saved.LastFailure);
        if (sensitiveRead) Assert.Contains("Bearer [redacted]", saved.Receipts.Single(r => r.ToolCallId == "fresh-read").Result);
        Assert.Equal(JsonSerializer.Serialize(h.Failed), JsonSerializer.Serialize(saved.Receipts.Single(r => r.Attempt == 1) with { HostResolution = null }));
        if (!trustedVerifier)
        {
            await foreach (var _ in goals.RunAsync(request, h.Failed.ObjectiveId)) { }
            Assert.Single(inner.Requests); // No autonomous verification/inspection loop.
            Assert.Equal(3, (await h.Store.GetAsync(h.Failed.ObjectiveId))!.AttemptCount);
        }
    }
}
