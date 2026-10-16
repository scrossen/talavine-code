using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Runtime;

/// <summary>Host-verified requests are bound to one durable input need, not a general permission bypass.</summary>
public sealed record MissionVerifiedInput(string CallerRequestId, string ReasonCode, IReadOnlyList<string> EvidenceReferences);

public static class MissionInteractionBoundary
{
    public static async Task CheckAsync(string callerRequestId, string source, string prompt, CancellationToken ct)
    {
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is not { MissionObjectiveId: not null } turn) return;
        var validate = turn.ValidateMissionExecution ?? throw new InvalidOperationException("Mission input route requires its durable execution fence.");
        await validate(ct);
        if (turn.VerifiedMissionInput is { } verified && verified.CallerRequestId == callerRequestId
            && !string.IsNullOrWhiteSpace(verified.ReasonCode) && verified.EvidenceReferences.Count > 0) return;
        if (turn.RecordMissionPermissionAttempt is { } record)
            await record(source, MissionInterruptionPolicy.IsRoutineConfirmation(prompt) || turn.MissionInvocationIsBounded, ct);
        throw new MissionActionSkippedException("Unverified human-input gate suppressed. Continue independent work, inspect failures and exhaust authorized alternatives; routine work does not need renewed permission.");
    }
}
