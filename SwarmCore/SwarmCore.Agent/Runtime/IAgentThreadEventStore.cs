namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Durable, provider-neutral ledger for the public events produced by agent
/// turns. The UI conversation store remains the compact presentation model and
/// the agent session store remains the tool transcript; this store is the
/// reconnect/replay boundary shared by UI, worker, and remote clients.
/// Failures before a turn has a durable identity and accepted
/// <see cref="TurnStarted"/>, or while this ledger is unavailable, can only be
/// reported live and are outside the replay guarantee.
/// </summary>
public interface IAgentThreadEventStore
{
    /// <summary>
    /// Append one typed item and atomically advance the containing turn and
    /// thread projections. Implementations must assign a monotonically
    /// increasing sequence within the thread before this method completes.
    /// <paramref name="eventId"/> is a caller-owned idempotency identity:
    /// retrying the same id and durable content returns the original item and
    /// sequence, including after a terminal event. Reusing it for different
    /// content must fail.
    /// A new turn must begin with exactly one <see cref="TurnStarted"/> and
    /// accepts no new event after its first terminal event, except an exact
    /// idempotent retry of that already-recorded event.
    /// Raw <see cref="ReasoningChunk"/> content must not be retained; persist
    /// an ordered <see cref="Thinking"/> activity marker in its place. Tool
    /// input objects, input deltas, and result bodies must likewise be reduced
    /// to receipt metadata rather than retained in this reconnect ledger.
    /// </summary>
    Task<AgentThreadItem> AppendAsync(
        string threadId,
        Guid turnId,
        string eventId,
        TurnEvent turnEvent,
        CancellationToken ct = default);

    Task<AgentThreadSnapshot?> GetThreadAsync(
        string threadId,
        CancellationToken ct = default);

    Task<AgentThreadTurnSnapshot?> GetTurnAsync(
        string threadId,
        Guid turnId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentThreadTurnSnapshot>> ListTurnsAsync(
        string threadId,
        CancellationToken ct = default);

    /// <summary>
    /// Replay items strictly after <paramref name="after"/>. Pass the returned
    /// cursor into the next call; sequences are thread-local and never reused.
    /// </summary>
    Task<AgentThreadItemPage> ReadItemsAsync(
        string threadId,
        AgentThreadCursor after,
        int take = 200,
        CancellationToken ct = default);

    /// <summary>
    /// Replay only selected public item kinds while retaining the same
    /// thread-local cursor semantics. The default scans bounded pages so
    /// existing host adapters remain compatible; durable hosts may override
    /// with an indexed kind-filtered query.
    /// </summary>
    async Task<AgentThreadItemPage> ReadItemsByKindAsync(
        string threadId,
        AgentThreadCursor after,
        IReadOnlySet<AgentThreadItemKind> kinds,
        int take = 200,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (take is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(take), "Page size must be between 1 and 1000.");
        if (kinds.Count == 0)
            return new AgentThreadItemPage([], after, HasMore: false);

        var selected = new List<AgentThreadItem>(take);
        var scanCursor = after;
        while (selected.Count < take)
        {
            var page = await ReadItemsAsync(threadId, scanCursor, 1_000, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                if (!kinds.Contains(item.Kind)) continue;
                selected.Add(item);
                if (selected.Count == take)
                {
                    return new AgentThreadItemPage(
                        selected,
                        new AgentThreadCursor(item.Sequence),
                        HasMore: page.HasMore || item.Sequence < page.NextCursor.Sequence);
                }
            }

            scanCursor = page.NextCursor;
            if (!page.HasMore)
                return new AgentThreadItemPage(selected, scanCursor, HasMore: false);
        }

        return new AgentThreadItemPage(selected, scanCursor, HasMore: false);
    }
}

/// <summary>
/// Stable thread state. <see cref="Ready"/> means the thread can accept a new
/// turn; <see cref="Faulted"/> preserves the latest failed/abandoned outcome
/// until another turn starts.
/// </summary>
public enum AgentThreadStatus
{
    Ready = 0,
    Running = 1,
    Faulted = 2,
}

public enum AgentThreadTurnStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Abandoned = 3,
}

/// <summary>Stable type tag for persisted public turn events.</summary>
public enum AgentThreadItemKind
{
    TurnStarted = 0,
    TextChunk = 1,
    ReasoningActivity = 2,
    ToolCallStarted = 3,
    ToolCallInputDelta = 4,
    ToolCallCompleted = 5,
    Thinking = 6,
    PermissionRequested = 7,
    TurnCompleted = 8,
    TurnErrored = 9,
    TurnAbandoned = 10,
    SteeringAccepted = 11,
    SteeringApplied = 12,
    SteeringDeferred = 13,
    WorkingContextCompactionStarted = 14,
    WorkingContextCompactionCompleted = 15,
    SteeringDeliveryChanged = 16,
    WorkingContextContinuation = 17,
    AdaptiveReasoningChanged = 18,
    UsageReported = 19,
}

public readonly record struct AgentThreadCursor(long Sequence)
{
    public static AgentThreadCursor Start => new(0);
}

public sealed record AgentThreadSnapshot(
    string ThreadId,
    AgentThreadStatus Status,
    long LastSequence,
    IReadOnlyList<Guid> ActiveTurnIds,
    Guid? LastTurnId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record AgentThreadTurnSnapshot(
    string ThreadId,
    Guid TurnId,
    string StreamTurnId,
    AgentThreadTurnStatus Status,
    long FirstSequence,
    long LastSequence,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc);

/// <summary>
/// One replayable item. <see cref="Kind"/> is the stable persisted type tag;
/// for <see cref="AgentThreadItemKind.ReasoningActivity"/>, <see cref="Event"/>
/// is a content-free <see cref="Thinking"/> marker.
/// </summary>
public sealed record AgentThreadItem(
    string ThreadId,
    Guid TurnId,
    string EventId,
    long Sequence,
    AgentThreadItemKind Kind,
    TurnEvent Event,
    DateTimeOffset CreatedUtc);

public sealed record AgentThreadItemPage(
    IReadOnlyList<AgentThreadItem> Items,
    AgentThreadCursor NextCursor,
    bool HasMore);
