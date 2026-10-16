using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Process-local soft quota tracker. Records the timestamp of every subscription call
/// and prunes anything older than the window length. Fast enough to call on the hot
/// path; non-persistent across restarts (the first window after restart will under-count,
/// which is the safer direction — the user just gets a bit more headroom).
///
/// Window/cap defaults come from <see cref="SubscriptionQuotaDefaults"/>; the
/// burn math lives in <see cref="SubscriptionQuotaTrackerCore"/>, shared with
/// the persistent tracker.
/// </summary>
public sealed class InMemorySubscriptionQuotaTracker : ISubscriptionQuotaTracker
{
    private readonly ConcurrentDictionary<string, SubscriptionQuotaConfig> _configs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<SubscriptionQuotaTrackerCore.Usage>> _calls =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SubscriptionQuotaTrackerCore.UsageHint> _remainingHints =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _windowResets =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemorySubscriptionQuotaTracker()
    {
        foreach (var config in SubscriptionQuotaDefaults.All)
        {
            Configure(config);
        }
    }

    public void Configure(SubscriptionQuotaConfig config)
    {
        _configs[config.ProviderName] = config;
    }

    public void RecordCall(
        string providerName,
        DateTimeOffset? now = null,
        decimal capacityUnits = 1m)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return;
        if (!_configs.ContainsKey(providerName)) return;
        if (capacityUnits <= 0m || capacityUnits > 10_000m) return;

        var queue = _calls.GetOrAdd(
            providerName,
            _ => new ConcurrentQueue<SubscriptionQuotaTrackerCore.Usage>());
        queue.Enqueue(new SubscriptionQuotaTrackerCore.Usage(
            now ?? DateTimeOffset.UtcNow,
            capacityUnits));
    }

    public void ApplyRemainingFractionHint(string providerName, decimal remainingFraction, DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return;
        if (!_configs.ContainsKey(providerName)) return;

        _remainingHints[providerName] = SubscriptionQuotaTrackerCore.CreateHint(remainingFraction, observedAt);
    }

    public void RecordWindowReset(string providerName, DateTimeOffset resetsAt)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return;
        if (!_configs.ContainsKey(providerName)) return;
        _windowResets.AddOrUpdate(providerName, resetsAt, (_, existing) => resetsAt > existing ? resetsAt : existing);
    }

    public decimal GetBurnFraction(string providerName)
    {
        var snap = GetSnapshot(providerName);
        return snap?.BurnFraction ?? 0m;
    }

    public SubscriptionQuotaSnapshot? GetSnapshot(string providerName)
    {
        if (!_configs.TryGetValue(providerName, out var config)) return null;
        if (!_calls.TryGetValue(providerName, out var queue))
            queue = new ConcurrentQueue<SubscriptionQuotaTrackerCore.Usage>();

        return SubscriptionQuotaTrackerCore.BuildSnapshot(
            providerName, config, queue, _remainingHints, DateTimeOffset.UtcNow,
            _windowResets.TryGetValue(providerName, out var reset) ? reset : null);
    }

    public IReadOnlyList<SubscriptionQuotaSnapshot> GetAllSnapshots()
    {
        var result = new List<SubscriptionQuotaSnapshot>(_configs.Count);
        foreach (var providerName in _configs.Keys)
        {
            var snap = GetSnapshot(providerName);
            if (snap is not null) result.Add(snap);
        }
        return result;
    }
}
