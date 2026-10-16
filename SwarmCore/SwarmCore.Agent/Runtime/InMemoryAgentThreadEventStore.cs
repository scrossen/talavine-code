using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Process-local reference implementation of <see cref="IAgentThreadEventStore"/>.
/// Host applications can replace it with SQLite, SQL Server, or another
/// transactional store without changing the runner or replay contract.
/// </summary>
public sealed class InMemoryAgentThreadEventStore : IAgentThreadEventStore
{
    private readonly ConcurrentDictionary<string, ThreadState> _threads =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryAgentThreadEventStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AgentThreadItem> AppendAsync(
        string threadId,
        Guid turnId,
        string eventId,
        TurnEvent turnEvent,
        CancellationToken ct = default)
    {
        ValidateThreadAndTurn(threadId, turnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (eventId.Length > 256)
            throw new ArgumentException("Event id cannot exceed 256 characters.", nameof(eventId));
        ArgumentNullException.ThrowIfNull(turnEvent);
        ct.ThrowIfCancellationRequested();

        var normalizedThreadId = threadId.Trim();
        var state = _threads.GetOrAdd(normalizedThreadId, static id => new ThreadState(id));
        lock (state.Sync)
        {
            ct.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            var kind = GetKind(turnEvent);
            var storedEvent = AgentThreadEventPolicy.Freeze(turnEvent);

            if (state.ItemsByEventId.TryGetValue(eventId, out var existing))
            {
                if (existing.TurnId != turnId
                    || existing.Kind != kind
                    || !EventsEquivalent(existing.Event, storedEvent))
                {
                    throw new InvalidOperationException(
                        $"Event id '{eventId}' was retried with different durable content.");
                }

                return Task.FromResult(CloneItem(existing));
            }

            state.Turns.TryGetValue(turnId, out var currentTurn);
            AgentThreadEventPolicy.ValidateAppend(currentTurn is null ? null : CreateTurnSnapshot(state.ThreadId, currentTurn), turnId, storedEvent);
            if (!state.Turns.TryGetValue(turnId, out var turn))
            {
                turn = new MutableTurn(turnId, storedEvent.TurnId, now);
                state.Turns.Add(turnId, turn);
                state.TurnOrder.Add(turnId);
            }

            var sequence = checked(++state.LastSequence);
            var item = new AgentThreadItem(
                normalizedThreadId,
                turnId,
                eventId,
                sequence,
                kind,
                storedEvent,
                now);
            state.Items.Add(item);
            state.ItemsByEventId.Add(eventId, item);

            turn.FirstSequence ??= sequence;
            turn.LastSequence = sequence;
            turn.UpdatedUtc = now;
            turn.Status = AgentThreadEventPolicy.StatusAfter(storedEvent);
            if (turn.Status != AgentThreadTurnStatus.Running) turn.CompletedUtc = now;

            state.CreatedUtc ??= now;
            state.UpdatedUtc = now;
            return Task.FromResult(CloneItem(item));
        }
    }

    public Task<AgentThreadSnapshot?> GetThreadAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ct.ThrowIfCancellationRequested();
        if (!_threads.TryGetValue(threadId.Trim(), out var state))
            return Task.FromResult<AgentThreadSnapshot?>(null);

        lock (state.Sync)
        {
            return Task.FromResult<AgentThreadSnapshot?>(CreateThreadSnapshot(state));
        }
    }

    public Task<AgentThreadTurnSnapshot?> GetTurnAsync(
        string threadId,
        Guid turnId,
        CancellationToken ct = default)
    {
        ValidateThreadAndTurn(threadId, turnId);
        ct.ThrowIfCancellationRequested();
        if (!_threads.TryGetValue(threadId.Trim(), out var state))
            return Task.FromResult<AgentThreadTurnSnapshot?>(null);

        lock (state.Sync)
        {
            return Task.FromResult(
                state.Turns.TryGetValue(turnId, out var turn)
                    ? CreateTurnSnapshot(state.ThreadId, turn)
                    : null);
        }
    }

    public Task<IReadOnlyList<AgentThreadTurnSnapshot>> ListTurnsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ct.ThrowIfCancellationRequested();
        if (!_threads.TryGetValue(threadId.Trim(), out var state))
            return Task.FromResult<IReadOnlyList<AgentThreadTurnSnapshot>>([]);

        lock (state.Sync)
        {
            IReadOnlyList<AgentThreadTurnSnapshot> result = state.TurnOrder
                .Select(id => CreateTurnSnapshot(state.ThreadId, state.Turns[id]))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<AgentThreadItemPage> ReadItemsAsync(
        string threadId,
        AgentThreadCursor after,
        int take = 200,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (after.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(after), "Cursor sequence cannot be negative.");
        if (take is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(take), "Page size must be between 1 and 1000.");
        ct.ThrowIfCancellationRequested();

        if (!_threads.TryGetValue(threadId.Trim(), out var state))
        {
            return Task.FromResult(new AgentThreadItemPage([], after, HasMore: false));
        }

        lock (state.Sync)
        {
            var candidates = state.Items
                .Where(item => item.Sequence > after.Sequence)
                .Take(take + 1)
                .ToArray();
            var hasMore = candidates.Length > take;
            var items = candidates
                .Take(take)
                .Select(CloneItem)
                .ToArray();
            var next = items.Length == 0
                ? after
                : new AgentThreadCursor(items[^1].Sequence);
            return Task.FromResult(new AgentThreadItemPage(items, next, hasMore));
        }
    }

    public Task<AgentThreadItemPage> ReadItemsByKindAsync(
        string threadId,
        AgentThreadCursor after,
        IReadOnlySet<AgentThreadItemKind> kinds,
        int take = 200,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(kinds);
        if (after.Sequence < 0) throw new ArgumentOutOfRangeException(nameof(after));
        if (take is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(take));
        ct.ThrowIfCancellationRequested();
        if (kinds.Count == 0 || !_threads.TryGetValue(threadId.Trim(), out var state))
            return Task.FromResult(new AgentThreadItemPage([], after, HasMore: false));

        lock (state.Sync)
        {
            var candidates = state.Items
                .Where(item => item.Sequence > after.Sequence && kinds.Contains(item.Kind))
                .Take(take + 1)
                .ToArray();
            var items = candidates.Take(take).Select(CloneItem).ToArray();
            return Task.FromResult(new AgentThreadItemPage(
                items,
                items.Length == 0 ? after : new AgentThreadCursor(items[^1].Sequence),
                candidates.Length > take));
        }
    }

    private static AgentThreadSnapshot CreateThreadSnapshot(ThreadState state)
    {
        var active = state.TurnOrder
            .Where(id => state.Turns[id].Status == AgentThreadTurnStatus.Running)
            .ToArray();
        // Concurrent turns are tolerated because existing hosts did not
        // serialize them. The last item, rather than creation order, owns the
        // stable terminal projection once no turn remains active.
        var latest = state.Items.Count == 0 ? null : state.Turns[state.Items[^1].TurnId];
        var status = active.Length > 0
            ? AgentThreadStatus.Running
            : latest?.Status is AgentThreadTurnStatus.Failed or AgentThreadTurnStatus.Abandoned
                ? AgentThreadStatus.Faulted
                : AgentThreadStatus.Ready;

        var updated = state.UpdatedUtc ?? _unixEpoch;
        return new AgentThreadSnapshot(
            state.ThreadId,
            status,
            state.LastSequence,
            active,
            latest?.TurnId,
            state.CreatedUtc ?? updated,
            updated);
    }

    private static readonly DateTimeOffset _unixEpoch = DateTimeOffset.UnixEpoch;

    private static AgentThreadTurnSnapshot CreateTurnSnapshot(string threadId, MutableTurn turn) => new(
        threadId,
        turn.TurnId,
        turn.StreamTurnId,
        turn.Status,
        turn.FirstSequence ?? 0,
        turn.LastSequence,
        turn.StartedUtc,
        turn.UpdatedUtc,
        turn.CompletedUtc);

    private static AgentThreadItemKind GetKind(TurnEvent turnEvent) => AgentThreadEventPolicy.GetKind(turnEvent);

    private static AgentThreadItem CloneItem(AgentThreadItem item) => item with
    {
        Event = AgentThreadEventPolicy.Freeze(item.Event),
    };

    private static bool EventsEquivalent(TurnEvent left, TurnEvent right)
    {
        return AgentThreadEventPolicy.Equivalent(left, right);
    }

    private static void ValidateThreadAndTurn(string threadId, Guid turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (turnId == Guid.Empty)
            throw new ArgumentException("A durable turn id is required.", nameof(turnId));
    }

    private sealed class ThreadState(string threadId)
    {
        public object Sync { get; } = new();
        public string ThreadId { get; } = threadId;
        public Dictionary<Guid, MutableTurn> Turns { get; } = [];
        public List<Guid> TurnOrder { get; } = [];
        public List<AgentThreadItem> Items { get; } = [];
        public Dictionary<string, AgentThreadItem> ItemsByEventId { get; } =
            new(StringComparer.Ordinal);
        public long LastSequence { get; set; }
        public DateTimeOffset? CreatedUtc { get; set; }
        public DateTimeOffset? UpdatedUtc { get; set; }
    }

    private sealed class MutableTurn(Guid turnId, string streamTurnId, DateTimeOffset startedUtc)
    {
        public Guid TurnId { get; } = turnId;
        public string StreamTurnId { get; } = streamTurnId;
        public AgentThreadTurnStatus Status { get; set; } = AgentThreadTurnStatus.Running;
        public long? FirstSequence { get; set; }
        public long LastSequence { get; set; }
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public DateTimeOffset UpdatedUtc { get; set; } = startedUtc;
        public DateTimeOffset? CompletedUtc { get; set; }
    }
}
