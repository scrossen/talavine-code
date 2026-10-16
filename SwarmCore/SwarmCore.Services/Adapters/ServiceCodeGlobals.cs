using SwarmCore.Services.Scripting;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Globals a C# service script runs against. <c>Input</c> contains only the
/// arguments supplied to this service call—not arbitrary app/database state.
/// <c>Web</c> is the audited HTTP channel and <c>Files</c> is the observable,
/// objective-scoped filesystem channel. <c>Progress</c> renews a no-progress
/// lease for long batch work, and <c>Cancellation</c> stops cooperatively when
/// the objective is cancelled. There is no fixed wall-clock deadline.
/// </summary>
public sealed class ServiceCodeGlobals
{
    public Dictionary<string, object?> Input { get; init; } = new();
    public ScriptWeb Web { get; init; } = default!;
    public ScriptFiles Files { get; init; } = default!;
    public ServiceExecutionProgress Progress { get; init; } = new();
    public CancellationToken Cancellation { get; init; }
}

/// <summary>
/// Observable liveness for an authored service. Report meaningful units (for
/// example messages processed), not a tight-loop heartbeat; the host uses the
/// timestamp to distinguish slow useful work from a wedged invocation.
/// </summary>
public sealed class ServiceExecutionProgress
{
    private long _lastProgressTicks = DateTime.UtcNow.Ticks;
    private long _completed;
    private long _total = -1;
    private string? _message;

    public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);
    public long Completed => Interlocked.Read(ref _completed);
    public long? Total
    {
        get
        {
            var total = Interlocked.Read(ref _total);
            return total < 0 ? null : total;
        }
    }
    public string? Message => Volatile.Read(ref _message);

    public void Report(long completed, long? total = null, string? message = null)
    {
        Interlocked.Exchange(ref _completed, completed);
        Interlocked.Exchange(ref _total, total ?? -1);
        Volatile.Write(ref _message, Bound(message));
        Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Record host-observed activity such as an outbound Web request.</summary>
    internal void Observe(string message)
    {
        Volatile.Write(ref _message, Bound(message));
        Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
    }

    internal string Describe()
    {
        var total = Total;
        var count = total is null ? Completed.ToString() : $"{Completed}/{total}";
        return string.IsNullOrWhiteSpace(Message) ? count : $"{count} — {Message}";
    }

    private static string? Bound(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= 240 ? trimmed : trimmed[..240];
    }
}
