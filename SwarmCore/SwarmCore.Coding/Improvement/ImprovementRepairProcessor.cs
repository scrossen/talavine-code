using SwarmCore.Abstractions.Crm;

namespace SwarmCore.Coding.Improvement;

public interface IImprovementRepairQueue
{
    Task<IReadOnlyList<ImprovementTicket>> ListAsync(CancellationToken ct);
    Task<ImprovementTicket> ClaimAsync(ImprovementTicket ticket, string owner, CancellationToken ct);
    Task<ImprovementTicket> AdvanceAsync(ImprovementTicket ticket, ImprovementAdvance change, CancellationToken ct);
}

public sealed record ImprovementRepairProof(bool RegressionFailedOnBase, bool RegressionPassedOnCandidate,
    bool VerificationGatePassed, string? Commit, string Evidence);

/// <summary>The host supplies repository isolation, credentials, configured tests and durable coding sessions.</summary>
public interface IImprovementRepairExecutor
{
    Task<ImprovementRepairProof> RepairAsync(ImprovementTicket ticket, CancellationToken ct);
}

/// <summary>Ticket-driven source repair. Release and original-task verification remain separate host contracts.</summary>
public sealed class ImprovementRepairProcessor(IImprovementRepairQueue queue, IImprovementRepairExecutor executor)
{
    public async Task<ImprovementTicket?> RunOneAsync(string owner, CancellationToken ct, long? resumeTicketId = null, string? product = null)
    {
        var next = (await queue.ListAsync(ct)).FirstOrDefault(t => t.Stage is ImprovementStage.Reported or ImprovementStage.Reproduced or ImprovementStage.Fixing
            && (product is null || string.Equals(t.Report.Product, product, StringComparison.Ordinal))
            && (resumeTicketId is null || t.TicketId == resumeTicketId)
            && (t.ClaimExpiresAt is null || t.ClaimExpiresAt <= DateTimeOffset.UtcNow
                || (resumeTicketId == t.TicketId && t.ClaimOwner == owner)));
        if (next is null) return null;
        next = await queue.ClaimAsync(next, owner, ct);
        var proof = await executor.RepairAsync(next, ct);
        if (!proof.RegressionFailedOnBase || !proof.RegressionPassedOnCandidate || !proof.VerificationGatePassed
            || string.IsNullOrWhiteSpace(proof.Commit))
        {
            // Source candidates without objective proof never enter the daily-build queue.
            return await queue.AdvanceAsync(next, new(next.Revision, ImprovementStage.NeedsInformation, proof.Evidence), ct);
        }
        if (next.Stage == ImprovementStage.Reported)
            next = await queue.AdvanceAsync(next, new(next.Revision, ImprovementStage.Reproduced, proof.Evidence), ct);
        if (next.Stage == ImprovementStage.Reproduced)
            next = await queue.AdvanceAsync(next, new(next.Revision, ImprovementStage.Fixing, proof.Evidence), ct);
        return await queue.AdvanceAsync(next, new(next.Revision, ImprovementStage.Validated, proof.Evidence, proof.Commit), ct);
    }
}
