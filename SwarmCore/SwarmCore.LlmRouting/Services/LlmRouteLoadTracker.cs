using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// In-process load per serving endpoint: calls in flight, split by whether a
/// person is waiting, and how long this process's recent calls there took. A
/// shared local node serves requests one after another, so the recent call
/// duration is what each queued call costs a newly arriving request.
/// Thread-safe; a lease disposed twice is released once.
/// </summary>
public sealed class LlmRouteLoadTracker(TimeProvider? timeProvider = null) : ILlmRouteLoadTracker
{
    private static readonly TimeSpan QuietPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Completed calls this recent describe the node as it is now.</summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);
    private const int MaximumRecentSamples = 32;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeState> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Begin(string provider, string model, LlmInteractivity interactivity)
    {
        if (string.IsNullOrWhiteSpace(provider)) return Lease.None;
        var key = provider.Trim();
        var userWaiting = interactivity == LlmInteractivity.UserWaiting;
        lock (_gate)
        {
            var node = Node(key);
            if (userWaiting) node.UserWaiting++;
            else node.Background++;
        }

        return new Lease(this, key, userWaiting, _time.GetUtcNow());
    }

    public async Task WaitForUserWaitingQuietAsync(string provider, TimeSpan maximumWait, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || maximumWait <= TimeSpan.Zero) return;
        var key = provider.Trim();
        var deadline = _time.GetUtcNow() + maximumWait;
        while (UserWaitingInFlight(key) > 0 && _time.GetUtcNow() < deadline)
        {
            var remaining = deadline - _time.GetUtcNow();
            await Task.Delay(remaining < QuietPollInterval ? remaining : QuietPollInterval, ct).ConfigureAwait(false);
        }
    }

    public ModelScoutRouteLoadContext Snapshot()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var loads = new Dictionary<string, ModelScoutRouteLoad>(StringComparer.OrdinalIgnoreCase);
            foreach (var (provider, node) in _nodes)
            {
                node.Prune(now - RecentWindow);
                var samples = node.Recent.Count;
                if (node.UserWaiting + node.Background == 0 && samples == 0) continue;
                loads[provider] = new ModelScoutRouteLoad(
                    node.UserWaiting,
                    node.Background,
                    samples == 0 ? null : (decimal)node.Recent.Average(sample => sample.DurationMs),
                    samples);
            }

            return loads.Count == 0 ? ModelScoutRouteLoadContext.Empty : new ModelScoutRouteLoadContext(loads);
        }
    }

    private NodeState Node(string provider)
    {
        if (!_nodes.TryGetValue(provider, out var node))
        {
            node = new NodeState();
            _nodes[provider] = node;
        }

        return node;
    }

    private int UserWaitingInFlight(string provider)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(provider, out var node) ? node.UserWaiting : 0;
        }
    }

    private void Release(string provider, bool userWaiting, DateTimeOffset startedAt)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_nodes.TryGetValue(provider, out var node)) return;
            if (userWaiting) node.UserWaiting = Math.Max(0, node.UserWaiting - 1);
            else node.Background = Math.Max(0, node.Background - 1);
            var durationMs = (now - startedAt).TotalMilliseconds;
            if (durationMs > 0)
            {
                node.Recent.Enqueue((now, durationMs));
                while (node.Recent.Count > MaximumRecentSamples) node.Recent.Dequeue();
            }

            node.Prune(now - RecentWindow);
            if (node.UserWaiting == 0 && node.Background == 0 && node.Recent.Count == 0) _nodes.Remove(provider);
        }
    }

    private sealed class NodeState
    {
        public int UserWaiting;
        public int Background;
        public Queue<(DateTimeOffset CompletedAt, double DurationMs)> Recent { get; } = new();

        public void Prune(DateTimeOffset cutoff)
        {
            while (Recent.TryPeek(out var oldest) && oldest.CompletedAt < cutoff) Recent.Dequeue();
        }
    }

    private sealed class Lease : IDisposable
    {
        public static readonly IDisposable None = new Lease(null, string.Empty, false, default);

        private readonly LlmRouteLoadTracker? _owner;
        private readonly string _provider;
        private readonly bool _userWaiting;
        private readonly DateTimeOffset _startedAt;
        private int _released;

        public Lease(LlmRouteLoadTracker? owner, string provider, bool userWaiting, DateTimeOffset startedAt)
        {
            _owner = owner;
            _provider = provider;
            _userWaiting = userWaiting;
            _startedAt = startedAt;
        }

        public void Dispose()
        {
            if (_owner is null || Interlocked.Exchange(ref _released, 1) != 0) return;
            _owner.Release(_provider, _userWaiting, _startedAt);
        }
    }
}
