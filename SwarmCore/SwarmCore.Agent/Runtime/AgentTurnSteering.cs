using System.Threading.Channels;
using SwarmCore.Abstractions.LlmRouting;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Accepts user-authored updates for the currently running turn without
/// starting a second turn. Callers must provide the stream turn id they saw
/// in <see cref="TurnStarted"/> so a late UI cannot steer newer work.
/// </summary>
public interface IAgentTurnSteeringService
{
    Task<AgentTurnSteeringRegistration?> GetActiveRegistrationAsync(string conversationId, CancellationToken ct = default)
        => Task.FromResult<AgentTurnSteeringRegistration?>(null);
    Task<AgentTurnSteeringResult> SteerAsync(AgentTurnSteeringRequest request, CancellationToken ct = default)
        => SteerAsync(request.ConversationId, request.ExpectedTurnId, request.Message, ct);
    Task<AgentTurnSteeringResult> SteerAsync(
        string conversationId,
        string expectedTurnId,
        string message,
        CancellationToken ct = default);
}

public enum AgentTurnSteeringStatus
{
    Accepted = 0,
    NoActiveTurn = 1,
    StaleExpectedTurn = 2,
    TurnEnded = 3,
    Rejected = 4,
}

public sealed record AgentTurnSteeringResult(
    AgentTurnSteeringStatus Status,
    string? SteeringId = null,
    string? ActiveTurnId = null)
{
    public long? Sequence { get; init; }
    public int? ConstraintRevision { get; init; }
    public string? Reason { get; init; }
    public bool Accepted => Status == AgentTurnSteeringStatus.Accepted;
}

/// <summary>One accepted update waiting for an engine boundary.</summary>
public sealed record AgentTurnSteeringUpdate(string SteeringId, string Message);

/// <summary>
/// Synchronous source read by the engine only at model/tool boundaries. The
/// coordinator performs durable acceptance; the engine merely drains updates.
/// </summary>
public interface IAgentTurnSteeringSource : IChatSteeringSource
{
    bool IChatSteeringSource.TryTake(out ChatSteeringUpdate update)
    {
        if (TryTake(out var value)) { update = new(value.SteeringId, value.Message); return true; }
        update = null!; return false;
    }
    async ValueTask IChatSteeringSource.WaitForUpdateAsync(CancellationToken ct)
        => await Task.Delay(25, ct).ConfigureAwait(false);
    bool TryTake(out AgentTurnSteeringUpdate update);
}

/// <summary>Runner-facing lifecycle around one registered active turn.</summary>
public interface IAgentTurnSteeringCoordinator : IAgentTurnSteeringService
{
    Task<IAgentTurnSteeringSource> RegisterAsync(AgentTurnSteeringRegistration registration, CancellationToken ct = default)
        => Task.FromResult(Register(registration.ConversationId, registration.DurableTurnId, registration.StreamTurnId));
    IAgentTurnSteeringSource Register(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId);

    /// <summary>
    /// Releases coordinator ownership only after the runner durably records
    /// that the engine applied this update. Until then, close recovery treats
    /// a dequeued update as accepted-but-unapplied and defers it.
    /// </summary>
    Task MarkAppliedAsync(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId,
        string steeringId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically stops accepting updates and records any accepted-but-not-yet
    /// consumed input as deferred. The returned events let the live UI show the
    /// same honest outcome that reconnect clients replay.
    /// </summary>
    Task<IReadOnlyList<TurnSteeringDeferred>> CloseAsync(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId,
        CancellationToken ct = default);
}

/// <summary>
/// Process-local rendezvous for active engines. The updates themselves are
/// durable thread items; only the live delivery queue is process-local.
/// </summary>
public sealed class AgentTurnSteeringCoordinator(
    IAgentThreadEventStore threadEvents, IMissionSteeringAuthority? missionAuthority = null) : IAgentTurnSteeringCoordinator
{
    private readonly ConcurrentDictionary<string, ActiveTurn> _active =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TurnKey, ActiveTurn> _turns = new();

    public async Task<AgentTurnSteeringRegistration?> GetActiveRegistrationAsync(string conversationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_active.TryGetValue(conversationId, out var state) || state.IsClosingOrClosed) return null;
        if (state.Registration is { MissionId: not null } registration && missionAuthority is not null
            && !await missionAuthority.IsActiveAsync(registration, ct))
        {
            _active.TryRemove(new KeyValuePair<string, ActiveTurn>(state.ConversationId, state));
            _turns.TryRemove(new KeyValuePair<TurnKey, ActiveTurn>(state.Key, state));
            return null;
        }
        return state.Registration;
    }

    public IAgentTurnSteeringSource Register(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamTurnId);
        if (durableTurnId == Guid.Empty)
            throw new ArgumentException("A durable turn id is required.", nameof(durableTurnId));

        var state = new ActiveTurn(conversationId.Trim(), durableTurnId, streamTurnId.Trim());
        if (!_turns.TryAdd(state.Key, state))
            throw new InvalidOperationException($"Turn '{state.StreamTurnId}' is already registered for steering.");
        _active.AddOrUpdate(state.ConversationId, state, (_, previous) =>
        {
            if (previous.Registration?.MissionId is not null)
                _turns.TryRemove(new KeyValuePair<TurnKey, ActiveTurn>(previous.Key, previous));
            return state;
        });
        return state;
    }

    public Task<AgentTurnSteeringResult> SteerAsync(string conversationId, string expectedTurnId,
        string message, CancellationToken ct = default)
        => SteerAsync(new(conversationId, expectedTurnId, message, Guid.NewGuid().ToString("N"), "authenticated-host"), ct);

    private static readonly IReadOnlySet<AgentThreadItemKind> RecoveryKinds = new HashSet<AgentThreadItemKind>
    {
        AgentThreadItemKind.TurnStarted, AgentThreadItemKind.SteeringAccepted,
        AgentThreadItemKind.SteeringApplied, AgentThreadItemKind.SteeringDeliveryChanged,
    };
    private static readonly IReadOnlySet<AgentThreadItemKind> AcceptedKind = new HashSet<AgentThreadItemKind> { AgentThreadItemKind.SteeringAccepted };

    public async Task<IAgentTurnSteeringSource> RegisterAsync(AgentTurnSteeringRegistration registration, CancellationToken ct = default)
    {
        var state = (ActiveTurn)Register(registration.ConversationId, registration.DurableTurnId, registration.StreamTurnId);
        var entered = false;
        try
        {
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        entered = true;
        state.Registration = registration;
        // Reconstruct accepted-but-unapplied work; process-local provider queues are never recovery proof.
        var accepted = new Dictionary<string, TurnSteeringAccepted>(StringComparer.Ordinal);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new Dictionary<Guid, Guid>();
        var cursor = AgentThreadCursor.Start;
        while (true)
        {
            // Only turn starts and steering lifecycle items: never every streamed chunk in the thread.
            var page = await threadEvents.ReadItemsByKindAsync(registration.ConversationId, cursor, RecoveryKinds, 1000, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                if (item.Event is TurnStarted started) profiles[item.TurnId] = started.ProfileId;
                if (item.Event is TurnSteeringAccepted a && a.Request?.MissionId == registration.MissionId
                    && (a.ProfileId ?? profiles.GetValueOrDefault(item.TurnId)) == registration.ProfileId) accepted[a.SteeringId] = a;
                if (item.Event is TurnSteeringApplied a2) applied.Add(a2.SteeringId);
                if (item.Event is TurnSteeringDeliveryChanged { Status: "Applied" } a3) applied.Add(a3.SteeringId);
            }
            cursor = page.NextCursor;
            if (!page.HasMore) break;
        }
        if (registration.MissionId is not null && missionAuthority is not null)
        {
            foreach (var recovered in await missionAuthority.RecoverAcceptedAsync(registration, ct).ConfigureAwait(false))
            {
                var id = SteeringId(recovered.Request);
                if (accepted.ContainsKey(id) || applied.Contains(id)) continue;
                var receipt = new TurnSteeringAccepted(registration.StreamTurnId, id, recovered.Request.Message)
                { Request = recovered.Request, ProfileId = registration.ProfileId, Sequence = recovered.Sequence, ConstraintRevision = recovered.ConstraintRevision };
                await threadEvents.AppendAsync(registration.ConversationId, registration.DurableTurnId,
                    EventId(registration.DurableTurnId, id, "accepted"), receipt, ct).ConfigureAwait(false);
                accepted[id] = receipt;
            }
        }
        foreach (var acceptedUpdate in accepted.Values.OrderBy(x => x.Sequence))
        {
            state.Remember(acceptedUpdate);
            if (!applied.Contains(acceptedUpdate.SteeringId))
                state.Enqueue(new(acceptedUpdate.SteeringId, acceptedUpdate.Message));
        }
        return state;
        }
        catch
        {
            state.BeginClosing(); state.CompleteClose();
            _active.TryRemove(new KeyValuePair<string, ActiveTurn>(state.ConversationId, state));
            _turns.TryRemove(new KeyValuePair<TurnKey, ActiveTurn>(state.Key, state));
            throw;
        }
        finally { if (entered) state.Gate.Release(); }
    }

    public async Task<AgentTurnSteeringResult> SteerAsync(AgentTurnSteeringRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedTurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AuthorId);
        var conversationId = request.ConversationId;
        var expectedTurnId = request.ExpectedTurnId;
        var message = request.Message;
        var normalizedConversationId = conversationId.Trim();
        var normalizedMessage = message.Trim();
        if (!_active.TryGetValue(normalizedConversationId, out var state))
            return new AgentTurnSteeringResult(AgentTurnSteeringStatus.NoActiveTurn);

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.IsClosingOrClosed || !_active.TryGetValue(normalizedConversationId, out var current)
                             || !ReferenceEquals(state, current))
            {
                return new AgentTurnSteeringResult(
                    AgentTurnSteeringStatus.TurnEnded,
                    ActiveTurnId: state.StreamTurnId);
            }

            if (!string.Equals(state.StreamTurnId, expectedTurnId.Trim(), StringComparison.Ordinal))
            {
                return new AgentTurnSteeringResult(
                    AgentTurnSteeringStatus.StaleExpectedTurn,
                    ActiveTurnId: state.StreamTurnId);
            }

            if (request.MissionId != state.Registration?.MissionId)
                return new(AgentTurnSteeringStatus.Rejected) { Reason = "mission-mismatch" };
            var id = SteeringId(request);
            if (!state.Accepted.ContainsKey(id))
            {
                // An append may commit and then lose its acknowledgment. Recover the exact
                // durable request before invoking the mission adapter or assigning a new sequence.
                var cursor = AgentThreadCursor.Start;
                while (true)
                {
                    var page = await threadEvents.ReadItemsByKindAsync(state.ConversationId, cursor, AcceptedKind, 1000, ct).ConfigureAwait(false);
                    var committed = page.Items.Select(i => i.Event).OfType<TurnSteeringAccepted>().FirstOrDefault(a => a.SteeringId == id);
                    if (committed is not null)
                    {
                        state.Remember(committed);
                        state.Enqueue(new(committed.SteeringId, committed.Message));
                        break;
                    }
                    if (!page.HasMore) break;
                    cursor = page.NextCursor;
                }
            }
            if (state.Accepted.TryGetValue(id, out var existing))
                return JsonSerializer.Serialize(existing.Request) == JsonSerializer.Serialize(request)
                    ? new(AgentTurnSteeringStatus.Accepted, id, state.StreamTurnId) { Sequence = existing.Sequence, ConstraintRevision = existing.ConstraintRevision }
                    : new(AgentTurnSteeringStatus.Rejected) { Reason = "idempotency-key-content-mismatch" };
            MissionSteeringAuthorityResult? authority = null;
            if (request.MissionId is not null)
            {
                if (missionAuthority is null || state.Registration is null)
                    return new(AgentTurnSteeringStatus.Rejected) { Reason = "mission-steering-adapter-unavailable" };
                authority = await missionAuthority.ApplyAsync(request, state.Registration, ct).ConfigureAwait(false);
                if (!authority.Accepted) return new(AgentTurnSteeringStatus.Rejected) { Reason = authority.Reason };
            }
            else if (request.Restriction is not null)
                return new(AgentTurnSteeringStatus.Rejected) { Reason = "mission-restriction-needs-mission" };
            var update = new AgentTurnSteeringUpdate(id, normalizedMessage);
            var acceptedEvent = new TurnSteeringAccepted(state.StreamTurnId, id, normalizedMessage)
            { Request = request, ProfileId = state.Registration?.ProfileId, Sequence = state.NextSequence(), ConstraintRevision = authority?.ConstraintRevision };
            try
            {
                await threadEvents.AppendAsync(
                    state.ConversationId,
                    state.DurableTurnId,
                    EventId(state.DurableTurnId, update.SteeringId, "accepted"),
                    acceptedEvent,
                    ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                if (authority?.Accepted == true)
                {
                    // The mission CAS committed this update, even if the model turn's
                    // transcript has closed. Registration recovery reads that receipt on
                    // the next execution; do not misreport durable acceptance as rejection.
                    state.Remember(acceptedEvent);
                    state.Enqueue(update);
                    return new(AgentTurnSteeringStatus.Accepted, id, state.StreamTurnId)
                    { Sequence = acceptedEvent.Sequence, ConstraintRevision = authority.ConstraintRevision,
                        Reason = "Accepted by the active mission; queued for its next execution boundary." };
                }
                // A terminal append can win during an answer-boundary race.
                // Do not claim acceptance when the durable turn rejected it.
                state.BeginClosing();
                state.CompleteClose();
                _active.TryRemove(new KeyValuePair<string, ActiveTurn>(state.ConversationId, state));
                _turns.TryRemove(new KeyValuePair<TurnKey, ActiveTurn>(state.Key, state));
                return new AgentTurnSteeringResult(
                    AgentTurnSteeringStatus.TurnEnded,
                    ActiveTurnId: state.StreamTurnId);
            }

            state.Remember(acceptedEvent);
            state.Enqueue(update);
            return new AgentTurnSteeringResult(
                AgentTurnSteeringStatus.Accepted,
                update.SteeringId,
                state.StreamTurnId) { Sequence = acceptedEvent.Sequence, ConstraintRevision = acceptedEvent.ConstraintRevision };
        }
        finally
        {
            state.Gate.Release();
        }
    }


    public async Task MarkAppliedAsync(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId,
        string steeringId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamTurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(steeringId);
        var key = new TurnKey(conversationId.Trim(), durableTurnId, streamTurnId.Trim());
        if (!_turns.TryGetValue(key, out var state)) return;

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.MarkApplied(steeringId);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<TurnSteeringDeferred>> CloseAsync(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId,
        CancellationToken ct = default)
    {
        var key = new TurnKey(conversationId.Trim(), durableTurnId, streamTurnId.Trim());
        if (!_turns.TryGetValue(key, out var state))
        {
            return [];
        }

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.IsClosed) return [];
            var missionContinues = state.Registration is { MissionId: not null } registration && missionAuthority is not null
                && await missionAuthority.IsActiveAsync(registration, ct);
            if (!missionContinues) state.BeginClosing();

            var deferred = new List<TurnSteeringDeferred>();
            foreach (var update in state.SnapshotUnapplied())
            {
                var turnEvent = new TurnSteeringDeferred(
                    state.StreamTurnId,
                    update.SteeringId,
                    update.Message,
                    "The turn ended before this update reached the model. It was kept for the next message.");
                await threadEvents.AppendAsync(
                    state.ConversationId,
                    state.DurableTurnId,
                    EventId(state.DurableTurnId, update.SteeringId, "deferred"),
                    turnEvent,
                    ct).ConfigureAwait(false);
                deferred.Add(turnEvent);
            }

            // Do not clear ownership until every idempotent deferred receipt
            // is durable. If one append fails, a later CloseAsync retries the
            // same event ids and no accepted input is lost.
            // Independent verification belongs to the same mission execution. Its durable
            // revision CAS still accepts corrections and invalidates stale acceptance proof.
            if (missionContinues) return deferred;
            state.CompleteClose();
            _active.TryRemove(new KeyValuePair<string, ActiveTurn>(state.ConversationId, state));
            _turns.TryRemove(new KeyValuePair<TurnKey, ActiveTurn>(state.Key, state));
            return deferred;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string SteeringId(AgentTurnSteeringRequest request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.ConversationId}\n{request.MissionId:D}\n{request.AuthorId}\n{request.IdempotencyKey}"))).ToLowerInvariant();

    private static string EventId(Guid turnId, string steeringId, string suffix)
        => $"{turnId:N}:steer:{steeringId}:{suffix}";

    private readonly record struct TurnKey(
        string ConversationId,
        Guid DurableTurnId,
        string StreamTurnId);

    private sealed class ActiveTurn(
        string conversationId,
        Guid durableTurnId,
        string streamTurnId) : IAgentTurnSteeringSource
    {
        public AgentTurnSteeringRegistration? Registration { get; set; }
        public Dictionary<string, TurnSteeringAccepted> Accepted { get; } = new(StringComparer.Ordinal);
        private long _sequence;
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        public long NextSequence() => ++_sequence;
        public void Remember(TurnSteeringAccepted accepted) { Accepted[accepted.SteeringId] = accepted; _sequence = Math.Max(_sequence, accepted.Sequence); }
        async ValueTask IChatSteeringSource.WaitForUpdateAsync(CancellationToken ct)
            => await _signal.Reader.ReadAsync(ct).ConfigureAwait(false);
        public string ConversationId { get; } = conversationId;
        public Guid DurableTurnId { get; } = durableTurnId;
        public string StreamTurnId { get; } = streamTurnId;
        public TurnKey Key { get; } = new(conversationId, durableTurnId, streamTurnId);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        private object Sync { get; } = new();
        private Queue<PendingUpdate> Pending { get; } = new();
        private Dictionary<string, PendingUpdate> InFlight { get; } = new(StringComparer.Ordinal);
        private long _nextOrder;
        private bool _closing;
        private bool _closed;

        public bool IsClosingOrClosed
        {
            get { lock (Sync) return _closing || _closed; }
        }

        public bool IsClosed
        {
            get { lock (Sync) return _closed; }
        }

        public void Enqueue(AgentTurnSteeringUpdate update)
        {
            lock (Sync)
            {
                if (_closing || _closed)
                    throw new InvalidOperationException("The turn ended before the update could be queued.");
                Pending.Enqueue(new PendingUpdate(++_nextOrder, update));
                _signal.Writer.TryWrite(true);
            }
        }

        public void BeginClosing()
        {
            lock (Sync) _closing = true;
        }

        public void MarkApplied(string steeringId)
        {
            lock (Sync) InFlight.Remove(steeringId);
        }

        public IReadOnlyList<AgentTurnSteeringUpdate> SnapshotUnapplied()
        {
            lock (Sync)
            {
                return Pending.Concat(InFlight.Values)
                    .OrderBy(item => item.Order)
                    .Select(item => item.Update)
                    .ToArray();
            }
        }

        public void CompleteClose()
        {
            lock (Sync)
            {
                Pending.Clear();
                InFlight.Clear();
                _closed = true;
            }
        }

        public bool TryTake(out AgentTurnSteeringUpdate update)
        {
            lock (Sync)
            {
                if (_closing || _closed || Pending.Count == 0)
                {
                    update = null!;
                    return false;
                }

                var pending = Pending.Dequeue();
                InFlight.Add(pending.Update.SteeringId, pending);
                update = pending.Update;
                return true;
            }
        }

        private sealed record PendingUpdate(long Order, AgentTurnSteeringUpdate Update);
    }
}
