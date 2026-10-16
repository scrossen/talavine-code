using System.Collections.Concurrent;

namespace SwarmCore.Scripting;

/// <summary>
/// Runs a guarded script on its own thread with a known stack size and a single-threaded
/// synchronization context, so every continuation of the script returns to that thread.
///
/// The caller gets an answer when the script finishes or the time limit passes, whichever is first.
/// On the time limit the cancellation token handed to the script (and to
/// <see cref="ScriptExecutionGuard"/>) is cancelled, the pump stops dispatching continuations (an awaiting
/// script never resumes), and the code running at that moment stops at its next guard checkpoint.
/// <c>onExit</c> then runs on the script thread itself, after script code has stopped running there, so
/// a host can dispose resources the script used without racing it.
/// </summary>
public static class IsolatedScriptThread
{
    /// <summary>Stack reserved for script threads: room for deep but legitimate runtime work (query translation).</summary>
    public const int DefaultMaxStackSize = 16 * 1024 * 1024;

    public static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> body,
        TimeSpan timeout,
        Action? onExit = null,
        int maxStackSize = DefaultMaxStackSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        var registration = stop.Token.Register(() => completion.TrySetException(cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : new TimeoutException($"The script did not finish within {timeout.TotalSeconds:0.#} seconds.")));

        var thread = new Thread(() =>
        {
            var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            Task<T>? task = null;
            Exception? failure = null;
            try
            {
                using var guard = ScriptExecutionGuard.Begin(stop.Token);
                try
                {
                    task = body(stop.Token);
                }
                catch (Exception ex)
                {
                    task = Task.FromException<T>(ex);
                }

                task.ContinueWith(_ => context.Complete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                failure = context.Run(stop.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);

                // Release the host's resources before reporting, so a caller that sees the result
                // also sees them released.
                try
                {
                    onExit?.Invoke();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                registration.Dispose();
            }

            if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else if (task is { IsCompletedSuccessfully: true })
            {
                completion.TrySetResult(task.Result);
            }
            else if (task is { IsFaulted: true })
            {
                completion.TrySetException(task.Exception!.InnerExceptions);
            }
            else if (task is { IsCanceled: true })
            {
                completion.TrySetCanceled();
            }
        }, maxStackSize)
        {
            IsBackground = true,
            Name = "Swarm hosted script",
        };

        try
        {
            thread.Start();
        }
        catch
        {
            registration.Dispose();
            onExit?.Invoke();
            throw;
        }

        return completion.Task;
    }

    /// <summary>Single-threaded context: continuations queue here and run on the script thread only.</summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The script finished or ran out of time; a late continuation is dropped.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == _threadId)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            Post(_ => { try { d(state); } finally { done.Set(); } }, null);
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        public void Complete() => _queue.CompleteAdding();

        /// <summary>Dispatches continuations until the script completes or <paramref name="stop"/> fires.</summary>
        public Exception? Run(CancellationToken stop)
        {
            try
            {
                foreach (var (callback, state) in _queue.GetConsumingEnumerable(stop))
                {
                    callback(state);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // A continuation threw outside any awaiting task.
                return ex;
            }
            finally
            {
                _queue.CompleteAdding();
            }

            return null;
        }
    }
}
