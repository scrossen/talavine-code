using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding;

/// <summary>Host configuration for the coding harness.</summary>
public sealed class CodingOptions
{
    public CodingFallbackDiscoveryOptions FallbackDiscovery { get; set; } = new();
    /// <summary>
    /// When a turn carries no workspace, may <see cref="Prompting.RepoInstructionsInjector"/> fall back to the
    /// host's <see cref="ICodingWorkspaceResolver"/> for the conversation? True (SwarmCode); a host that runs
    /// ordinary turns in bound conversations (Talavine) sets false so only workspace turns get the header.
    /// </summary>
    public bool InstructionsFromWorkspaceResolver { get; set; } = true;

    public const string SectionName = "Coding";

    /// <summary>
    /// Optional absolute path of the git executable for every host-owned git call (<c>Coding:GitExecutable</c>; the <c>SWARMCODE_GIT</c>
    /// environment variable is the next candidate). Unset: <c>git</c> on PATH, then on macOS the system and
    /// Homebrew gits (<see cref="SwarmCore.Agent.Git.GitExecutable"/>).
    /// </summary>
    public string? GitExecutable { get; set; }

    /// <summary>Repository root the harness works in. Also becomes the sandbox root.</summary>
    public string RepositoryRoot { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>Display name of the coding profile.</summary>
    public string ProfileName { get; set; } = "Talavine Code";

    /// <summary>Stable profile identity used by conversation and session stores.</summary>
    public Guid ProfileId { get; set; } = Guid.Parse("5c0d1a6e-0000-4000-8000-5c0d1a6e0001");

    /// <summary>Extra instructions appended after the built-in coding prompt.</summary>
    public string? AdditionalInstructions { get; set; }

    /// <summary>Repository instruction files discovered from the root upward and injected into the prompt.</summary>
    public List<string> InstructionFileNames { get; set; } = ["AGENTS.md", "CLAUDE.md"];

    /// <summary>Maximum characters of instruction files injected per turn.</summary>
    public int MaxInstructionCharacters { get; set; } = 24_000;

    /// <summary>Tool ids offered to the model. Empty means every tool the registrar published.</summary>
    public List<string> EnabledToolIds { get; set; } = [];

    /// <summary>Maximum engine iterations per turn (unattended turns only; user-requested turns are unbounded).</summary>
    public int MaxIterations { get; set; } = 60;

    /// <summary>Hard wall-clock limit per turn (unattended turns only).</summary>
    public TimeSpan HardTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many times the same tool may run with identical arguments and fail before the
    /// harness refuses the next identical call and tells the model to change approach.
    /// </summary>
    public int RepeatedCallLimit { get; set; } = 3;

    /// <summary>
    /// How many refused identical calls in one lane turn count as "stuck", at which point the
    /// orchestrator stops that lane and hands the request to the next one.
    /// </summary>
    public int StuckAfterBlockedCalls { get; set; } = 2;

    /// <summary>Optional host attachment budgets. Zero values leave imports unlimited.</summary>
    public Files.CodingAttachmentOptions AttachmentLimits { get; set; } = new();

    /// <summary>Settings for lanes that run an external coding agent (Claude Code, Codex) natively.</summary>
    public NativeAgentOptions NativeAgents { get; set; } = new();

    /// <summary>Explicit limits for attended and unattended work, shared across native and API lanes. Zero/infinite means unlimited.</summary>
    public CodingExecutionLimits ExecutionLimits { get; set; } = new();

    /// <summary>Host-configured acceptance commands. An empty contract means completion is unverified.</summary>
    public Routing.CodingAcceptanceOptions Acceptance { get; set; } = new();

    /// <summary>Maximum durable transcript characters supplied to each native launch, including resumed sessions.</summary>
    public int MaxNativeHandoffCharacters { get; set; } = 64_000;

    /// <summary>
    /// Further context injectors (by <see cref="SwarmCore.Agent.Runtime.IContextInjector.Name"/>) the single coding profile of
    /// <see cref="Prompting.CodingInjectorListProvider"/> runs after the repository instructions, for example
    /// <c>repoMemory</c> or <c>personal-memory</c>. Unknown names are skipped.
    /// </summary>
    public List<string> AdditionalInjectorNames { get; set; } = [];

    /// <summary>
    /// Context injectors whose output native CLI lanes receive in their prompt (they do not run the engine's injectors).
    /// Opt-in per host: each block is framed as reference data, bounded by <see cref="MaxNativeHostContextCharacters"/>,
    /// and on a resumed CLI session only blocks that changed since the session last saw them are re-sent.
    /// </summary>
    public List<string> NativeLaneInjectorNames { get; set; } = [];

    /// <summary>Total characters of <see cref="NativeLaneInjectorNames"/> output given to one native launch.</summary>
    public int MaxNativeHostContextCharacters { get; set; } = 10_000;

    /// <summary>Repository memory (MEMORY.md index, memory/*.md topics, reviewable edit proposals).</summary>
    public Memory.RepoMemoryOptions RepoMemory { get; set; } = new();

    /// <summary>
    /// The person's allow and deny rules (<c>Coding:Permissions:Allow</c> / <c>Deny</c>, tool-id globs). A host combines
    /// them with the person's mode into <see cref="Routing.CodingTurnRequest.Permissions"/>; deny rules also withhold
    /// read-only tools from <see cref="CodingProfileResolver"/>'s profile.
    /// </summary>
    public Permissions.CodingPermissionRules Permissions { get; set; } = new();

    /// <summary>
    /// The person's own extension folder (<c>commands/</c>, <c>skills/</c>) beside the repository's <c>.swarmcode/</c> and
    /// <c>.claude/</c> folders. Set by the host (SwarmCode: its data directory); null means repository extensions only.
    /// </summary>
    public string? UserExtensionsDirectory { get; set; }

    /// <summary>User-level hooks (<c>Coding:Hooks</c>); repository hooks come from <c>.swarmcode/hooks.json</c> after approval.</summary>
    public Extensions.CodingHooksOptions Hooks { get; set; } = new();

    /// <summary>Per-turn working-tree checkpoints that let a host undo one turn's file edits (<see cref="Checkpoints.TurnCheckpointService"/>).</summary>
    public Checkpoints.TurnCheckpointOptions Checkpoints { get; set; } = new();
}

/// <summary>Limits selected by the user/host, never inferred from repository instructions or a model answer.</summary>
public sealed record CodingExecutionLimits
{
    public TimeSpan Timeout { get; init; } = System.Threading.Timeout.InfiniteTimeSpan;
    public int MaxToolCalls { get; init; }
    /// <summary>Per-lane-attempt model iterations (API engine and Claude native). Codex does not expose comparable rounds.</summary>
    public int MaxIterations { get; init; }
    public long MaxCumulativeTokens { get; init; }
}

/// <summary>How native coding-agent CLIs are launched and what they are given.</summary>
public sealed class NativeAgentOptions
{
    /// <summary>
    /// The MCP server that bridges SwarmCode's vault into a native agent: <c>credentials_list</c>
    /// (names only) and <c>run_with_secrets</c> (a shell command whose <c>{{secret:name}}</c>
    /// references are injected at launch and scrubbed from the output). Null command means no bridge.
    /// The host sets this to its own executable (for example <c>swarmcode --mcp-server</c>).
    /// </summary>
    public McpServerLaunch? McpServer { get; set; }

    /// <summary>
    /// Set by a host that looked for its vault bridge and found none: an unconfined native lane with no configured
    /// <see cref="McpServer"/> shows this text as a <c>CodingLaneNotice</c> once per workspace (per process), so the
    /// person learns that the native agent has no access to stored credentials. Null means nothing to report.
    /// </summary>
    public string? McpServerUnavailableReason { get; set; }

    /// <summary>
    /// Further stdio MCP servers the user configured for this host (for example a Talavine data
    /// hub). Native agents receive them beside the bridge under their own names, exactly as
    /// launched: no repository argument and no host environment is added. A server named like
    /// the bridge, or whose name is not a plain identifier (letters, digits, '_' and '-'), is
    /// skipped. Mounting a server grants no authority; the agent's own permission rules apply.
    /// </summary>
    public List<McpServerLaunch> ExternalMcpServers { get; set; } = [];

    /// <summary>
    /// Whether an unconfined Codex run may also use the user's own Codex app connectors (Codex's built-in
    /// "apps" feature, which connects services the user linked in Codex/ChatGPT and answers from their live
    /// data). Off by default: the run passes <c>-c features.apps=false</c>. Additional MCP servers from Codex's
    /// own user configuration can still load in an unconfined run. A confined run never gets app connectors,
    /// whatever this says, and separately excludes user configuration and MCP servers.
    /// </summary>
    public bool AllowCodexAppConnectors { get; set; }

    /// <summary>
    /// Whether an unconfined Claude Code run may also load the connectors the user linked on claude.ai (Gmail,
    /// Drive, Calendar and so on), which Claude Code otherwise loads automatically and answers from with live
    /// data. Off by default, matching <see cref="AllowCodexAppConnectors"/>: the run sets
    /// <c>ENABLE_CLAUDEAI_MCP_SERVERS=false</c>. MCP servers from Claude Code's own configuration and the servers
    /// this host mounts are unaffected. A confined run never gets them, whatever this says.
    /// </summary>
    public bool AllowClaudeAiConnectors { get; set; }

    /// <summary>
    /// Codex sandbox policy: "bypass" (default; no approvals, no OS sandbox, like Claude Code with
    /// permissions bypassed) or "workspace-write" (no approvals, Codex's own workspace sandbox).
    /// </summary>
    public string CodexSandbox { get; set; } = "bypass";

    /// <summary>
    /// How native agents run when a request names no containment. SwarmCode keeps
    /// <see cref="NativeLaneContainment.Bypass"/>; a governed host (Talavine) configures
    /// <see cref="NativeLaneContainment.WorkspaceConfined"/>. A "workspace-write" <see cref="CodexSandbox"/>
    /// also confines Codex.
    /// </summary>
    public NativeLaneContainment DefaultContainment { get; set; } = NativeLaneContainment.Bypass;
}

/// <summary>How much a native coding agent may do without the host's approval broker.</summary>
public enum NativeLaneContainment
{
    /// <summary>Permissions and the CLI's sandbox bypassed (SwarmCode's direct-user policy).</summary>
    Bypass = 0,

    /// <summary>
    /// File edits only inside the workspace, no unapproved commands outside it: Claude Code
    /// <c>--permission-mode acceptEdits --add-dir &lt;workspace&gt;</c>, Codex <c>-s workspace-write -c approval_policy="never"</c>,
    /// Gemini CLI <c>--approval-mode auto_edit</c>.
    /// </summary>
    WorkspaceConfined = 1,
}

/// <summary>A stdio MCP server command line plus environment the native agent should start.</summary>
public sealed class McpServerLaunch
{
    public const string DefaultServerName = "swarmcode";

    public string? Command { get; set; }
    public string? Url { get; set; }
    /// <summary>A single {{secret:name}} reference. Resolved only by the host at connection/launch time.</summary>
    public string? BearerTokenReference { get; set; }
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new();
    public string ServerName { get; set; } = DefaultServerName;

    /// <summary>
    /// The user marked this server trusted in the host configuration. Only then may a server's own
    /// <c>readOnlyHint</c> classify a projected tool as read-only (which skips approval); an
    /// untrusted server's hint is a claim, not evidence. Default false.
    /// </summary>
    public bool Trusted { get; set; }

    /// <summary>
    /// The user marked this server required: a native agent that supports it (Codex <c>required</c>)
    /// refuses to start a turn without it instead of silently running without its tools. Default false.
    /// </summary>
    public bool Required { get; set; }

    public bool IsConfigured => Url is not null
        ? McpHttpLaunch.ValidationProblem(this) is null && string.IsNullOrWhiteSpace(Command)
        : !string.IsNullOrWhiteSpace(Command);
}
