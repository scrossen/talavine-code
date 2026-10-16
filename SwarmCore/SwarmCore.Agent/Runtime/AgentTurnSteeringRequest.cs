using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Runtime;

public enum AgentSteeringKind { TaskCorrection, SideQuestion }

/// <summary>Authenticated host input. Never construct from tool or retrieved content.</summary>
public sealed record AgentTurnSteeringRequest(
    string ConversationId, string ExpectedTurnId, string Message,
    string IdempotencyKey, string AuthorId)
{
    public Guid? MissionId { get; init; }
    public int? ExpectedConstraintRevision { get; init; }
    public AgentSteeringKind Kind { get; init; }
    public MissionSteeringRestriction? Restriction { get; init; }
}

/// <summary>Host-resolved task restrictions; null fields preserve current scope.
/// This contract never modifies permanent preferences. Resource restrictions only narrow authority;
/// replacing an account selection requires a separately validated explicit user choice.</summary>
public sealed record MissionSteeringRestriction
{
    public IReadOnlyList<Guid>? AllowedSourceAccountIds { get; init; }
    /// <summary>Only a host-validated explicit user account choice may replace a prior scope.
    /// Requires an expected constraint revision; model/tool input cannot set this authority.</summary>
    public bool ReplaceAccountScope { get; init; }
    public IReadOnlyList<MissionResourceGrant> ResourceExclusions { get; init; } = [];
    public DateOnly? ReminderDueDate { get; init; }
    public string? ReminderTimeZoneId { get; init; }
    public DateTimeOffset? ReminderDueAtUtc { get; init; }
    public IReadOnlyList<string> AdditionalConstraints { get; init; } = [];
}

/// <summary>Runner-authenticated identity of the one active turn.</summary>
public sealed record AgentTurnSteeringRegistration(
    string ConversationId, Guid DurableTurnId, string StreamTurnId, Guid? ProfileId, Guid? MissionId);

public sealed record MissionSteeringAuthorityResult(bool Accepted, string? Reason = null,
    int? ConstraintRevision = null);

/// <summary>Host adapter performs mission-owner checks and idempotent revision-CAS restriction writes.
/// Durable mission acceptance and dispatch use the same serialization boundary. Retrying after a
/// thread-event append failure must recover the same receipt, never apply the restriction twice.</summary>
public sealed record RecoveredMissionSteering(AgentTurnSteeringRequest Request, long Sequence, int? ConstraintRevision);

public interface IMissionSteeringAuthority
{
    Task<bool> IsActiveAsync(AgentTurnSteeringRegistration registration, CancellationToken ct)
        => Task.FromResult(false);
    Task<IReadOnlyList<RecoveredMissionSteering>> RecoverAcceptedAsync(AgentTurnSteeringRegistration activeTurn, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RecoveredMissionSteering>>([]);
    Task<MissionSteeringAuthorityResult> ApplyAsync(AgentTurnSteeringRequest request,
        AgentTurnSteeringRegistration activeTurn, CancellationToken ct);
}
