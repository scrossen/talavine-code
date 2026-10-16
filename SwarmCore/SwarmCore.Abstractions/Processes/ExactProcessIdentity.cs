using System.ComponentModel;
using System.Diagnostics;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Reuse-safe identity for an operating-system process. A PID alone is never
/// authority to join, signal, or kill a process.
/// </summary>
public sealed record ExactProcessIdentity(
    int ProcessId,
    DateTime StartedAtUtc,
    string ExecutablePath)
{
    public ExactProcessInstanceIdentity Instance
        => new(ProcessId, StartedAtUtc);
}

/// <summary>
/// Reuse-safe operating-system process instance identity. Executing a new
/// image in-place preserves both PID and start time, so ownership/containment
/// must use this identity rather than treating an executable-path transition
/// as a different process.
/// </summary>
public sealed record ExactProcessInstanceIdentity(
    int ProcessId,
    DateTime StartedAtUtc);

public enum ExactProcessInspectionState
{
    Absent,
    Matches,
    IdentityMismatch,
    Unverifiable,
}

/// <summary>
/// Tri-state-plus-mismatch inspection result. Callers may discard stale markers
/// only for <see cref="ExactProcessInspectionState.Absent"/> or
/// <see cref="ExactProcessInspectionState.IdentityMismatch"/>; unverifiable
/// identity must remain fail-closed.
/// </summary>
public sealed record ExactProcessInspection(
    ExactProcessInspectionState State,
    Process? Process) : IDisposable
{
    public void Dispose() => Process?.Dispose();
}

/// <summary>
/// Single process identity/inspection implementation shared by provider-child,
/// detached-helper, parent-wait, and lease/status boundaries.
/// </summary>
public static class ExactProcessIdentityInspector
{
    public static ExactProcessIdentity? CaptureCurrent()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return Capture(process);
        }
        catch
        {
            return null;
        }
    }

    public static ExactProcessIdentity? Capture(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return null;
            }

            return new ExactProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime(),
                Path.GetFullPath(executable));
        }
        catch
        {
            return null;
        }
    }

    public static ExactProcessInstanceIdentity? CaptureInstance(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            var identity = new ExactProcessInstanceIdentity(
                process.Id,
                process.StartTime.ToUniversalTime());
            return process.HasExited ? null : identity;
        }
        catch
        {
            return null;
        }
    }

    public static ExactProcessIdentity? CaptureWithStartupRetry(
        Process process,
        int attempts = 50,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        attempts = Math.Max(1, attempts);
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(10);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var identity = Capture(process);
            if (identity is not null)
            {
                return identity;
            }
            if (HasExited(process))
            {
                return null;
            }
            if (attempt + 1 < attempts && delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }
        }
        return null;
    }

    public static ExactProcessInstanceIdentity? CaptureInstanceWithStartupRetry(
        Process process,
        int attempts = 50,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        attempts = Math.Max(1, attempts);
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(10);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var identity = CaptureInstance(process);
            if (identity is not null)
            {
                return identity;
            }
            if (HasExited(process))
            {
                return null;
            }
            if (attempt + 1 < attempts && delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }
        }
        return null;
    }

    public static ExactProcessInspection Inspect(ExactProcessIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return Inspect(expected.ProcessId, expected.StartedAtUtc, expected.ExecutablePath);
    }

    public static ExactProcessInspection Inspect(
        int processId,
        DateTime expectedStartedAtUtc,
        string expectedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            return new ExactProcessInspection(ExactProcessInspectionState.Unverifiable, null);
        }

        var instanceInspection = InspectInstance(processId, expectedStartedAtUtc);
        if (instanceInspection.State != ExactProcessInspectionState.Matches)
        {
            return instanceInspection;
        }

        var process = instanceInspection.Process!;
        try
        {
            var actualExecutable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualExecutable))
            {
                if (HasExited(process))
                {
                    process.Dispose();
                    return new ExactProcessInspection(ExactProcessInspectionState.Absent, null);
                }

                process.Dispose();
                return new ExactProcessInspection(ExactProcessInspectionState.Unverifiable, null);
            }

            if (!PathsEqual(actualExecutable, expectedExecutablePath))
            {
                process.Dispose();
                return new ExactProcessInspection(ExactProcessInspectionState.IdentityMismatch, null);
            }

            return new ExactProcessInspection(ExactProcessInspectionState.Matches, process);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or Win32Exception
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            process.Dispose();
            return new ExactProcessInspection(ExactProcessInspectionState.Unverifiable, null);
        }
    }

    /// <summary>
    /// Inspects ownership of one process instance by PID and start time only.
    /// Executable paths are intentionally excluded: Unix shims and interpreters
    /// commonly <c>exec</c> the real child in-place without changing this
    /// process-instance identity.
    /// </summary>
    public static ExactProcessInspection InspectInstance(ExactProcessInstanceIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return InspectInstance(expected.ProcessId, expected.StartedAtUtc);
    }

    public static ExactProcessInspection InspectInstance(
        int processId,
        DateTime expectedStartedAtUtc)
    {
        if (processId <= 0 || expectedStartedAtUtc <= DateTime.UnixEpoch)
        {
            return new ExactProcessInspection(ExactProcessInspectionState.Unverifiable, null);
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return new ExactProcessInspection(ExactProcessInspectionState.Absent, null);
            }

            var actualStartedAtUtc = process.StartTime.ToUniversalTime();
            if (process.HasExited)
            {
                process.Dispose();
                return new ExactProcessInspection(ExactProcessInspectionState.Absent, null);
            }

            if (actualStartedAtUtc != expectedStartedAtUtc)
            {
                process.Dispose();
                return new ExactProcessInspection(ExactProcessInspectionState.IdentityMismatch, null);
            }

            return new ExactProcessInspection(ExactProcessInspectionState.Matches, process);
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            return new ExactProcessInspection(ExactProcessInspectionState.Absent, null);
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            return new ExactProcessInspection(ExactProcessInspectionState.Absent, null);
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            process?.Dispose();
            return new ExactProcessInspection(ExactProcessInspectionState.Unverifiable, null);
        }
    }

    public static bool Matches(Process process, ExactProcessIdentity expected)
    {
        var actual = Capture(process);
        return actual is not null
               && actual.ProcessId == expected.ProcessId
               && actual.StartedAtUtc == expected.StartedAtUtc
               && PathsEqual(actual.ExecutablePath, expected.ExecutablePath);
    }

    public static bool MatchesInstance(
        Process process,
        ExactProcessInstanceIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            return !process.HasExited
                   && process.Id == expected.ProcessId
                   && process.StartTime.ToUniversalTime() == expected.StartedAtUtc
                   && !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch { return false; }
    }
}
