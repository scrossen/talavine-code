using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Defers an embedding-dependent background pass after a profile-wide pin
/// failure. It never changes vector identity or acknowledges pending work.
/// Interactive readiness checks remain independent of this background cooldown.
/// </summary>
public sealed class EmbeddingPinReadinessGate(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _retryAt;

    /// <summary>Owned by a single serialized background consumer.</summary>
    public async Task<bool> IsReadyAsync(
        IEmbeddingModelPin pin,
        Action<Exception, DateTimeOffset> unavailable,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_timeProvider.GetUtcNow() < _retryAt) return false;
        try
        {
            await pin.GetAsync(ct).ConfigureAwait(false);
            _retryAt = default;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _retryAt = _timeProvider.GetUtcNow() + RetryInterval;
            unavailable(ex, _retryAt);
            return false;
        }
    }
}
