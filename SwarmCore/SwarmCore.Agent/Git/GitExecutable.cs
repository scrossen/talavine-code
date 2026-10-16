using SwarmCore.Abstractions.Processes;
using System.ComponentModel;
using System.Diagnostics;

namespace SwarmCore.Agent.Git;

/// <summary>No git candidate could be started and answer <c>git --version</c>.</summary>
public sealed class GitNotRunnableException(string message) : InvalidOperationException(message);

/// <summary>
/// The git executable every host-owned git call starts (the coding harness tools, the coding-task engine and the
/// verified-mission workspace service; see <see cref="GitInvocation"/>), resolved once per process:
/// <list type="number">
/// <item>a configured path: <c>Coding:GitExecutable</c> (<see cref="Configure"/>), then the
/// <see cref="EnvironmentVariable"/> environment variable. A configured path must be absolute and name an existing
/// file; otherwise it is reported and skipped without being started;</item>
/// <item><c>git</c> from PATH;</item>
/// <item>on macOS, <c>/usr/bin/git</c> then <c>/opt/homebrew/bin/git</c> (an Intel-only <c>/usr/local/bin/git</c>
/// first on PATH fails on Apple silicon with "Bad CPU type in executable").</item>
/// </list>
/// The first candidate whose <c>--version</c> starts and exits 0 is kept for the process (until the configured
/// path changes). When none does, a <see cref="GitNotRunnableException"/> names each candidate and why it failed;
/// that failure is remembered for <see cref="FailureRetryInterval"/> so a broken machine does not re-probe every
/// candidate on every call, and a later fix (git installed, path configured) is picked up after it.
/// </summary>
public static class GitExecutable
{
    public const string EnvironmentVariable = "SWARMCODE_GIT";

    /// <summary>How long a failed resolution is reused before the candidates are probed again.</summary>
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(2);

    private static readonly GitExecutableResolver Shared = new(GitExecutableResolver.Probe, File.Exists, () => DateTimeOffset.UtcNow);

    /// <summary>Sets the configured path (host options). A change drops the cached resolution and any cached failure.</summary>
    public static void Configure(string? path) => Shared.Configure(path);

    /// <summary>The git to start. Throws <see cref="GitNotRunnableException"/> when no candidate runs.</summary>
    public static string Resolve() => Shared.Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable), OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst());
}

/// <summary>The resolution rules behind <see cref="GitExecutable"/>, with the probe, file check and clock injectable.</summary>
internal sealed class GitExecutableResolver(Func<string, string?> probe, Func<string, bool> fileExists, Func<DateTimeOffset> clock)
{
    private readonly object _gate = new();
    private string? _configured;
    private string? _resolved;
    private GitNotRunnableException? _failure;
    private DateTimeOffset _failedAt;

    public int ProbeCount { get; private set; }

    public void Configure(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        lock (_gate)
        {
            if (string.Equals(_configured, value, StringComparison.Ordinal)) return;
            _configured = value;
            _resolved = null;
            _failure = null;
        }
    }

    public string Resolve(string? environment, bool isMacOS)
    {
        lock (_gate)
        {
            if (_resolved is not null) return _resolved;
            if (_failure is not null && clock() - _failedAt < GitExecutable.FailureRetryInterval) throw _failure;

            var failures = new List<string>();
            var candidates = new List<string>();
            void Add(string candidate)
            {
                if (!candidates.Contains(candidate, StringComparer.Ordinal)) candidates.Add(candidate);
            }
            foreach (var (value, source) in new[] { (_configured, "Coding:GitExecutable"), (environment, GitExecutable.EnvironmentVariable) })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var path = value.Trim();
                if (ConfiguredPathProblem(path) is { } problem) failures.Add($"{path} ({source}): {problem}");
                else Add(path);
            }
            Add("git");
            if (isMacOS)
            {
                Add("/usr/bin/git");
                Add("/opt/homebrew/bin/git");
            }

            foreach (var candidate in candidates)
            {
                ProbeCount++;
                if (probe(candidate) is not { } failure)
                {
                    _failure = null;
                    return _resolved = candidate;
                }
                failures.Add(candidate + ": " + failure);
            }
            _failure = new GitNotRunnableException(
                "git is not runnable. Tried " + string.Join("; ", failures)
                + $". Install git or set Coding:GitExecutable (or {GitExecutable.EnvironmentVariable}) to the absolute path of a git that runs on this machine.");
            _failedAt = clock();
            throw _failure;
        }
    }

    /// <summary>Why a configured path is not tried: it must be absolute and name an existing file.</summary>
    internal string? ConfiguredPathProblem(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return "a configured git must be an absolute path";
        if (!fileExists(path)) return "no file exists at that path";
        return null;
    }

    /// <summary>Null when <c>&lt;candidate&gt; --version</c> starts and exits 0; otherwise why not.</summary>
    internal static string? Probe(string candidate)
    {
        try
        {
            var psi = new ProcessStartInfo(candidate)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            GitInvocation.ScrubEnvironment(psi.Environment);
            using var process = ChildProcessLauncher.Start(psi);
            if (process is null) return "did not start";
            try { process.StandardInput.Close(); } catch { /* best effort */ }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return "timed out";
            }
            if (process.ExitCode == 0) return null;
            var text = (stderr.Result.Length > 0 ? stderr.Result : stdout.Result).Trim();
            var line = text.Split(['\r', '\n'], 2)[0];
            return $"exited with code {process.ExitCode}" + (line.Length > 0 ? " (" + line + ")" : "");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return ex.Message;
        }
    }
}
