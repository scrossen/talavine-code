using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Helpers for child processes that are intentionally not part of a request/response
/// command. On Unix-like hosts, a child that exits must still be waited on by its
/// parent; otherwise it remains as a zombie until the parent process exits.
/// </summary>
public static class ChildProcessReaper
{
    public static void Track(Process? process, string? description = null, ILogger? logger = null)
    {
        if (process is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process may have already exited or become inaccessible.
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed waiting for detached child process {Description}.", description ?? process.StartInfo.FileName);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        });
    }

    public static async Task<bool> WaitForExitQuietlyAsync(
        Process process,
        TimeSpan timeout,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            if (process.HasExited) return true;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed waiting for child process {ProcessId} to exit.", SafeId(process));
            return false;
        }
    }

    public static async Task KillProcessTreeAndWaitAsync(
        Process process,
        TimeSpan timeout,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed killing child process {ProcessId}.", SafeId(process));
        }

        await WaitForExitQuietlyAsync(process, timeout, logger, ct).ConfigureAwait(false);
    }

    public static void Track(IChildProcess? process, string? description = null, ILogger? logger = null)
    {
        if (process is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex, "Failed waiting for detached child process {Description}.", description);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        });
    }

    public static async Task<bool> WaitForExitQuietlyAsync(
        IChildProcess process,
        TimeSpan timeout,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            if (process.HasExited) return true;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed waiting for child process {ProcessId} to exit.", SafeId(process));
            return false;
        }
    }

    public static async Task KillProcessTreeAndWaitAsync(
        IChildProcess process,
        TimeSpan timeout,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed killing child process {ProcessId}.", SafeId(process));
        }

        await WaitForExitQuietlyAsync(process, timeout, logger, ct).ConfigureAwait(false);
    }

    private static int? SafeId(Process process)
    {
        try { return process.Id; }
        catch { return null; }
    }

    private static int? SafeId(IChildProcess process)
    {
        try { return process.Id; }
        catch { return null; }
    }
}
