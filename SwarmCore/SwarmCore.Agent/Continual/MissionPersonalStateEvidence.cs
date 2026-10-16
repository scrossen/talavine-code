using System.Text.Json;
using SwarmCore.Abstractions.Context;

namespace SwarmCore.Agent.Continual;

/// <summary>Portable evidence-state projection adapter; hosts retain source access/privacy checks.</summary>
public static class MissionPersonalStateEvidence
{
    public static async Task<MissionObservedState?> CaptureAsync(IContextFactRepository repository,
        string sourceRecordId, Guid accountId, MissionDefinition mission, CancellationToken ct)
    {
        var source = (await repository.ListBySourceRecordIdsAsync([sourceRecordId], ct))
            .Where(f => f.SourceAccountId == accountId && !string.IsNullOrWhiteSpace(f.StateKey)).ToArray();
        if (source.Length is 0 or > 200) return null;
        var ids = source.Select(f => f.Id).Order().ToArray();
        var effective = mission.PlanIsModelAuthored ? null : mission.EvidenceEffectiveAtUtc;
        var known = mission.PlanIsModelAuthored ? null : mission.EvidenceKnownAtUtc;
        var current = await ReadAsync(repository, ids, accountId, effective, known, ct);
        if (current.Count == 0) return null;
        return new(MissionEvidenceKind.PersonalState, "source:" + sourceRecordId, Digest(current), accountId.ToString("D"))
        {
            ClaimIds = ids, EffectiveAtUtc = effective, KnownAtUtc = known,
            StateSummary = JsonSerializer.Serialize(current.OrderBy(f => f.Id).Select(f => new
                { f.Id, f.StateKey, f.StateStatus, f.ValidFrom, f.SourceRecordId }))
        };
    }

    public static async Task<bool> ValidateAsync(IContextFactRepository repository, MissionObservedState observed,
        MissionDefinition mission, CancellationToken ct)
    {
        if (observed.Kind != MissionEvidenceKind.PersonalState || !observed.Target.StartsWith("source:")
            || observed.ClaimIds.Count is 0 or > 200 || !Guid.TryParse(observed.AccountId, out var account)) return false;
        if (observed.EffectiveAtUtc != (mission.PlanIsModelAuthored ? null : mission.EvidenceEffectiveAtUtc)
            || observed.KnownAtUtc != (mission.PlanIsModelAuthored ? null : mission.EvidenceKnownAtUtc)) return false;
        var source = (await repository.ListBySourceRecordIdsAsync([observed.Target[7..]], ct))
            .Where(f => f.SourceAccountId == account && !string.IsNullOrWhiteSpace(f.StateKey)).Select(f => f.Id).Order();
        if (!source.SequenceEqual(observed.ClaimIds.Order())) return false;
        // Re-resolve the original candidates with all their current siblings. A later
        // correction can invalidate a source even when its own bytes did not change.
        var current = await ReadAsync(repository, observed.ClaimIds, account, observed.EffectiveAtUtc, observed.KnownAtUtc, ct);
        return current.Count > 0 && Digest(current) == observed.VersionDigest;
    }

    private static Task<IReadOnlyList<ContextFact>> ReadAsync(IContextFactRepository repository, IReadOnlyList<Guid> ids,
        Guid account, DateTimeOffset? effective, DateTimeOffset? known, CancellationToken ct)
        => repository.SearchAsync(new() { CandidateFactIds = ids, AllowedSourceAccountIds = new HashSet<Guid> { account },
            EffectiveAt = effective ?? DateTimeOffset.UtcNow, KnownAt = known, IncludeSensitive = false,
            IncludeSuperseded = false, Limit = 200 }, ct);

    private static string Digest(IReadOnlyList<ContextFact> facts) => MissionVerification.Digest(JsonSerializer.Serialize(
        facts.OrderBy(f => f.Id).Select(f => new { f.Id, f.NormalizedKey, f.Text, f.StateKey, f.StateStatus,
            f.ValidFrom, f.ExpiresAt, f.SourceAccountId, f.SourceRecordId, f.Confidence, f.IsUserConfirmed })));
}
