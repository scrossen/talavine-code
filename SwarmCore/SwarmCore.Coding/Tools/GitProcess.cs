using SwarmCore.Abstractions.Processes;
using System.Diagnostics;
using SwarmCore.Agent.Git;

namespace SwarmCore.Coding.Tools;

/// <summary>
/// Runs the harness git tools through the shared builder (<see cref="GitInvocation"/>): the resolved git, the
/// command-line hardening, the repository's own filter/diff drivers blanked, and a scrubbed environment. A coding
/// turn can write files in its repository, so nothing the repository configures may turn a git call into a command.
/// </summary>
internal static class GitProcess
{
    /// <summary>The shared hardening (<see cref="GitInvocation.HardeningConfig"/>).</summary>
    internal static IReadOnlyList<string> HardeningConfig => GitInvocation.HardeningConfig;

    /// <summary>
    /// A hardened start info for a call that runs no filter or diff driver (rev-parse and the like). Calls that can
    /// run one go through <see cref="RunAsync"/>, which blanks the repository's drivers first.
    /// Throws <see cref="GitNotRunnableException"/> when no git can run.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string workingDirectory, IEnumerable<string> args, IReadOnlyList<string>? driverOverrides = null)
        => GitInvocation.CreateStartInfo(workingDirectory, args, driverOverrides);

    /// <summary>
    /// Runs git. ExitCode is -1 when git cannot be started (or the repository's config cannot be fenced) and -2 on
    /// timeout (Stderr explains). A command that can run a filter or diff driver first lists the repository's drivers
    /// (<see cref="GitInvocation.RepositoryDriverQuery"/>) and runs with them blanked; a failed listing fails the call.
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string workingDirectory, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        IReadOnlyList<string>? drivers = null;
        if (GitInvocation.RunsRepositoryDrivers(args))
        {
            var (failure, overrides) = await QueryDriversAsync(workingDirectory, timeout, ct).ConfigureAwait(false);
            if (failure is { } f) return f;
            drivers = overrides;
        }
        return await RunOnceAsync(workingDirectory, args, drivers, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the repository's filter and diff drivers once (<see cref="GitInvocation.RepositoryDriverQuery"/>) and returns the
    /// overrides that blank them, or the failure that must refuse the git calls. A host operation that runs several git
    /// commands in the same directory, while no turn is writing to the repository, reuses the overrides through
    /// <see cref="RunWithDriversAsync"/>.
    /// </summary>
    internal static async Task<((int ExitCode, string Stdout, string Stderr)? Failure, IReadOnlyList<string> Overrides)> QueryDriversAsync(
        string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var query = await RunOnceAsync(workingDirectory, [.. GitInvocation.RepositoryDriverQuery], null, timeout, ct).ConfigureAwait(false);
        if (query.ExitCode is not (0 or 1)) // 1: no driver configured
            return (query.ExitCode is -1 or -2
                ? query
                : (-1, "", "git could not read the repository's filter and diff driver configuration, so it was not run: " + ErrorText(query.ExitCode, query.Stdout, query.Stderr)), []);
        try { return (null, GitInvocation.RepositoryDriverOverrides(query.Stdout, workingDirectory)); }
        catch (GitConfigurationRefusedException e) { return ((-1, "", e.Message), []); }
    }

    /// <summary>
    /// Runs git with driver overrides from <see cref="QueryDriversAsync"/>. <paramref name="environment"/> is applied after
    /// the scrub, so only host-owned values (a temporary index file the host created) reach the child; <paramref name="standardInput"/>
    /// is written to git's stdin before it is closed.
    /// </summary>
    internal static Task<(int ExitCode, string Stdout, string Stderr)> RunWithDriversAsync(
        string workingDirectory, string[] args, IReadOnlyList<string> drivers, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null, string? standardInput = null)
        => RunOnceAsync(workingDirectory, args, drivers, timeout, ct, environment, standardInput);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOnceAsync(
        string workingDirectory, string[] args, IReadOnlyList<string>? drivers, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null, string? standardInput = null)
    {
        IChildProcess proc;
        try
        {
            var psi = CreateStartInfo(workingDirectory, args, drivers);
            if (environment is not null)
                foreach (var (name, value) in environment) psi.Environment[name] = value;
            if (standardInput is not null) psi.StandardInputEncoding = new System.Text.UTF8Encoding(false);
            proc = ChildProcessLauncher.Start(psi) ?? throw new InvalidOperationException("git did not start.");
        }
        catch (GitNotRunnableException e)
        {
            return (-1, "", e.Message);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return (-1, "", "git could not be started: " + e.Message);
        }

        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (standardInput is not null)
            {
                try { await proc.StandardInput.WriteAsync(standardInput).ConfigureAwait(false); }
                catch (IOException) { /* git exited early; its exit code explains */ }
            }
            try { proc.StandardInput.Close(); } catch { /* best effort */ }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (ct.IsCancellationRequested) throw;
                return (-2, "", $"git timed out after {timeout.TotalSeconds:0}s.");
            }
            return (proc.ExitCode, await stdout, await stderr);
        }
    }

    public static string FirstLine(string text)
    {
        var s = text.Trim();
        var i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }

    public static string ErrorText(int exit, string stdout, string stderr)
    {
        var e = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
        return e.Length == 0 ? $"git exited with code {exit}." : e;
    }
}
