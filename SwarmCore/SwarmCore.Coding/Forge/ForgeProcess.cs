using System.Diagnostics;
using System.Text;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Coding.Forge;

/// <summary>
/// One child process a forge operation starts (gh or git). <see cref="Environment"/> holds changes to the inherited
/// environment (a null value removes the variable). Credentials appear only in <see cref="Environment"/> or
/// <see cref="StandardInput"/>, never in <see cref="Arguments"/>.
/// </summary>
public sealed record ForgeProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout,
    string? StandardInput = null);

/// <summary>Exit code (-1: could not start, -2: timed out; <see cref="Stderr"/> explains), stdout and stderr.</summary>
public sealed record ForgeProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Starts forge child processes. Tests replace it to observe arguments and environment without running anything.</summary>
public interface IForgeProcessRunner
{
    Task<ForgeProcessResult> RunAsync(ForgeProcessSpec spec, CancellationToken ct);
}

/// <summary>Default runner over <see cref="ChildProcessLauncher"/> (posix_spawn on Apple platforms).</summary>
public sealed class ForgeProcessRunner : IForgeProcessRunner
{
    public async Task<ForgeProcessResult> RunAsync(ForgeProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (key, value) in spec.Environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        IChildProcess proc;
        try { proc = ChildProcessLauncher.Start(psi); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ForgeProcessResult(-1, "", $"{Path.GetFileName(spec.FileName)} could not be started: {e.Message}");
        }

        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                if (spec.StandardInput is { } input)
                {
                    var stdin = proc.StandardInput;
                    await stdin.WriteAsync(input).ConfigureAwait(false);
                    await stdin.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (IOException) { /* the child exited early; its exit code and stderr explain */ }
            finally { try { proc.StandardInput.Close(); } catch { /* best effort */ } }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(spec.Timeout);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (ct.IsCancellationRequested) throw;
                return new ForgeProcessResult(-2, "", $"{Path.GetFileName(spec.FileName)} timed out after {spec.Timeout.TotalSeconds:0}s.");
            }
            return new ForgeProcessResult(proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }
}
