using System.Runtime.ExceptionServices;

namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// Finishes independent items even when one fails, but acknowledges the batch
/// only if every item succeeds. Callers retain ownership of failed work.
/// </summary>
public static class IndependentWorkCompletion
{
    public static async Task RunAsync<T>(IEnumerable<T> items,
        Func<T, CancellationToken, Task> execute, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<Exception>? failures = null;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try { await execute(item, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        ct.ThrowIfCancellationRequested();
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 }) throw new AggregateException("Independent work did not fully complete.", failures);
    }
}
