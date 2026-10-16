using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.LocalRuntime;

/// <summary>
/// Exact identity of a runtime daemon a host process started and deliberately
/// left running for every process of this OS user. This record is the only
/// authority to stop that daemon later: a process name, or a PID without its
/// start time and executable, never is.
/// </summary>
public sealed record OwnedLocalDaemonRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Runtime { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public string BindAddress { get; init; } = string.Empty;
    public int BindPort { get; init; }

    /// <summary>The host process that started the daemon, kept for audit only.</summary>
    public int? StartedByProcessId { get; init; }
    public DateTime? StartedByStartedAtUtc { get; init; }
    public string? StartedByExecutablePath { get; init; }
    public DateTime RecordedAtUtc { get; init; }

    [JsonIgnore]
    public ExactProcessIdentity Daemon => new(ProcessId, StartedAtUtc, ExecutablePath);

    [JsonIgnore]
    public IPEndPoint BindEndpoint => new(IPAddress.Parse(BindAddress), BindPort);

    public static OwnedLocalDaemonRecord Create(
        string runtime,
        ExactProcessIdentity daemon,
        IPEndPoint bindEndpoint,
        ExactProcessIdentity? startedBy,
        DateTime recordedAtUtc)
        => new()
        {
            Runtime = runtime,
            ProcessId = daemon.ProcessId,
            StartedAtUtc = daemon.StartedAtUtc,
            ExecutablePath = daemon.ExecutablePath,
            BindAddress = bindEndpoint.Address.ToString(),
            BindPort = bindEndpoint.Port,
            StartedByProcessId = startedBy?.ProcessId,
            StartedByStartedAtUtc = startedBy?.StartedAtUtc,
            StartedByExecutablePath = startedBy?.ExecutablePath,
            RecordedAtUtc = recordedAtUtc,
        };

    public bool NamesSameDaemon(OwnedLocalDaemonRecord other)
        => other.ProcessId == ProcessId
           && other.StartedAtUtc == StartedAtUtc
           && ExactProcessIdentityInspector.PathsEqual(other.ExecutablePath, ExecutablePath);

    internal bool IsValidFor(string runtime)
        => SchemaVersion == CurrentSchemaVersion
           && string.Equals(Runtime, runtime, StringComparison.Ordinal)
           && ProcessId > 0
           && StartedAtUtc > DateTime.UnixEpoch
           && !string.IsNullOrWhiteSpace(ExecutablePath)
           && IPAddress.TryParse(BindAddress, out _)
           && BindPort is > 0 and <= IPEndPoint.MaxPort;
}

/// <summary>
/// Machine-level coordination for one local runtime, shared by every host
/// process of this OS user and independent of any profile: the record of the
/// daemon a host started, a lifecycle lock that serializes starting and
/// stopping that daemon, and a package-operation lock held while the
/// runtime's package is installed or upgraded. Locks are OS file locks, so a
/// crashed holder releases them.
/// </summary>
public sealed class OwnedLocalDaemonStore
{
    private const string RecordFileName = "owned-daemon.json";
    private const string LifecycleLockFileName = "lifecycle.lock";
    private const string PackageOperationLockFileName = "package-operation.lock";
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OwnedLocalDaemonStore(string runtime, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        Runtime = runtime.Trim().ToLowerInvariant();
        StateDirectory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory(Runtime) : Path.GetFullPath(directory);
    }

    public string Runtime { get; }

    public string StateDirectory { get; }

    private string RecordPath => Path.Combine(StateDirectory, RecordFileName);

    /// <summary>
    /// Per-user and outside every product's data root, because the daemon
    /// serves one machine-wide port whichever profile or product started it.
    /// </summary>
    public static string DefaultDirectory(string runtime)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "SwarmCore", "local-runtimes", runtime.Trim().ToLowerInvariant());
    }

    public bool HasRecord()
    {
        try { return File.Exists(RecordPath); }
        catch { return false; }
    }

    /// <summary>The recorded daemon, or null when there is none or the file is not a valid record.</summary>
    public OwnedLocalDaemonRecord? TryLoad()
    {
        if (!HasRecord()) return null;
        return AtomicJsonFile.TryRead<OwnedLocalDaemonRecord>(RecordPath, out var record, JsonOptions)
               && record!.IsValidFor(Runtime)
            ? record
            : null;
    }

    public void Save(OwnedLocalDaemonRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = record with { Runtime = Runtime };
        if (!normalized.IsValidFor(Runtime))
            throw new ArgumentException("The daemon record has no exact identity or bind endpoint.", nameof(record));
        Directory.CreateDirectory(StateDirectory);
        AtomicJsonFile.Write(RecordPath, normalized, JsonOptions);
    }

    /// <summary>
    /// Removes the record only while it still names <paramref name="expected"/>'s
    /// daemon, so a record another process just wrote for a new daemon survives.
    /// Call it under the lifecycle lock.
    /// </summary>
    public bool TryDelete(OwnedLocalDaemonRecord expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = TryLoad();
        if (current is null || !current.NamesSameDaemon(expected)) return false;
        try
        {
            File.Delete(RecordPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a file that exists but is not a valid record. It names no
    /// daemon, so it authorizes nothing and must not block a new record.
    /// </summary>
    public bool TryDeleteUnreadable()
    {
        if (!HasRecord() || TryLoad() is not null) return false;
        try
        {
            File.Delete(RecordPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Serializes starting and stopping the recorded daemon across processes.</summary>
    public Task<IDisposable?> TryAcquireLifecycleLockAsync(TimeSpan timeout, CancellationToken ct = default)
        => TryAcquireFileLockAsync(Path.Combine(StateDirectory, LifecycleLockFileName), timeout, ct);

    /// <summary>
    /// Held for the whole install or upgrade of the runtime's package. The
    /// installer stops every runtime process and relaunches the user's app, so
    /// a daemon started meanwhile would either be killed or end up sharing the
    /// port with the relaunched app's server.
    /// </summary>
    public Task<IDisposable?> TryAcquirePackageOperationAsync(TimeSpan timeout, CancellationToken ct = default)
        => TryAcquireFileLockAsync(Path.Combine(StateDirectory, PackageOperationLockFileName), timeout, ct);

    public bool IsPackageOperationInProgress()
    {
        var path = Path.Combine(StateDirectory, PackageOperationLockFileName);
        try
        {
            if (!File.Exists(path)) return false;
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException ex)
        {
            return IsLockConflict(ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<IDisposable?> TryAcquireFileLockAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(StateDirectory);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The file is left in place on release: deleting a lock file
                // lets a waiter that opened the old file and a newcomer that
                // created a new one both believe they hold the lock.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockConflict(IOException ex)
    {
        if (!OperatingSystem.IsWindows())
        {
            // .NET reports another process's FileShare.None lock (flock) with
            // the raw errno; any other I/O failure here is equally a reason
            // not to assume the package is idle.
            return true;
        }

        const int sharingViolation = 32;
        const int lockViolation = 33;
        return (ex.HResult & 0xFFFF) is sharingViolation or lockViolation;
    }
}
