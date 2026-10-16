namespace SwarmCore.Agent.CodingTasks;

/// <summary>One CompatibilityAttempt run of a coding CLI as an agent inside the task's isolated worktree.</summary>
/// <param name="Cli">Normalized CLI name (<see cref="CodingTaskService.ClaudeCli"/> or <see cref="CodingTaskService.CodexCli"/>).</param>
/// <param name="Prompt">The task prompt (bug description plus fix instructions).</param>
/// <param name="WorktreePath">The isolated worktree the agent works in; never the user's checkout.</param>
/// <param name="Title">Task title, for the run's presentation.</param>
/// <param name="BranchName">The task branch checked out in the worktree.</param>
/// <param name="InactivityTimeout">The run is stopped when the agent reports nothing for this long.</param>
public sealed record CodingTaskAgentRun(
    string Cli,
    string Prompt,
    string WorktreePath,
    string Title,
    string BranchName,
    TimeSpan InactivityTimeout);

/// <summary>
/// Runs a coding CLI as an agent for <see cref="CodingTaskService"/>'s CompatibilityAttempt mode.
/// The coding harness supplies the implementation over its native lane runners (Claude Code,
/// Codex), so the legacy coding-task path and coding conversations share one CLI runner. The
/// service keeps worktree isolation, branch naming, the commit on the task branch, and never
/// merges or pushes; the runner only runs the agent and reports its transcript in the
/// <see cref="CodingTaskProcessResult"/> shape the service already summarizes.
/// </summary>
public interface ICodingTaskAgentRunner
{
    bool Supports(string cli);

    Task<CodingTaskProcessResult> RunAsync(CodingTaskAgentRun run, CancellationToken ct = default);
}

/// <summary>Conversation ids the harness-backed coding-task runner records its transcripts under.</summary>
public static class CodingTaskConversationIds
{
    /// <summary>Hosts hide this namespace from the user's chat history list; direct reads still work.</summary>
    public const string Prefix = "coding-task-";

    public static bool IsCodingTask(string? conversationId)
        => conversationId?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
