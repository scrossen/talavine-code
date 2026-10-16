using System.Diagnostics;
using SwarmCore.Agent.Git;

namespace SwarmCore.Testing;

/// <summary>
/// Plain git for test fixtures (repository setup and the "plain git control" that proves a fence is meaningful).
/// It starts the git the production resolver picks (<see cref="GitExecutable"/>), so a PATH whose first git cannot
/// run on this machine (an Intel-only <c>/usr/local/bin/git</c> on Apple silicon fails with "Bad CPU type in
/// executable") does not break setup before the code under test is reached. Unlike <see cref="GitInvocation"/>, it
/// adds no hardening and does not scrub the environment: controls must still run the repository's hooks, filters
/// and drivers exactly as a user's own git would.
/// </summary>
internal static class TestGit
{
    /// <summary>The resolved git executable (the same one host-owned git calls start).</summary>
    public static string Executable => GitExecutable.Resolve();

    /// <summary>True when the resolver finds a git that runs on this machine.</summary>
    public static bool Available()
    {
        try { _ = GitExecutable.Resolve(); return true; }
        catch (GitNotRunnableException) { return false; }
    }

    /// <summary>A start info for plain git in <paramref name="dir"/> with redirected output and no window.</summary>
    public static ProcessStartInfo StartInfo(string dir, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs plain git and returns its exit code and output.</summary>
    public static (int ExitCode, string Output, string Error) Run(string dir, params string[] args)
    {
        using var p = Process.Start(StartInfo(dir, args))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>Runs plain git asynchronously and returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string dir, params string[] args)
    {
        using var p = Process.Start(StartInfo(dir, args))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Runs plain git and fails the fixture with git's own message when it exits non-zero.</summary>
    public static string RunChecked(string dir, params string[] args)
    {
        var (exitCode, output, error) = Run(dir, args);
        if (exitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {exitCode}: {error.Trim()}");
        return output;
    }

    /// <summary>
    /// Initialises a fixture repository with the settings a user's global config must not change: a fixed identity,
    /// no commit signing and no line-ending conversion.
    /// </summary>
    public static void InitRepository(string dir)
    {
        RunChecked(dir, "init", "-q");
        RunChecked(dir, "config", "user.email", "test@example.invalid");
        RunChecked(dir, "config", "user.name", "Test");
        RunChecked(dir, "config", "commit.gpgsign", "false");
        RunChecked(dir, "config", "core.autocrlf", "false");
    }
}

/// <summary>
/// A fact that needs a runnable git. Without one it reports Skipped with a reason
/// instead of returning early and reporting Passed with nothing asserted.
/// </summary>
internal sealed class GitFactAttribute : Xunit.FactAttribute
{
    public GitFactAttribute()
    {
        if (!TestGit.Available()) Skip = "Requires a git executable that runs on this machine.";
    }
}
