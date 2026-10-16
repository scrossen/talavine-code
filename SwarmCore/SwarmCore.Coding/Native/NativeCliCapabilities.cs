using SwarmCore.Abstractions.Processes;
using System.Collections.Concurrent;
using System.Diagnostics;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Coding.Native;

/// <summary>
/// Which optional flags an installed coding CLI understands, read once per executable from its own
/// <c>--help</c>. Confinement flags differ by CLI version (Claude Code's <c>--restricted</c> arrived in
/// 2.1.248); the runners prefer the strongest flag the installed CLI offers and fall back to equivalent
/// explicit flags otherwise. A probe failure counts as "not supported" and is retried after
/// <see cref="FailedProbeRetryAfter"/>, so a CLI installed or upgraded later is detected.
/// </summary>
public static class NativeCliCapabilities
{
    private sealed record Entry(string? Help, DateTime ProbedUtc);

    private static readonly ConcurrentDictionary<string, Entry> HelpText = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a failed probe is remembered before the CLI is asked again.</summary>
    public static TimeSpan FailedProbeRetryAfter { get; set; } = TimeSpan.FromMinutes(5);

    // Test seams are async-local: a test's override applies only to its own flow, never to another test running
    // in parallel (a shared static override made probe counts nondeterministic under parallel load).
    private static readonly AsyncLocal<Func<string, string[], string?>?> ProbeOverrideLocal = new();
    private static readonly AsyncLocal<Func<DateTime>?> UtcNowLocal = new();

    /// <summary>Test seam (async-local): replaces the help probe (executable, help arguments) → help text.</summary>
    internal static Func<string, string[], string?>? ProbeOverride
    {
        get => ProbeOverrideLocal.Value;
        set => ProbeOverrideLocal.Value = value;
    }

    /// <summary>Test seam (async-local): the clock used to expire failed probes; null means the system clock.</summary>
    internal static Func<DateTime>? UtcNowOverride
    {
        get => UtcNowLocal.Value;
        set => UtcNowLocal.Value = value;
    }

    private static DateTime UtcNow() => UtcNowLocal.Value?.Invoke() ?? DateTime.UtcNow;

    public static bool Supports(string executable, string flag, params string[] helpArguments)
        => GetHelp(executable, helpArguments)?.Contains(flag, StringComparison.Ordinal) == true;

    /// <summary>Skip stale or broken shims; file existence alone is not executable readiness.</summary>
    internal static string? ResolveExecutable(IEnumerable<string> candidates)
        => candidates.FirstOrDefault(path => File.Exists(path) && GetHelp(path, []) is not null);

    private static string? GetHelp(string executable, string[] helpArguments)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var key = executable + " " + string.Join(' ', helpArguments);
        var entry = HelpText.GetOrAdd(key, _ => new Entry(Probe(executable, helpArguments.Length == 0 ? ["--help"] : helpArguments), UtcNow()));
        if (entry.Help is null && UtcNow() - entry.ProbedUtc >= FailedProbeRetryAfter)
        {
            var fresh = new Entry(Probe(executable, helpArguments.Length == 0 ? ["--help"] : helpArguments), UtcNow());
            HelpText.TryUpdate(key, fresh, entry);
            entry = fresh;
        }
        return entry.Help;
    }

    internal static void Reset() => HelpText.Clear();

    private static string? Probe(string executable, string[] arguments)
    {
        if (ProbeOverride is { } probe) return probe(executable, arguments);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            // The same launch path as a turn, so a shim that answers --help here also starts there.
            using var process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi));
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            if (process.ExitCode != 0) return null;
            return stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
        }
        // ArgumentException: a shim path the batch bridge cannot carry safely is not a usable executable.
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
