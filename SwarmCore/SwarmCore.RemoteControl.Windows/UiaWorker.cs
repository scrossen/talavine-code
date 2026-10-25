using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>
/// One long-lived MTA thread that runs every UI Automation call, as Microsoft recommends for desktop UIA clients:
/// the caller's apartment never matters, and UIA never runs on an STA thread that could need to pump our own
/// windows while blocked in a cross-process call. Exceptions reach the caller unchanged (original stack kept).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UiaWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private int disposed;

    public UiaWorker(string name)
    {
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Run()
    {
        foreach (var work in queue.GetConsumingEnumerable())
        {
            // Invoke's items capture their own exceptions; this guards cleanup items and anything else queued, since
            // an exception escaping a thread's start routine terminates the process.
            try { work(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"UI Automation worker item failed: {ex}"); }
        }
    }

    public T Invoke<T>(Func<T> work)
    {
        if (Thread.CurrentThread == thread) return work();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        try
        {
            queue.Add(() =>
            {
                try { result = work(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                finally { done.Set(); }
            });
        }
        catch (InvalidOperationException) // CompleteAdding raced with us
        {
            throw new ObjectDisposedException(nameof(UiaWorker));
        }
        done.Wait();
        failure?.Throw();
        return result;
    }

    public void Invoke(Action work) => Invoke<object?>(() => { work(); return null; });

    /// <summary>Runs <paramref name="cleanup"/> on the worker (COM objects are released in their apartment), then stops it.</summary>
    public void Dispose(Action? cleanup)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (cleanup is not null)
        {
            try { queue.Add(cleanup); } catch (InvalidOperationException) { }
        }
        queue.CompleteAdding();
        if (Thread.CurrentThread == thread) return; // the loop ends after this item; the queue is collected with us
        thread.Join();
        queue.Dispose();
    }

    public void Dispose() => Dispose(null);
}
