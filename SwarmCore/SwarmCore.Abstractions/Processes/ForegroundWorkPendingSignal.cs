using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Cross-process "user-waiting work is pending" sentinel, keyed by the same
/// workload keys as <see cref="ChildProcessLifetimeContainment"/> markers.
/// <para>
/// Purpose: SCHEDULING, never containment. When the desktop app's foreground
/// (user-waiting) launch is declined because another process owns a live child
/// for the same workload, the app raises this signal; unserialized hosts
/// (worker/bulk processes) check it before starting their next child for that
/// workload and defer briefly so the user's turn wins the next slot. Losing or
/// ignoring the sentinel costs only latency — it must never gate correctness,
/// which is why every read path fails open (proceed) rather than closed.
/// </para>
/// <para>
/// Crash safety comes from the TTL: a raiser that dies simply stops refreshing
/// and the sentinel goes stale. Stale or unreadable sentinels are ignored and
/// opportunistically deleted by the next reader.
/// </para>
/// </summary>
public sealed class ForegroundWorkPendingSignal
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// How long a raised sentinel stays fresh without a refresh. Long enough to
    /// bridge the raiser's retry cadence, short enough that a crashed raiser
    /// only delays bulk work by seconds.
    /// </summary>
    public static TimeSpan DefaultTimeToLive { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The shared machine-wide instance every production caller uses.</summary>
    public static ForegroundWorkPendingSignal Default { get; } = new();

    private readonly string _directory;
    private readonly Func<DateTime> _utcNow;

    public ForegroundWorkPendingSignal(
        string? directory = null,
        TimeSpan? timeToLive = null,
        Func<DateTime>? utcNow = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Path.GetTempPath(), "SwarmCore", "foreground-pending")
            : directory;
        TimeToLive = timeToLive is { } ttl && ttl > TimeSpan.Zero
            ? ttl
            : DefaultTimeToLive;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    public TimeSpan TimeToLive { get; }

    /// <summary>
    /// Raises (or refreshes) the pending sentinel for one workload. Best-effort:
    /// an I/O failure is swallowed because the signal is an optimization, and
    /// the raiser's decline path must never gain a new failure mode.
    /// </summary>
    public void Raise(string workloadKey)
    {
        try
        {
            AtomicJsonFile.Write(
                GetSentinelPath(workloadKey),
                new PendingSentinel
                {
                    WorkloadKey = ChildProcessLifetimeContainment.NormalizeWorkloadKey(workloadKey),
                    RaisedAtUtc = _utcNow(),
                    RaisedByProcessId = Environment.ProcessId,
                },
                JsonOptions);
        }
        catch
        {
            // Fail open: a sentinel that cannot be written only means bulk work
            // is not asked to defer.
        }
    }

    /// <summary>
    /// Clears the sentinel for one workload — called once the user-waiting
    /// child has actually started, so bulk lanes stop deferring immediately
    /// instead of waiting out the TTL.
    /// </summary>
    public void Clear(string workloadKey)
    {
        var path = GetSentinelPath(workloadKey);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The TTL retires an undeletable sentinel shortly anyway.
        }
    }

    /// <summary>
    /// True while a fresh sentinel exists for the workload. Stale or unreadable
    /// sentinels report false and are opportunistically deleted.
    /// </summary>
    public bool IsPending(string workloadKey)
    {
        var path = GetSentinelPath(workloadKey);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var raisedAtUtc = ReadRaisedAtUtc(path);
            if (raisedAtUtc is null)
            {
                // No usable timestamp at all (unreadable content AND an
                // unreadable write time): fail open and clean up the husk.
                TryDelete(path);
                return false;
            }

            // Symmetric window: a far-future timestamp (clock skew, a bad
            // write) is as untrustworthy as an expired one.
            var age = _utcNow() - raisedAtUtc.Value;
            if (age >= TimeToLive || age <= -TimeToLive)
            {
                TryDelete(path);
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits while the workload's sentinel stays fresh, up to
    /// <paramref name="maximumWait"/>. Returns true when the sentinel cleared
    /// or expired (or was never pending); false when the bounded wait elapsed
    /// with the sentinel still fresh — the caller should proceed either way,
    /// the bound exists precisely so bulk work can never deadlock behind a
    /// wedged raiser.
    /// </summary>
    public async Task<bool> WaitWhilePendingAsync(
        string workloadKey,
        TimeSpan maximumWait,
        CancellationToken ct = default,
        TimeSpan? pollInterval = null)
    {
        var poll = pollInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : TimeSpan.FromMilliseconds(250);
        var deadline = _utcNow() + maximumWait;
        while (IsPending(workloadKey))
        {
            if (_utcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(poll, ct).ConfigureAwait(false);
        }

        return true;
    }

    private string GetSentinelPath(string workloadKey)
        => Path.Combine(
            _directory,
            $"{ChildProcessLifetimeContainment.NormalizeWorkloadKey(workloadKey)}.pending.json");

    private static DateTime? ReadRaisedAtUtc(string path)
    {
        if (AtomicJsonFile.TryRead<PendingSentinel>(path, out var sentinel, JsonOptions)
            && sentinel is { SchemaVersion: CurrentSchemaVersion }
            && sentinel.RaisedAtUtc > DateTime.UnixEpoch)
        {
            return sentinel.RaisedAtUtc;
        }

        try
        {
            // Fallback for an unknown schema from another build: the file's own
            // write time still bounds how recently someone raised it.
            var lastWrite = File.GetLastWriteTimeUtc(path);
            return lastWrite > DateTime.UnixEpoch ? lastWrite : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A later reader retries cleanup.
        }
    }

    private sealed class PendingSentinel
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string WorkloadKey { get; set; } = string.Empty;
        public DateTime RaisedAtUtc { get; set; }
        public int RaisedByProcessId { get; set; }
    }
}
