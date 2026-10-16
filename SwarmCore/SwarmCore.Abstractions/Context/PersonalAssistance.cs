namespace SwarmCore.Abstractions.Context;

public enum PersonalActionState { Proposed, Assigned, Executing, Verified, Changed, Retired, Uncertain, Cancelled }
public enum PersonalActionKind { Task, ReplyDraft }
public sealed record PersonalAssistancePage<T>(IReadOnlyList<T> Items, string? NextCursor);
public enum PersonalRecurrenceIntentKind { None, CreateOrUpdate, Pause }
public sealed record PersonalRecurrenceIntent(PersonalRecurrenceIntentKind Kind, string SourceQuote);

/// <summary>Host-owned assignment; source evidence never creates this authority.</summary>
public sealed record PersonalActionAuthority
{
    public required Guid ProfileId { get; init; }
    public required string AssignmentId { get; init; }
    public required string OwnerId { get; init; }
    public required string Provenance { get; init; }
    public int Revision { get; init; } = 1;
    public IReadOnlyList<Guid> AccountIds { get; init; } = [];
    public bool IncludeSensitive { get; init; }
    public Guid? ObjectiveId { get; init; }
    public int? MissionConstraintVersion { get; init; }
    public int? MissionResourceRevision { get; init; }
    public Guid? RuleId { get; init; }
    public bool Revoked { get; init; }
}

public sealed record PersonalActionOperation
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public long Revision { get; init; }
    public required PersonalActionAuthority Authority { get; init; }
    public Guid? OriginalObjectiveId { get; init; }
    public required PersonalActionKind Kind { get; init; }
    public PersonalActionState State { get; init; } = PersonalActionState.Assigned;
    public required Guid FactId { get; init; }
    public required Guid SourceAccountId { get; init; }
    public required Guid SourceMessageId { get; init; }
    public bool SourceIsEmail { get; init; }
    public required string SourceFingerprint { get; init; }
    public required string ObligationKey { get; init; }
    public required string Why { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public string? Recipient { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? DueTimeZoneId { get; init; }
    public required Guid TargetId { get; init; }
    public string? TargetFingerprint { get; init; }
    public string? Outcome { get; init; }
    public Guid? ReceiptId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public Guid? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public int ConsecutiveUncertainOutcomes { get; init; }
}

/// <summary>Bounded local maintenance work; this does not authorize model spend.</summary>
public sealed class PersonalAssistanceLimits
{
    public int MaximumActionsPerPass { get; set; } = 10;
    public int? MaximumConsecutiveUncertainOutcomes { get; set; }
}

public sealed record PersonalAssistanceRule
{
    public required Guid Id { get; init; }
    public long Revision { get; init; }
    public required PersonalActionAuthority Authority { get; init; }
    public required string Instructions { get; init; }
    public bool Enabled { get; init; }
    public string FactKind { get; init; } = "commitment";
    public PersonalActionKind Action { get; init; } = PersonalActionKind.Task;
    public TimeOnly? QuietStartUtc { get; init; }
    public TimeOnly? QuietEndUtc { get; init; }
    public DateTimeOffset? SnoozedUntil { get; init; }
    public bool DigestOnly { get; init; } = true;
    public IReadOnlyList<string> SuppressedObligationKeys { get; init; } = [];
    public string? Cursor { get; init; }
}
