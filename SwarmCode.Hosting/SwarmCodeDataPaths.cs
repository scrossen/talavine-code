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

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string LogPath => Path.Combine(LogsDirectory, "swarmcode.log");

    public static string ResolveDefaultRoot()
    {
        var env = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(local, "SwarmCode");
    }
}
