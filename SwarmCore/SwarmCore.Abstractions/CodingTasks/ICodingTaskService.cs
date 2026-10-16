namespace SwarmCore.Abstractions.CodingTasks;

/// <summary>
/// Turns an assigned code change into a verified source patch through a coding mission,
/// or an explicit compatibility-mode CLI attempt.
/// <para>
/// Verified work defaults to an isolated worktree and preserves unrelated source state;
/// an existing checkout requires explicit assignment authority. Automatic system repair
/// always uses isolation. Verified mode does not commit, merge, push, deploy, or activate
/// running code; compatibility mode retains its legacy local-branch commit behavior.
/// </para>
/// </summary>
public interface ICodingTaskService
{
    Task<CodingTaskResult> RunAsync(CodingTaskRequest request, CancellationToken ct = default);
}

/// <summary>One automated coding-fix attempt.</summary>
public sealed record CodingTaskRequest
{
    public CodingTaskMode Mode { get; init; }
    public string? TaskKey { get; init; }
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public IReadOnlyList<CodingAcceptanceCriterion> AcceptanceCriteria { get; init; } = [];
    public IReadOnlyList<string> AllowedToolIds { get; init; } = [];
    public IReadOnlyList<string> AdmittedPaths { get; init; } = [];
    public string? BaselineRef { get; init; }
    public bool UseExistingCheckout { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public int? MaximumAttempts { get; init; }
    public string? BudgetReference { get; init; }

    /// <summary>Short task title. Used for the branch name slug and the commit subject.</summary>
    public required string Title { get; init; }

    /// <summary>Full task prompt for the coding CLI: the bug description /
    /// stack trace plus fix instructions. Passed via stdin, so length is not
    /// limited by the Windows command line.</summary>
    public required string Prompt { get; init; }

    /// <summary>Absolute path to the git repository. Falls back to
    /// <see cref="CodingTaskOptions.DefaultRepoPath"/> when omitted.</summary>
    public string? RepoPath { get; init; }

    /// <summary>"claude" or "codex". Falls back to
    /// <see cref="CodingTaskOptions.PreferredCli"/> when omitted.</summary>
    public string? PreferredCli { get; init; }

    /// <summary>Provenance of the triggering item (e.g. the error-alert email id).</summary>
    public string? SourceRef { get; init; }
}

/// <summary>Outcome of source coding work; success does not establish deployment or live repair.</summary>
public sealed record CodingTaskResult
{
    /// <summary>Source verification never establishes activation in a running application.</summary>
    public CodingApplicationState ApplicationState => CodingApplicationState.NotApplied;
    public string RuntimeVerification => "Not performed: the running application still uses its existing binaries. Build/install the verified change and repeat the original live failure probe before claiming runtime repair.";
    /// <summary>The model-facing projection omitted text; the mission retains the complete proof.</summary>
    public bool Truncated { get; init; }
    public CodingTaskOutcome Outcome { get; init; }
    public Guid? RunId { get; init; }
    public Guid? MissionId { get; init; }
    public string? RequestHash { get; init; }
    public CodingWorkspaceIdentity? Workspace { get; init; }
    public CodingVerificationProof? Verification { get; init; }

    /// <summary>True for an independently verified final source tree in Verified mode,
    /// or a successful local branch attempt in CompatibilityAttempt mode.</summary>
    public bool Success { get; init; }

    /// <summary>The task branch (e.g. <c>agents/fix/crm-null-ref-20260731-101500</c>).</summary>
    public string BranchName { get; init; } = string.Empty;

    /// <summary>Path of the isolated worktree, kept for review when changes
    /// were made; null when the worktree was cleaned up (no changes).</summary>
    public string? WorktreePath { get; init; }

    /// <summary>Human-readable summary: leading CLI output plus the diffstat.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary><c>git diff --stat</c> of the committed changes against the base commit.</summary>
    public string DiffStat { get; init; } = string.Empty;

    /// <summary>Clear failure description (CLI not installed, not a git repo,
    /// timeout, no changes produced, …); null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Which CLI actually ran ("claude" / "codex"), when one was selected.</summary>
    public string CliUsed { get; init; } = string.Empty;

    /// <summary>Wall-clock duration of the whole attempt.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Last N chars of the combined CLI stdout+stderr (N =
    /// <see cref="CodingTaskOptions.TranscriptTailChars"/>).</summary>
    public string TranscriptTail { get; init; } = string.Empty;
}

/// <summary>Coding results describe source changes; activation requires a separate host deployment receipt.</summary>
public enum CodingApplicationState { NotApplied }

/// <summary>Host configuration for the coding-task engine (bound from the
/// <c>CodingTasks</c> configuration section by Talavine hosts).</summary>
public sealed class CodingTaskOptions
{
    /// <summary>Isolated worktrees; never inside an app package's virtualized storage (<see cref="CodingTaskWorkspaceRoots"/>).</summary>
    public string WorkspaceRoot { get; set; } = CodingTaskWorkspaceRoots.DefaultRoot();
    /// <summary>Repo used when a request carries no RepoPath. When both are
    /// absent the service refuses with a clear error instead of guessing.</summary>
    public string? DefaultRepoPath { get; set; }

    /// <summary>Default CLI: "codex" or "claude" (Claude Code).</summary>
    public string PreferredCli { get; set; } = "codex";

    /// <summary>Renewable inactivity lease for one CLI run. Any stdout/stderr
    /// progress renews it; only a silent/hung process is killed.</summary>
    public int TimeoutMinutes { get; set; } = 25;

    /// <summary>Maximum concurrent coding tasks (semaphore-enforced).</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>Branch namespace for task branches.</summary>
    public string BranchPrefix { get; set; } = "agents/fix";

    /// <summary>How many trailing characters of CLI output to keep in
    /// <see cref="CodingTaskResult.TranscriptTail"/>.</summary>
    public int TranscriptTailChars { get; set; } = 4000;
}
