namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// Watches completed work on a dedicated thread so thread-pool starvation
/// cannot strand transport recovery. Progress extends the host's existing
/// inactivity allowance; total operation duration is unrestricted.
/// </summary>
public sealed class InactivityWatchdog : ICompletedWorkProgress, IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private readonly Action _onStalled;
    private readonly Thread _thread;
    private long _lastProgress;
    private State _state;
    private enum State { Running, Completed, Stalled }

    public InactivityWatchdog(TimeSpan timeout, Action onStalled, TimeProvider? timeProvider = null)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentNullException.ThrowIfNull(onStalled);
        _timeout = timeout;
        _clock = timeProvider ?? TimeProvider.System;
        _onStalled = onStalled;
        _lastProgress = _clock.GetTimestamp();
        _thread = new Thread(Watch) { IsBackground = true, Name = "Completed-work watchdog" };
        _thread.Start();
    }

    public bool IsStalled { get { lock (_gate) return _state == State.Stalled; } }
    public Exception? CallbackException { get; private set; }

    public void ReportCompletedWork(string checkpoint)
    {
        lock (_gate)
        {
            if (_state != State.Running) return;
            _lastProgress = _clock.GetTimestamp();
            Monitor.PulseAll(_gate);
        }
    }

    private void Watch()
    {
        lock (_gate)
        {
            while (_state == State.Running)
            {
                var remaining = _timeout - _clock.GetElapsedTime(_lastProgress);
                if (remaining <= TimeSpan.Zero)
                {
                    _state = State.Stalled;
                    break;
                }
                Monitor.Wait(_gate, (int)Math.Ceiling(remaining.TotalMilliseconds));
            }
            if (_state != State.Stalled) return;
        }
        try { _onStalled(); }
        catch (Exception ex) { CallbackException = ex; }
    }

    /// <summary>
    /// Atomically settles completion against a stall and drains the watchdog.
    /// No delayed callback may dispose a transport used by the next operation.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == State.Running) _state = State.Completed;
            Monitor.PulseAll(_gate);
        }
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}
