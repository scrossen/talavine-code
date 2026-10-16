using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;

namespace SwarmCore.Agent.Runtime;

/// <summary>Legacy consent routes cannot turn assigned work into a permission card.
/// Only a currently fenced adapter that enforces resource effects can satisfy a missing grant.</summary>
public sealed class MissionPermissionConsentPolicy : IPermissionConsentPolicy
{
    public async Task<PermissionConsentResult?> EvaluateAsync(PermissionConsentRequest request, CancellationToken ct)
    {
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is not { MissionObjectiveId: not null } turn) return null;
        var validate = turn.ValidateMissionExecution ?? throw new InvalidOperationException("Mission consent route requires the host execution fence.");
        await validate(ct);
        var recorder = turn.RecordMissionPermissionAttempt
            ?? throw new InvalidOperationException("Mission consent route requires durable interruption recording.");
        var sessionMatches = request.SessionId == turn.ConversationId;
        var allowed = sessionMatches && turn.MissionInvocationIsBounded;
        await recorder(request.SkillId + "." + request.FunctionName, allowed, ct);
        // This is a one-invocation result derived from the already-bound action. It neither
        // clicks a card nor stores a new standing grant. Unknown actions are skipped.
        return new PermissionConsentResult { Approved = allowed, Scope = PermissionGrantScope.Once };
    }
}
