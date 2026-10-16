namespace SwarmCore.Mcp.OAuth;

/// <summary>In-process wakeups for durable relay queues; subscriptions are scoped to a waiting request.</summary>
public sealed class RelayWakeupRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<Subscription>> _waiters = [];
    public Subscription Subscribe(Guid key)
    {
        var subscription = new Subscription(this, key);
        lock (_gate)
        {
            if (!_waiters.TryGetValue(key, out var set)) _waiters[key] = set = [];
            set.Add(subscription);
        }
        return subscription;
    }
    public void Signal(Guid key)
    {
        Subscription[] targets;
        lock (_gate) targets = _waiters.TryGetValue(key, out var set) ? set.ToArray() : [];
        foreach (var target in targets) target.Completion.TrySetResult();
    }
    private void Remove(Subscription subscription, Guid key)
    {
        lock (_gate)
        {
            if (_waiters.TryGetValue(key, out var set))
            {
                set.Remove(subscription);
                if (set.Count == 0) _waiters.Remove(key);
            }
        }
    }
    public sealed class Subscription : IDisposable
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RelayWakeupRegistry _owner;
        private readonly Guid _key;
        internal Subscription(RelayWakeupRegistry owner, Guid key) { _owner = owner; _key = key; }
        public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => Completion.Task.WaitAsync(timeout, ct);
        public void Dispose() => _owner.Remove(this, _key);
    }
}
