namespace SwarmCore.Agent.Interactions;

/// <summary>
/// Provider-neutral persistence boundary for user interactions requested by an
/// agent host. This contract records a caller's existing need for input; it
/// does not decide whether input is required and must never add an approval
/// step to an already-authorized operation.
/// </summary>
public interface IInteractionRequestStore
{
    Task<InteractionRequestSnapshot> CreateAsync(
        InteractionRequestCreate request,
        CancellationToken ct = default);

    Task<InteractionRequestSnapshot?> GetAsync(
        string partitionKey,
        string interactionId,
        CancellationToken ct = default);

    Task<InteractionRequestSnapshot?> GetByCallerIdAsync(
        string partitionKey,
        string callerRequestId,
        CancellationToken ct = default);

    Task<IReadOnlyList<InteractionRequestSnapshot>> ListPendingAsync(
        string partitionKey,
        InteractionRequestQuery? query = null,
        CancellationToken ct = default);

    /// <summary>Returns each changed snapshot after the supplied partition-local cursor.</summary>
    Task<InteractionRequestPage> ReadChangesAsync(
        string partitionKey,
        InteractionRequestCursor after,
        int take = 200,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a terminal result exactly once. Retrying the same result is
    /// idempotent; a different result for an already-terminal request fails.
    /// </summary>
    Task<InteractionRequestSnapshot> CompleteAsync(
        string partitionKey,
        string interactionId,
        InteractionRequestCompletion completion,
        CancellationToken ct = default);
}

public enum InteractionRequestKind
{
    Approval = 0,
    Clarification = 1,
    CredentialOrLogin = 2,
    ReviewOrDecision = 3,
    Other = 4,
    QueuedChatUpdate = 5,
}

public enum InteractionRequestState
{
    Pending = 0,
    Resolved = 1,
    Cancelled = 2,
    Superseded = 3,
}

public enum InteractionResolutionKind
{
    Approved = 0,
    Denied = 1,
    Submitted = 2,
    Acknowledged = 3,
    Cancelled = 4,
    Superseded = 5,
}

public readonly record struct InteractionRequestCursor(long Sequence)
{
    public static InteractionRequestCursor Start => new(0);
}

public sealed record InteractionChoice(string Id, string Label, string? Description = null);

public sealed record InteractionFormField(
    string Name,
    string Label,
    string Kind = "text",
    bool Required = true,
    string? HelpText = null);

public sealed record InteractionRequestCreate
{
    public required string PartitionKey { get; init; }
    public required string CallerRequestId { get; init; }
    public required InteractionRequestKind Kind { get; init; }
    public required bool IsBlocking { get; init; }
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    public string? ThreadId { get; init; }
    public Guid? TurnId { get; init; }
    public string? ObjectiveId { get; init; }
    public string? AgentId { get; init; }
    public required string OwnerKind { get; init; }
    public required string OwnerId { get; init; }
    public CapturedSteeringTarget? SteeringTarget { get; init; }
    public IReadOnlyList<InteractionChoice> Choices { get; init; } = [];
    public IReadOnlyList<InteractionFormField> FormFields { get; init; } = [];
    public DateTimeOffset? ExpiresUtc { get; init; }
}

public sealed record InteractionRequestCompletion
{
    public required InteractionResolutionKind Kind { get; init; }
    public required string Actor { get; init; }
    public string? ChoiceId { get; init; }
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>();
    public string? Message { get; init; }
    /// <summary>
    /// SHA-256 identity of the normalized original completion, including
    /// transient secret values that are intentionally omitted from storage.
    /// </summary>
    [System.Text.Json.Serialization.JsonInclude]
    public string? DeliveryFingerprint { get; internal init; }
}

public sealed record InteractionRequestSnapshot(
    string InteractionId,
    string PartitionKey,
    string CallerRequestId,
    InteractionRequestKind Kind,
    InteractionRequestState State,
    bool IsBlocking,
    string Title,
    string Prompt,
    string? ThreadId,
    Guid? TurnId,
    string? ObjectiveId,
    string? AgentId,
    string OwnerKind,
    string OwnerId,
    IReadOnlyList<InteractionChoice> Choices,
    IReadOnlyList<InteractionFormField> FormFields,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? ResolvedUtc,
    InteractionRequestCompletion? Resolution,
    long Cursor)
{
    public CapturedSteeringTarget? SteeringTarget { get; init; }
}

public sealed record CapturedSteeringTarget(string StreamTurnId, Guid? MissionId, int? ConstraintRevision)
{
    public Runtime.AgentSteeringKind Kind { get; init; }
    public IReadOnlyList<Guid>? AccountIds { get; init; }
}

public sealed record InteractionRequestQuery(
    string? ThreadId = null,
    string? ObjectiveId = null,
    string? AgentId = null,
    InteractionRequestKind? Kind = null,
    bool? IsBlocking = null);

public sealed record InteractionRequestPage(
    IReadOnlyList<InteractionRequestSnapshot> Items,
    InteractionRequestCursor NextCursor,
    bool HasMore);
