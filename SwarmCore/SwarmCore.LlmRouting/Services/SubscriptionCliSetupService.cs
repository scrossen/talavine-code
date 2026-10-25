using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Diagnostics;
using SwarmCore.Abstractions.Processes;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Detect, install, and authenticate the local subscription-backed LLM CLIs
/// (Claude Code / Codex CLI). Product setup surfaces use this so the user can
/// route through a subscription instead of an API key without leaving the app,
/// and so a bad install (wrong-arch binary, missing from PATH) gets a human
/// message instead of a raw "Bad CPU type in executable" stack trace.
/// </summary>
public interface ISubscriptionCliSetupService
{
    /// <summary>
    /// Probe the binary for <paramref name="providerName"/> (use the
    /// <see cref="SubscriptionCliProviders"/> constants) and report what
    /// step the user is at: missing, wrong arch, present-but-unauthenticated,
    /// or fully ready. Best-effort — never throws on detection failure.
    /// </summary>
    Task<SubscriptionCliStatus> DetectAsync(string providerName, CancellationToken ct = default);

    /// <summary>
    /// Run the canonical install path for <paramref name="providerName"/>.
    /// On macOS that's <c>npm install -g @anthropic-ai/claude-code</c> for
    /// ClaudeCode and the equivalent for Codex. Streams stdout/stderr to
    /// <paramref name="progress"/> so the dialog can render a live log.
    /// Returns the post-install status from a fresh <see cref="DetectAsync"/>.
    /// </summary>
    Task<SubscriptionCliInstallResult> InstallAsync(
        string providerName,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Spawn the CLI's supported interactive login flow. The process is
    /// detached so the user can complete terminal/browser authentication at
    /// their own pace; callers must re-probe before granting route authority.
    /// </summary>
    Task<bool> StartLoginAsync(string providerName, CancellationToken ct = default);
}

/// <summary>Stage in the install→login→ready ladder for a subscription CLI.</summary>
public enum SubscriptionCliStage
{
    /// <summary>Binary not found on PATH or in any known location.</summary>
    NotInstalled,

    /// <summary>Binary is on disk but the wrong CPU architecture for this computer
    /// (e.g. x86_64 binary on arm64). Reinstall with the matching build.</summary>
    WrongArchitecture,

    /// <summary>Binary runs but can't reach the subscription — usually means
    /// the user hasn't completed the CLI's login flow yet.</summary>
    NotAuthenticated,

    /// <summary>
    /// The ChatGPT desktop package is installed and its bundled connection was
    /// found, but this process could not yet verify a routable command session.
    /// This is deliberately distinct from <see cref="NotInstalled"/>: app
    /// discovery is positive evidence, while routing readiness remains a
    /// separate proof.
    /// </summary>
    DesktopAppDetected,

    /// <summary>Binary installed, runs, and confirms an authenticated
    /// subscription session. Chat turns through this provider should work.</summary>
    Ready,

    /// <summary>The probe could not run at all — the launch was blocked by the
    /// cross-process co-run guard, or the probe timed out or was cancelled
    /// before producing a verdict. This is NOT evidence of a signed-out
    /// session (the last observed verdict may still be "authenticated"); the
    /// next sweep simply retries. Attention surfaces must never raise a
    /// sign-in task for this stage.</summary>
    ProbeUnavailable,
}

public sealed record SubscriptionCliStatus(
    string ProviderName,
    SubscriptionCliStage Stage,
    string? ExecutablePath,
    string? Version,
    string? DetectionDetail)
{
    /// <summary>Recovery may disprove only failures predating this physical probe.</summary>
    public DateTime? ProbeStartedAtUtc { get; init; }
}

public sealed record SubscriptionCliInstallResult(
    bool Succeeded,
    string? ErrorSummary,
    SubscriptionCliStatus Status);

public sealed record SubscriptionCliProviderSpec(
    string ProviderName,
    string CommandName,
    string NpmPackage,
    IReadOnlyList<string> LoginArgs);

public static class SubscriptionCliDiscovery
{
    public static SubscriptionCliProviderSpec ResolveProviderSpec(string providerName)
    {
        if (ProviderPackCatalog.TryGet(providerName, out var providerPack)
            && providerPack!.SubscriptionCli is { } cli)
        {
            return new SubscriptionCliProviderSpec(
                providerPack.ProviderName,
                cli.Command,
                cli.NpmPackage,
                cli.LoginArguments);
        }
        throw new ArgumentException($"Unknown subscription CLI provider: {providerName}", nameof(providerName));
    }

    public static string? ResolveExecutable(
        string providerName,
        SubscriptionCliExecutableOptions? options = null)
        => ResolveExecutableCandidates(providerName, options).FirstOrDefault();

    /// <summary>
    /// Returns every plausible executable in deterministic preference order.
    /// A path being present does not prove that the current process can launch it
    /// (WindowsApps aliases are a common counterexample), so runtime callers must
    /// probe candidates rather than treating the first file as authoritative.
    /// </summary>
    public static IReadOnlyList<string> ResolveExecutableCandidates(
        string providerName,
        SubscriptionCliExecutableOptions? options = null)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var candidates = new List<string>();
        var seen = new HashSet<string>(comparer);

        void AddPath(string path)
        {
            if (seen.Add(path) && IsRunnableOnCurrentArchitecture(path))
            {
                candidates.Add(path);
            }
        }

        void AddCandidate(string? pathOrCommand)
        {
            if (string.IsNullOrWhiteSpace(pathOrCommand))
            {
                return;
            }

            var expanded = Environment.ExpandEnvironmentVariables(pathOrCommand);
            if (Path.IsPathFullyQualified(expanded))
            {
                if (File.Exists(expanded))
                {
                    AddPath(expanded);
                }

                return;
            }

            foreach (var discovered in FindExecutableCandidates(expanded))
            {
                AddPath(discovered);
            }
        }

        if (options is not null)
        {
            foreach (var env in options.EnvironmentVariables)
            {
                AddCandidate(Environment.GetEnvironmentVariable(env));
            }

            AddCandidate(options.ExecutablePath);
        }

        var spec = ResolveProviderSpec(providerName);
        foreach (var discovered in FindExecutableCandidates(spec.CommandName))
        {
            AddPath(discovered);
        }

        return candidates;
    }

    public static string? FindExecutableForProvider(string providerName)
    {
        var spec = ResolveProviderSpec(providerName);
        var candidates = FindExecutableCandidates(spec.CommandName);
        // The MSIX package's internal CLI is visible to File.Exists and PATH,
        // but a normal desktop child process is not allowed to execute it.
        // Prefer a real CLI/shim whenever one is installed; retain the package
        // candidate only as desktop-install evidence for setup surfaces.
        return candidates.FirstOrDefault(candidate => !IsProtectedDesktopPackageCli(candidate));
    }

    /// <summary>
    /// Finds a different installed executable after a CLI started successfully
    /// but proved too old for the requested provider/model protocol. Unlike the
    /// ordinary stable discovery order, recovery prefers the freshest viable
    /// installation and never returns the executable that just failed.
    /// </summary>
    public static string? FindReplacementExecutableForProvider(
        string providerName,
        string? failedExecutable)
    {
        var spec = ResolveProviderSpec(providerName);
        return SelectFreshestReplacementExecutable(
            FindExecutableCandidates(spec.CommandName)
                .Where(candidate => !IsProtectedDesktopPackageCli(candidate)),
            failedExecutable);
    }

    public static string? SelectFreshestReplacementExecutable(
        IEnumerable<string> candidates,
        string? failedExecutable)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var failed = NormalizeExecutablePath(failedExecutable);

        return candidates
            .Select((candidate, index) => new
            {
                Path = candidate,
                Normalized = NormalizeExecutablePath(candidate),
                Freshness = ExecutableFreshnessUtc(candidate),
                Index = index,
            })
            .Where(candidate => string.IsNullOrWhiteSpace(failed)
                                || !string.Equals(candidate.Normalized, failed, comparison))
            .OrderByDescending(candidate => candidate.Freshness)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    public static bool LooksLikeCodexCliUpgradeRequired(string? diagnostic)
        => !string.IsNullOrWhiteSpace(diagnostic)
           && (diagnostic.Contains("requires a newer version of Codex", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("upgrade to the latest app or CLI", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim().Trim('"');
        try { return Path.IsPathFullyQualified(trimmed) ? Path.GetFullPath(trimmed) : trimmed; }
        catch { return trimmed; }
    }

    private static DateTime ExecutableFreshnessUtc(string path)
    {
        try
        {
            var freshness = File.GetLastWriteTimeUtc(path);
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null)
            {
                var targetFreshness = target.LastWriteTimeUtc;
                if (targetFreshness > freshness) freshness = targetFreshness;
            }
            return freshness;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Windows exposes the private executable inside the Microsoft Store
    /// package through PATH, although ordinary desktop processes receive
    /// Access Denied when they try to start it. It is not a headless Codex CLI.
    /// Detection is intentionally host-independent because a synced or migrated
    /// profile can contain a Windows path while it is being inspected on macOS.
    /// </summary>
    public static bool IsProtectedDesktopPackageCli(string? executablePath)
        => !string.IsNullOrWhiteSpace(executablePath)
           && executablePath.Replace('/', '\\').Contains(
               "\\WindowsApps\\OpenAI.Codex_",
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies the bundled command companion inside the current Windows
    /// ChatGPT desktop package. The package retained an <c>OpenAI.Codex</c>
    /// family name after the product was folded into ChatGPT, so this is an
    /// installation-discovery detail only; it must never leak into product UI.
    /// </summary>
    public static bool IsChatGptDesktopCompanion(string providerName, string? executablePath)
        => providerName.Equals(SubscriptionCliProviders.CodexCli, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(executablePath)
           && executablePath.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;

    public static IReadOnlyList<string> FindExecutableCandidates(string command)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var candidates = new List<string>();
        var seen = new HashSet<string>(comparer);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (seen.Add(expanded)
                && File.Exists(expanded)
                && IsRunnableOnCurrentArchitecture(expanded))
            {
                candidates.Add(expanded);
            }
        }

        foreach (var path in KnownPathCandidates(command)) Add(path);
        foreach (var path in RunningProcessCandidates(command)) Add(path);
        foreach (var path in PathEnvironmentCandidates(command)) Add(path);

        return candidates;
    }

    private static bool IsRunnableOnCurrentArchitecture(string path)
        => (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
           || AppleSiliconExecutable.IsCompatible(path);

    public static string? ReresolveLatest(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)
            || (File.Exists(storedPath) && !IsProtectedDesktopPackageCli(storedPath))) return null;
        if (!Path.IsPathRooted(storedPath)) return null;

        // Claude Code's macOS installer can retain a stable, versioned shim at
        // ~/.local/bin/claude-code-latest while an older SwarmSpan profile still
        // points at the conventional `claude` command. Heal that very common
        // transition before attempting the platform-specific version folders.
        var storedFileName = Path.GetFileName(storedPath);
        if (storedFileName.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || storedFileName.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
        {
            var storedDirectory = Path.GetDirectoryName(storedPath);
            if (!string.IsNullOrWhiteSpace(storedDirectory))
            {
                var latestShim = Path.Combine(storedDirectory, "claude-code-latest");
                if (File.Exists(latestShim))
                {
                    return latestShim;
                }
            }
        }

        var normalized = storedPath.Replace('/', '\\');

        var claudeMarker = "\\Claude\\claude-code\\";
        var claudeIdx = normalized.IndexOf(claudeMarker, StringComparison.OrdinalIgnoreCase);
        if (claudeIdx >= 0)
        {
            var baseDir = storedPath.Substring(0, claudeIdx + claudeMarker.Length - 1);
            return NewestVersioned(baseDir, "claude.exe", _ => true);
        }

        var extMarker = "\\.vscode\\extensions\\";
        var extIdx = normalized.IndexOf(extMarker, StringComparison.OrdinalIgnoreCase);
        if (extIdx >= 0)
        {
            var extensions = storedPath.Substring(0, extIdx + extMarker.Length - 1);

            if (normalized.Contains("openai.chatgpt-", StringComparison.OrdinalIgnoreCase))
            {
                return NewestVersioned(
                    extensions,
                    Path.Combine("bin", "windows-x86_64", "codex.exe"),
                    name => name.StartsWith("openai.chatgpt-", StringComparison.OrdinalIgnoreCase));
            }

            if (normalized.Contains("anthropic.claude-code-", StringComparison.OrdinalIgnoreCase))
            {
                return NewestVersioned(
                    extensions,
                    Path.Combine("resources", "native-binary", "claude.exe"),
                    name => name.StartsWith("anthropic.claude-code-", StringComparison.OrdinalIgnoreCase));
            }
        }

        // A saved absolute application or Homebrew path can go stale after a
        // reinstall or a switch between the desktop and standalone CLI. Once
        // the more precise version-folder repairs above have had priority, use
        // the complete discovery sequence for a bare `codex` or `claude`
        // command. This includes ~/.local/bin on macOS/Linux and known app
        // locations on Windows.
        var providerName = storedFileName.Equals("codex", StringComparison.OrdinalIgnoreCase)
                           || storedFileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
            ? SubscriptionCliProviders.CodexCli
            : storedFileName.Equals("claude", StringComparison.OrdinalIgnoreCase)
              || storedFileName.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
                ? SubscriptionCliProviders.ClaudeCode
                : null;
        if (providerName is not null)
        {
            var resolved = FindExecutableForProvider(providerName);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    public static string? NewestVersioned(
        string baseDir,
        string executableRelativePath,
        Func<string, bool> nameFilter)
    {
        if (!Directory.Exists(baseDir)) return null;

        string? best = null;
        var bestKey = -1L;
        foreach (var dir in Directory.EnumerateDirectories(baseDir))
        {
            var name = Path.GetFileName(dir);
            if (!nameFilter(name)) continue;
            var match = SemVer.Match(name);
            if (!match.Success) continue;
            var executable = Path.Combine(dir, executableRelativePath);
            if (!File.Exists(executable)) continue;

            var key = long.Parse(match.Groups[1].Value) * 1_000_000_000_000L
                      + long.Parse(match.Groups[2].Value) * 1_000_000L
                      + long.Parse(match.Groups[3].Value);
            if (key > bestKey)
            {
                bestKey = key;
                best = executable;
            }
        }

        return best;
    }

    private static readonly System.Text.RegularExpressions.Regex SemVer = new(@"(\d+)\.(\d+)\.(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Codex Desktop updates its usable headless runtime in a content-addressed
    /// child directory before the stable bin/codex.exe launcher is necessarily
    /// refreshed. Prefer the freshest child runtime so newly published models
    /// do not get sent through a stale CLI that cannot decode the current model
    /// catalog. Keep the stable launcher as a fallback for older installs.
    /// </summary>
    internal static IEnumerable<string> CodexDesktopRuntimeCandidates(string localAppData)
    {
        var binDirectory = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        var versionedRuntime = FindNewestFileUnderMatchingDirectories(
            binDirectory,
            "*",
            "codex.exe");
        if (!string.IsNullOrWhiteSpace(versionedRuntime))
            yield return versionedRuntime;

        yield return Path.Combine(binDirectory, "codex.exe");
    }

    private static IEnumerable<string> PathEnvironmentCandidates(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) yield break;
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) yield break;

        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                yield return Path.Combine(dir.Trim(), command + ext);
            }
        }
    }

    private static IEnumerable<string> KnownPathCandidates(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            foreach (var candidate in WindowsCommandCandidates(appData, "npm", command)) yield return candidate;
            foreach (var candidate in WindowsCommandCandidates(localAppData, "npm", command)) yield return candidate;
            foreach (var candidate in WindowsCommandCandidates(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "nodejs",
                         command))
                yield return candidate;
            foreach (var candidate in WindowsCommandCandidates(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "nodejs",
                         command))
                yield return candidate;

            // The Windows installer ships a private Node runtime specifically
            // so a clean machine can connect a subscription CLI without a
            // separate Node install. npm installs global command shims beside
            // that runtime, so resolve both npm and Codex from the same trusted
            // app-owned directory after setup has added the package.
            foreach (var candidate in WindowsCommandCandidates(
                         AppContext.BaseDirectory,
                         "Tools",
                         "Node",
                         command))
                yield return candidate;
            foreach (var candidate in WindowsCommandCandidates(userProfile, ".volta", "bin", command)) yield return candidate;
            foreach (var candidate in WindowsCommandCandidates(userProfile, "scoop", "shims", command)) yield return candidate;
            foreach (var candidate in WindowsCommandCandidates(programData, "chocolatey", "bin", command)) yield return candidate;

            if (command.Equals("codex", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(localAppData))
            {
                foreach (var candidate in CodexDesktopRuntimeCandidates(localAppData))
                    yield return candidate;
            }

            if (command.Equals("claude", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(appData))
            {
                var appScoped = NewestVersioned(
                    Path.Combine(appData, "Claude", "claude-code"),
                    "claude.exe",
                    _ => true);
                if (!string.IsNullOrWhiteSpace(appScoped)) yield return appScoped;
            }

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                if (command.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    var vscode = NewestVersioned(
                        Path.Combine(userProfile, ".vscode", "extensions"),
                        Path.Combine("bin", "windows-x86_64", "codex.exe"),
                        name => name.StartsWith("openai.chatgpt-", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(vscode)) yield return vscode;
                }

                if (command.Equals("claude", StringComparison.OrdinalIgnoreCase))
                {
                    var vscode = NewestVersioned(
                        Path.Combine(userProfile, ".vscode", "extensions"),
                        Path.Combine("resources", "native-binary", "claude.exe"),
                        name => name.StartsWith("anthropic.claude-code-", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(vscode)) yield return vscode;
                }
            }

            yield break;
        }

        // Mac Catalyst reports IsMacOS() == false; without the explicit
        // Catalyst check the packaged app enumerates no CLI paths at all.
        if (!OperatingSystem.IsMacOS()
            && !OperatingSystem.IsMacCatalyst()
            && !OperatingSystem.IsLinux())
        {
            yield break;
        }
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var homeRelativeCandidates = new[]
        {
            Path.Combine(home, ".npm-global", "bin", command),
            Path.Combine(home, ".local", "bin", command),
            Path.Combine(home, ".local", "share", "pnpm", command),
            Path.Combine(home, ".volta", "bin", command),
            Path.Combine(home, ".asdf", "shims", command),
            Path.Combine(home, ".local", "share", "mise", "shims", command),
            Path.Combine(home, ".nodenv", "shims", command),
            Path.Combine(home, ".nvm", "versions", "node", "current", "bin", command)
        };

        // Prefer the user-installed CLI before package-manager and legacy app
        // locations. ChatGPT is the desktop surface; SwarmSpan's headless work
        // needs the actual Codex CLI executable, which is commonly installed
        // at ~/.local/bin/codex. Claude Code's local installer similarly uses
        // the stable claude-code-latest shim rather than `claude`.
        if (command.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(home, ".local", "bin", "claude-code-latest");
        }

        foreach (var candidate in homeRelativeCandidates) yield return candidate;

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            // /usr/local is not inherently Intel (several native installers
            // use it on Apple Silicon), so retain it and let the architecture
            // filter reject only an actual x86-only executable.
            yield return "/opt/homebrew/bin/" + command;
            yield return "/usr/local/bin/" + command;
            yield return "/opt/local/bin/" + command;

            if (command.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidate in MacCodexApplicationBundleCandidates(
                             home,
                             "/Applications"))
                {
                    yield return candidate;
                }

                var architectureDirectory = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "macos-aarch64"
                    : "macos-x86_64";
                foreach (var candidate in EditorExtensionBinaryCandidates(
                             home,
                             "openai.chatgpt-*",
                             Path.Combine("bin", architectureDirectory, "codex")))
                {
                    yield return candidate;
                }
            }

            if (command.Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidate in ClaudeDesktopCodeBinaryCandidates(home))
                {
                    yield return candidate;
                }

                foreach (var candidate in EditorExtensionBinaryCandidates(
                             home,
                             "anthropic.claude-code-*",
                             Path.Combine("resources", "native-binary", "claude")))
                {
                    yield return candidate;
                }
            }
        }
        else
        {
            yield return "/usr/local/bin/" + command;
            yield return "/usr/bin/" + command;
            yield return "/bin/" + command;
        }

        var nvmRoot = Path.Combine(home, ".nvm", "versions", "node");
        if (Directory.Exists(nvmRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(nvmRoot).OrderByDescending(Path.GetFileName))
            {
                yield return Path.Combine(dir, "bin", command);
            }
        }
    }

    /// <summary>
    /// Claude Desktop's managed Claude Code installs, newest version first.
    /// Older Desktop builds used <c>claude-code/&lt;version&gt;/claude.app</c>;
    /// current ones add a build directory,
    /// <c>claude-code/&lt;version&gt;/&lt;build&gt;/claude.app</c>. Both are accepted.
    /// </summary>
    internal static IEnumerable<string> ClaudeDesktopCodeBinaryCandidates(string home)
    {
        if (string.IsNullOrWhiteSpace(home)) yield break;

        var root = Path.Combine(home, "Library", "Application Support", "Claude", "claude-code");
        if (!Directory.Exists(root)) yield break;

        List<string> candidates;
        try
        {
            candidates = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(VersionKey)
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .SelectMany(versionDirectory => Directory
                    .EnumerateDirectories(versionDirectory, "*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Prepend(versionDirectory))
                .Select(directory => Path.Combine(directory, "claude.app", "Contents", "MacOS", "claude"))
                .Where(File.Exists)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }

        static Version VersionKey(string directory)
            => Version.TryParse(Path.GetFileName(directory), out var version) ? version : new Version(0, 0);
    }

    /// <summary>
    /// Finds the Codex runtime in both the current ChatGPT desktop bundle and
    /// the retired standalone Codex bundle. Keep the root injectable so the
    /// complete bundle-layout contract is provider-free testable on every OS.
    /// </summary>
    public static IEnumerable<string> MacCodexApplicationBundleCandidates(
        string home,
        string systemApplicationsRoot)
    {
        foreach (var applicationsRoot in new[]
                 {
                     systemApplicationsRoot,
                     string.IsNullOrWhiteSpace(home)
                         ? string.Empty
                         : Path.Combine(home, "Applications"),
                 }.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            // ChatGPT is the current desktop product and owns the bundled Codex
            // runtime. Retain the historical Codex.app location for machines
            // that have not yet migrated.
            yield return Path.Combine(
                applicationsRoot,
                "ChatGPT.app",
                "Contents",
                "Resources",
                "codex");
            yield return Path.Combine(
                applicationsRoot,
                "Codex.app",
                "Contents",
                "Resources",
                "codex");

            if (!Directory.Exists(applicationsRoot))
            {
                continue;
            }

            IEnumerable<string> bundles;
            try
            {
                bundles = Directory
                    .EnumerateDirectories(applicationsRoot, "ChatGPT*.app", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateDirectories(
                        applicationsRoot,
                        "Codex*.app",
                        SearchOption.TopDirectoryOnly))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var bundle in bundles)
            {
                yield return Path.Combine(bundle, "Contents", "Resources", "codex");
            }
        }
    }

    public static IEnumerable<string> EditorExtensionBinaryCandidates(
        string home,
        string extensionDirectoryPattern,
        string executableRelativePath)
    {
        if (string.IsNullOrWhiteSpace(home)) yield break;

        foreach (var root in new[]
                 {
                     Path.Combine(home, ".vscode", "extensions"),
                     Path.Combine(home, ".vscode-insiders", "extensions"),
                     Path.Combine(home, ".cursor", "extensions"),
                     Path.Combine(home, ".windsurf", "extensions"),
                 })
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> directories;
            try
            {
                directories = Directory
                    .EnumerateDirectories(root, extensionDirectoryPattern, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, executableRelativePath);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> WindowsCommandCandidates(string root, params string[] relativeParts)
    {
        if (string.IsNullOrWhiteSpace(root) || relativeParts.Length == 0) yield break;

        var command = relativeParts[^1];
        var basePath = relativeParts.Length == 1
            ? root
            : relativeParts[..^1].Aggregate(root, Path.Combine);

        yield return Path.Combine(basePath, command + ".cmd");
        yield return Path.Combine(basePath, command + ".exe");
        yield return Path.Combine(basePath, command + ".bat");
    }

    private static IEnumerable<string> RunningProcessCandidates(string command)
    {
        foreach (var candidate in ProcessCommandLineCandidates(command))
        {
            yield return candidate;
        }

        // Windows discovery already checks PATH and every supported install
        // location. Inspecting Process properties races with short-lived CLI
        // probes and reports normal exits as first-chance ArgumentExceptions.
        if (OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (!ProcessNameMatchesCommand(processName, command))
                {
                    continue;
                }

                string? fileName;
                try
                {
                    fileName = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    yield return fileName;
                }
            }
        }
    }

    private static IEnumerable<string> ProcessCommandLineCandidates(string command)
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst())
        {
            // Catalyst avoids this optional /bin/ps child. Known paths, PATH,
            // and Process APIs still provide candidates without adding
            // ungated posix_spawn churn to the interpreted UI process.
            return candidates;
        }

        var psPath = File.Exists("/bin/ps")
            ? "/bin/ps"
            : File.Exists("/usr/bin/ps")
                ? "/usr/bin/ps"
                : null;
        if (psPath is null)
        {
            return candidates;
        }

        var psi = new ProcessStartInfo
        {
            FileName = psPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-axo");
        psi.ArgumentList.Add("comm=,args=");

        try
        {
            using var proc = ChildProcessLauncher.Start(psi);

            return CaptureProcessCommandLineCandidates(proc, command, TimeSpan.FromMilliseconds(1500));
        }
        catch
        {
            // best-effort process inspection only
        }

        return candidates;
    }

    internal static IReadOnlyList<string> CaptureProcessCommandLineCandidates(
        IChildProcess proc,
        string command,
        TimeSpan timeout)
    {
        var candidates = new List<string>();
        var deadline = new CancellationTokenSource(timeout);
        var token = deadline.Token;
        // Schedule both drains before waiting for exit. A full redirected pipe
        // must not prevent this optional discovery child from completing.
        var stdout = Task.Run(() => DefaultSubscriptionCliProcessRunner.ReadBoundedToEndAsync(
            proc.StandardOutput,
            DefaultSubscriptionCliProcessRunner.DefaultStandardOutputCaptureLimitChars,
            drainBeyondLimit: true,
            token));
        var stderr = Task.Run(() => DefaultSubscriptionCliProcessRunner.ReadBoundedToEndAsync(
            proc.StandardError,
            captureLimitChars: 1,
            drainBeyondLimit: true,
            token));
        var exited = Task.Run(() => proc.WaitForExitAsync(token));
        var completed = Task.WhenAll(stdout, stderr, exited);
        try
        {
            // EOF shares the exit deadline: a descendant may retain a pipe
            // after the inspected root exits. Never synchronously read to EOF.
            completed.WaitAsync(token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            var capture = stdout.GetAwaiter().GetResult();
            if (capture.WasTruncated) return candidates;

            using var reader = new StringReader(capture.Text);
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                foreach (var candidate in ExtractCommandPathsFromProcessLine(line, command))
                {
                    candidates.Add(candidate);
                }
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            candidates.Clear();
            deadline.Cancel();
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort, owned child only */ }
            try { proc.WaitForExit(TimeSpan.FromMilliseconds(100)); } catch { /* bounded reap attempt */ }
        }
        finally
        {
            if (completed.IsCompleted)
            {
                _ = completed.Exception;
                deadline.Dispose();
            }
            else
            {
                // Keep cancellation alive until detached reads/exit settle and
                // observe late faults without an unbounded teardown await.
                _ = completed.ContinueWith(task =>
                {
                    _ = task.Exception;
                    deadline.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return candidates;
    }

    private static IEnumerable<string> ExtractCommandPathsFromProcessLine(string line, string command)
    {
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = token.Trim('"', '\'');
            if (trimmed.IndexOf(Path.DirectorySeparatorChar) < 0)
            {
                continue;
            }

            if (ProcessNameMatchesCommand(Path.GetFileName(trimmed), command) && File.Exists(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static bool ProcessNameMatchesCommand(string processName, string command)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var normalizedProcessName = Path.GetFileNameWithoutExtension(processName);
        var normalizedCommand = Path.GetFileNameWithoutExtension(command);
        return normalizedProcessName.Equals(normalizedCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindNewestFileUnderMatchingDirectories(
        string root,
        string directoryPattern,
        params string[] relativePathParts)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.EnumerateDirectories(root, directoryPattern, SearchOption.TopDirectoryOnly)
                .Select(dir => relativePathParts.Aggregate(dir, Path.Combine))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

public sealed class SubscriptionCliSetupService : ISubscriptionCliSetupService
{
    // Process timeouts are deliberately short for detection (we don't want a
    // hung binary to freeze the chat) and longer for install (npm pulling
    // packages over slow links is normal). All are cancellable via the ct.
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger<SubscriptionCliSetupService> _logger;
    private readonly IOptions<SubscriptionCliProviderOptions> _options;
    private readonly ICrashBreadcrumbSink _crashBreadcrumbs;
    private readonly Func<(bool IsAuthenticated, string Detail)> _claudeSubscriptionSessionProbe;

    public SubscriptionCliSetupService(
        ILogger<SubscriptionCliSetupService> logger,
        IOptions<SubscriptionCliProviderOptions> options,
        ICrashBreadcrumbSink? crashBreadcrumbs = null,
        Func<(bool IsAuthenticated, string Detail)>? claudeSubscriptionSessionProbe = null)
    {
        _logger = logger;
        _options = options;
        _crashBreadcrumbs = crashBreadcrumbs ?? NullCrashBreadcrumbSink.Instance;
        _claudeSubscriptionSessionProbe = claudeSubscriptionSessionProbe
            ?? (() => Providers.ClaudeCodeAuthProbe.Probe());
    }

    public async Task<SubscriptionCliStatus> DetectAsync(string providerName, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var status = await DetectCoreAsync(providerName, ct).ConfigureAwait(false);
        return status with { ProbeStartedAtUtc = startedAt };
    }

    private async Task<SubscriptionCliStatus> DetectCoreAsync(string providerName, CancellationToken ct)
    {
        var candidatePaths = SubscriptionCliDiscovery.ResolveExecutableCandidates(
            providerName,
            GetExecutableOptions(providerName));

        if (candidatePaths.Count == 0)
        {
            return new SubscriptionCliStatus(providerName, SubscriptionCliStage.NotInstalled, null, null, null);
        }

        SubscriptionCliStatus? wrongArchitecture = null;
        SubscriptionCliStatus? notAuthenticated = null;
        SubscriptionCliStatus? desktopAppDetected = null;
        SubscriptionCliStatus? probeUnavailable = null;
        SubscriptionCliStatus? firstFailure = null;

        // Run `<cli> --version` for every plausible path. Desktop apps often
        // inherit a narrower or differently ordered PATH than Terminal, and
        // old Intel npm shims can sit beside the correct Apple Silicon install.
        // Keep probing until a runnable binary wins.
        foreach (var path in candidatePaths)
        {
            var status = await ProbeExecutableAsync(providerName, path, ct);
            if (status.Stage == SubscriptionCliStage.Ready)
            {
                return status;
            }

            if (status.Stage == SubscriptionCliStage.NotAuthenticated)
            {
                notAuthenticated ??= status;
            }
            else if (status.Stage == SubscriptionCliStage.DesktopAppDetected)
            {
                desktopAppDetected ??= status;
            }
            else if (status.Stage == SubscriptionCliStage.WrongArchitecture)
            {
                wrongArchitecture ??= status;
            }
            else if (status.Stage == SubscriptionCliStage.ProbeUnavailable)
            {
                probeUnavailable ??= status;
            }

            firstFailure ??= status;
        }

        // A blocked/timed-out probe outranks a generic failure verdict: when
        // launch infrastructure is unavailable, no candidate produced evidence
        // and the honest answer is "no verdict this sweep", not NotInstalled.
        return notAuthenticated
               ?? wrongArchitecture
               ?? desktopAppDetected
               ?? probeUnavailable
               ?? firstFailure
               ?? new SubscriptionCliStatus(providerName, SubscriptionCliStage.NotInstalled, null, null, null);
    }

    internal async Task<SubscriptionCliStatus> ProbeExecutableAsync(
        string providerName,
        string path,
        CancellationToken ct)
    {
        // Run `<cli> --version` to validate the executable, then a provider-
        // specific login-status command before reporting Ready. A version string
        // proves installation, not an authenticated subscription route.
        // Outcomes we care about:
        //   1. Runnable binary + verified subscription session → Ready.
        //   2. "Bad CPU type" / "exec format" in stderr → WrongArchitecture.
        //   3. Anything else → NotInstalled fallback so the user gets the
        //      install button instead of a mystery state.
        var probe = await RunProcessAsync(
            path,
            ["--version"],
            DetectionTimeout,
            ct,
            providerName: providerName,
            operation: "version-probe");

        if (probe.LaunchInfrastructureBlocked)
        {
            // The version probe never ran (co-run gate, timeout, or cancel):
            // there is no verdict about this binary at all, so neither
            // NotInstalled nor NotAuthenticated would be honest.
            return ProbeUnavailableStatus(providerName, path, version: null);
        }

        if (probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StdOut))
        {
            var version = probe.StdOut.Trim();
            var executableOptions = GetExecutableOptions(providerName);
            if (providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase)
                && executableOptions.PreferLocalSubscriptionAuthProof)
            {
                var claudeProbe = await Task.Run(_claudeSubscriptionSessionProbe, ct).ConfigureAwait(false);
                // A refresh-token-only session falls through to the CLI's own
                // status command, the boundary that can actually renew it.
                if (!Providers.ClaudeCodeAuthProbe.IsRenewalPending(claudeProbe.Detail))
                {
                    return new SubscriptionCliStatus(
                        providerName,
                        claudeProbe.IsAuthenticated
                            ? SubscriptionCliStage.Ready
                            : SubscriptionCliStage.NotAuthenticated,
                        path,
                        version,
                        claudeProbe.IsAuthenticated ? null : claudeProbe.Detail);
                }
            }

            var statusArguments = SubscriptionCliLoginProbe.GetLoginStatusArguments(providerName);
            if (statusArguments is null)
            {
                // Gemini has no status command; its auth state is on disk.
                if (providerName.Equals(SubscriptionCliProviders.GeminiCli, StringComparison.OrdinalIgnoreCase))
                {
                    var geminiProbe = Providers.GeminiCliAuthProbe.Probe();
                    return new SubscriptionCliStatus(
                        providerName,
                        geminiProbe.IsAuthenticated
                            ? SubscriptionCliStage.Ready
                            : SubscriptionCliStage.NotAuthenticated,
                        path,
                        version,
                        geminiProbe.IsAuthenticated ? null : geminiProbe.Detail);
                }

                return new SubscriptionCliStatus(
                    providerName,
                    SubscriptionCliStage.NotAuthenticated,
                    path,
                    version,
                    "CLI is installed, but this version does not expose a supported non-interactive subscription login-status check.");
            }

            var loginStatus = await RunProcessAsync(
                path,
                statusArguments,
                DetectionTimeout,
                ct,
                providerName: providerName,
                operation: "login-status");
            var loginOutcome = SubscriptionCliLoginProbe.Classify(
                providerName,
                loginStatus.ExitCode,
                loginStatus.StdOut,
                loginStatus.StdErr,
                loginStatus.LaunchInfrastructureBlocked);
            if (loginOutcome == SubscriptionCliLoginProbeStatus.InfrastructureBlocked)
            {
                // Mirror the runtime provider's anyProbeCompleted principle:
                // NotAuthenticated only when a login-status probe genuinely
                // completed. A co-run gate block or a killed hang during the
                // 6-hourly sweep must not read as a signed-out session — that
                // filed authoritative 14-day "Sign in" tasks for signed-in
                // users.
                return ProbeUnavailableStatus(providerName, path, version);
            }

            if (loginOutcome == SubscriptionCliLoginProbeStatus.CompletedAuthenticated)
            {
                return new SubscriptionCliStatus(
                    providerName,
                    SubscriptionCliStage.Ready,
                    path,
                    version,
                    null);
            }

            if (providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase)
                && !SubscriptionCliLoginProbe.IsRecognizedStatusOutput(
                    providerName,
                    loginStatus.StdOut,
                    loginStatus.StdErr))
            {
                // Claude Code 2.1.38 lacks `auth status`. If the completed
                // command did not emit a recognizable auth verdict, fall back
                // to local OAuth metadata rather than mistaking the model's
                // response to the words "auth status" for a signed-out state.
                var claudeProbe = await Task.Run(_claudeSubscriptionSessionProbe, ct).ConfigureAwait(false);
                return new SubscriptionCliStatus(
                    providerName,
                    claudeProbe.IsAuthenticated
                        ? SubscriptionCliStage.Ready
                        : SubscriptionCliStage.NotAuthenticated,
                    path,
                    version,
                    claudeProbe.IsAuthenticated ? null : claudeProbe.Detail);
            }

            // A COMPLETED login-status probe with a non-zero exit (or exit 0
            // without an authenticated session) keeps its historic
            // NotAuthenticated mapping: genuinely logged-out CLIs commonly
            // exit non-zero from their status commands, and losing that
            // mapping would hide real sign-in problems.
            // Login-status output may include account identifiers. Do not put
            // raw stdout/stderr in setup state, logs, or UI.
            return new SubscriptionCliStatus(
                providerName,
                SubscriptionCliStage.NotAuthenticated,
                path,
                version,
                "CLI is installed but no authenticated subscription session is ready.");
        }

        var combined = (probe.StdErr ?? "") + "\n" + (probe.StdOut ?? "");
        if (LooksLikeWrongArchitecture(combined))
        {
            return new SubscriptionCliStatus(
                providerName,
                SubscriptionCliStage.WrongArchitecture,
                path,
                null,
                $"Binary at {path} is the wrong CPU type for this computer ({RuntimeInformation.OSArchitecture}).");
        }

        if (LooksLikeAuthFailure(combined))
        {
            return new SubscriptionCliStatus(
                providerName,
                SubscriptionCliStage.NotAuthenticated,
                path,
                null,
                "CLI is installed but reports it isn't logged in.");
        }

        if (SubscriptionCliDiscovery.IsChatGptDesktopCompanion(providerName, path))
        {
            return new SubscriptionCliStatus(
                providerName,
                SubscriptionCliStage.DesktopAppDetected,
                path,
                null,
                "ChatGPT desktop is installed, but its protected package cannot provide a CLI connection directly. Select Connect to add a private ChatGPT CLI for this app, then sign in once.");
        }

        return new SubscriptionCliStatus(
            providerName,
            SubscriptionCliStage.NotInstalled,
            path,
            null,
            string.IsNullOrWhiteSpace(combined) ? null : combined.Trim());
    }

    private static SubscriptionCliStatus ProbeUnavailableStatus(
        string providerName,
        string path,
        string? version)
        => new(
            providerName,
            SubscriptionCliStage.ProbeUnavailable,
            path,
            version,
            "The subscription CLI probe could not run (the CLI is in use by another process, or the probe timed out). No login verdict; it will be re-checked on the next sweep.");

    // Login-status arguments and the authenticated-session parser live in the
    // shared SubscriptionCliLoginProbe — one implementation for the runtime
    // provider stack and this setup/detection stack.

    public async Task<SubscriptionCliInstallResult> InstallAsync(
        string providerName,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var spec = SubscriptionCliDiscovery.ResolveProviderSpec(providerName);

        // npm is the canonical install path for both Anthropic's claude-code
        // and OpenAI's codex CLIs. If npm isn't on PATH, we can't proceed
        // automatically — surface a clear "install Node.js first" message so
        // the user knows exactly what's blocking them.
        var npm = await FindRunnableExecutableAsync("npm", ct, providerName);
        if (npm is null)
        {
            progress?.Report("npm not found. Install Node.js from https://nodejs.org first, then try again.");
            var status = await DetectAsync(providerName, ct);
            return new SubscriptionCliInstallResult(
                false,
                "Node.js / npm is required to install this CLI. Install Node.js, then retry.",
                status);
        }

        progress?.Report($"Running: npm install -g {spec.NpmPackage}");
        var install = await RunProcessAsync(
            npm,
            ["install", "-g", spec.NpmPackage],
            InstallTimeout,
            ct,
            line => progress?.Report(line),
            providerName,
            operation: "install",
            foreground: true);

        if (install.ExitCode != 0)
        {
            progress?.Report($"npm exited with code {install.ExitCode}.");
            var status = await DetectAsync(providerName, ct);
            return new SubscriptionCliInstallResult(
                false,
                Truncate(install.StdErr ?? install.StdOut, 600) ?? $"npm install failed with exit code {install.ExitCode}.",
                status);
        }

        progress?.Report("Install complete. Probing binary…");
        var fresh = await DetectAsync(providerName, ct);
        return new SubscriptionCliInstallResult(
            fresh.Stage is SubscriptionCliStage.Ready or SubscriptionCliStage.NotAuthenticated,
            null,
            fresh);
    }

    public async Task<bool> StartLoginAsync(string providerName, CancellationToken ct = default)
    {
        var spec = SubscriptionCliDiscovery.ResolveProviderSpec(providerName);
        var path = await FindRunnableExecutableAsync(
            SubscriptionCliDiscovery.ResolveExecutableCandidates(
                providerName,
                GetExecutableOptions(providerName)),
            ct,
            providerName);
        if (path is null) return false;

        try
        {
            var supportsBrowserLogin = await SupportsBrowserLoginAsync(providerName, path, spec.LoginArgs, ct);
            if ((OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
                && SubscriptionCliLoginLauncher.RequiresInteractiveTerminal(providerName, supportsBrowserLogin))
            {
                // Older Claude versions and Gemini require terminal input;
                // Codex keeps its browser callback/error state visible.
                var interactiveArguments = SubscriptionCliLoginLauncher
                    .GetInteractiveLoginArguments(providerName, spec.LoginArgs);
                var terminalProcess = ChildProcessLauncher.Start(
                    SubscriptionCliLoginLauncher.CreateMacOsLoginStartInfo(path, interactiveArguments));
                try
                {
                    await Task.Delay(100, ct); // allow osascript to ask Terminal for a new session
                    return !terminalProcess.HasExited || terminalProcess.ExitCode == 0;
                }
                finally
                {
                    ChildProcessReaper.Track(terminalProcess, $"{providerName} login launcher", _logger);
                }
            }

            // The login subcommand prints a URL + opens the browser itself.
            // We launch it detached so the parent app stays responsive while
            // the user completes the OAuth dance in their browser.
            var psi = SubscriptionCliProcessStartInfo.Create(
                path, spec.LoginArgs, privateCliDataRoot: _options.Value.PrivateCliDataRoot);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            SubscriptionCliEnvironment.ScrubApiCredentialEnvironment(psi.Environment);

            var proc = ChildProcessLauncher.Start(psi);
            try
            {
                await Task.Delay(50, ct); // give it a moment to spawn
                return !proc.HasExited || proc.ExitCode == 0;
            }
            finally
            {
                ChildProcessReaper.Track(proc, $"{providerName} login", _logger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to spawn login flow for {Provider}", providerName);
            return false;
        }
    }

    internal async Task<bool> SupportsBrowserLoginAsync(
        string providerName,
        string executablePath,
        IReadOnlyList<string> loginArguments,
        CancellationToken ct)
    {
        if (!providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase))
            return false;

        var help = await RunProcessAsync(
            executablePath,
            [.. loginArguments, "--help"],
            DetectionTimeout,
            ct,
            providerName: providerName,
            operation: "login-capability-probe",
            foreground: true);
        ct.ThrowIfCancellationRequested();
        return help.ExitCode == 0
               && !help.LaunchInfrastructureBlocked
               && SubscriptionCliLoginLauncher.AdvertisesLoginCommand(help.StdOut, loginArguments);
    }

    private static bool LooksLikeWrongArchitecture(string text)
    {
        return text.Contains("Bad CPU type", StringComparison.OrdinalIgnoreCase)
               || text.Contains("exec format error", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not a valid Win32 application", StringComparison.OrdinalIgnoreCase)
               || text.Contains("mach-o, but built for", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Apple Silicon requires an arm64 executable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAuthFailure(string text)
    {
        return text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
               || text.Contains("please log in", StringComparison.OrdinalIgnoreCase)
               || text.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
               || text.Contains("run `claude /login`", StringComparison.OrdinalIgnoreCase)
               || text.Contains("run /auth", StringComparison.OrdinalIgnoreCase)
               || text.Contains("login with google", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    // ─────────────────────────── path resolution ───────────────────────────

    private async Task<string?> FindRunnableExecutableAsync(
        string command,
        CancellationToken ct,
        string? providerName = null)
        => await FindRunnableExecutableAsync(
            SubscriptionCliDiscovery.FindExecutableCandidates(command),
            ct,
            providerName);

    private async Task<string?> FindRunnableExecutableAsync(
        IReadOnlyList<string> candidates,
        CancellationToken ct,
        string? providerName = null)
    {
        foreach (var candidate in candidates)
        {
            var probe = await RunProcessAsync(
                candidate,
                ["--version"],
                DetectionTimeout,
                ct,
                providerName: providerName,
                operation: "executable-probe",
                foreground: true);
            if (probe.ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private SubscriptionCliExecutableOptions GetExecutableOptions(string providerName)
    {
        if (providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase))
            return _options.Value.ClaudeCode;
        if (providerName.Equals(SubscriptionCliProviders.CodexCli, StringComparison.OrdinalIgnoreCase))
            return _options.Value.CodexCli;
        if (providerName.Equals(SubscriptionCliProviders.GeminiCli, StringComparison.OrdinalIgnoreCase))
            return _options.Value.GeminiCli;
        throw new ArgumentException($"Unknown subscription CLI provider: {providerName}", nameof(providerName));
    }

    // ─────────────────────────── process runner ───────────────────────────

    /// <summary>
    /// Result of one probe/install child process. <paramref name="LaunchInfrastructureBlocked"/>
    /// distinguishes "the process never produced a verdict" (co-run gate
    /// blocked the launch, or the process timed out / was cancelled before
    /// exiting) from a genuinely completed run — both historically shared
    /// ExitCode -1, which made a blocked 6-hourly sweep read as a signed-out
    /// CLI and file a false 14-day "Sign in" human task.
    /// </summary>
    internal sealed record ProcessResult(
        int ExitCode,
        string StdOut,
        string StdErr,
        bool LaunchInfrastructureBlocked = false);

    /// <summary>
    /// Test seam: when set, replaces the real child-process launch entirely so
    /// probe-outcome mapping (including <see cref="ProcessResult.LaunchInfrastructureBlocked"/>)
    /// can be exercised without spawning processes.
    /// </summary>
    internal Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>>?
        ProcessRunnerOverride { get; set; }

    internal async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onLine = null,
        string? providerName = null,
        string operation = "executable-probe",
        bool foreground = false)
    {
        if (ProcessRunnerOverride is { } overrideRunner)
        {
            return await overrideRunner(fileName, arguments, timeout, ct).ConfigureAwait(false);
        }

        var psi = SubscriptionCliProcessStartInfo.Create(
            fileName, arguments, privateCliDataRoot: _options.Value.PrivateCliDataRoot);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        SubscriptionCliEnvironment.ScrubApiCredentialEnvironment(psi.Environment);

        IChildProcess? proc = null;
        IDisposable? executionLease = null;
        var serialized = SubscriptionCliProcessExecutionPolicy.RequiresSerialization();
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        try
        {
            if (serialized)
            {
                WriteBreadcrumb(providerName, operation, "gate-wait");
                executionLease = await SubscriptionCliProcessExecutionPolicy.SharedGate
                    .AcquireAsync(foreground, ct)
                    .ConfigureAwait(false);
                WriteBreadcrumb(providerName, operation, "gate-acquired");
            }

            // Detection and installation are bounded app-owned operations. On
            // Unix the durable marker lets the next app process reap an exact
            // orphan after a crash; on Windows the kill-on-close job handles
            // the same boundary immediately. The explicitly user-driven login
            // flow above remains intentionally detached. Containment keys are
            // provider-scoped (plus a transition sweep of the legacy shared
            // key) so a live child of one CLI never blocks setup work for a
            // different CLI.
            var workloadKey = SubscriptionCliProcessExecutionPolicy
                .GetLifetimeWorkloadKey(providerName);
            var sweep = await SubscriptionCliProcessExecutionPolicy
                .ReapOrphansWithLegacyTransitionAsync(
                    workloadKey,
                    SubscriptionCliProcessExecutionPolicy.LegacyLifetimeWorkloadKey,
                    _logger,
                    ct)
                .ConfigureAwait(false);
            if (!sweep.Succeeded
                || (serialized && sweep.LiveOwnerMarkers > 0))
            {
                return new ProcessResult(
                    -1,
                    stdout.ToString(),
                    (stderr.ToString()
                     + "\nA previous subscription CLI setup process could not be safely reconciled.")
                    .Trim(),
                    LaunchInfrastructureBlocked: true);
            }

            WriteBreadcrumb(providerName, operation, "process-starting");
            proc = ChildProcessLauncher.Start(psi);
            using var diagnosticsProcess = proc.AttachDiagnosticsProcess();
            using var containment = diagnosticsProcess is null
                ? null
                : ChildProcessLifetimeContainment.Attach(
                    diagnosticsProcess,
                    workloadKey,
                    _logger);
            WriteBreadcrumb(
                providerName,
                operation,
                "process-started",
                childProcessId: proc.Id);
            proc.OutputLineReceived += line =>
            {
                if (line is null) return;
                stdout.AppendLine(line);
                onLine?.Invoke(line);
            };
            proc.ErrorLineReceived += line =>
            {
                if (line is null) return;
                stderr.AppendLine(line);
                onLine?.Invoke(line);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
            combined.CancelAfter(timeout);

            await proc.WaitForExitAsync(combined.Token);
            WriteBreadcrumb(
                providerName,
                operation,
                "process-complete",
                outcome: "exited",
                childProcessId: proc.Id);
            return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException ex)
        {
            if (proc is not null)
            {
                WriteBreadcrumb(
                    providerName,
                    operation,
                    "process-cancel-reap-start",
                    outcome: "cancelled",
                    childProcessId: proc.Id);
                await ChildProcessReaper.KillProcessTreeAndWaitAsync(proc, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                WriteBreadcrumb(
                    providerName,
                    operation,
                    "process-cancel-kill-wait-complete",
                    outcome: "cancelled",
                    childProcessId: proc.Id);
            }
            else
            {
                WriteBreadcrumb(
                    providerName,
                    operation,
                    serialized && executionLease is null
                        ? "gate-wait-cancelled"
                        : "process-cancelled-before-start",
                    outcome: "cancelled",
                    failureType: ex.GetType().Name);
            }

            return new ProcessResult(
                -1,
                stdout.ToString(),
                (stderr.ToString() + "\nProcess timed out or was cancelled.").Trim(),
                LaunchInfrastructureBlocked: true);
        }
        catch (Exception ex)
        {
            WriteBreadcrumb(
                providerName,
                operation,
                "process-failure",
                outcome: "failed",
                failureType: ex.GetType().Name,
                childProcessId: proc?.Id);
            return new ProcessResult(-1, stdout.ToString(), (stderr.ToString() + "\n" + ex.Message).Trim());
        }
        finally
        {
            try
            {
                proc?.Dispose();
            }
            finally
            {
                executionLease?.Dispose();
                if (serialized && executionLease is not null)
                {
                    WriteBreadcrumb(providerName, operation, "gate-released");
                }
            }
        }
    }

    private void WriteBreadcrumb(
        string? providerName,
        string operation,
        string stage,
        string? outcome = null,
        string? failureType = null,
        int? childProcessId = null)
    {
        try
        {
            _crashBreadcrumbs.Write(new CrashBreadcrumbEvent
            {
                Component = nameof(SubscriptionCliSetupService),
                Operation = operation,
                Stage = stage,
                Provider = providerName,
                Outcome = outcome,
                FailureType = failureType,
                ChildProcessId = childProcessId
            });
        }
        catch
        {
            // Diagnostics must never alter setup behavior or strand the shared
            // Catalyst execution lease.
        }
    }
}
