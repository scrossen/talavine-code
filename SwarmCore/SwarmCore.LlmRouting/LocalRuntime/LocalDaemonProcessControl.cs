using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.LocalRuntime;

/// <summary>
/// Starts a detached runtime daemon and later inspects or stops exactly that
/// process instance.
/// </summary>
public interface ILocalDaemonProcessControl
{
    /// <summary>
    /// Starts a daemon that outlives this process and returns its exact
    /// identity, or null when it exited before its identity could be read.
    /// </summary>
    ExactProcessIdentity? Start(ProcessStartInfo startInfo, string description, ILogger? logger);

    ExactProcessInspectionState Inspect(ExactProcessIdentity daemon);

    /// <summary>
    /// The exact identity of the process running as <paramref name="processId"/>
    /// right now, or null when it is gone or cannot be read.
    /// </summary>
    ExactProcessIdentity? Identify(int processId);

    /// <summary>
    /// Stops the exact recorded instance and its process tree (a runtime's
    /// model runners are its children). True when that instance is gone
    /// afterwards; a PID that now names another process is never signaled.
    /// </summary>
    Task<bool> StopAsync(ExactProcessIdentity daemon, TimeSpan timeout, CancellationToken ct);
}

public sealed class SystemLocalDaemonProcessControl : ILocalDaemonProcessControl
{
    public static SystemLocalDaemonProcessControl Instance { get; } = new();

    public ExactProcessIdentity? Start(ProcessStartInfo startInfo, string description, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var process = ChildProcessLauncher.Start(startInfo);
        // Drain the daemon's request logs so a long-running hidden server
        // cannot block on a full stdout/stderr pipe.
        if (startInfo.RedirectStandardOutput) process.BeginOutputReadLine();
        if (startInfo.RedirectStandardError) process.BeginErrorReadLine();

        ExactProcessIdentity? identity = null;
        using (var attached = process.AttachDiagnosticsProcess())
        {
            if (attached is not null)
            {
                identity = ExactProcessIdentityInspector.CaptureWithStartupRetry(attached);
            }
        }

        if (identity is null && !process.HasExited)
        {
            // A daemon without a recorded identity could never be stopped
            // later, so it is not left running.
            logger?.LogWarning("Could not read the identity of {Description}; stopping it", description);
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger?.LogDebug(ex, "Stopping {Description} failed", description);
            }
        }

        ChildProcessReaper.Track(process, description, logger);
        return identity;
    }

    public ExactProcessInspectionState Inspect(ExactProcessIdentity daemon)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        using var inspection = ExactProcessIdentityInspector.Inspect(daemon);
        return inspection.State;
    }

    public ExactProcessIdentity? Identify(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return ExactProcessIdentityInspector.Capture(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<bool> StopAsync(ExactProcessIdentity daemon, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        using var inspection = ExactProcessIdentityInspector.Inspect(daemon);
        switch (inspection.State)
        {
            case ExactProcessInspectionState.Absent:
            case ExactProcessInspectionState.IdentityMismatch:
                return true;
            case ExactProcessInspectionState.Unverifiable:
                return false;
        }

        var process = inspection.Process!;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }

        return await ChildProcessReaper.WaitForExitQuietlyAsync(process, timeout, ct: ct).ConfigureAwait(false);
    }
}
