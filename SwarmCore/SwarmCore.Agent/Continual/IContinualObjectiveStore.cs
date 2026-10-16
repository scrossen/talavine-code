namespace SwarmCore.Agent.Continual;

/// <summary>
/// Provider-neutral durability boundary. SQLite, SQL Server, or another host
/// store owns schema and partition enforcement. The domain layer owns status
/// transitions and backoff; implementations persist the supplied decision.
/// </summary>
public interface IContinualObjectiveStore
{
    /// <summary>Explicit host opt-in; the legacy default checkpoint remains unsupported.</summary>
    bool SupportsRetainedVerificationCandidates => false;
    /// <summary>Lease-bound checkpoint before independent verification; retain the lease while the audit runs.</summary>
    Task<bool> SaveVerificationCandidateAsync(Guid id, string leaseToken, string output, DateTimeOffset atUtc, CancellationToken ct = default)
        => Task.FromResult(false);
    /// <summary>
    /// Atomically creates or joins an objective by the unique tuple
    /// (PartitionKey, Surface, IdempotencyKey). Competing delivery of the same
    /// manual message or scheduler occurrence must return one durable
    /// objective. Tuple members use trimmed, ordinal/case-sensitive identity;
    /// SQL adapters must choose a binary/case-sensitive index collation or save
    /// an equivalent canonical hash. When joining, the existing objective Id
    /// wins; adapters must reject a key collision whose conversation or
    /// executor identity differs.
    /// </summary>
    Task<ContinualObjectiveSnapshot> CreateOrJoinAsync(
        ContinualObjectiveStart start,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<ContinualObjectiveSnapshot?> GetAsync(Guid objectiveId, CancellationToken ct = default);

    Task<ContinualObjectiveSnapshot?> GetLatestForConversationAsync(
        string partitionKey,
        string conversationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ContinualObjectiveSnapshot>> GetDueAsync(
        DateTimeOffset nowUtc,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically acquires an execution lease only when the stored logical
    /// version equals <paramref name="expectedVersion"/>, the objective is due,
    /// and no unexpired lease exists. Exactly one competing caller may win.
    /// </summary>
    Task<ContinualObjectiveLease?> TryClaimAsync(
        Guid objectiveId,
        long expectedVersion,
        string ownerId,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Extends only the matching active lease. A heartbeat is liveness—not
    /// progress—and must not increment attempts or reset no-progress state.
    /// </summary>
    Task<bool> HeartbeatAsync(
        Guid objectiveId,
        string leaseToken,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Idempotently upserts by (objective, attempt, tool-call id). Stores must
    /// reject a receipt whose Sequence is older than LastEventSequence or the
    /// saved receipt, preventing a delayed start/progress event from undoing a
    /// completed receipt.
    /// </summary>
    Task<bool> UpsertReceiptAsync(
        Guid objectiveId,
        string leaseToken,
        ContinualToolReceipt receipt,
        CancellationToken ct = default);

    /// <summary>
    /// Persists progress only when sequence is not older than the objective's
    /// LastEventSequence. Equal sequence is rejected because it may be a
    /// delayed progress callback racing a completion event with that sequence.
    /// </summary>
    Task<bool> RecordProgressAsync(
        Guid objectiveId,
        string leaseToken,
        long sequence,
        string? toolName,
        string status,
        DateTimeOffset atUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a host-computed transition under the active lease. A revoked
    /// lease or an already stopped/terminal objective must reject the write.
    /// </summary>
    Task<bool> ApplyTransitionAsync(
        Guid objectiveId,
        string leaseToken,
        ContinualObjectiveTransition transition,
        CancellationToken ct = default);

    /// <summary>
    /// Explicit operator stop. The expected version makes the update a CAS;
    /// stores also revoke any active lease when it succeeds.
    /// </summary>
    Task<bool> TryStopAsync(
        Guid objectiveId,
        long expectedVersion,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Closes a read-only tool call left <c>Started</c> by an earlier, torn-down attempt (for
    /// example a task-correction boundary that cancelled it mid-flight). Read-only calls have no
    /// side effect to inspect, so unlike a receipt needing inspection they can be closed
    /// immediately as not-dispatched instead of staying Started forever and leaking into
    /// PendingCallIds/continuation prompts after the mission otherwise completes.
    /// </summary>
    Task<bool> CloseAbandonedReadOnlyReceiptAsync(
        Guid objectiveId,
        string leaseToken,
        int attempt,
        string toolCallId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
        => Task.FromResult(false);
}

public sealed record ContinualObjectiveLease(
    Guid ObjectiveId,
    string OwnerId,
    string Token,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long ClaimedVersion);
