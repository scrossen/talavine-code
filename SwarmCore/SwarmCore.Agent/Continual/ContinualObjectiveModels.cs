using System.Collections.ObjectModel;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Host-owned lifecycle of a durable objective. Numeric values deliberately
/// match Talavine's existing worker status values so persistence adapters
/// can migrate without rewriting stored rows.
/// </summary>
public enum ContinualObjectiveStatus
{
    InProgress = 0,
    Completed = 1,
    Idle = 2,
    RetryScheduled = 3,
    VerificationPending = 4,
    WaitingForInput = 5,
    DeliveryBlocked = 6,
    AttentionRequired = 7,
    Failed = 8,
    Paused = 9,
    Cancelled = 10,
    Uncertain = 11,
}

/// <summary>The trusted source of authority carried by an objective.</summary>
public enum ContinualAuthorityKind
{
    /// <summary>A person directly requested this objective.</summary>
    UserRequest = 0,

    /// <summary>A person durably delegated this class of work to an agent.</summary>
    DurableDelegation = 1,

    /// <summary>A host-created schedule is executing within pre-existing authority.</summary>
    ScheduledAutomation = 2,

    /// <summary>Internal maintenance initiated by the host itself.</summary>
    System = 3,
}

/// <summary>
/// Typed, host-authored authority that must survive continuation unchanged.
/// Scope values are opaque identifiers interpreted only by the owning host;
/// they must contain references, never credentials.
/// </summary>
public sealed record ContinualObjectiveAuthority(
    ContinualAuthorityKind Kind,
    string AuthorityId,
    bool UserInitiated,
    bool Unattended,
    string? ScopeKind = null,
    IReadOnlyList<string>? ScopeValues = null)
{
    public MissionAuthority? MissionResources { get; init; }
    public IReadOnlyList<string> EffectiveScopeValues { get; } =
        new ReadOnlyCollection<string>((ScopeValues ?? []).ToArray());

    public static ContinualObjectiveAuthority UserRequested(
        string authorityId,
        string? scopeKind = null,
        IReadOnlyList<string>? scopeValues = null,
        bool unattended = false)
        => new(
            ContinualAuthorityKind.UserRequest,
            RequireAuthority(authorityId),
            UserInitiated: true,
            Unattended: unattended,
            scopeKind,
            scopeValues);

    public static ContinualObjectiveAuthority Delegated(
        string authorityId,
        string? scopeKind = null,
        IReadOnlyList<string>? scopeValues = null,
        bool unattended = true)
        => new(
            ContinualAuthorityKind.DurableDelegation,
            RequireAuthority(authorityId),
            UserInitiated: true,
            Unattended: unattended,
            scopeKind,
            scopeValues);

    private static string RequireAuthority(string authorityId)
        => string.IsNullOrWhiteSpace(authorityId)
            ? throw new ArgumentException("An authority id is required.", nameof(authorityId))
            : authorityId.Trim();
}

/// <summary>How much retry caution a receipt requires.</summary>
public enum ContinualSideEffectClass
{
    /// <summary>The host could not classify the tool; verify before repeating it.</summary>
    Unknown = -1,
    ReadOnly = 0,
    WritesLocal = 1,
    ExternalSend = 2,
    Destructive = 3,
    Exec = 4,
}

public enum ContinualToolReceiptState
{
    Started = 0,
    Completed = 1,
}

/// <summary>
/// Durable evidence for one tool invocation. A Started receipt is written
/// before execution; the same key is then upserted to Completed. Sequence is
/// monotonic within an attempt so delayed progress cannot overwrite newer
/// completion state.
/// </summary>
public sealed record ContinualToolReceipt(
    Guid ObjectiveId,
    int Attempt,
    long Sequence,
    string ToolCallId,
    string ToolName,
    ContinualToolReceiptState State,
    ContinualSideEffectClass SideEffect,
    string? InputJson,
    string? Result,
    bool IsError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc = null,
    long? ElapsedMs = null,
    string? OperationKey = null,
    string? RecoveryJson = null)
{
    public bool WasDispatched { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SwarmCore.Agent.Catalog.ToolDispatchPlan? DispatchPlan { get; init; }
    /// <summary>Captured from the executing host scope, unchanged when a late result arrives.</summary>
    public int? MissionAuthorityRevision { get; init; }
    public bool LocalProcessCompleted { get; init; }
    /// <summary>Set only from the host tool descriptor for an idempotent mission checkpoint operation.</summary>
    public bool SupportsIdempotentMissionReplay { get; init; }
    /// <summary>Host-recorded content identities of this mission's tracked file artifacts.</summary>
    public IReadOnlyDictionary<string, string>? ArtifactHashes { get; init; }
    public string? ArtifactEvidenceError { get; init; }
    public string? CodingTreeDigest { get; init; }
    public MissionReceiptEvidence? Evidence { get; init; }
    public IReadOnlyList<MissionObservedState> ObservedStates { get; init; } = [];
}

/// <summary>Immutable provider-neutral view returned by persistence adapters.</summary>
public sealed record ContinualObjectiveSnapshot
{
    public ContinualVerificationCandidate? VerificationCandidate { get; init; }
    public bool CompletionVerificationPending { get; init; }
    public required Guid Id { get; init; }
    public required string Surface { get; init; }
    public required string PartitionKey { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string ConversationId { get; init; }
    public required string ExecutorKey { get; init; }
    /// <summary>
    /// Opaque, host-authored rehydration data. It must contain only durable
    /// references—not credentials—and must remain valid JSON when a host uses
    /// JSON. The core processor deliberately does not truncate or rewrite it.
    /// </summary>
    public string? ExecutorPayloadJson { get; init; }
    public MissionDefinition? Mission { get; init; }
    public MissionJoinWait? JoinWait { get; init; }
    public IReadOnlyList<MissionSteeringReceipt> SteeringHistory { get; init; } = [];
    /// <summary>Historical defects remain visible even after autonomous recovery succeeds.</summary>
    public int UnnecessaryConfirmationCount => MissionInterruptionPolicy.UnnecessaryConfirmationCount(Receipts);
    public MissionCheckpoint? Checkpoint { get; init; }
    public MissionTargetClarification? TargetClarification { get; init; }
    public MissionExternalDependency? ExternalDependency { get; init; }
    public MissionConstraintConflict? ConstraintConflict { get; init; }
    public MissionIntegrationConflict? IntegrationConflict { get; init; }
    public Guid? PausedByParentId { get; init; }
    public Guid? InitialTurnId { get; init; }
    public Guid? LatestTurnId { get; init; }
    public required string ObjectiveText { get; init; }
    public required string ObjectiveSummary { get; init; }
    public required ContinualObjectiveAuthority Authority { get; init; }
    public ContinualObjectiveStatus Status { get; init; } = ContinualObjectiveStatus.InProgress;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset LastProgressAtUtc { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public int AttemptCount { get; init; } = 1;
    public int ConsecutiveNoProgress { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? CurrentToolName { get; init; }
    public bool HasPossibleSideEffects { get; init; }
    public IReadOnlyList<ContinualToolReceipt> Receipts { get; init; } = [];
    public IReadOnlyList<MissionVerificationRun> VerificationHistory { get; init; } = [];
    public bool VerificationCandidatePending { get; init; }
    public string? LastOutput { get; init; }
    public string? LastFailure { get; init; }
    public string? LastOutputFingerprint { get; init; }
    public string? ContinuationFeedback { get; init; }

    /// <summary>
    /// Highest persisted execution-event sequence. Stores use this to reject a
    /// delayed progress callback that arrives after a newer completion receipt.
    /// </summary>
    public long LastEventSequence { get; init; }

    /// <summary>Logical compare-and-swap version maintained by the store.</summary>
    public long Version { get; init; }

    public string? LeaseOwner { get; init; }
    public string? LeaseToken { get; init; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; init; }
}

/// <summary>Host-owned input for creating an objective.</summary>
public sealed record ContinualObjectiveStart
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Surface { get; init; }
    public required string PartitionKey { get; init; }
    /// <summary>
    /// Stable host-authored identity of the trigger, such as a persisted chat
    /// turn id, boss-message id, or scheduler occurrence id. It must not be
    /// derived only from objective text because a user may intentionally issue
    /// the same instruction twice.
    /// </summary>
    public required string IdempotencyKey { get; init; }
    public required string ConversationId { get; init; }
    public required string ExecutorKey { get; init; }
    /// <inheritdoc cref="ContinualObjectiveSnapshot.ExecutorPayloadJson"/>
    public string? ExecutorPayloadJson { get; init; }
    public MissionDefinition? Mission { get; init; }

    public MissionCheckpoint? Checkpoint { get; init; }
    public Guid? InitialTurnId { get; init; }
    public required string ObjectiveText { get; init; }
    public required string ObjectiveSummary { get; init; }
    public required ContinualObjectiveAuthority Authority { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}

public enum ContinualAcceptanceOutcome
{
    Delivered = 0,
    Retry = 1,
    NeedsUserHelp = 2,
    VerificationUnavailable = 3,
    /// <summary>A checked, sealed audit rejection; retain the candidate without retrying execution or audit.</summary>
    ClosedAuditHold = 4,
    /// <summary>The host cannot prove the audit ledger is open or closed; retain the candidate without retrying.</summary>
    AuditClosureUnknownHold = 5,
    /// <summary>The retained local result or immutable dispatched effect contract is denied; no execution or audit retry is permitted.</summary>
    LocalResultContractHold = 6,
}

public enum ContinualRetainedVerificationHoldKind { LocalResultOrEffectContractDenied = 1 }

public sealed record ContinualAcceptanceVerdict(
    ContinualAcceptanceOutcome Outcome,
    string? Reason = null)
{
    public MissionVerificationRun? Verification { get; init; }
    /// <summary>
    /// Retry only: the host proved from its own typed receipts that the attempt performed no physical action,
    /// so the retained candidate and possible-effect marker are cleared and the next claim may execute again.
    /// A host must never set this from model text or from an uncertain outcome.
    /// </summary>
    public bool NoEffectConfirmed { get; init; }

    /// <summary>
    /// The acceptance check could not judge the attempt (for example, the
    /// auditor had no usable route), so the outcome is the host's conservative
    /// fallback. A retry on this basis is not evidence of progress and advances
    /// the no-progress backoff even when the model reworded its output.
    /// </summary>
    public bool EvaluationUnavailable { get; init; }

    public static readonly ContinualAcceptanceVerdict Delivered =
        new(ContinualAcceptanceOutcome.Delivered);
}

public sealed record ContinualAcceptanceRequest(
    Guid ObjectiveId,
    string ObjectiveText,
    string FinalOutput,
    IReadOnlyList<ContinualToolReceipt> Receipts,
    bool HasPossibleSideEffects,
    string? Provider = null,
    string? Model = null)
{
    public ContinualObjectiveSnapshot? Objective { get; init; }
    /// <summary>Explicit host policy; missing exact route fails before any audit inference. Ordinary hosts retain their routing behavior.</summary>
    public bool RequireExactRoute { get; init; }
}

/// <summary>A state-machine decision for one atomic store transition.</summary>
public sealed record ContinualObjectiveTransition(
    ContinualObjectiveStatus Status,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset LastProgressAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastOutput,
    string? LastFailure,
    string? LastOutputFingerprint,
    string? ContinuationFeedback,
    int ConsecutiveNoProgress,
    bool ClearCurrentTool = true,
    bool ReleaseLease = true)
{
    public MissionVerificationRun? Verification { get; init; }
    public ContinualVerificationExpectation? ExpectedVerificationCandidate { get; init; }
    /// <summary>Retain an exact saved candidate even when the chosen status is not VerificationPending.</summary>
    public bool RetainVerificationCandidate { get; init; }
    /// <summary>Drop the saved candidate and the possible-effect marker: the host proved the attempt had no effect.</summary>
    public bool ClearVerificationCandidate { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ContinualRetainedVerificationHoldKind? RetainedHoldKind { get; init; }
}
