using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Agent.Git;

namespace SwarmCore.Agent.CodingTasks;

/// <summary>
/// Runs one automated coding-fix attempt: validates the target repo, cuts an
/// isolated <c>git worktree</c> + branch from HEAD (system temp — NEVER the
/// user's working tree, which is never stashed, cleaned, or checked out), runs
/// the chosen coding CLI as an agent inside that worktree, commits whatever it
/// changed on the task branch, and reports branch + diffstat + summary for
/// HUMAN review. Never merges, never pushes.
/// <para>
/// The CLI runs through the coding harness's native lane runners when the host
/// composes the harness (<see cref="ICodingTaskAgentRunner"/>); a host without
/// it falls back to the obsolete headless process invocation
/// (<see cref="BuildCliInvocation"/>). Git always goes through the injectable
/// <see cref="ICodingTaskProcessRunner"/>; CLI executables are found by the
/// injectable <see cref="ICodingTaskCliLocator"/>. Failures come back as
/// clear <see cref="CodingTaskResult.Error"/> strings — never exceptions —
/// so tool callers can surface them verbatim.
/// </para>
/// </summary>
public sealed class CodingTaskService : ICodingTaskService
{
    /// <summary>Command names as the CLIs are installed.</summary>
    public const string ClaudeCli = "claude";
    public const string CodexCli = "codex";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    private readonly IOptions<CodingTaskOptions> _options;
    private readonly ICodingTaskProcessRunner _runner;
    private readonly ICodingTaskCliLocator _cliLocator;
    private readonly ILogger<CodingTaskService> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ICodingTaskAgentRunner? _agentRunner;

    public CodingTaskService(
        IOptions<CodingTaskOptions> options,
        ICodingTaskProcessRunner runner,
        ICodingTaskCliLocator cliLocator,
        ILogger<CodingTaskService> logger, IServiceScopeFactory? scopes = null,
        ICodingTaskAgentRunner? agentRunner = null)
    {
        _scopes = scopes;
        _agentRunner = agentRunner;
        _options = options;
        _runner = runner;
        _cliLocator = cliLocator;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrent));
    }

    public async Task<CodingTaskResult> RunAsync(CodingTaskRequest request, CancellationToken ct = default)
    {
        if (request.Mode == CodingTaskMode.Verified)
        {
            if (_scopes is null) return new() { Outcome = CodingTaskOutcome.Blocked, Error = "Verified mission execution is not registered." };
            await using var scope = _scopes.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetService<IVerifiedCodingTaskExecutor>();
            return executor is null
                ? new() { Outcome = CodingTaskOutcome.Blocked, Error = "Verified mission execution is not registered." }
                : await executor.RunAsync(request, ct).ConfigureAwait(false);
        }
        if (request.Mode != CodingTaskMode.CompatibilityAttempt)
            return new() { Outcome = CodingTaskOutcome.Blocked, Error = "Unknown coding task mode." };
        var options = _options.Value;
        var stopwatch = Stopwatch.StartNew();

        CodingTaskResult Fail(string error, string cliUsed = "", string branch = "", string tail = "") => new()
        {
            Success = false,
            Error = error,
            CliUsed = cliUsed,
            BranchName = branch,
            TranscriptTail = tail,
            Duration = stopwatch.Elapsed,
        };

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Fail("title is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Fail("prompt is required (the bug description/stack trace plus fix instructions).");
        }

        var repoPath = FirstNonEmpty(request.RepoPath, options.DefaultRepoPath);
        if (repoPath is null)
        {
            return Fail(
                "No repository path configured. Pass repoPath on the request or set the " +
                "CodingTasks:DefaultRepoPath configuration value — the engine never guesses a repo.");
        }

        repoPath = Path.GetFullPath(repoPath);
        if (!Directory.Exists(repoPath))
        {
            return Fail($"Repository path does not exist: {repoPath}");
        }

        var cli = NormalizeCli(FirstNonEmpty(request.PreferredCli, options.PreferredCli) ?? CodexCli);
        if (cli is null)
        {
            return Fail(
                $"Unknown coding CLI '{request.PreferredCli ?? options.PreferredCli}'. " +
                $"Supported: '{ClaudeCli}' (Claude Code) or '{CodexCli}'.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Refuse anything that is not a git repository.
            var revParse = await GitAsync(repoPath, ["rev-parse", "--is-inside-work-tree"], ct)
                .ConfigureAwait(false);
            if (revParse.ExitCode != 0
                || !revParse.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    $"'{repoPath}' is not a git repository (git rev-parse --is-inside-work-tree failed: " +
                    $"{FirstLine(revParse.StandardError)}). Coding tasks only run against git repos.", cli);
            }

            // 2. Locate the CLI before creating anything.
            var executable = _cliLocator.FindExecutable(cli);
            if (executable is null)
            {
                return Fail(
                    $"The '{cli}' CLI is not installed on this machine (no executable found on PATH or in " +
                    "known install locations). Install it — or pass preferredCli for the other CLI — and retry.",
                    cli);
            }

            // 3. Isolated worktree + branch from HEAD, under the system temp dir
            //    so the user's checkout is completely untouched.
            var timestamp = DateTime.UtcNow;
            var branch = GenerateBranchName(options.BranchPrefix, request.Title, timestamp);
            var worktreePath = Path.Combine(
                Path.GetTempPath(), "talavine-coding-tasks", branch.Replace('/', '-'));
            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

            var worktreeAdd = await GitAsync(
                    repoPath, ["worktree", "add", "-b", branch, worktreePath, "HEAD"], ct)
                .ConfigureAwait(false);
            if (worktreeAdd.ExitCode != 0)
            {
                return Fail(
                    $"git worktree add failed: {Tail(Combine(worktreeAdd), 500)}", cli, branch);
            }

            try
            {
                var baseCommit = (await GitAsync(worktreePath, ["rev-parse", "HEAD"], ct)
                    .ConfigureAwait(false)).StandardOutput.Trim();

                // 4. Run the coding CLI as an agent inside the worktree: through the harness's
                //    native lane runner when composed, else the obsolete headless invocation.
                var inactivity = TimeSpan.FromMinutes(Math.Max(1, options.TimeoutMinutes));
                _logger.LogInformation(
                    "Coding task '{Title}' running {Cli} in isolated worktree {Worktree} (branch {Branch})",
                    request.Title, cli, worktreePath, branch);

                CodingTaskProcessResult run;
                // A failure inside the agent runner can follow real edits; such a run is reported as a failed
                // run (not a failed launch) and its partial work is committed like any other run's.
                var agentRunnerUsed = _agentRunner?.Supports(cli) == true;
                Exception? runFailure = null;
                try
                {
                    run = agentRunnerUsed
                        ? await _agentRunner!.RunAsync(new CodingTaskAgentRun(cli, request.Prompt, worktreePath,
                            request.Title.Trim(), branch, inactivity), ct).ConfigureAwait(false)
#pragma warning disable CS0618 // Hosts without the coding harness keep the legacy headless invocation.
                        : await _runner.RunAsync(BuildCliInvocation(cli, executable, request.Prompt, worktreePath, inactivity), ct)
                            .ConfigureAwait(false);
#pragma warning restore CS0618
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    runFailure = ex;
                    run = new CodingTaskProcessResult(-1, string.Empty, ex.Message, TimedOut: false);
                }

                var transcript = Combine(run);
                var transcriptTail = Tail(transcript, Math.Max(200, options.TranscriptTailChars));

                // 5. Commit whatever the CLI changed (even after an inactivity timeout —
                //    partial work is still worth human eyes).
                await GitAsync(worktreePath, ["add", "-A"], CancellationToken.None).ConfigureAwait(false);
                var status = await GitAsync(worktreePath, ["status", "--porcelain"], CancellationToken.None)
                    .ConfigureAwait(false);
                var hasChanges = !string.IsNullOrWhiteSpace(status.StandardOutput);

                var diffStat = string.Empty;
                if (hasChanges)
                {
                    var commit = await GitAsync(
                            worktreePath,
                            [
                                "-c", "user.name=Talavine Agent",
                                "-c", "user.email=agent@talavine.local",
                                "commit",
                                "-m", request.Title.Trim(),
                                "-m", "Automated fix attempt by Talavine agent."
                            ],
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (commit.ExitCode != 0)
                    {
                        return Fail(
                            $"The CLI made changes but git commit failed: {Tail(Combine(commit), 500)}. " +
                            $"The worktree at '{worktreePath}' is kept for inspection.",
                            cli, branch, transcriptTail);
                    }

                    diffStat = (await GitAsync(
                                worktreePath, ["diff", "--stat", $"{baseCommit}..HEAD"], CancellationToken.None)
                            .ConfigureAwait(false))
                        .StandardOutput.Trim();
                }
                else
                {
                    // Nothing to review — drop the worktree registration and the
                    // contentless branch so failed attempts leave no residue.
                    await CleanUpWorktreeAsync(repoPath, worktreePath, branch).ConfigureAwait(false);
                }

                string? error =
                    runFailure is not null
                        ? agentRunnerUsed || hasChanges
                            ? $"The '{cli}' run failed: {runFailure.Message}" + (hasChanges
                                ? " Partial changes were committed on the branch for inspection."
                                : string.Empty)
                            : $"Could not launch the '{cli}' CLI at '{executable}': {runFailure.Message}"
                    : run.TimedOut
                        ? $"The '{cli}' CLI was silent for {options.TimeoutMinutes} minutes and its process " +
                          "tree was killed." + (hasChanges
                              ? " Partial changes were committed on the branch for inspection."
                              : string.Empty)
                        : run.ExitCode != 0
                            ? $"The '{cli}' CLI exited with code {run.ExitCode}." + LoginHint(cli, transcript)
                            : !hasChanges
                                ? $"The '{cli}' CLI completed but made no changes; the worktree and branch were cleaned up."
                                : null;

                var success = error is null;
                var summary = BuildSummary(run, hasChanges, diffStat, branch);
                _logger.LogInformation(
                    "Coding task '{Title}' finished: success={Success} changes={HasChanges} branch={Branch}",
                    request.Title, success, hasChanges, branch);

                return new CodingTaskResult
                {
                    Success = success,
                    BranchName = branch,
                    WorktreePath = hasChanges ? worktreePath : null,
                    Summary = summary,
                    DiffStat = diffStat,
                    Error = error,
                    CliUsed = cli,
                    Duration = stopwatch.Elapsed,
                    TranscriptTail = transcriptTail,
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanUpWorktreeAsync(repoPath, worktreePath, branch).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── pure helpers (unit-tested) ──────────────────────────────

    /// <summary>Deterministic branch name: <c>{prefix}/{slug}-{yyyyMMdd-HHmmss}</c>
    /// with a lowercase alphanumeric-and-hyphen slug from the title.</summary>
    public static string GenerateBranchName(string branchPrefix, string title, DateTime timestampUtc)
    {
        var slug = Slugify(title);
        var prefix = branchPrefix.Trim().Trim('/');
        if (prefix.Length == 0)
        {
            prefix = "agents/fix";
        }

        return $"{prefix}/{slug}-{timestampUtc:yyyyMMdd-HHmmss}";
    }

    private static string Slugify(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            return "task";
        }

        return slug.Length <= 40 ? slug : slug[..40].TrimEnd('-');
    }

    /// <summary>"claude"/"claudecode" → claude; "codex"/"codexcli" → codex; else null.</summary>
    public static string? NormalizeCli(string preferredCli)
    {
        var value = preferredCli.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
        return value switch
        {
            "claude" or "claudecode" or "claudecli" => ClaudeCli,
            "codex" or "codexcli" => CodexCli,
            _ => null,
        };
    }

    /// <summary>
    /// The one place CLI argument sets live. The prompt always travels on
    /// stdin (both CLIs read it there), never on the command line.
    /// <list type="bullet">
    /// <item>claude: <c>-p --input-format text --output-format text
    /// --permission-mode acceptEdits</c> — headless print mode with file edits
    /// auto-accepted inside the isolated worktree (flag family verified
    /// against the SubscriptionCli providers' current argument sets).</item>
    /// <item>codex: <c>-c approval_policy='never' exec --cd &lt;worktree&gt;
    /// --skip-git-repo-check --sandbox workspace-write -</c> — the config
    /// override replaces the removed <c>--ask-for-approval</c>/<c>--full-auto</c>
    /// flag family (same approach the CodexCli LLM provider uses), and
    /// workspace-write scopes edits to the worktree.</item>
    /// </list>
    /// </summary>
    [Obsolete("Hosts that compose the coding harness run the CLI through its native lane runners (ICodingTaskAgentRunner). Kept only for hosts without the harness.")]
    public static CodingTaskProcessRequest BuildCliInvocation(
        string cli, string executable, string prompt, string worktreePath, TimeSpan timeout)
    {
        IReadOnlyList<string> arguments = cli switch
        {
            ClaudeCli =>
            [
                "-p",
                "--input-format", "text",
                "--output-format", "text",
                "--permission-mode", "acceptEdits",
            ],
            CodexCli =>
            [
                "-c", "approval_policy='never'",
                "exec",
                "--cd", worktreePath,
                "--skip-git-repo-check",
                "--sandbox", "workspace-write",
                "-",
            ],
            _ => throw new ArgumentException($"Unknown CLI '{cli}'.", nameof(cli)),
        };

        return new CodingTaskProcessRequest(executable, arguments, worktreePath, prompt, timeout);
    }

    // ── plumbing ────────────────────────────────────────────────

    /// <summary>
    /// Every coding-task git call goes through the shared builder (<see cref="GitInvocation"/>): the resolved git, the
    /// command-line hardening, the repository's own filter/diff drivers blanked (the worktree checkout and the commit
    /// of the CLI's changes would otherwise run them), and a scrubbed child environment. A git that cannot run, or
    /// a repository configuration that cannot be fenced, is a failed call (exit -1), never an unhardened one.
    /// </summary>
    private async Task<CodingTaskProcessResult> GitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        string git;
        try { git = GitExecutable.Resolve(); }
        catch (GitNotRunnableException ex) { return new CodingTaskProcessResult(-1, "", ex.Message, false); }

        IReadOnlyList<string> prefix = OperatingSystem.IsWindows() ? ["-c", "core.longpaths=true"] : [];
        var environment = GitInvocation.EnvironmentChanges();
        IReadOnlyList<string>? drivers = null;
        if (GitInvocation.RunsRepositoryDrivers(arguments))
        {
            var query = await _runner.RunAsync(new CodingTaskProcessRequest(git, GitInvocation.Arguments([.. prefix, .. GitInvocation.RepositoryDriverQuery]),
                workingDirectory, null, GitTimeout) { Environment = environment }, ct).ConfigureAwait(false);
            if (query.TimedOut || query.ExitCode is not (0 or 1)) // 1: no driver configured
                return new CodingTaskProcessResult(query.TimedOut ? -1 : query.ExitCode, "",
                    "git could not read the repository's filter and diff driver configuration, so it was not run: " + query.StandardError.Trim(), query.TimedOut);
            try { drivers = GitInvocation.RepositoryDriverOverrides(query.StandardOutput, workingDirectory); }
            catch (GitConfigurationRefusedException ex) { return new CodingTaskProcessResult(-1, "", ex.Message, false); }
        }

        return await _runner.RunAsync(
            new CodingTaskProcessRequest(git, GitInvocation.Arguments([.. prefix, .. arguments], drivers), workingDirectory, null, GitTimeout)
            { Environment = environment }, ct).ConfigureAwait(false);
    }

    private async Task CleanUpWorktreeAsync(string repoPath, string worktreePath, string branch)
    {
        try
        {
            await GitAsync(repoPath, ["worktree", "remove", "--force", worktreePath], CancellationToken.None)
                .ConfigureAwait(false);
            await GitAsync(repoPath, ["branch", "-D", branch], CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up coding-task worktree {Worktree}", worktreePath);
        }
    }

    private static string BuildSummary(
        CodingTaskProcessResult run, bool hasChanges, string diffStat, string branch)
    {
        var builder = new StringBuilder();
        var headLines = run.StandardOutput
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(10)
            .ToList();
        if (headLines.Count > 0)
        {
            builder.AppendLine(string.Join('\n', headLines));
        }

        builder.AppendLine(hasChanges
            ? $"Changes committed on branch '{branch}' — review the diff before merging (nothing was merged or pushed)."
            : "No changes were made; the worktree and branch were cleaned up.");
        if (!string.IsNullOrWhiteSpace(diffStat))
        {
            builder.AppendLine(diffStat);
        }

        return builder.ToString().TrimEnd();
    }

    private static string LoginHint(string cli, string transcript) =>
        transcript.Contains("log in", StringComparison.OrdinalIgnoreCase)
        || transcript.Contains("login", StringComparison.OrdinalIgnoreCase)
        || transcript.Contains("logged out", StringComparison.OrdinalIgnoreCase)
        || transcript.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
            ? cli == ClaudeCli
                ? " The CLI may not be signed in — run 'claude /login' once interactively."
                : " The CLI may not be signed in — run 'codex login' once interactively."
            : string.Empty;

    private static string Combine(CodingTaskProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : $"{result.StandardOutput}\n{result.StandardError}";

    private static string Tail(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];

    private static string FirstLine(string value)
    {
        var trimmed = value.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd('\r');
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
