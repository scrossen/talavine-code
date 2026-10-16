namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// One physical child process at a time for this Mac Catalyst process.
/// <para>
/// The interpreted runtime has failed natively (SIGSEGV in
/// <c>mono_interp_exec_method</c>) under concurrent child-process and
/// continuation churn. The constraint is therefore about CHILD PROCESSES, not
/// about any one kind of them: it lives here beside
/// <see cref="ChildProcessLauncher"/>, <see cref="ChildProcessReaper"/> and
/// <see cref="ChildProcessLifetimeContainment"/> so every recurring spawner —
/// subscription CLIs, benchmark harnesses, anything automatic — can share it
/// rather than each one inventing its own answer.
/// </para>
/// <para>
/// Work the user is waiting on is admitted ahead of queued background work,
/// and may preempt a running background holder that opted in. A preempted
/// holder is still expected to kill and reap its child BEFORE releasing: the
/// point is never to have two live children, not merely to take turns.
/// </para>
/// </summary>
public sealed class ChildProcessExecutionGate
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly object _priorityLock = new();
    private TaskCompletionSource _foregroundDrained = CompletedSource();
    private int _foregroundPending;
    private CancellationTokenSource? _backgroundPreempt;

    /// <summary>
    /// Waits for the single execution slot. Dispose the returned lease once the
    /// child has exited and been reaped.
    /// </summary>
    public async Task<ChildProcessExecutionLease> AcquireAsync(
        bool foreground,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (foreground)
        {
            lock (_priorityLock)
            {
                if (_foregroundPending == 0)
                {
                    _foregroundDrained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _foregroundPending++;
            }

            // Waiting politely behind a background probe costs a user-visible
            // turn that probe's whole timeout plus the kill/wait that follows.
            // Background work that opted in is abandonable, so ask the current
            // holder to stop instead of queueing behind it.
            PreemptCurrentBackground();

            try
            {
                await _slot.WaitAsync(ct).ConfigureAwait(false);
                return new ChildProcessExecutionLease(this, foreground: true, CancellationToken.None);
            }
            catch
            {
                ReleaseForegroundPending();
                throw;
            }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task foregroundDrained;
            lock (_priorityLock)
            {
                foregroundDrained = _foregroundDrained.Task;
            }

            await foregroundDrained.WaitAsync(ct).ConfigureAwait(false);
            await _slot.WaitAsync(ct).ConfigureAwait(false);

            var preempt = new CancellationTokenSource();
            lock (_priorityLock)
            {
                if (_foregroundPending == 0)
                {
                    _backgroundPreempt = preempt;
                    return new ChildProcessExecutionLease(this, foreground: false, preempt.Token);
                }
            }

            preempt.Dispose();
            _slot.Release();
        }
    }

    private void PreemptCurrentBackground()
    {
        CancellationTokenSource? holder;
        lock (_priorityLock)
        {
            holder = _backgroundPreempt;
        }

        try
        {
            holder?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The holder finished between the read and the cancel.
        }
    }

    internal void ReleaseLease(bool foreground)
    {
        if (!foreground)
        {
            CancellationTokenSource? preempt;
            lock (_priorityLock)
            {
                preempt = _backgroundPreempt;
                _backgroundPreempt = null;
            }

            preempt?.Dispose();
        }

        _slot.Release();
        if (foreground)
        {
            ReleaseForegroundPending();
        }
    }

    private void ReleaseForegroundPending()
    {
        TaskCompletionSource? drained = null;
        lock (_priorityLock)
        {
            if (_foregroundPending == 0)
            {
                return;
            }

            if (--_foregroundPending == 0)
            {
                drained = _foregroundDrained;
            }
        }

        drained?.TrySetResult();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }
}

/// <summary>
/// A held execution slot. Background leases carry <see cref="Preempted"/>,
/// which fires when foreground work needs the slot; a holder that observes it
/// is expected to stop, kill and reap its child, then dispose the lease.
/// </summary>
public sealed class ChildProcessExecutionLease : IDisposable
{
    private ChildProcessExecutionGate? _owner;
    private readonly bool _foreground;

    internal ChildProcessExecutionLease(
        ChildProcessExecutionGate owner,
        bool foreground,
        CancellationToken preempted)
    {
        _owner = owner;
        _foreground = foreground;
        Preempted = preempted;
    }

    /// <summary>Fires when foreground work is waiting for this slot.</summary>
    public CancellationToken Preempted { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.ReleaseLease(_foreground);
    }
}

/// <summary>
/// Process-wide policy for child-process serialization.
/// </summary>
public static class ChildProcessExecutionPolicy
{
    /// <summary>The one gate every recurring child spawner shares.</summary>
    public static ChildProcessExecutionGate SharedGate { get; } = new();

    /// <summary>
    /// True only where the runtime has actually proven unsafe under concurrent
    /// child-process churn. Everywhere else, spawning stays unserialized.
    /// </summary>
    public static bool RequiresSerialization()
        => OperatingSystem.IsMacCatalyst();
}
