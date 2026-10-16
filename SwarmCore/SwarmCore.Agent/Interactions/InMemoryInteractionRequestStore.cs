using System.Collections.Concurrent;
using System.Text.Json;

namespace SwarmCore.Agent.Interactions;

public sealed class InMemoryInteractionRequestStore(TimeProvider? timeProvider = null) : IInteractionRequestStore
{
    private readonly ConcurrentDictionary<string, PartitionState> _partitions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<InteractionRequestSnapshot> CreateAsync(InteractionRequestCreate request, CancellationToken ct = default)
    {
        Validate(request);
        request = Normalize(request);
        ct.ThrowIfCancellationRequested();
        var partition = _partitions.GetOrAdd(request.PartitionKey.Trim(), static _ => new PartitionState());
        lock (partition.Sync)
        {
            if (partition.ByCaller.TryGetValue(request.CallerRequestId.Trim(), out var existing))
            {
                if (!Equivalent(existing.Create, request))
                    throw new InvalidOperationException($"Caller request id '{request.CallerRequestId}' was reused with different content.");
                return Task.FromResult(existing.Snapshot);
            }

            var now = _time.GetUtcNow();
            var cursor = ++partition.Cursor;
            var interactionId = Guid.NewGuid().ToString("N");
            var snapshot = Snapshot(interactionId, request, InteractionRequestState.Pending, now, now, null, null, cursor);
            var item = new Entry(request, snapshot);
            partition.ById.Add(interactionId, item);
            partition.ByCaller.Add(request.CallerRequestId.Trim(), item);
            partition.Changes.Add(snapshot);
            return Task.FromResult(snapshot);
        }
    }

    public Task<InteractionRequestSnapshot?> GetAsync(string partitionKey, string interactionId, CancellationToken ct = default)
    {
        ValidateKey(partitionKey, nameof(partitionKey));
        ValidateKey(interactionId, nameof(interactionId));
        ct.ThrowIfCancellationRequested();
        if (!_partitions.TryGetValue(partitionKey.Trim(), out var partition)) return Task.FromResult<InteractionRequestSnapshot?>(null);
        lock (partition.Sync) return Task.FromResult(partition.ById.TryGetValue(interactionId.Trim(), out var entry) ? entry.Snapshot : null);
    }

    public Task<InteractionRequestSnapshot?> GetByCallerIdAsync(string partitionKey, string callerRequestId, CancellationToken ct = default)
    {
        ValidateKey(partitionKey, nameof(partitionKey));
        ValidateKey(callerRequestId, nameof(callerRequestId));
        ct.ThrowIfCancellationRequested();
        if (!_partitions.TryGetValue(partitionKey.Trim(), out var partition)) return Task.FromResult<InteractionRequestSnapshot?>(null);
        lock (partition.Sync) return Task.FromResult(partition.ByCaller.TryGetValue(callerRequestId.Trim(), out var entry) ? entry.Snapshot : null);
    }

    public Task<IReadOnlyList<InteractionRequestSnapshot>> ListPendingAsync(string partitionKey, InteractionRequestQuery? query = null, CancellationToken ct = default)
    {
        ValidateKey(partitionKey, nameof(partitionKey));
        ct.ThrowIfCancellationRequested();
        if (!_partitions.TryGetValue(partitionKey.Trim(), out var partition)) return Task.FromResult<IReadOnlyList<InteractionRequestSnapshot>>([]);
        lock (partition.Sync)
        {
            IReadOnlyList<InteractionRequestSnapshot> result = partition.ById.Values.Select(e => e.Snapshot)
                .Where(s => s.State == InteractionRequestState.Pending && Matches(s, query))
                .OrderBy(s => s.CreatedUtc).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<InteractionRequestPage> ReadChangesAsync(string partitionKey, InteractionRequestCursor after, int take = 200, CancellationToken ct = default)
    {
        ValidateKey(partitionKey, nameof(partitionKey));
        if (after.Sequence < 0) throw new ArgumentOutOfRangeException(nameof(after));
        if (take is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(take));
        ct.ThrowIfCancellationRequested();
        if (!_partitions.TryGetValue(partitionKey.Trim(), out var partition)) return Task.FromResult(new InteractionRequestPage([], after, false));
        lock (partition.Sync)
        {
            var candidates = partition.Changes.Where(s => s.Cursor > after.Sequence).Take(take + 1).ToArray();
            var items = candidates.Take(take).ToArray();
            var next = items.Length == 0 ? after : new InteractionRequestCursor(items[^1].Cursor);
            return Task.FromResult(new InteractionRequestPage(items, next, candidates.Length > take));
        }
    }

    public Task<InteractionRequestSnapshot> CompleteAsync(string partitionKey, string interactionId, InteractionRequestCompletion completion, CancellationToken ct = default)
    {
        ValidateKey(partitionKey, nameof(partitionKey));
        ValidateKey(interactionId, nameof(interactionId));
        Validate(completion);
        ct.ThrowIfCancellationRequested();
        if (!_partitions.TryGetValue(partitionKey.Trim(), out var partition)) throw new KeyNotFoundException($"Interaction '{interactionId}' was not found.");
        lock (partition.Sync)
        {
            if (!partition.ById.TryGetValue(interactionId.Trim(), out var entry)) throw new KeyNotFoundException($"Interaction '{interactionId}' was not found.");
            completion = InteractionCompletionCanonicalizer.Canonicalize(completion, entry.Snapshot.FormFields);
            if (entry.Snapshot.State != InteractionRequestState.Pending)
            {
                if (InteractionCompletionCanonicalizer.Equivalent(entry.Snapshot.Resolution, completion)) return Task.FromResult(entry.Snapshot);
                throw new InvalidOperationException($"Interaction '{interactionId}' already has a different terminal resolution.");
            }

            var state = completion.Kind switch
            {
                InteractionResolutionKind.Cancelled => InteractionRequestState.Cancelled,
                InteractionResolutionKind.Superseded => InteractionRequestState.Superseded,
                _ => InteractionRequestState.Resolved,
            };
            var now = _time.GetUtcNow();
            entry.Snapshot = entry.Snapshot with
            {
                State = state, UpdatedUtc = now, ResolvedUtc = now,
                Resolution = completion, Cursor = ++partition.Cursor,
            };
            partition.Changes.Add(entry.Snapshot);
            return Task.FromResult(entry.Snapshot);
        }
    }

    private static InteractionRequestSnapshot Snapshot(string id, InteractionRequestCreate r, InteractionRequestState state,
        DateTimeOffset created, DateTimeOffset updated, DateTimeOffset? resolved, InteractionRequestCompletion? completion, long cursor)
        => new(id, r.PartitionKey.Trim(), r.CallerRequestId.Trim(), r.Kind, state, r.IsBlocking, r.Title.Trim(), r.Prompt.Trim(),
            Trim(r.ThreadId), r.TurnId, Trim(r.ObjectiveId), Trim(r.AgentId), r.OwnerKind.Trim(), r.OwnerId.Trim(),
            r.Choices.ToArray(), r.FormFields.ToArray(), created, updated, r.ExpiresUtc, resolved, completion, cursor) { SteeringTarget = r.SteeringTarget };

    private static bool Matches(InteractionRequestSnapshot s, InteractionRequestQuery? q) => q is null
        || ((q.ThreadId is null || s.ThreadId == q.ThreadId) && (q.ObjectiveId is null || s.ObjectiveId == q.ObjectiveId)
            && (q.AgentId is null || s.AgentId == q.AgentId) && (q.Kind is null || s.Kind == q.Kind)
            && (q.IsBlocking is null || s.IsBlocking == q.IsBlocking));

    private static bool Equivalent(object? a, object? b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private static InteractionRequestCreate Normalize(InteractionRequestCreate r) => r with
    {
        PartitionKey = r.PartitionKey.Trim(), CallerRequestId = r.CallerRequestId.Trim(),
        Title = r.Title.Trim(), Prompt = r.Prompt.Trim(), ThreadId = Trim(r.ThreadId),
        ObjectiveId = Trim(r.ObjectiveId), AgentId = Trim(r.AgentId), OwnerKind = r.OwnerKind.Trim(),
        OwnerId = r.OwnerId.Trim(), Choices = r.Choices.Select(c => c with { }).ToArray(),
        FormFields = r.FormFields.Select(f => f with { }).ToArray(),
    };
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Validate(InteractionRequestCreate r)
    {
        ArgumentNullException.ThrowIfNull(r);
        QueuedChatCredentialPolicy.Validate(r);
        ValidateKey(r.PartitionKey, nameof(r.PartitionKey), 200);
        ValidateKey(r.CallerRequestId, nameof(r.CallerRequestId), 256);
        ValidateKey(r.Title, nameof(r.Title), 300);
        ValidateKey(r.Prompt, nameof(r.Prompt), 4000);
        ValidateKey(r.OwnerKind, nameof(r.OwnerKind), 80);
        ValidateKey(r.OwnerId, nameof(r.OwnerId), 256);
        if (r.Choices.Count > 20 || r.FormFields.Count > 20) throw new ArgumentException("Interaction metadata is limited to 20 choices and 20 fields.");
    }
    private static void Validate(InteractionRequestCompletion c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ValidateKey(c.Actor, nameof(c.Actor), 200);
        if (c.Values.Count > 20) throw new ArgumentException("A resolution is limited to 20 values.");
    }
    private static void ValidateKey(string value, string name, int max = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Trim().Length > max) throw new ArgumentException($"{name} cannot exceed {max} characters.", name);
    }

    private sealed class PartitionState
    {
        public object Sync { get; } = new();
        public Dictionary<string, Entry> ById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Entry> ByCaller { get; } = new(StringComparer.Ordinal);
        public List<InteractionRequestSnapshot> Changes { get; } = [];
        public long Cursor { get; set; }
    }
    private sealed class Entry(InteractionRequestCreate create, InteractionRequestSnapshot snapshot)
    {
        public InteractionRequestCreate Create { get; } = create;
        public InteractionRequestSnapshot Snapshot { get; set; } = snapshot;
    }
}
