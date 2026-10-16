using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

public sealed class MissionSteeringAuthority(IMissionStore store, IContinualObjectiveExecutionControl execution,
    TimeProvider time) : IMissionSteeringAuthority
{
    public async Task<bool> IsActiveAsync(AgentTurnSteeringRegistration registration, CancellationToken ct)
    {
        if (registration.MissionId is not { } id) return false;
        var mission = await store.GetAsync(id, ct);
        return mission?.Mission is not null && mission.PartitionKey == registration.ProfileId?.ToString("D")
            && mission.ConversationId == registration.ConversationId && mission.LatestTurnId == registration.DurableTurnId
            && mission.Status is ContinualObjectiveStatus.InProgress or ContinualObjectiveStatus.VerificationPending;
    }
    public async Task<IReadOnlyList<RecoveredMissionSteering>> RecoverAcceptedAsync(
        AgentTurnSteeringRegistration activeTurn, CancellationToken ct)
    {
        if (activeTurn.MissionId is not { } id || activeTurn.ProfileId is not { } profile) return [];
        var saved = await store.GetAsync(id, ct);
        if (saved is null || saved.PartitionKey != profile.ToString("D") || saved.ConversationId != activeTurn.ConversationId)
            return [];
        return saved.SteeringHistory.Where(r => r.Request is { } request
                && request.MissionId == id && request.ConversationId == activeTurn.ConversationId
                && request.AuthorId == profile.ToString("D"))
            .OrderBy(r => r.Sequence)
            .Select(r => new RecoveredMissionSteering(r.Request!, r.Sequence, r.ConstraintRevision)).ToArray();
    }

    public async Task<MissionSteeringAuthorityResult> ApplyAsync(AgentTurnSteeringRequest request,
        AgentTurnSteeringRegistration activeTurn, CancellationToken ct)
    {
        var committed = await store.ApplySteeringAsync(request, activeTurn, time.GetUtcNow(), ct);
        // The committed revision fences queued work even if this process dies before cancellation.
        // A repeated request also retries cancellation; already completed operations retain receipts.
        if (committed.Result.Accepted && committed.RestartExecution && request.MissionId is { } id)
            execution.Cancel(id);
        return committed.Result;
    }
}
