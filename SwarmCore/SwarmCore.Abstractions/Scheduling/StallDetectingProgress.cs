namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// A producer reports this only after a work unit completes. Status text,
/// retry attempts and timer heartbeats are not completed-work evidence.
/// </summary>
public interface ICompletedWorkProgress
{
    void ReportCompletedWork(string checkpoint);
}

public static class CompletedWorkProgressExtensions
{
    public static void ReportCompletedWork<T>(this IProgress<T>? progress, string checkpoint)
        => (progress as ICompletedWorkProgress)?.ReportCompletedWork(checkpoint);
}

/// <summary>
/// Cancels stalled work without limiting the total duration of a progressing
/// operation. Hosts supply the inactivity tolerance; producers supply actual
/// completed-work checkpoints separately from display-only progress.
/// </summary>
public sealed class StallDetectingProgress<T> : IProgress<T>, ICompletedWorkProgress, IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _stallTimeout;
    private readonly IProgress<T>? _inner;
    private readonly CancellationTokenSource _stall;
    private readonly CancellationTokenSource _linked;
    private bool _disposed;

    public StallDetectingProgress(
        TimeSpan stallTimeout,
        CancellationToken cancellationToken,
        IProgress<T>? inner = null,
        TimeProvider? timeProvider = null)
    {
        if (stallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallTimeout));
        _stallTimeout = stallTimeout;
        _inner = inner;
        _stall = new CancellationTokenSource(stallTimeout, timeProvider ?? TimeProvider.System);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stall.Token);
    }

    public CancellationToken Token => _linked.Token;
    public bool IsStalled => _stall.IsCancellationRequested;
    public long CompletedWorkCount { get; private set; }
    public string? LastCheckpoint { get; private set; }

    public void Report(T value) => _inner?.Report(value);

    public void ReportCompletedWork(string checkpoint)
    {
        lock (_gate)
        {
            if (_disposed || _linked.IsCancellationRequested) return;
            CompletedWorkCount++;
            LastCheckpoint = checkpoint;
            _stall.CancelAfter(_stallTimeout);
        }
        // Checkpoints deliberately do not trigger UI rendering.
        _inner.ReportCompletedWork(checkpoint);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _linked.Dispose();
            _stall.Dispose();
        }
    }
}
