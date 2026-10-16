namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// Owns independent asynchronous work without making the polling thread wait
/// for a whole task. Resource admission remains with the work's runtime. A key
/// stays owned through cancellation and cleanup, preventing duplicate launches.
/// </summary>
public sealed class KeyedBackgroundWork<TKey>(Action<TKey, Exception> onError) : IAsyncDisposable
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TaskCompletionSource> _active = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _stop;

    public IReadOnlyCollection<TKey> ActiveKeys
    {
        get { lock (_gate) return _active.Keys.ToArray(); }
    }

    public bool TryStart(TKey key, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_stop is not null || ct.IsCancellationRequested || _active.ContainsKey(key)) return false;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(key, completion);
            // Do not pass a cancellation token to Task.Run: even work cancelled
            // before dispatch must release ownership and complete its receipt.
            _ = Task.Run(async () =>
            {
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
                try { await work(lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    try { onError(key, ex); }
                    catch { /* A failed diagnostic sink must not strand ownership. */ }
                }
                finally
                {
                    lifetime.Dispose();
                    lock (_gate)
                    {
                        _active.Remove(key);
                        completion.TrySetResult();
                    }
                }
            });
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            var active = _active.Values.Select(value => value.Task).ToArray();
            return _stop = Task.Run(async () =>
            {
                try { await _shutdown.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    await Task.WhenAll(active).ConfigureAwait(false);
                    _shutdown.Dispose();
                }
            });
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
