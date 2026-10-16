using System.Diagnostics;
using System.Text;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Agent.CodingTasks;

/// <summary>One child-process invocation for the coding-task engine (git or a
/// coding CLI). Stdin carries the prompt so command lines stay short — the
/// Windows command line caps out around 32k characters, a stack-trace prompt
/// does not.</summary>
public sealed record CodingTaskProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput,
    TimeSpan Timeout)
{
    /// <summary>Environment changes for the child (a null value removes an inherited variable); null leaves it as inherited.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>Captured outcome of one child-process invocation. When
/// <see cref="TimedOut"/> is true the whole process tree was killed after its
/// renewable output-activity lease expired and <see cref="ExitCode"/> is meaningless.</summary>
public sealed record CodingTaskProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

/// <summary>
/// Injectable process seam for <see cref="CodingTaskService"/>. Tests fake
/// this so no real git or coding CLI ever runs; production uses
/// <see cref="DefaultCodingTaskProcessRunner"/>.
/// </summary>
public interface ICodingTaskProcessRunner
{
    Task<CodingTaskProcessResult> RunAsync(CodingTaskProcessRequest request, CancellationToken ct = default);
}

/// <summary>
/// Real process runner built on the shared <see cref="ChildProcessLauncher"/>
/// (fork-safe on Apple platforms). The request timeout is an inactivity lease:
/// every output line renews it, while a genuinely silent/hung CLI is killed
/// with its entire process tree so it cannot orphan node children.
/// </summary>
public sealed class DefaultCodingTaskProcessRunner : ICodingTaskProcessRunner
{
    private const int MaxCapturedChars = 512 * 1024;

    public async Task<CodingTaskProcessResult> RunAsync(
        CodingTaskProcessRequest request, CancellationToken ct = default)
    {
        var startInfo = CreateStartInfo(request);
        using var process = ChildProcessLauncher.Start(startInfo);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        void RenewInactivityLease()
        {
            try { timeoutCts.CancelAfter(request.Timeout); }
            catch (ObjectDisposedException) { /* a final buffered line raced disposal */ }
        }

        RenewInactivityLease();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputLineReceived += line =>
        {
            if (line is null) return;
            RenewInactivityLease();
            if (stdout.Length < MaxCapturedChars) stdout.AppendLine(line);
        };
        process.ErrorLineReceived += line =>
        {
            if (line is null) return;
            RenewInactivityLease();
            if (stderr.Length < MaxCapturedChars) stderr.AppendLine(line);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(request.StandardInput);
                await process.StandardInput.FlushAsync(ct);
                RenewInactivityLease();
            }
            catch (IOException)
            {
                // The child exited before consuming stdin; its exit code tells the story.
            }
            try { process.StandardInput.Close(); } catch { /* best effort */ }
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            KillTree(process);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation: don't leave the child running headless.
            KillTree(process);
            throw;
        }

        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new CodingTaskProcessResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    private static void KillTree(IChildProcess process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        try
        {
            process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// npm on Windows commonly exposes the coding CLIs as .cmd shims, which
    /// cannot be started with UseShellExecute=false directly — bridge those
    /// through cmd.exe. Every argument here is internally generated (fixed
    /// flags plus the worktree path) and the prompt travels on stdin, so
    /// nothing user-controlled reaches this command line; the characters cmd
    /// would reinterpret are still rejected defensively.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(CodingTaskProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (request.StandardInput is not null)
        {
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        foreach (var (name, value) in request.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        var isBatchShim = OperatingSystem.IsWindows()
            && (request.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || request.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        if (!isBatchShim)
        {
            startInfo.FileName = request.FileName;
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        foreach (var value in request.Arguments.Prepend(request.FileName))
        {
            if (value.IndexOfAny(['\0', '\r', '\n', '"', '%']) >= 0)
            {
                throw new InvalidOperationException(
                    "Windows batch-shim CLI paths and arguments cannot contain quotes, percent signs, or line breaks.");
            }
        }

        startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec")
                             ?? Path.Combine(
                                 Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "cmd.exe");
        var command = new StringBuilder();
        command.Append('"').Append(request.FileName).Append('"');
        foreach (var argument in request.Arguments)
        {
            command.Append(" \"").Append(argument).Append('"');
        }

        // cmd.exe parses text after /c from the raw command line; ArgumentList
        // escaping would surface literal backslashes, so use Arguments directly.
        startInfo.Arguments = $"/d /s /c \"{command}\"";
        return startInfo;
    }
}
