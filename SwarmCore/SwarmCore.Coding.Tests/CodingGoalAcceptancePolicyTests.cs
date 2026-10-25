using System.Text.Json;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCore.Coding.Tests;

public sealed class CodingGoalAcceptancePolicyTests
{
    [Fact]
    public async Task Successful_execution_metadata_preserves_inline_output_without_demanding_physical_resource_readback()
    {
        var metadata = Receipt(3, "reply", "The exact host acknowledgement was saved.", ContinualSideEffectClass.ExecutionMetadata);
        var request = Request([metadata], "Write an original poem.") with { HasPossibleSideEffects = false, FinalOutput = "An original poem, in full." };
        var auditor = new Auditor();
        var verdict = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, verdict.Outcome);
        var evaluated = Assert.Single(auditor.Requests);
        Assert.False(evaluated.HasPossibleSideEffects);
        Assert.Equal("An original poem, in full.", evaluated.FinalOutput);
        Assert.Same(metadata, Assert.Single(evaluated.Receipts));
    }

    [Theory]
    [InlineData(ContinualToolReceiptState.Started, false)]
    [InlineData(ContinualToolReceiptState.Completed, true)]
    public async Task Unknown_or_failed_execution_metadata_still_requires_exact_inspection(ContinualToolReceiptState state, bool error)
    {
        var metadata = Receipt(3, "reply", "Unconfirmed metadata", ContinualSideEffectClass.ExecutionMetadata, error) with { State = state };
        var auditor = new Auditor();
        var verdict = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, Request([metadata]) with { HasPossibleSideEffects = false });
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, verdict.Outcome);
        Assert.Empty(auditor.Requests);
    }

    [Fact]
    public async Task Metadata_cannot_erase_mixed_resource_effects_or_an_independent_possible_effect_flag()
    {
        var metadata = Receipt(3, "reply", "Acknowledged", ContinualSideEffectClass.ExecutionMetadata);
        var write = Receipt(1, "write", "Saved resource", ContinualSideEffectClass.WritesLocal);
        foreach (var request in new[] { Request([metadata]), Request([write, metadata]) with { HasPossibleSideEffects = false } })
        {
            var auditor = new Auditor();
            var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
            Assert.Equal(ContinualAcceptanceOutcome.Retry, result.Outcome);
            Assert.Empty(auditor.Requests);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class Auditor(bool retained = false) : IContinualObjectiveAcceptanceService
    {
        public bool RequiresRetainedVerificationCandidate => retained;
        public List<ContinualAcceptanceRequest> Requests { get; } = [];
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(ContinualAcceptanceVerdict.Delivered);
        }
    }

    private static ContinualToolReceipt Receipt(int attempt, string call, string result,
        ContinualSideEffectClass effect = ContinualSideEffectClass.ReadOnly, bool error = false)
        => new(Id, attempt, attempt * 1000L + call.Length, call, "host.read", ContinualToolReceiptState.Completed,
            effect, "{\"path\":\"actual-proof\"}", result, error, Now, Now);

    private static ContinualAcceptanceRequest Request(IReadOnlyList<ContinualToolReceipt> receipts, string objective = "Implement and verify the complete requested feature")
        => new(Id, objective, "The full requested result is implemented and verified.", receipts, true)
        {
            Objective = new()
            {
                Id = Id, ExecutorKey = CodingGoalOrchestrator.ExecutorKey, Surface = "coding-goal",
                PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "test", ConversationId = "test",
                ObjectiveText = objective, ObjectiveSummary = objective, AttemptCount = 3, Receipts = receipts,
                Authority = ContinualObjectiveAuthority.UserRequested("test"),
            },
        };

    [Fact]
    public async Task Twelve_megabytes_and_783_prior_receipts_do_not_enter_the_current_completion_audit()
    {
        var prior = Enumerable.Range(0, 783).Select(index => Receipt(1, "prior-" + index,
            new string('p', index == 0 ? 749_000 : 16_000))).ToArray();
        var fresh = Receipt(3, "fresh-readback", "Complete current file and test readback with all acceptance evidence.");
        var original = Request([.. prior, fresh]);
        var before = JsonSerializer.Serialize(original.Objective);
        var auditor = new Auditor();

        var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, original);

        Assert.Equal(ContinualAcceptanceOutcome.Delivered, result.Outcome);
        var projected = Assert.Single(auditor.Requests);
        Assert.Same(fresh, Assert.Single(projected.Receipts));
        Assert.Same(original.Objective, projected.Objective);
        Assert.Same(original.ObjectiveText, projected.ObjectiveText);
        Assert.Same(original.FinalOutput, projected.FinalOutput);
        Assert.True(projected.HasPossibleSideEffects);
        Assert.True(projected.UseHostReceiptResolutions);
        Assert.InRange(projected.HostPriorOutcomeSummary!.Length, 1, 2_000);
        Assert.Contains("\"PriorReceiptCount\":783", projected.HostPriorOutcomeSummary);
        Assert.Contains("not proof", projected.HostPriorOutcomeSummary);
        Assert.DoesNotContain(new string('p', 100), projected.HostPriorOutcomeSummary);
        Assert.Equal(before, JsonSerializer.Serialize(original.Objective));
        Assert.False(ContinualDeliveryAuditEvidence.TryFormat(prior.Select(r =>
            new DeliveryAuditEvidenceReceipt(r.ToolCallId, r.ToolName, "ok", r.SideEffect.ToString(), r.InputJson, r.Result)), out _));
    }

    [Fact]
    public async Task A_749k_current_receipt_requests_new_focused_proof_without_audit_or_truncation()
    {
        var large = Receipt(3, "large-current", new string('x', 749_000));
        var original = Request([large]);
        var auditor = new Auditor();

        var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, original);

        Assert.Equal(ContinualAcceptanceOutcome.Retry, result.Outcome);
        Assert.Contains("fresh, focused read-only proof", result.Reason);
        Assert.Contains("Do not repeat", result.Reason);
        Assert.Empty(auditor.Requests);
        Assert.Equal(749_000, Assert.Single(original.Receipts).Result!.Length);
    }

    [Theory]
    [InlineData(ContinualToolReceiptState.Started)]
    [InlineData(ContinualToolReceiptState.Completed)]
    public async Task An_old_uncertain_effect_is_checked_before_history_is_projected(ContinualToolReceiptState state)
    {
        var unknown = Receipt(1, "old-operation", "Uncertain operation", ContinualSideEffectClass.ExternalSend, true) with { State = state };
        var auditor = new Auditor();
        var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, Request([unknown, Receipt(3, "fresh", "Current proof")]));
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, result.Outcome);
        Assert.Empty(auditor.Requests);
    }

    [Fact]
    public async Task A_callers_smaller_receipt_view_cannot_hide_an_uncertain_operation_in_the_canonical_snapshot()
    {
        var unknown = Receipt(1, "old-effect", "Unknown outcome", ContinualSideEffectClass.ExternalSend, true);
        var fresh = Receipt(3, "fresh", "Current proof");
        var request = Request([unknown, fresh]) with { Receipts = [fresh] };
        var auditor = new Auditor();
        var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, result.Outcome);
        Assert.Empty(auditor.Requests);
    }

    [Fact]
    public async Task A_valid_exact_host_resolution_is_bound_into_the_prior_ledger_without_rewriting_history()
    {
        var old = Receipt(1, "failed-old-operation", "Original error", ContinualSideEffectClass.Unknown, true);
        var read = Receipt(2, "inspection", "Host readback establishes the exact outcome.");
        var proof = new ContinualReceiptResolution(Id, old.Attempt, old.ToolCallId, ContinualReceiptResolutionPolicy.Fingerprint(old),
            ContinualReceiptResolutionOutcome.KnownNoEffect, "host-verifier:bound", "Verified no effect",
            [new(read.Attempt, read.ToolCallId, ContinualReceiptResolutionPolicy.Fingerprint(read))], Now);
        old = old with { HostResolution = proof };
        var request = Request([old, read, Receipt(3, "fresh", "Full current proof")]);
        var auditor = new Auditor();

        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);

        var projected = Assert.Single(auditor.Requests);
        Assert.Contains("\"HostResolvedKnownNoEffect\":1", projected.HostPriorOutcomeSummary);
        Assert.True(old.IsError);
        Assert.Equal("Original error", old.Result);
        Assert.True(MissionTransitions.NeedsInspection(old));
        Assert.Single(projected.Receipts);
    }

    [Fact]
    public async Task Historical_hashes_and_a_new_write_cannot_replace_current_readback()
    {
        var auditor = new Auditor();
        var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, Request([
            Receipt(1, "old-read", "Old success"), Receipt(3, "new-write", "Write succeeded", ContinualSideEffectClass.WritesLocal)]));
        Assert.Equal(ContinualAcceptanceOutcome.Retry, result.Outcome);
        Assert.Contains("no successful fresh readback", result.Reason);
        Assert.Empty(auditor.Requests);
    }

    [Fact]
    public async Task Projecting_history_cannot_erase_physical_effect_context_when_the_request_flag_is_stale()
    {
        var old = Receipt(1, "old-send", "Send completed", ContinualSideEffectClass.ExternalSend);
        var request = Request([old, Receipt(3, "fresh", "Current readback proves the result")]) with { HasPossibleSideEffects = false };
        var auditor = new Auditor();
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        Assert.True(Assert.Single(auditor.Requests).HasPossibleSideEffects);
        Assert.False(request.HasPossibleSideEffects);
    }

    [Fact]
    public async Task Current_proof_is_forwarded_in_full_and_prior_digest_changes_when_retained_facts_change()
    {
        var auditor = new Auditor();
        var old = Receipt(1, "old", "Historical body");
        var fresh = Receipt(3, "fresh", new string('f', 10_000) + "CRITICAL TRAILING PROOF");
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, Request([old, fresh]));
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, Request([old with { Result = "Changed historical body" }, fresh]));
        Assert.Equal(fresh.Result, auditor.Requests[0].Receipts.Single().Result);
        Assert.EndsWith("CRITICAL TRAILING PROOF", auditor.Requests[0].Receipts.Single().Result);
        Assert.NotEqual(auditor.Requests[0].HostPriorOutcomeSummary, auditor.Requests[1].HostPriorOutcomeSummary);
    }

    [Fact]
    public async Task Previously_truncated_current_proof_is_rejected_instead_of_forwarding_a_prefix()
    {
        var auditor = new Auditor();
        var result = await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, Request([Receipt(3, "clipped", "prefix …[truncated]")]));
        Assert.Equal(ContinualAcceptanceOutcome.Retry, result.Outcome);
        Assert.Empty(auditor.Requests);
    }

    [Fact]
    public async Task Retained_audit_hosts_receive_the_exact_original_request_even_with_oversized_or_uncertain_receipts()
    {
        var request = Request([Receipt(1, "old", new string('x', 749_000), ContinualSideEffectClass.ExternalSend, true)]);
        var auditor = new Auditor(retained: true);
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        Assert.Same(request, Assert.Single(auditor.Requests));
    }

    [Fact]
    public async Task Missions_receive_the_exact_original_request()
    {
        var original = Request([Receipt(1, "old", new string('x', 749_000), ContinualSideEffectClass.Unknown, true)]);
        var request = original with { Objective = original.Objective! with { Mission = new MissionDefinition { OwnerId = "test", SuccessCriteria = [], EvidenceRequirements = [] } } };
        var auditor = new Auditor();
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        Assert.Same(request, Assert.Single(auditor.Requests));
    }

    [Fact]
    public async Task Correction_candidates_receive_the_exact_original_request()
    {
        var original = Request([Receipt(1, "old", "Uncertain", ContinualSideEffectClass.Unknown, true)]);
        var candidate = new ContinualVerificationCandidate(1, "output", "authority", Now, "saved-report-correction");
        var request = original with { Objective = original.Objective! with { VerificationCandidate = candidate } };
        var auditor = new Auditor();
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        Assert.Same(request, Assert.Single(auditor.Requests));
    }

    [Fact]
    public async Task Inline_creative_work_without_physical_actions_keeps_ordinary_independent_semantic_auditing()
    {
        var request = Request([], "Write a short poem in your reply") with { HasPossibleSideEffects = false, FinalOutput = "The complete poem." };
        var auditor = new Auditor();
        await CodingGoalAcceptancePolicy.EvaluateAsync(auditor, request);
        var projected = Assert.Single(auditor.Requests);
        Assert.Equal("The complete poem.", projected.FinalOutput);
        Assert.Empty(projected.Receipts);
        Assert.Null(projected.HostPriorOutcomeSummary);
    }
}
