namespace SwarmCore.Agent.Continual;

/// <summary>
/// Compatibility facade for the shared scheduling primitive. Agent runtimes
/// and other hosts use the same ownership, cancellation and cleanup behavior.
/// </summary>
public sealed class KeyedBackgroundWork<TKey>(Action<TKey, Exception> onError) : IAsyncDisposable
    where TKey : notnull
{
    private readonly SwarmCore.Abstractions.Scheduling.KeyedBackgroundWork<TKey> _work = new(onError);
    public IReadOnlyCollection<TKey> ActiveKeys => _work.ActiveKeys;
    public bool TryStart(TKey key, Func<CancellationToken, Task> work, CancellationToken ct = default)
        => _work.TryStart(key, work, ct);
    public Task StopAsync() => _work.StopAsync();
    public ValueTask DisposeAsync() => _work.DisposeAsync();
}
