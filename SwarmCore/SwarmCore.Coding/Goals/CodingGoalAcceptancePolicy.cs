using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Coding.Goals;

/// <summary>
/// Ordinary coding goals audit fresh completion evidence, while their complete retained history
/// continues to fence uncertain effects. This does not change mission or retained-audit contracts.
/// </summary>
public static class CodingGoalAcceptancePolicy
{
    public static Task<ContinualAcceptanceVerdict> EvaluateAsync(IContinualObjectiveAcceptanceService inner,
        ContinualAcceptanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var objective = request.Objective;
        if (objective is null || objective.ExecutorKey != CodingGoalOrchestrator.ExecutorKey
            || objective.Mission is not null || inner.RequiresRetainedVerificationCandidate
            || ContinualVerificationCandidatePolicy.RequiresCorrectionProvenance(objective.VerificationCandidate))
            return inner.EvaluateAsync(request, ct);

        var history = objective.Receipts;
        // Inspect the whole original history BEFORE choosing a smaller audit view. A stale
        // error or unknown operation must not disappear merely because it belongs to an old turn.
        if (objective.Id != request.ObjectiveId || history.Any(receipt => receipt.ObjectiveId != objective.Id
                || receipt.Attempt > objective.AttemptCount
                || ContinualReceiptResolutionPolicy.NeedsInspection(receipt, history)))
            return Task.FromResult(new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.VerificationUnavailable,
                "A retained operation still has an unverified outcome. Host inspection is required before completion can be audited; do not replay it.")
                { EvaluationUnavailable = true });

        var current = history.Where(receipt => receipt.Attempt == objective.AttemptCount).ToArray();
        var prior = history.Where(receipt => receipt.Attempt != objective.AttemptCount).ToArray();
        var summary = prior.Length == 0 ? null : PriorOutcomeSummary(objective.Id, prior, history, ct);
        var evidence = current.Select(receipt => new DeliveryAuditEvidenceReceipt(receipt.ToolCallId, receipt.ToolName,
            !receipt.WasDispatched ? "SKIPPED/NOT EXECUTED"
                : receipt.State == ContinualToolReceiptState.Started ? "STARTED/OUTCOME UNKNOWN"
                : receipt.IsError ? "ERROR" : "ok", receipt.SideEffect.ToString(), receipt.InputJson, receipt.Result)
            { HostResolution = ContinualReceiptResolutionPolicy.IsResolved(receipt, history) ? receipt.HostResolution : null });
        if (!ContinualDeliveryAuditEvidence.TryFormat(evidence, out var formatted)
            || (long)formatted.Length + (summary?.Length ?? 0) > ContinualDeliveryAuditEvidence.MaximumCharacters)
            return FreshProof("The current attempt's complete evidence exceeds one lossless delivery audit, or contains previously truncated evidence.");

        var possibleEffects = request.HasPossibleSideEffects || history.Any(receipt => receipt.WasDispatched
            && ContinualSideEffects.MayAffectTaskResources(receipt.SideEffect));
        if (possibleEffects
            && !current.Any(receipt => receipt.State == ContinualToolReceiptState.Completed && !receipt.IsError
                && receipt.WasDispatched && receipt.SideEffect == ContinualSideEffectClass.ReadOnly
                && !string.IsNullOrWhiteSpace(receipt.Result)))
            return FreshProof("The current attempt has no successful fresh readback of the requested result.");

        // The snapshot and all its receipts remain intact in storage and on Objective. The
        // bounded summary is host context, never a replacement for current completion proof.
        return inner.EvaluateAsync(request with
        {
            Receipts = current, HasPossibleSideEffects = possibleEffects,
            UseHostReceiptResolutions = true, HostPriorOutcomeSummary = summary,
        }, ct);
    }

    private static Task<ContinualAcceptanceVerdict> FreshProof(string reason)
        => Task.FromResult(new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry,
            reason + " Preserve prior actions and their receipts. Obtain fresh, focused read-only proof of the full goal in the next attempt; "
            + "include the complete relevant bodies within the audit bound. Do not repeat a write, send or execution to obtain a smaller receipt. "
            + "Historical hashes and summaries cannot prove completion."));

    private static string PriorOutcomeSummary(Guid id, IReadOnlyList<ContinualToolReceipt> prior,
        IReadOnlyList<ContinualToolReceipt> all, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var knownNoEffect = 0;
        var knownOutcome = 0;
        foreach (var receipt in prior.OrderBy(receipt => receipt.Attempt).ThenBy(receipt => receipt.Sequence)
                     .ThenBy(receipt => receipt.ToolCallId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            digest.AppendData(Encoding.ASCII.GetBytes(ContinualReceiptResolutionPolicy.Fingerprint(receipt)));
            if (ContinualReceiptResolutionPolicy.IsResolved(receipt, all))
            {
                digest.AppendData(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt.HostResolution)));
                if (receipt.HostResolution!.Outcome == ContinualReceiptResolutionOutcome.KnownNoEffect) knownNoEffect++;
                else knownOutcome++;
            }
            else digest.AppendData(new byte[32]);
        }
        return JsonSerializer.Serialize(new
        {
            Kind = "retained-coding-goal-prior-outcomes-v1", ObjectiveId = id,
            PriorReceiptCount = prior.Count,
            RetainedHistorySha256 = Convert.ToHexString(digest.GetHashAndReset()),
            CompletedErrors = prior.Count(receipt => receipt.State == ContinualToolReceiptState.Completed && receipt.IsError),
            SuccessfulDispatched = prior.Count(receipt => receipt.State == ContinualToolReceiptState.Completed && !receipt.IsError && receipt.WasDispatched),
            HostResolvedKnownNoEffect = knownNoEffect, HostResolvedKnownOutcome = knownOutcome,
            Meaning = "All original receipts remain retained. This ledger identifies prior history and settled outcomes; "
                + "it is not proof of current task completion or permission to replay an operation. Require the complete final output and fresh current readback to prove the full goal.",
        });
    }
}
