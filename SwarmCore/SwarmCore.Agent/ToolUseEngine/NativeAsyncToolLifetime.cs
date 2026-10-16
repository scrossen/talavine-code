namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>Required native work belongs to its active turn, never a fire-and-forget task.</summary>
internal sealed class NativeAsyncToolLifetime(CancellationToken parent) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
    private readonly Dictionary<string, Task> _jobs = new(StringComparer.Ordinal);
    private DurableToolScheduler? _scheduler;
    private ToolSchedulerBatchLease? _batch;
    public async Task<(DurableToolScheduler Scheduler, ToolSchedulerBatchLease Lease)> OpenAsync(
        Func<DurableToolScheduler> create, string profile)
    {
        _scheduler ??= create();
        _batch ??= await _scheduler.OpenNativeBatchAsync(profile, _stop.Token);
        return (_scheduler, _batch);
    }
    public CancellationToken Token => _stop.Token;
    public bool Contains(string id) => _jobs.ContainsKey(id);
    public bool HasJobs => _jobs.Count > 0;
    public void Add(string id, Task job)
    {
        if (!_jobs.TryAdd(id, job)) throw new InvalidDataException("Duplicate native async call.");
    }
    public async Task WaitAsync()
    {
        await Task.WhenAll(_jobs.Values);
        if (_batch is not null) { await _batch.DisposeAsync(); _batch = null; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await _stop.CancelAsync(); }
        finally
        {
            try { await Task.WhenAll(_jobs.Values); }
            finally
            {
                try { if (_batch is not null) await _batch.DisposeAsync(); }
                finally { _stop.Dispose(); }
            }
        }
    }
}
