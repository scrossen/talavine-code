using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Profile/host-owned cache and physical single-flight for exact model
/// validation. Register one singleton in a profile DI root; directly constructed
/// routers receive a private instance so validation/audit authority never leaks
/// across unrelated profiles.
/// </summary>
public sealed class LlmModelValidationCoordinator
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<LlmModelValidationResult>>> _flights =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<LlmModelValidationResult> RunAsync(
        string validationIdentity,
        bool requireFresh,
        Func<Task<LlmModelValidationResult>> physicalProbe,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationIdentity);
        ArgumentNullException.ThrowIfNull(physicalProbe);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        if (!requireFresh
            && _cache.TryGetValue(validationIdentity, out var cached))
        {
            if (now - cached.CheckedAt < CacheLifetime)
            {
                // Keep the original probe message: readiness writers persist this
                // string as the user-visible disable reason, and a bare
                // "Invalid (cached)" hides why the provider actually failed.
                return cached.Result with
                {
                    Message = cached.Result.IsValid
                        ? "OK (cached)"
                        : $"{cached.Result.Message} (cached)"
                };
            }

            RemoveExactCacheEntry(validationIdentity, cached);
        }

        Lazy<Task<LlmModelValidationResult>>? candidate = null;
        candidate = new Lazy<Task<LlmModelValidationResult>>(
            () => RunOwnedAsync(validationIdentity, candidate!, physicalProbe),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _flights.GetOrAdd(validationIdentity, candidate);
        return await flight.Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LlmModelValidationResult> RunOwnedAsync(
        string validationIdentity,
        Lazy<Task<LlmModelValidationResult>> owner,
        Func<Task<LlmModelValidationResult>> physicalProbe)
    {
        try
        {
            var result = await physicalProbe().ConfigureAwait(false);
            if (result.Status is LlmModelValidationStatus.Valid
                or LlmModelValidationStatus.Invalid)
            {
                _cache[validationIdentity] = new CacheEntry(result, DateTime.UtcNow);
                PruneCacheIfNeeded();
            }

            return result;
        }
        finally
        {
            if (_flights.TryGetValue(validationIdentity, out var current)
                && ReferenceEquals(current, owner))
            {
                _flights.TryRemove(validationIdentity, out _);
            }
        }
    }

    private void PruneCacheIfNeeded()
    {
        if (_cache.Count <= MaxCacheEntries)
        {
            return;
        }

        var expiredBefore = DateTime.UtcNow - CacheLifetime;
        foreach (var entry in _cache)
        {
            if (entry.Value.CheckedAt <= expiredBefore)
            {
                RemoveExactCacheEntry(entry.Key, entry.Value);
            }
        }

        var excess = _cache.Count - MaxCacheEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (var entry in _cache
                     .OrderBy(static entry => entry.Value.CheckedAt)
                     .Take(excess))
        {
            RemoveExactCacheEntry(entry.Key, entry.Value);
        }
    }

    private void RemoveExactCacheEntry(string key, CacheEntry expected)
    {
        // ICollection.Remove on ConcurrentDictionary is an atomic key+value remove;
        // it cannot delete a newer completed probe that replaced this snapshot.
        _ = ((ICollection<KeyValuePair<string, CacheEntry>>)_cache)
            .Remove(new KeyValuePair<string, CacheEntry>(key, expected));
    }

    private sealed record CacheEntry(
        LlmModelValidationResult Result,
        DateTime CheckedAt);
}
