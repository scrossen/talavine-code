using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.Continual;

public sealed record MissionSteeringReceipt(string AuthorId, string IdempotencyKey, string RequestDigest,
    int ConstraintRevision, long Sequence, DateTimeOffset AcceptedAtUtc, string Message,
    bool RestartExecution, bool RestrictsSources)
{
    public AgentSteeringKind Kind { get; init; }
    public long PriorReceiptSequence { get; init; }
    public int? PriorAuthorityRevision { get; init; }
    public AgentTurnSteeringRequest? Request { get; init; }
}

public sealed record MissionSteeringCommit(ContinualObjectiveSnapshot? Snapshot,
    MissionSteeringAuthorityResult Result, bool RestartExecution = false, bool Changed = false);

/// <summary>Shared restriction-only transition. Persistence adapters commit the resulting
/// snapshot under the same row CAS used by dispatch intent and completion.</summary>
public static class MissionSteeringPolicy
{
    public static bool CanUseReceiptContent(ContinualObjectiveSnapshot objective, ContinualToolReceipt receipt)
    {
        var narrowing = objective.SteeringHistory.LastOrDefault(s => s.RestrictsSources);
        return narrowing is null || narrowing.PriorAuthorityRevision is { } revision
            && receipt.MissionAuthorityRevision is { } observedRevision && observedRevision > revision;
    }

    public static MissionSteeringCommit Apply(ContinualObjectiveSnapshot current,
        AgentTurnSteeringRequest request, AgentTurnSteeringRegistration registration, DateTimeOffset now)
    {
        MissionSteeringCommit Reject(string reason) => new(null, new(false, reason));
        if (registration.ProfileId is not { } profile || request.AuthorId != profile.ToString("D")
            || current.PartitionKey != profile.ToString("D") || request.MissionId != current.Id
            || registration.MissionId != current.Id || current.ConversationId != request.ConversationId
            || registration.ConversationId != request.ConversationId || registration.StreamTurnId != request.ExpectedTurnId)
            return Reject("mission-steering-identity-mismatch");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        var prior = current.SteeringHistory.SingleOrDefault(s => s.AuthorId == request.AuthorId && s.IdempotencyKey == request.IdempotencyKey);
        if (prior is not null)
            return prior.RequestDigest == digest
                ? new(current, new(true, ConstraintRevision: prior.ConstraintRevision), prior.RestartExecution)
                : Reject("steering-idempotency-conflict");
        if (current.Mission is not { } mission || ContinualObjectiveStateMachine.IsTerminal(current.Status)
            || current.Status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.WaitingForInput
            || current.LatestTurnId != registration.DurableTurnId)
            return Reject("mission-turn-ended");
        if (request.ExpectedConstraintRevision is { } expected && expected != mission.ConstraintVersion)
            return Reject("stale-constraint-revision");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.Message)
            || !Enum.IsDefined(request.Kind))
            return Reject("invalid-steering-input");
        var patch = request.Restriction;
        if (patch?.ReplaceAccountScope == true && (request.ExpectedConstraintRevision is null || patch.AllowedSourceAccountIds is null))
            return Reject("explicit-account-replacement-requires-revision-and-targets");
        if (request.Kind == AgentSteeringKind.SideQuestion && patch is not null)
            return Reject("side-question-cannot-change-authority");
        var authority = current.Authority;
        if (patch?.AllowedSourceAccountIds is { } accounts)
        {
            if (accounts.Any(a => a == Guid.Empty)) return Reject("invalid-account-scope");
            var priorAccounts = authority.EffectiveScopeValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!patch.ReplaceAccountScope && authority.ScopeKind != nameof(AgentCommunicationScopeKind.TrustedProfileWide)
                && accounts.Any(a => !priorAccounts.Contains(a.ToString("D"))))
                return Reject("steering-cannot-widen-account-scope");
            // Reconstruct: the record's EffectiveScopeValues is captured by its constructor.
            authority = new(authority.Kind, authority.AuthorityId, authority.UserInitiated, authority.Unattended,
                nameof(AgentCommunicationScopeKind.SelectedAccounts), accounts.Distinct().Select(a => a.ToString("D")).ToArray())
                { MissionResources = authority.MissionResources };
        }
        var restricted = patch is not null && (patch.AllowedSourceAccountIds is not null
            || patch.ResourceExclusions.Count > 0 || patch.ReminderDueAtUtc is not null || patch.ReminderDueDate is not null
            || patch.AdditionalConstraints.Count > 0);
        if (patch?.ResourceExclusions.Count > 0 && authority.MissionResources is null)
            return Reject("mission-resource-authority-unavailable");
        if (restricted && authority.MissionResources is { } resources)
        {
            if (patch!.ResourceExclusions.Any(e => string.IsNullOrWhiteSpace(e.Resource.Identity)
                || !Enum.IsDefined(e.Resource.Kind) || e.Effects == MissionEffect.None))
                return Reject("invalid-resource-exclusion");
            authority = authority with { MissionResources = resources with
            {
                Revision = resources.Revision + 1,
                Exclusions = resources.Exclusions.Concat(patch.ResourceExclusions.Select(e => e with
                    { Provenance = "user-steering:" + request.AuthorId + ":" + request.IdempotencyKey, Explicit = true })).Distinct().ToArray()
            }};
        }
        if (request.Kind != AgentSteeringKind.SideQuestion)
            mission = mission with
            {
                ConstraintVersion = mission.ConstraintVersion + 1,
                Constraints = mission.Constraints.Concat(patch?.AdditionalConstraints ?? []).Distinct(StringComparer.Ordinal).ToArray(),
                ReminderDueAtUtc = patch?.ReminderDueAtUtc ?? (patch?.ReminderDueDate is not null ? null : mission.ReminderDueAtUtc),
                ReminderDueDate = patch?.ReminderDueDate ?? mission.ReminderDueDate,
                ReminderTimeZoneId = patch?.ReminderTimeZoneId ?? mission.ReminderTimeZoneId
            };
        var receipt = new MissionSteeringReceipt(request.AuthorId, request.IdempotencyKey, digest,
            mission.ConstraintVersion, current.SteeringHistory.Count + 1L, now, request.Message,
            restricted, patch?.AllowedSourceAccountIds is not null)
            { Kind = request.Kind, PriorReceiptSequence = current.LastEventSequence,
                PriorAuthorityRevision = current.Authority.MissionResources?.Revision, Request = request };
        var next = current with
        {
            Authority = authority, Mission = mission, SteeringHistory = [.. current.SteeringHistory, receipt], UpdatedAtUtc = now,
            Status = restricted ? ContinualObjectiveStatus.RetryScheduled : current.Status,
            NextAttemptAtUtc = restricted ? now : current.NextAttemptAtUtc,
            VerificationCandidatePending = request.Kind == AgentSteeringKind.SideQuestion && current.VerificationCandidatePending,
            LastOutput = restricted ? null : current.LastOutput,
            LastFailure = restricted ? null : current.LastFailure,
            ContinuationFeedback = request.Kind == AgentSteeringKind.SideQuestion ? current.ContinuationFeedback : request.Message,
            Checkpoint = receipt.RestrictsSources ? null : current.Checkpoint
        };
        return new(next, new(true, ConstraintRevision: mission.ConstraintVersion), restricted, true);
    }
}
