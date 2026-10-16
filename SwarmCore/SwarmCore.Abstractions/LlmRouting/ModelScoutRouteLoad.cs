namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Current load on one serving endpoint (a local runtime or a LAN node), keyed
/// by provider name. In-flight counts come from this process; recent response
/// time comes from shared telemetry, so it also reflects work other processes
/// placed on the same node.
/// </summary>
public sealed record ModelScoutRouteLoad(
    int InFlightUserWaiting,
    int InFlightBackground,
    decimal? RecentResponseMs = null,
    int RecentSampleCount = 0)
{
    public int InFlight => Math.Max(0, InFlightUserWaiting) + Math.Max(0, InFlightBackground);
}

/// <summary>
/// Live load handed to the Model Scout ranker so a user-waiting request is not
/// projected onto a shared local node from historical speed while that node is
/// busy. An empty context leaves ranking unchanged.
/// </summary>
public sealed class ModelScoutRouteLoadContext
{
    public static readonly ModelScoutRouteLoadContext Empty =
        new(new Dictionary<string, ModelScoutRouteLoad>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, ModelScoutRouteLoad> _loadByProvider;

    public ModelScoutRouteLoadContext(IReadOnlyDictionary<string, ModelScoutRouteLoad> loadByProvider)
    {
        ArgumentNullException.ThrowIfNull(loadByProvider);
        _loadByProvider = new Dictionary<string, ModelScoutRouteLoad>(loadByProvider, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string provider, out ModelScoutRouteLoad load)
    {
        if (!string.IsNullOrWhiteSpace(provider)
            && _loadByProvider.TryGetValue(provider.Trim(), out var found))
        {
            load = found;
            return true;
        }

        load = new ModelScoutRouteLoad(0, 0);
        return false;
    }

    /// <summary>
    /// Overlay recent shared-telemetry response times, which also cover work
    /// other processes placed on the node. A telemetry average replaces this
    /// process's own when it has at least as many samples.
    /// </summary>
    public ModelScoutRouteLoadContext WithRecentResponses(
        IReadOnlyDictionary<string, (decimal ResponseMs, int Samples)> recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        var merged = new Dictionary<string, ModelScoutRouteLoad>(_loadByProvider, StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, sample) in recent)
        {
            if (string.IsNullOrWhiteSpace(provider) || sample.ResponseMs <= 0 || sample.Samples <= 0) continue;
            var key = provider.Trim();
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = new ModelScoutRouteLoad(0, 0, sample.ResponseMs, sample.Samples);
            }
            else if (existing.RecentResponseMs is null || sample.Samples >= existing.RecentSampleCount)
            {
                merged[key] = existing with { RecentResponseMs = sample.ResponseMs, RecentSampleCount = sample.Samples };
            }
        }

        return new ModelScoutRouteLoadContext(merged);
    }
}

/// <summary>
/// Process-wide in-flight accounting for local and LAN model calls. The router
/// opens a lease around every physical call to a local provider; Model Scout
/// reads the snapshot. Background calls to a shared node yield while
/// user-waiting calls are running there, so bulk agents cannot starve a person
/// who is waiting for an answer.
/// </summary>
public interface ILlmRouteLoadTracker
{
    IDisposable Begin(string provider, string model, LlmInteractivity interactivity);

    /// <summary>
    /// Waits (bounded) while any user-waiting call is in flight on
    /// <paramref name="provider"/>. Returns immediately when none is.
    /// </summary>
    Task WaitForUserWaitingQuietAsync(string provider, TimeSpan maximumWait, CancellationToken ct = default);

    ModelScoutRouteLoadContext Snapshot();
}
