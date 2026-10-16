namespace SwarmCore.Mcp.ExternalClients;

public enum ExternalMcpClientPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>How a client stores its MCP server list.</summary>
public enum ExternalMcpClientConfigKind
{
    /// <summary>A JSON document with a top-level <c>mcpServers</c> object (Claude Desktop, Cursor).</summary>
    JsonMcpServers,

    /// <summary>Codex CLI's <c>config.toml</c> with <c>[mcp_servers.&lt;name&gt;]</c> tables.</summary>
    CodexToml,
}

/// <summary>A supported external AI app that can launch local stdio MCP servers.</summary>
public sealed record ExternalMcpClientDescriptor(
    string Id,
    string DisplayName,
    ExternalMcpClientConfigKind ConfigKind,
    string RestartInstruction);

/// <summary>
/// Where a client lives on this computer. <see cref="ConfigPath"/> is null only when the
/// client has no known configuration location on this platform.
/// </summary>
public sealed record ExternalMcpClientLocation(
    ExternalMcpClientDescriptor Client,
    bool IsInstalled,
    string? ConfigPath,
    bool ConfigFileExists);

/// <summary>The external clients SwarmCore knows how to register a stdio MCP server with.</summary>
public static class ExternalMcpClients
{
    public const string ClaudeDesktopId = "claude-desktop";
    public const string CursorId = "cursor";
    public const string CodexCliId = "codex-cli";
    public const string SwarmCodeId = "swarmcode";

    /// <summary>
    /// The file SwarmCode reads external stdio MCP servers from, inside its data folder
    /// (<c>SWARMCODE_HOME</c>, default <c>%LOCALAPPDATA%\SwarmCode</c> or the platform equivalent).
    /// Same <c>mcpServers</c> document as Claude Desktop.
    /// </summary>
    public const string SwarmCodeServersFileName = "mcp-servers.json";

    /// <summary>The environment variable that relocates SwarmCode's data folder.</summary>
    public const string SwarmCodeHomeVariable = "SWARMCODE_HOME";

    public static ExternalMcpClientDescriptor ClaudeDesktop { get; } = new(
        ClaudeDesktopId,
        "Claude Desktop",
        ExternalMcpClientConfigKind.JsonMcpServers,
        "Quit Claude Desktop completely (including from the system tray or menu bar), then open it again.");

    public static ExternalMcpClientDescriptor Cursor { get; } = new(
        CursorId,
        "Cursor",
        ExternalMcpClientConfigKind.JsonMcpServers,
        "Restart Cursor, or turn the server off and on again under Cursor Settings > MCP.");

    public static ExternalMcpClientDescriptor CodexCli { get; } = new(
        CodexCliId,
        "Codex CLI",
        ExternalMcpClientConfigKind.CodexToml,
        "Start a new Codex session.");

    public static ExternalMcpClientDescriptor SwarmCode { get; } = new(
        SwarmCodeId,
        "Talavine Code",
        ExternalMcpClientConfigKind.JsonMcpServers,
        "Reopen the repository in Talavine Code, or start a new swarmcode run, to load the change.");

    public static IReadOnlyList<ExternalMcpClientDescriptor> All { get; } = [ClaudeDesktop, Cursor, CodexCli, SwarmCode];

    public static ExternalMcpClientDescriptor? Find(string? id) =>
        All.FirstOrDefault(client => string.Equals(client.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The per-user folders a client locator consults. Tests substitute temporary folders;
/// production uses <see cref="FromCurrentProcess"/>.
/// </summary>
public sealed record ExternalMcpClientEnvironment
{
    public required ExternalMcpClientPlatform Platform { get; init; }

    public required string HomeDirectory { get; init; }

    /// <summary>Windows <c>%APPDATA%</c> (roaming application data).</summary>
    public string? RoamingAppDataDirectory { get; init; }

    /// <summary>Windows <c>%LOCALAPPDATA%</c>.</summary>
    public string? LocalAppDataDirectory { get; init; }

    /// <summary>Linux <c>$XDG_CONFIG_HOME</c>; defaults to <c>~/.config</c>.</summary>
    public string? XdgConfigDirectory { get; init; }

    /// <summary>Codex CLI's <c>CODEX_HOME</c> override.</summary>
    public string? CodexHome { get; init; }

    /// <summary>SwarmCode's <c>SWARMCODE_HOME</c> override; null means the platform default data folder.</summary>
    public string? SwarmCodeHome { get; init; }

    /// <summary>Linux <c>$XDG_DATA_HOME</c>; defaults to <c>~/.local/share</c>.</summary>
    public string? XdgDataDirectory { get; init; }

    /// <summary>PATH-style directory list used to detect command-line clients.</summary>
    public string? SearchPath { get; init; }

    /// <summary>Windows <c>PATHEXT</c>.</summary>
    public string? ExecutableExtensions { get; init; }

    /// <summary>macOS application folders (<c>/Applications</c>, <c>~/Applications</c>).</summary>
    public IReadOnlyList<string> ApplicationDirectories { get; init; } = [];

    public static ExternalMcpClientEnvironment FromCurrentProcess()
    {
        var platform = OperatingSystem.IsWindows()
            ? ExternalMcpClientPlatform.Windows
            : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
                ? ExternalMcpClientPlatform.MacOS
                : ExternalMcpClientPlatform.Linux;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? string.Empty;
        }

        // GUI apps on macOS inherit a minimal PATH; also look where CLI installers put tools.
        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extraDirectories = platform switch
        {
            ExternalMcpClientPlatform.Windows => new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            },
            _ => new[]
            {
                "/opt/homebrew/bin",
                "/usr/local/bin",
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".npm-global", "bin"),
            },
        };
        foreach (var directory in extraDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory))
                searchPath = string.IsNullOrEmpty(searchPath) ? directory : searchPath + Path.PathSeparator + directory;
        }

        return new ExternalMcpClientEnvironment
        {
            Platform = platform,
            HomeDirectory = home,
            RoamingAppDataDirectory = platform == ExternalMcpClientPlatform.Windows
                ? NullIfBlank(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                : null,
            LocalAppDataDirectory = platform == ExternalMcpClientPlatform.Windows
                ? NullIfBlank(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                : null,
            XdgConfigDirectory = platform == ExternalMcpClientPlatform.Linux
                ? NullIfBlank(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
                : null,
            CodexHome = NullIfBlank(Environment.GetEnvironmentVariable("CODEX_HOME")),
            SwarmCodeHome = NullIfBlank(Environment.GetEnvironmentVariable(ExternalMcpClients.SwarmCodeHomeVariable)),
            XdgDataDirectory = platform == ExternalMcpClientPlatform.Linux
                ? NullIfBlank(Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
                : null,
            SearchPath = searchPath,
            ExecutableExtensions = platform == ExternalMcpClientPlatform.Windows
                ? Environment.GetEnvironmentVariable("PATHEXT")
                : null,
            ApplicationDirectories = platform == ExternalMcpClientPlatform.MacOS
                ? ["/Applications", Path.Combine(home, "Applications")]
                : [],
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Finds supported clients and the configuration file each one reads. Detection only
/// checks for the presence of folders and executables; it never reads configuration.
/// </summary>
public sealed class ExternalMcpClientLocator
{
    private const string ClaudeConfigFileName = "claude_desktop_config.json";
    private readonly ExternalMcpClientEnvironment _environment;

    public ExternalMcpClientLocator(ExternalMcpClientEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    public IReadOnlyList<ExternalMcpClientLocation> LocateAll() =>
        ExternalMcpClients.All.Select(Locate).ToArray();

    public ExternalMcpClientLocation Locate(ExternalMcpClientDescriptor client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var (installed, configPath) = client.Id switch
        {
            ExternalMcpClients.ClaudeDesktopId => LocateClaudeDesktop(),
            ExternalMcpClients.CursorId => LocateCursor(),
            ExternalMcpClients.CodexCliId => LocateCodex(),
            ExternalMcpClients.SwarmCodeId => LocateSwarmCode(),
            _ => (false, null),
        };

        var configExists = configPath is not null && File.Exists(configPath);
        return new ExternalMcpClientLocation(client, installed || configExists, configPath, configExists);
    }

    private (bool Installed, string? ConfigPath) LocateClaudeDesktop()
    {
        var home = _environment.HomeDirectory;
        switch (_environment.Platform)
        {
            case ExternalMcpClientPlatform.Windows:
            {
                var roaming = _environment.RoamingAppDataDirectory ?? Path.Combine(home, "AppData", "Roaming");
                var local = _environment.LocalAppDataDirectory ?? Path.Combine(home, "AppData", "Local");
                var realDirectory = Path.Combine(roaming, "Claude");

                // The MSIX build of Claude Desktop can have its roaming data virtualized into
                // the package's private LocalCache. When that private folder exists the app
                // reads (and newly creates) its settings there, so that copy wins.
                var packageDirectories = SafeEnumerateDirectories(Path.Combine(local, "Packages"), "Claude_*");
                var virtualizedDirectories = packageDirectories
                    .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude"))
                    .Where(Directory.Exists)
                    .ToArray();
                var virtualizedDirectory =
                    virtualizedDirectories.FirstOrDefault(dir => File.Exists(Path.Combine(dir, ClaudeConfigFileName)))
                    ?? virtualizedDirectories.FirstOrDefault();

                var installed = Directory.Exists(realDirectory)
                                || packageDirectories.Count > 0
                                || Directory.Exists(Path.Combine(local, "AnthropicClaude"));
                return (installed, Path.Combine(virtualizedDirectory ?? realDirectory, ClaudeConfigFileName));
            }

            case ExternalMcpClientPlatform.MacOS:
            {
                var directory = Path.Combine(home, "Library", "Application Support", "Claude");
                var installed = Directory.Exists(directory) || HasMacApplication("Claude.app");
                return (installed, Path.Combine(directory, ClaudeConfigFileName));
            }

            default:
            {
                var configRoot = _environment.XdgConfigDirectory ?? Path.Combine(home, ".config");
                var directory = Path.Combine(configRoot, "Claude");
                return (Directory.Exists(directory), Path.Combine(directory, ClaudeConfigFileName));
            }
        }
    }

    private (bool Installed, string? ConfigPath) LocateCursor()
    {
        var home = _environment.HomeDirectory;
        var directory = Path.Combine(home, ".cursor");
        var installed = Directory.Exists(directory);
        if (!installed && _environment.Platform == ExternalMcpClientPlatform.Windows)
        {
            var local = _environment.LocalAppDataDirectory ?? Path.Combine(home, "AppData", "Local");
            installed = File.Exists(Path.Combine(local, "Programs", "cursor", "Cursor.exe"));
        }
        else if (!installed && _environment.Platform == ExternalMcpClientPlatform.MacOS)
        {
            installed = HasMacApplication("Cursor.app");
        }

        return (installed, Path.Combine(directory, "mcp.json"));
    }

    private (bool Installed, string? ConfigPath) LocateCodex()
    {
        var codexHome = _environment.CodexHome ?? Path.Combine(_environment.HomeDirectory, ".codex");
        var installed = Directory.Exists(codexHome) || IsOnSearchPath("codex");
        return (installed, Path.Combine(codexHome, "config.toml"));
    }

    /// <summary>
    /// SwarmCode keeps its data in <c>SWARMCODE_HOME</c> or the platform's local application data
    /// folder (the same rule as SwarmCode's own data paths). It counts as installed when that
    /// folder exists, its console is on PATH, or (on macOS) its app bundle is present.
    /// </summary>
    private (bool Installed, string? ConfigPath) LocateSwarmCode()
    {
        var home = _environment.HomeDirectory;
        var dataRoot = _environment.SwarmCodeHome ?? _environment.Platform switch
        {
            ExternalMcpClientPlatform.Windows => Path.Combine(
                _environment.LocalAppDataDirectory ?? Path.Combine(home, "AppData", "Local"), "SwarmCode"),
            ExternalMcpClientPlatform.MacOS => Path.Combine(home, "Library", "Application Support", "SwarmCode"),
            _ => Path.Combine(_environment.XdgDataDirectory ?? Path.Combine(home, ".local", "share"), "SwarmCode"),
        };

        var installed = Directory.Exists(dataRoot)
                        || IsOnSearchPath("swarmcode")
                        || (_environment.Platform == ExternalMcpClientPlatform.MacOS
                            && (HasMacApplication("Talavine Code.app") || HasMacApplication("SwarmCode.app")));
        return (installed, Path.Combine(dataRoot, ExternalMcpClients.SwarmCodeServersFileName));
    }

    private bool HasMacApplication(string bundleName) =>
        _environment.ApplicationDirectories.Any(directory => Directory.Exists(Path.Combine(directory, bundleName)));

    private bool IsOnSearchPath(string commandName)
    {
        if (string.IsNullOrWhiteSpace(_environment.SearchPath))
            return false;

        var extensions = _environment.Platform == ExternalMcpClientPlatform.Windows
            ? (_environment.ExecutableExtensions ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(".ps1")
                .ToArray()
            : [string.Empty];

        foreach (var directory in _environment.SearchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim(), commandName + extension)))
                        return true;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH segment; skip it.
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(string parent, string pattern)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, pattern).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
