namespace SwarmCode.Hosting;

/// <summary>
/// Where SwarmCode keeps its per-user data. Defaults to %LOCALAPPDATA%/SwarmCode
/// (or the platform equivalent); override with the SWARMCODE_HOME environment variable.
/// </summary>
public sealed class SwarmCodeDataPaths
{
    public const string HomeEnvironmentVariable = "SWARMCODE_HOME";

    public SwarmCodeDataPaths(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? ResolveDefaultRoot() : rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>File workspace for chats with no selected project. Never use the profile root itself.</summary>
    public string ChatWorkspaceDirectory => Path.Combine(RootDirectory, "chat-workspace");

    public bool IsChatWorkspace(string? root) => !string.IsNullOrWhiteSpace(root)
        && string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
            ChatWorkspaceDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>User configuration (Coding / CodingLanes / LlmProviders sections). Optional.</summary>
    public string UserConfigPath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>Provider settings written by /key (API keys, endpoints).</summary>
    public string SettingsPath => Path.Combine(RootDirectory, "llm-settings.json");

    /// <summary>SQLite secret vault: API keys, site passwords, and tokens, envelope-protected by the OS keystore.</summary>
    public string SecretsDbPath => Path.Combine(RootDirectory, "secrets.db");

    /// <summary>
    /// External stdio MCP servers (Claude-Desktop-compatible <c>mcpServers</c> JSON). Other apps,
    /// such as Talavine's "Use your data hub in other AI apps" card, write their entry here; native
    /// lanes mount these servers and API lanes see their tools as <c>mcp.&lt;server&gt;.&lt;tool&gt;</c>.
    /// </summary>
    public string McpServersPath => Path.Combine(RootDirectory, SwarmCore.Mcp.ExternalClients.ExternalMcpClients.SwarmCodeServersFileName);

    public string UsagePath => Path.Combine(RootDirectory, "llm-usage.json");

    public string RoutingAuditPath => Path.Combine(RootDirectory, "llm-routing-audit.json");

    public string SessionsDirectory => Path.Combine(RootDirectory, "sessions");

    /// <summary>Index of sessions and recently opened repositories (desktop app sidebar).</summary>
    public string SessionIndexPath => Path.Combine(SessionsDirectory, "index.json");

    /// <summary>Per-conversation provider transcripts so a session resumes after restart.</summary>
    public string TranscriptsDirectory => Path.Combine(SessionsDirectory, "transcripts");

    /// <summary>Per-conversation journal of tool calls between request and durable result (crash recovery).</summary>
    public string PendingToolCallsDirectory => Path.Combine(SessionsDirectory, "pending-tools");

    /// <summary>App preferences (theme, default lane, hand-off, approval mode) edited by the desktop app.</summary>
    public string PreferencesPath => Path.Combine(RootDirectory, "preferences.json");

    /// <summary>Personal memory (facts about the user, never repository content) and repository-memory proposals.</summary>
    public string MemoryDirectory => Path.Combine(RootDirectory, "memory");

    /// <summary>The user's personal memory: claims, facts, forget rules and an audit journal, rewritten atomically.</summary>
    public string PersonalMemoryPath => Path.Combine(MemoryDirectory, "personal-memory.json");
    public string ModelResponseMemoryPath => Path.Combine(MemoryDirectory, "model-responses.db");

    /// <summary>Reviewable repository-memory edit proposals (one file each); never applied without the person.</summary>
    public string RepoMemoryProposalsDirectory => Path.Combine(MemoryDirectory, "proposals");

    /// <summary>Agents that sessions started (one record each); their transcripts are ordinary sessions.</summary>
    public string AgentsDirectory => Path.Combine(SessionsDirectory, "agents");

    /// <summary>Isolated agents' git worktrees (outside every repository), one folder per repository, one per agent.</summary>
    public string WorktreesDirectory => Path.Combine(RootDirectory, "worktrees");
    /// <summary>Scheduled runs (one record each); each run is an ordinary session.</summary>
    public string SchedulesDirectory => Path.Combine(RootDirectory, "schedules");

    /// <summary>Self-improvement backlog (one record per defect or feature request); cycles are ordinary sessions.</summary>
    public string SelfImprovementDirectory => Path.Combine(RootDirectory, "self-improvement");

    /// <summary>Private immutable harness evidence; importing a report never activates its recommendation.</summary>
    public string HarnessExperimentsDirectory => Path.Combine(RootDirectory, "harness-experiments");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string LogPath => Path.Combine(LogsDirectory, "swarmcode.log");

    public static string ResolveDefaultRoot()
    {
        var env = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var current = Path.Combine(local, "SwarmCode");
        if (!OperatingSystem.IsMacOS()) return current;
        // Catalyst historically resolved LocalApplicationData to Documents.
        // Adopt its established profile in place; never replace it with an empty
        // profile created by the native runtime's different folder mapping.
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "SwarmCode");
        return SelectMacDataRoot(current, legacy);
    }

    internal static string SelectMacDataRoot(string current, string legacy)
        => HasUserData(legacy) && !HasUserData(current) ? legacy : current;

    private static bool HasUserData(string root)
    {
        if (!Directory.Exists(root)) return false;
        if (new[] { "preferences.json", "settings.json", "llm-settings.json", "mcp-servers.json", "services.db" }
            .Any(name => File.Exists(Path.Combine(root, name)))) return true;
        var sessions = Path.Combine(root, "sessions");
        if (Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, "*.json").Any(path => Path.GetFileName(path) != "index.json")) return true;
        var vault = Path.Combine(root, "secrets.db");
        if (!File.Exists(vault)) return false;
        // Empty native bootstrap databases are not user data. Open read-only:
        // a locked or damaged existing vault must fail visibly, never be
        // mistaken for an empty profile and silently switch the person's data.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = vault, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM secrets LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
}
