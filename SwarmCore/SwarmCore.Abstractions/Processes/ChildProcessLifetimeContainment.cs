using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Result of checking exact child-ownership markers before a new process-backed
/// workload starts. A false result is deliberately fail-closed: at least one
/// exact previous child could not be proven stopped without risking an
/// unrelated process.
/// </summary>
public sealed record ChildProcessOrphanSweepResult(
    bool Succeeded,
    int ReapedProcesses,
    int LiveOwnerMarkers,
    int UnverifiableMarkers);

/// <summary>
/// Binds a spawned child to the current process lifetime without ever sweeping
/// by executable name. Windows uses a kill-on-close Job Object. Unix-like hosts
/// persist a secret-free ownership marker containing exact owner and child
/// PID/start identities plus the executable observed at attachment for audit.
/// The next process reaps only the same PID/start instance whose exact owner is
/// gone; an in-place executable transition remains the same owned process.
/// </summary>
public sealed class ChildProcessLifetimeContainment : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan SweepLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly ChildOwnershipMarker? _marker;
    private readonly string? _markerPath;
    private readonly SafeFileHandle? _windowsJob;
    private readonly ILogger? _logger;
    private int _disposed;

    private ChildProcessLifetimeContainment(
        Process process,
        ChildOwnershipMarker? marker,
        string? markerPath,
        SafeFileHandle? windowsJob,
        ILogger? logger)
    {
        _process = process;
        _marker = marker;
        _markerPath = markerPath;
        _windowsJob = windowsJob;
        _logger = logger;
    }

    /// <summary>
    /// Attaches containment immediately after <see cref="Process.Start()"/>.
    /// If exact identity capture or durable marker creation fails, the child is
    /// stopped and the caller receives an exception rather than running an
    /// unowned provider process.
    /// </summary>
    public static ChildProcessLifetimeContainment Attach(
        Process process,
        string workloadKey,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        var normalizedWorkload = NormalizeWorkloadKey(workloadKey);
        SafeFileHandle? windowsJob = null;
        if (OperatingSystem.IsWindows())
        {
            // Attach before metadata capture so the only Windows crash window is
            // the unavoidable Process.Start -> Attach call boundary.
            windowsJob = TryAttachWindowsKillOnCloseJob(process, logger);
            if (windowsJob is null)
            {
                if (HasExited(process))
                {
                    return new ChildProcessLifetimeContainment(
                        process,
                        marker: null,
                        markerPath: null,
                        windowsJob: null,
                        logger);
                }

                TryKillSpawnedProcess(process, logger);
                throw new InvalidOperationException(
                    "The child could not be attached to the Windows process-lifetime job.");
            }
        }

        var owner = ExactProcessIdentityInspector.CaptureCurrent();
        var child = ExactProcessIdentityInspector.CaptureWithStartupRetry(process);
        if (child is null && HasExited(process))
        {
            // Very short connectivity/version probes can finish between Start
            // and identity capture. There is no live child to contain or mark.
            windowsJob?.Dispose();
            return new ChildProcessLifetimeContainment(
                process,
                marker: null,
                markerPath: null,
                windowsJob: null,
                logger);
        }
        if (owner is null || child is null)
        {
            windowsJob?.Dispose();
            TryKillSpawnedProcess(process, logger);
            throw new InvalidOperationException(
                owner is null
                    ? "Could not capture the current process identity for child-process containment."
                    : "Could not capture the spawned child identity for child-process containment.");
        }

        var marker = new ChildOwnershipMarker
        {
            WorkloadKey = normalizedWorkload,
            MarkerId = Guid.NewGuid().ToString("N"),
            OwnerProcessId = owner.ProcessId,
            OwnerStartedAtUtc = owner.StartedAtUtc,
            OwnerExecutablePath = owner.ExecutablePath,
            ChildProcessId = child.ProcessId,
            ChildStartedAtUtc = child.StartedAtUtc,
            ChildExecutablePath = child.ExecutablePath,
            CreatedAtUtc = DateTime.UtcNow,
        };
        string? markerPath = null;
        try
        {
            markerPath = SaveMarker(marker);
            return new ChildProcessLifetimeContainment(
                process,
                marker,
                markerPath,
                windowsJob,
                logger);
        }
        catch
        {
            windowsJob?.Dispose();
            TryKillExactProcess(child.Instance, logger);
            if (markerPath is not null)
            {
                TryDelete(markerPath);
            }
            throw;
        }
    }

    /// <summary>
    /// Reaps exact orphan markers for one workload before a new child starts.
    /// A live exact owner is never disturbed. A reused PID, changed start time,
    /// is treated as a stale marker and never killed. An executable-path change
    /// with the same PID/start is an expected Unix exec transition and remains
    /// contained.
    /// </summary>
    public static async Task<ChildProcessOrphanSweepResult> ReapOrphansAsync(
        string workloadKey,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var normalizedWorkload = NormalizeWorkloadKey(workloadKey);
        var markerDirectory = GetMarkerDirectory(normalizedWorkload);
        Directory.CreateDirectory(markerDirectory);
        await using var sweepLock = await TryAcquireSweepLockAsync(
            markerDirectory,
            ct).ConfigureAwait(false);
        if (sweepLock is null)
        {
            logger?.LogWarning(
                "Could not acquire the {Workload} child-containment sweep lock; no new child should be started",
                normalizedWorkload);
            return new ChildProcessOrphanSweepResult(false, 0, 0, 1);
        }

        var reaped = 0;
        var liveOwners = 0;
        var unverifiable = 0;
        foreach (var path in Directory.EnumerateFiles(markerDirectory, "*.owner.json"))
        {
            ct.ThrowIfCancellationRequested();
            MarkerEnumeratedForTests?.Invoke(path);
            var marker = TryLoadMarker(path);
            if (marker is null && !File.Exists(path))
            {
                // The marker vanished between enumeration and read: its live
                // owner disposed containment after the child exited, or another
                // process's sweep reaped it first. A file that no longer exists
                // describes no child and is authority for nothing, so it must
                // neither block this launch nor be mistaken for a malformed
                // final marker. Only a marker that still exists but cannot be
                // read fails closed below.
                continue;
            }

            if (!IsValid(marker, normalizedWorkload))
            {
                // Markers are created by atomic rename. A malformed final file
                // cannot provide an exact process identity and is not authority
                // either to kill anything or to assume that no owned child is
                // still alive. Retain it and fail closed until an operator can
                // inspect/remove it or a future compatible reader can prove it
                // stale.
                unverifiable++;
                logger?.LogWarning(
                    "Unverifiable final child-containment marker {MarkerPath} blocks new {Workload} child processes",
                    path,
                    normalizedWorkload);
                continue;
            }

            using var childInspection = ExactProcessIdentityInspector.InspectInstance(
                marker!.ChildProcessId,
                marker.ChildStartedAtUtc);
            if (childInspection.State is ExactProcessInspectionState.Absent
                or ExactProcessInspectionState.IdentityMismatch)
            {
                TryDelete(path);
                continue;
            }
            if (childInspection.State == ExactProcessInspectionState.Unverifiable)
            {
                unverifiable++;
                continue;
            }

            using var ownerInspection = ExactProcessIdentityInspector.InspectInstance(
                marker.OwnerProcessId,
                marker.OwnerStartedAtUtc);
            if (ownerInspection.State == ExactProcessInspectionState.Matches)
            {
                liveOwners++;
                continue;
            }
            if (ownerInspection.State == ExactProcessInspectionState.Unverifiable)
            {
                unverifiable++;
                continue;
            }

            var child = childInspection.Process!;
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
                var exited = await ChildProcessReaper.WaitForExitQuietlyAsync(
                    child,
                    KillWaitTimeout,
                    logger,
                    ct).ConfigureAwait(false);
                if (!exited)
                {
                    unverifiable++;
                    continue;
                }

                reaped++;
                TryDelete(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unverifiable++;
                logger?.LogWarning(
                    ex,
                    "Could not stop exact orphan child PID {ProcessId} for {Workload}; a new child remains blocked",
                    marker.ChildProcessId,
                    normalizedWorkload);
            }
        }

        return new ChildProcessOrphanSweepResult(
            unverifiable == 0,
            reaped,
            liveOwners,
            unverifiable);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_marker is null)
        {
            _windowsJob?.Dispose();
            return;
        }

        var exited = TryKillExactProcess(ToInstanceIdentity(_marker), _logger);
        _windowsJob?.Dispose();
        if (!exited)
        {
            exited = WaitForExit(_process, KillWaitTimeout);
        }
        if (exited)
        {
            TryDelete(_markerPath!);
        }
        else
        {
            _logger?.LogWarning(
                "Child PID {ProcessId} did not exit during containment disposal; its exact ownership marker was retained for the next process",
                _marker.ChildProcessId);
        }
    }

    /// <summary>
    /// Cheap, lock-free check for any ownership markers under one workload key.
    /// Used to skip the locked sweep of a directory that is known-empty (e.g.
    /// the legacy pre-provider-scoped key after its one-time transition sweep).
    /// Unreadable state reports true so the authoritative locked sweep decides.
    /// </summary>
    public static bool HasOwnershipMarkers(string workloadKey)
    {
        var directory = GetMarkerDirectory(NormalizeWorkloadKey(workloadKey));
        try
        {
            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, "*.owner.json").Any();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Test seam invoked with each enumerated marker path before it is read,
    /// so a test can remove the file deterministically inside that window.
    /// </summary>
    internal static Action<string>? MarkerEnumeratedForTests { get; set; }

    internal static string GetMarkerDirectoryForTests(string workloadKey)
        => GetMarkerDirectory(NormalizeWorkloadKey(workloadKey));

    internal static string WriteMarkerForTests(
        string workloadKey,
        Process child,
        int ownerProcessId,
        DateTime ownerStartedAtUtc,
        string ownerExecutablePath,
        DateTime? childStartedAtUtc = null,
        string? childExecutablePath = null)
    {
        var childIdentity = ExactProcessIdentityInspector.Capture(child)
            ?? ExactProcessIdentityInspector.CaptureWithStartupRetry(child)
            ?? throw new InvalidOperationException("Could not capture test child identity.");
        return SaveMarker(new ChildOwnershipMarker
        {
            WorkloadKey = NormalizeWorkloadKey(workloadKey),
            MarkerId = Guid.NewGuid().ToString("N"),
            OwnerProcessId = ownerProcessId,
            OwnerStartedAtUtc = ownerStartedAtUtc,
            OwnerExecutablePath = ownerExecutablePath,
            ChildProcessId = childIdentity.ProcessId,
            ChildStartedAtUtc = childStartedAtUtc ?? childIdentity.StartedAtUtc,
            ChildExecutablePath = childExecutablePath ?? childIdentity.ExecutablePath,
            CreatedAtUtc = DateTime.UtcNow,
        });
    }

    private static async Task<FileStream?> TryAcquireSweepLockAsync(
        string markerDirectory,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SweepLockTimeout;
        var path = Path.Combine(markerDirectory, ".sweep.lock");
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static string SaveMarker(ChildOwnershipMarker marker)
    {
        var directory = GetMarkerDirectory(marker.WorkloadKey);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{marker.MarkerId}.owner.json");
        AtomicJsonFile.Write(path, marker, JsonOptions);
        return path;
    }

    private static ChildOwnershipMarker? TryLoadMarker(string path)
        => AtomicJsonFile.TryRead<ChildOwnershipMarker>(path, out var marker, JsonOptions)
            ? marker
            : null;

    private static bool IsValid(ChildOwnershipMarker? marker, string workloadKey)
        => marker is
           {
               SchemaVersion: CurrentSchemaVersion,
               MarkerId.Length: > 0,
               OwnerProcessId: > 0,
               OwnerExecutablePath.Length: > 0,
               ChildProcessId: > 0,
               ChildExecutablePath.Length: > 0,
           }
           && marker.OwnerStartedAtUtc > DateTime.UnixEpoch
           && marker.ChildStartedAtUtc > DateTime.UnixEpoch
           && string.Equals(marker.WorkloadKey, workloadKey, StringComparison.Ordinal);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch { return false; }
    }

    private static bool TryKillExactProcess(
        ExactProcessInstanceIdentity expected,
        ILogger? logger)
    {
        try
        {
            using var inspection = ExactProcessIdentityInspector.InspectInstance(expected);
            if (inspection.State == ExactProcessInspectionState.Absent)
            {
                return true;
            }
            if (inspection.State != ExactProcessInspectionState.Matches)
            {
                return false;
            }

            var exactProcess = inspection.Process!;
            exactProcess.Kill(entireProcessTree: true);
            return WaitForExit(exactProcess, KillWaitTimeout);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not stop exact contained child PID {ProcessId}", expected.ProcessId);
            return false;
        }
    }

    private static void TryKillSpawnedProcess(Process process, ILogger? logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            _ = WaitForExit(process, KillWaitTimeout);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not stop a child after containment identity capture failed");
        }
    }

    private static bool WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }
            return process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SafeFileHandle? TryAttachWindowsKillOnCloseJob(
        Process process,
        ILogger? logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        SafeFileHandle? job = null;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                job.Dispose();
                return null;
            }
            var information = new JobObjectExtendedLimitInformationNative
            {
                BasicLimitInformation = new JobObjectBasicLimitInformationNative
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>())
                || !AssignProcessToJobObject(job, process.Handle))
            {
                job.Dispose();
                return null;
            }
            return job;
        }
        catch (Exception ex)
        {
            job?.Dispose();
            logger?.LogDebug(
                ex,
                "Could not attach child PID {ProcessId} to a Windows kill-on-close job; exact orphan-marker recovery remains active",
                SafeProcessId(process));
            return null;
        }
    }

    private static ExactProcessInstanceIdentity ToInstanceIdentity(ChildOwnershipMarker marker)
        => new(
            marker.ChildProcessId,
            marker.ChildStartedAtUtc);

    internal static string NormalizeWorkloadKey(string workloadKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadKey);
        var normalized = new string(workloadKey.Trim().ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Take(64)
            .ToArray());
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The workload key has no usable characters.", nameof(workloadKey));
        }
        return normalized;
    }

    private static string GetMarkerDirectory(string normalizedWorkloadKey)
        => Path.Combine(
            Path.GetTempPath(),
            "SwarmCore",
            "child-process-owners",
            normalizedWorkloadKey);

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
            // A later exact sweep can retry cleanup.
        }
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return null; }
    }

    private sealed class ChildOwnershipMarker
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string WorkloadKey { get; set; } = string.Empty;
        public string MarkerId { get; set; } = string.Empty;
        public int OwnerProcessId { get; set; }
        public DateTime OwnerStartedAtUtc { get; set; }
        public string OwnerExecutablePath { get; set; } = string.Empty;
        public int ChildProcessId { get; set; }
        public DateTime ChildStartedAtUtc { get; set; }
        public string ChildExecutablePath { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformationNative information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationNative
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformationNative BasicLimitInformation;
        public IoCountersNative IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
