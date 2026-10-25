namespace SwarmCore.Agent.Communication;

/// <summary>Authenticated arrival order; null position identifies a question rather than an objective instruction.</summary>
public sealed record GoalInputAuthority(string MessageId, int? Position, DateTimeOffset CreatedUtc);

/// <summary>Host-owned authority metadata. Delivery and acknowledgement do not change this chronology.</summary>
public sealed record GoalInputChronology(int ObjectivePosition, DateTimeOffset ObjectiveUpdatedUtc,
    string ObjectiveSha256, IReadOnlyList<GoalInputAuthority> Messages);
