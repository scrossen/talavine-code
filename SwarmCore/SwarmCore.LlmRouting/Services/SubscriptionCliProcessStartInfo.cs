using System.Diagnostics;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Builds a direct child-process command line for subscription CLIs. Windows
/// npm installs commonly expose .cmd/.bat wrappers, which cannot be started
/// directly with UseShellExecute=false; they run through the shared
/// <see cref="WindowsBatchLaunch"/> bridge, so user-controlled model/path text
/// cannot become command syntax and is not inherited by the provider process.
/// </summary>
internal static class SubscriptionCliProcessStartInfo
{
    internal static ProcessStartInfo Create(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? childEnvironmentSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        // Codex resolves its codex-code-mode-host companion beside its launch
        // path, so a convenient ~/.local/bin/codex symlink must be traded for
        // the versioned runtime binary it points at before launch.
        fileName = CodexCliLaunchPath.Resolve(fileName) ?? fileName;

        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        if (childEnvironmentSnapshot is null)
        {
            ConfigureBundledNodePath(startInfo, fileName);
            ConfigurePrivateWindowsChatGptCliHome(startInfo, fileName);
        }
        if (!WindowsBatchLaunch.IsBatchFile(fileName))
        {
            startInfo.FileName = fileName;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ApplyExplicitEnvironment(startInfo, childEnvironmentSnapshot);
            return startInfo;
        }

        if (childEnvironmentSnapshot is not null)
            throw new UnauthorizedAccessException("An admitted launch must use the direct executable, not a command shim.");
        WindowsBatchLaunch.Configure(startInfo, fileName, arguments);
        return startInfo;
    }

    private static void ApplyExplicitEnvironment(ProcessStartInfo info,
        IReadOnlyDictionary<string, string?>? snapshot)
    {
        if (snapshot is null) return;
        info.Environment.Clear();
        foreach (var (name, value) in snapshot) info.Environment[name] = value;
    }

    /// <summary>
    /// Keeps Talavine's Windows-installed ChatGPT CLI configuration separate
    /// from an operator's general-purpose Codex configuration. The Windows
    /// setup flow installs and authenticates a private CLI, and an older
    /// desktop-package runtime can otherwise reject settings written by a newer
    /// sibling app before it reaches <c>login</c>.
    ///
    /// macOS is deliberately excluded. The current ChatGPT bundle and the
    /// standalone Codex CLI both use the user's normal <c>~/.codex</c> session.
    /// Overriding <c>CODEX_HOME</c> there makes a healthy ChatGPT subscription
    /// look signed out after executable discovery has already succeeded.
    /// </summary>
    private static void ConfigurePrivateWindowsChatGptCliHome(
        ProcessStartInfo startInfo,
        string fileName)
    {
        if (!OperatingSystem.IsWindows()
            || !Path.GetFileNameWithoutExtension(fileName)
                .Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Only our bundled npm installation owns this private session. A
        // discovered desktop/PATH CLI must retain its existing login home.
        var bundledRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Tools", "Node"))
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(fileName).StartsWith(bundledRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return;
        }

        var cliHome = Path.Combine(localApplicationData, "Talavine", "subscription-cli", "chatgpt");
        Directory.CreateDirectory(cliHome);
        startInfo.Environment["CODEX_HOME"] = cliHome;
    }

    /// <summary>
    /// npm's Windows command shims call <c>node</c> by name. The signed
    /// installer carries a private Node runtime beside those shims, so make
    /// that sibling directory available to the child only; a clean customer
    /// machine never needs a machine-wide Node installation.
    /// </summary>
    private static void ConfigureBundledNodePath(ProcessStartInfo startInfo, string fileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.GetDirectoryName(fileName);
        if (string.IsNullOrWhiteSpace(directory)
            || !File.Exists(Path.Combine(directory, "node.exe")))
        {
            return;
        }

        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        inheritedPath ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathEntries = inheritedPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathEntries.Any(entry => string.Equals(entry, directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
            ? directory
            : directory + Path.PathSeparator + inheritedPath;
    }
}
