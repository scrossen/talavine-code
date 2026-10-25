using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Decisions;

/// <summary>
/// A single assessment's active-work budget and separate cumulative local-admission budget.
/// Ambient state carries timer accounting only; it conveys no execution or selection authority.
/// </summary>
public sealed class DecisionExecutionBudget : IDisposable
{
    public static readonly TimeSpan DefaultAdmissionTimeout = TimeSpan.FromSeconds(120);
    private static readonly AsyncLocal<DecisionExecutionBudget?> Ambient = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource active;
    private readonly CancellationToken token;
    private TimeSpan activeRemaining;
    private TimeSpan admissionRemaining;
    private long activeStarted;
    private bool clockRunning = true;
    private bool admitting;
    private bool parallelObserved;
    private bool disposed;
    private int operations;

    public static DecisionExecutionBudget? Current => Ambient.Value;
    public CancellationToken Token => token;

    private DecisionExecutionBudget(TimeSpan activeTimeout, TimeSpan admissionTimeout, CancellationToken ct)
    {
        activeRemaining = activeTimeout;
        admissionRemaining = admissionTimeout;
        active = CancellationTokenSource.CreateLinkedTokenSource(ct);
        token = active.Token;
        activeStarted = Stopwatch.GetTimestamp();
        active.CancelAfter(activeTimeout);
    }

    public static DecisionExecutionBudget Begin(TimeSpan activeTimeout, CancellationToken ct = default,
        TimeSpan? admissionTimeout = null)
    {
        if (Current is not null) throw new InvalidOperationException("A decision assessment already owns this execution budget.");
        if (activeTimeout <= TimeSpan.Zero || admissionTimeout is { } admission && admission <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(activeTimeout));
        return Ambient.Value = new(activeTimeout, admissionTimeout ?? DefaultAdmissionTimeout, ct);
    }

    /// <summary>Concurrent provider calls retain the active timer rather than exempting unrelated active work.</summary>
    public IDisposable EnterOperation()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Token.ThrowIfCancellationRequested();
            if (++operations > 1)
            {
                parallelObserved = true;
                ResumeClock();
            }
        }
        return new Release(() => { lock (gate) operations--; });
    }

    public async Task<IDisposable> AcquireLocalAsync(ILocalLlmRuntimeManager runtime,
        LocalLlmRuntimeRequest request, CancellationToken ct)
    {
        TimeSpan remaining;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ct.ThrowIfCancellationRequested();
            Token.ThrowIfCancellationRequested();
            // Overlapping admissions do not obtain multiple exemptions or renew the queue budget.
            if (admitting) throw new InvalidOperationException("Concurrent decision admissions require separate assessments.");
            if (admissionRemaining <= TimeSpan.Zero) throw new LocalLlmRuntimeBusyException("The decision's local admission budget expired.");
            remaining = admissionRemaining;
            if (operations == 1 && !parallelObserved) PauseClock();
            Token.ThrowIfCancellationRequested();
            admitting = true;
        }
        var started = Stopwatch.GetTimestamp();
        using var queueTimeout = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Token, queueTimeout.Token);
        try
        {
            var admissionRequest = request with { AdmissionTimeout = remaining };
            var admissionToken = linked.Token;
            // Contain a synchronous adapter prefix too. ExecutionContext carries the trusted owner scope.
            var acquisition = Task.Run(() => runtime.AcquireAsync(admissionRequest, admissionToken), CancellationToken.None);
            IDisposable lease;
            try { lease = await acquisition.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch
            {
                // An old or non-cooperative adapter can return after this assessment has ended.
                // Observe its outcome and release any eventual lease; never dispatch inference from it.
                _ = ReleaseLateLeaseAsync(acquisition);
                throw;
            }
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= remaining)
                    throw new LocalLlmRuntimeBusyException("The decision's local admission budget expired.");
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (queueTimeout.IsCancellationRequested && !ct.IsCancellationRequested && !Token.IsCancellationRequested)
        {
            throw new LocalLlmRuntimeBusyException("The decision's local admission budget expired.");
        }
        finally
        {
            lock (gate)
            {
                admissionRemaining -= Stopwatch.GetElapsedTime(started);
                admitting = false;
                if (!disposed) ResumeClock();
            }
        }
    }

    private static async Task ReleaseLateLeaseAsync(Task<IDisposable> acquisition)
    {
        try { (await acquisition.ConfigureAwait(false)).Dispose(); }
        catch (Exception) { /* The bounded caller already observed failure; late faults are observed here. */ }
    }

    private void PauseClock()
    {
        if (!clockRunning) return;
        active.CancelAfter(Timeout.InfiniteTimeSpan);
        activeRemaining -= Stopwatch.GetElapsedTime(activeStarted);
        clockRunning = false;
        if (activeRemaining <= TimeSpan.Zero) active.Cancel();
    }

    private void ResumeClock()
    {
        if (clockRunning || active.IsCancellationRequested) return;
        clockRunning = true;
        activeStarted = Stopwatch.GetTimestamp();
        if (activeRemaining <= TimeSpan.Zero) active.Cancel();
        else active.CancelAfter(activeRemaining);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            active.Cancel();
            active.Dispose();
        }
        if (ReferenceEquals(Current, this)) Ambient.Value = null;
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
