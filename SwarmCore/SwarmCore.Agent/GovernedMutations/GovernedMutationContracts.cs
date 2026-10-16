namespace SwarmCore.Agent.GovernedMutations;

/// <summary>Reusable host-neutral contract for a validated agent mutation.</summary>
public enum GovernedMutationOperation
{
    Create,
    Update,
    Upsert
}

public sealed record GovernedMutationRequest(
    string EntityType,
    GovernedMutationOperation Operation,
    long? RecordId,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record GovernedMutationFieldChange(string Field, string? PreviousValue, string? NewValue);

/// <summary>A host-produced, tenant-scoped plan. Only the host may issue or apply its token.</summary>
public sealed record GovernedMutationPreview(
    bool IsValid,
    string? Error,
    string EntityType,
    GovernedMutationOperation Operation,
    long? RecordId,
    bool WillCreate,
    IReadOnlyList<GovernedMutationFieldChange> Changes,
    string? PlanToken = null,
    string? RecordLink = null);

public sealed record GovernedMutationResult(
    bool Ok,
    string? Error,
    string EntityType,
    long? RecordId,
    bool Created,
    string? RecordLink,
    IReadOnlyList<GovernedMutationFieldChange> Changes);
