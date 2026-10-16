using System.Runtime.ExceptionServices;

namespace SwarmCore.Abstractions.Scheduling;

/// <summary>Retries an optimistic conflict only after observing a newer persisted revision.</summary>
public static class RevisionConflictRecovery
{
    /// <remarks>
    /// Capture attemptedRevision before the write: a failed writer may mutate
    /// its input. A missing row settles deletion; an unchanged or older revision
    /// provides no evidence for retry and preserves the original failure.
    /// </remarks>
    public static async Task<T?> ReloadAsync<T>(long attemptedRevision,
        Func<CancellationToken, Task<T?>> reload, Func<T, long> revision,
        Exception conflict, CancellationToken ct = default) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var latest = await reload(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (latest is not null && revision(latest) <= attemptedRevision)
            ExceptionDispatchInfo.Capture(conflict).Throw();
        // A busy store may complete each async operation synchronously. Yield
        // between advancing conflicts so cancellation and other work can run.
        await Task.Yield();
        return latest;
    }
}
