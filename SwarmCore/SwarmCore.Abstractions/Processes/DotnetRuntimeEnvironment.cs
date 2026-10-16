using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Resolves the .NET runtime root for framework-dependent companion helpers
/// (Talavine.Backfill, Talavine.WorkerHost) spawned from the packaged
/// Mac Catalyst app. The app bundles its own runtime and its environment
/// carries no DOTNET_ROOT, so a spawned apphost falls back to
/// /etc/dotnet/install_location*, which Homebrew installs never register —
/// every helper launch then dies with "You must install .NET to run this
/// application". This class finds a usable runtime root once (env override,
/// well-known roots, then CLI-shim discovery) and exposes the DOTNET_ROOT
/// variables each launch path must hand to the child process.
/// </summary>
public static class DotnetRuntimeEnvironment
{
    private static readonly Lazy<string?> CachedRootDirectory = new(ResolveRootDirectory);

    /// <summary>The resolved runtime root, or null when none could be found
    /// (e.g. Windows, where the installer registers the location itself).</summary>
    public static string? RootDirectory => CachedRootDirectory.Value;

    /// <summary>
    /// DOTNET_ROOT-style variables a spawned helper needs. Empty when no root
    /// was resolved or the app's own environment already carries them (an
    /// explicit user override always wins).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetHelperVariables()
        => BuildHelperVariables(RootDirectory, Environment.GetEnvironmentVariable);

    /// <summary>Applies <see cref="GetHelperVariables"/> to a child process
    /// start info without clobbering anything already set there.</summary>
    public static void Apply(ProcessStartInfo startInfo)
    {
        foreach (var pair in GetHelperVariables())
        {
            if (!startInfo.Environment.TryGetValue(pair.Key, out var existing)
                || string.IsNullOrWhiteSpace(existing))
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// Makes an installed macOS SDK discoverable to an unattended child process.
    /// Menu-bar apps, launch agents, and paired-device agent bridges commonly
    /// start with a reduced PATH even though Homebrew or a user-local SDK is
    /// installed. This deliberately adds only directories that actually contain
    /// the <c>dotnet</c> executable and never replaces an existing PATH.
    /// </summary>
    public static void AddDotnetCliToPath(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst()) return;

        var directories = new List<string>();
        foreach (var candidate in EnumerateDotnetCliCandidates())
        {
            if (!File.Exists(candidate)) continue;
            var directory = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrWhiteSpace(directory) && !directories.Contains(directory, StringComparer.Ordinal))
                directories.Add(directory);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var userDotnet = Path.Combine(home, ".dotnet", "dotnet");
            if (File.Exists(userDotnet))
            {
                var directory = Path.GetDirectoryName(userDotnet)!;
                if (!directories.Contains(directory, StringComparer.Ordinal)) directories.Add(directory);
            }
        }

        if (directories.Count == 0) return;
        var existing = (startInfo.Environment.TryGetValue("PATH", out var suppliedPath)
            ? suppliedPath
            : Environment.GetEnvironmentVariable("PATH")) ?? string.Empty;
        var entries = existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var missing = directories.Where(directory => !entries.Contains(directory, StringComparer.Ordinal)).ToArray();
        if (missing.Length == 0) return;
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, missing) +
                                        (string.IsNullOrWhiteSpace(existing) ? string.Empty : Path.PathSeparator + existing);
    }

    public static IReadOnlyList<KeyValuePair<string, string>> BuildHelperVariables(
        string? rootDirectory,
        Func<string, string?> currentEnvironment)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return [];
        }

        var variables = new List<KeyValuePair<string, string>>();
        foreach (var name in GetDotnetRootVariableNames())
        {
            if (string.IsNullOrWhiteSpace(currentEnvironment(name)))
            {
                variables.Add(new KeyValuePair<string, string>(name, rootDirectory));
            }
        }

        return variables;
    }

    public static IReadOnlyList<string> GetDotnetRootVariableNames()
        => RuntimeInformation.OSArchitecture switch
        {
            // The apphost consults the arch-specific variable first.
            Architecture.Arm64 => ["DOTNET_ROOT_ARM64", "DOTNET_ROOT"],
            Architecture.X64 => ["DOTNET_ROOT_X64", "DOTNET_ROOT"],
            _ => ["DOTNET_ROOT"],
        };

    private static string? ResolveRootDirectory()
    {
        foreach (var name in GetDotnetRootVariableNames())
        {
            var existing = Environment.GetEnvironmentVariable(name);
            if (IsDotnetRootDirectory(existing))
            {
                return Path.GetFullPath(existing!);
            }
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
        {
            return null;
        }

        foreach (var candidate in EnumerateMacRootCandidates())
        {
            if (IsDotnetRootDirectory(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateMacRootCandidates()
    {
        // Stable Homebrew opt-links first (survive version upgrades), then the
        // official installer location, then a user-local install-script root.
        yield return "/opt/homebrew/opt/dotnet/libexec";
        yield return "/usr/local/opt/dotnet/libexec";
        yield return "/usr/local/share/dotnet";
        yield return "/opt/homebrew/share/dotnet";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".dotnet");
        }

        // CLI-shim discovery: wherever a `dotnet` on PATH (or a well-known bin
        // symlink) actually lives is either the root itself or a Homebrew
        // Cellar bin/ wrapper whose sibling libexec/ is the root.
        foreach (var cliPath in EnumerateDotnetCliCandidates())
        {
            var resolved = TryResolveFinalTarget(cliPath);
            var directory = Path.GetDirectoryName(resolved);
            if (string.IsNullOrEmpty(directory)) continue;

            yield return directory;

            var parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parent))
            {
                yield return Path.Combine(parent, "libexec");
            }
        }
    }

    private static IEnumerable<string> EnumerateDotnetCliCandidates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "dotnet");
            if (seen.Add(candidate) && File.Exists(candidate)) yield return candidate;
        }

        foreach (var candidate in new[]
                 {
                     "/opt/homebrew/bin/dotnet",
                     "/usr/local/bin/dotnet",
                     "/usr/bin/dotnet",
                 })
        {
            if (seen.Add(candidate) && File.Exists(candidate)) yield return candidate;
        }
    }

    private static string TryResolveFinalTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    public static bool IsDotnetRootDirectory(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            // A root the apphost can use must carry the hostfxr layout and the
            // shared frameworks; a bin/ directory holding only the CLI (or a
            // wrapper script) does not qualify.
            var dotnetExecutable = Path.Combine(
                directory,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

            return Directory.Exists(Path.Combine(directory, "host", "fxr"))
                   && Directory.Exists(Path.Combine(directory, "shared"))
                   && File.Exists(dotnetExecutable)
                   && ((!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
                       || AppleSiliconExecutable.IsCompatible(dotnetExecutable));
        }
        catch
        {
            return false;
        }
    }
}
