namespace SwarmCore.Mcp.OAuth;

/// <summary>Bound independent reads while retaining ordered barriers around every mutation.</summary>
public static class RemoteReadBatchRunner
{
    public static async Task RunAsync<T>(IEnumerable<T> requests, Func<T, bool> isReadOnly,
        Func<T, CancellationToken, Task> execute, CancellationToken ct, int concurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        using var slots = new SemaphoreSlim(concurrency);
        var reads = new List<Task>();
        try
        {
            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                if (!isReadOnly(request))
                {
                    await Task.WhenAll(reads);
                    reads.Clear();
                    await execute(request, ct);
                    continue;
                }
                reads.Add(Task.Run(async () =>
                {
                    await slots.WaitAsync(ct);
                    try { await execute(request, ct); }
                    finally { slots.Release(); }
                }, ct));
            }
        }
        finally { await Task.WhenAll(reads); }
    }
}
