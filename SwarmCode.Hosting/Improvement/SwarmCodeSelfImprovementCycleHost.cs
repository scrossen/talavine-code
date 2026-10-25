using System.Text;
using Microsoft.Extensions.Logging;
using SwarmCode.Hosting.Scheduling;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Improvement;

/// <summary>
/// SwarmCode's <see cref="ISelfImprovementCycleHost"/>. A cycle is: a fresh git worktree of the source checkout on its
/// own branch (<c>swarmcode/self-improvement-&lt;id&gt;</c>), a temporary session in that worktree whose workspace
/// carries the build and test commands as acceptance checks (so the harness verifies, and repairs once, before the turn
/// ends), the person's lane and preferences, and, only when the host sees changed files plus passing checks, one commit
/// on that branch. The person merges the branch; <see cref="SelfImprovementOptions.MergeVerifiedFixes"/> fast-forwards a
/// clean checkout for them. A failed cycle's worktree is removed (the session transcript keeps the evidence), but
/// an interrupted cycle retains its unverified files and branch as a recoverable checkpoint.
/// </summary>
public sealed class SwarmCodeSelfImprovementCycleHost(
    WorkspaceSessionCatalog sessions,
    AppPreferencesStore preferences,
    IScheduledRunWorkspaces workspaces,
    SessionWorkspaceOverrides overrides,
    SwarmCodeDataPaths paths,
    Func<SelfImprovementOptions> options,
    ILogger? logger = null,
    string? baseDirectory = null) : ISelfImprovementCycleHost
{
    public const string BranchPrefix = "swarmcode/";

    public static string SessionTitle(SelfImprovementItem item)
        => (item.Kind == SelfImprovementKind.Defect ? "🛠 Fix: " : "🛠 Build: ") + (item.Title.Length <= 80 ? item.Title : item.Title[..77] + "...");

    public string? ResolveRepositoryRoot() => SwarmCodeSourceRepository.Resolve(options().RepositoryRoot, baseDirectory);

    public async Task<bool> CanRunAsync(SelfImprovementItem item, CancellationToken ct = default)
        => item.GoalRepair is not { } link || await new Goals.FileCodingGoalStore(paths).GetAsync(link.GoalId, ct).ConfigureAwait(false) is { } goal
            && link.CanResume(goal, DateTimeOffset.UtcNow);

    public async Task<SelfImprovementCycleResult> RunAsync(SelfImprovementItem item, SelfImprovementCycleContext context, CancellationToken ct)
    {
        if (!await CanRunAsync(item, ct).ConfigureAwait(false))
            return SelfImprovementCycleResult.Cancelled("The original goal changed or is paused; no repair was started.");
        var settings = context.Options;
        var checks = settings.Checks.Count > 0 ? settings.Checks : SelfImprovementChecks.Discover(context.RepositoryRoot);
        if (checks.Count == 0)
            return SelfImprovementCycleResult.Unverifiable("No build or test commands are known for the source repository. Add them under Coding:SelfImprovement:Checks in settings.json.");

        AgentWorktree worktree;
        try
        {
            worktree = await AgentWorktrees.CreateAsync(context.RepositoryRoot, paths.WorktreesDirectory, "self-improvement-" + item.Id[..Math.Min(8, item.Id.Length)], ct, BranchPrefix).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return SelfImprovementCycleResult.Unverifiable("Could not create an isolated worktree: " + ex.Message); }

        string? sessionId = null;
        var keepWorktree = false;
        try
        {
            var prefs = preferences.Current;
            var lane = settings.Lane ?? prefs.DefaultLane;
            var model = settings.Model ?? (settings.Lane is null && prefs.DefaultLane is not null ? prefs.DefaultModel : null);
            var session = await sessions.CreateAsync(worktree.WorkingRoot, SessionTitle(item), lane, ct, model, workingRoot: worktree.WorkingRoot).ConfigureAwait(false);
            sessionId = session.Id;
            await sessions.SetTemporaryAsync(session.Id, true, ct).ConfigureAwait(false);
            context.SessionStarted?.Invoke(session.Id);
            overrides.Set(session.Id, new CodingWorkspace { RepositoryRoot = worktree.WorkingRoot, LaneName = lane, AcceptanceChecks = checks });

            TurnResult result;
            CodingAcceptanceResult? acceptance;
            var lease = await workspaces.AcquireAsync(worktree.WorkingRoot, session.Id, ct).ConfigureAwait(false);
            try
            {
                var runner = lease.Runner;
                var completion = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnFinished(TurnResult finished) => completion.TrySetResult(finished);
                runner.TurnFinished += OnFinished;
                try
                {
                    using var stop = ct.Register(runner.Cancel);
                    if (!runner.TrySend(SelfImprovementPrompts.Cycle(item, checks), lane, prefs.Mode, prefs.HandOffOnRefusal, model,
                            origin: TurnOrigin.SelfImprovement(item.Id, settings.AllowNativeLanes)))
                        throw new InvalidOperationException("The cycle's session could not start its turn.");
                    result = await completion.Task.ConfigureAwait(false);
                    acceptance = runner.Acceptance;
                }
                finally { runner.TurnFinished -= OnFinished; }
            }
            finally
            {
                overrides.Remove(session.Id);
                await lease.ReleaseAsync().ConfigureAwait(false);
            }

            if (result.Cancelled || ct.IsCancellationRequested)
                return Cancelled(session.Id, Evidence(acceptance, result));

            var evidence = Evidence(acceptance, result);
            if (result.Error is { } error)
                return Failed("The agent turn failed: " + error, evidence, session.Id);
            if (!result.Completed)
                return Failed("The agent turn ended without completing.", evidence, session.Id);
            if (!await SelfImprovementGit.HasChangesAsync(worktree.Path, ct).ConfigureAwait(false))
                return Failed("The agent changed no files.", evidence, session.Id);
            if (acceptance is null || acceptance.Status != CodingAcceptanceStatus.Passed)
                return Failed(acceptance is null ? "The build and test checks did not run." : "Verification failed: " + acceptance.Summary, evidence, session.Id);

            var commit = await SelfImprovementGit.CommitAllAsync(worktree.Path, SelfImprovementPrompts.CommitMessage(item), ct).ConfigureAwait(false);
            keepWorktree = true;
            var note = new StringBuilder($"Verified: checks passed and the fix is committed on {worktree.Branch} ({commit[..Math.Min(10, commit.Length)]}).");
            var merged = false;
            if (settings.MergeVerifiedFixes)
            {
                var (ok, message) = await SelfImprovementGit.TryFastForwardAsync(worktree.RepositoryRoot, worktree.Branch, worktree.BaseCommit, ct).ConfigureAwait(false);
                merged = ok;
                note.Append(' ').Append(message);
                if (ok)
                {
                    var cleanup = await AgentWorktrees.TryRemoveAsync(worktree, ct).ConfigureAwait(false);
                    keepWorktree = !cleanup.Removed;
                }
            }
            else note.Append(" Merge the branch to ship it.");
            return new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Verified, note.ToString(), evidence, worktree.Branch, commit,
                keepWorktree ? worktree.Path : null, session.Id, merged);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Cancelled(sessionId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Self-improvement cycle {Item} failed in the host.", item.Id);
            return Failed("The cycle failed: " + ex.Message, null, sessionId);
        }
        finally
        {
            if (!keepWorktree && !ct.IsCancellationRequested && !settings.KeepFailedWorktrees)
            {
                try
                {
                    var problem = await SelfImprovementGit.RemoveWorktreeAsync(worktree.RepositoryRoot, worktree.Path, worktree.Branch, force: true, CancellationToken.None).ConfigureAwait(false);
                    if (problem is not null) logger?.LogInformation("Kept the cycle worktree {Path}: {Problem}", worktree.Path, problem);
                }
                catch (Exception ex) { logger?.LogInformation(ex, "Could not remove the cycle worktree {Path}.", worktree.Path); }
            }
        }

        SelfImprovementCycleResult Failed(string note, string? evidence, string? session)
            => new(SelfImprovementCycleOutcome.Failed, note, evidence, SessionId: session,
                WorktreePath: settings.KeepFailedWorktrees ? worktree.Path : null);

        SelfImprovementCycleResult Cancelled(string? session, string? evidence = null)
        {
            keepWorktree = true;
            var checkpoint = $"Interrupted cycle checkpoint retained at {worktree.Path} (branch {worktree.Branch}). These files are not a verified repair.";
            return new(SelfImprovementCycleOutcome.Cancelled, "Stopped before the cycle finished. " + checkpoint,
                string.IsNullOrWhiteSpace(evidence) ? checkpoint : checkpoint + Environment.NewLine + evidence,
                Branch: worktree.Branch, WorktreePath: worktree.Path, SessionId: session);
        }
    }

    private static string Evidence(CodingAcceptanceResult? acceptance, TurnResult result)
    {
        var text = new StringBuilder();
        text.Append("Turn: ").Append(result.Completed ? "completed" : result.Cancelled ? "stopped" : "incomplete")
            .Append(", tool calls ").Append(result.ToolCalls).Append(", lanes ").AppendLine(string.Join(" > ", result.LanesUsed));
        if (result.Error is { } error) text.Append("Error: ").AppendLine(error);
        if (acceptance is null) text.AppendLine("Verification: did not run.");
        else
        {
            text.Append("Verification: ").AppendLine(acceptance.Summary);
            foreach (var check in acceptance.Checks)
            {
                text.Append(check.Passed ? "PASS " : "FAIL ").Append(check.Name);
                if (check.ExitCode is { } code) text.Append(" (exit ").Append(code).Append(')');
                if (check.TimedOut) text.Append(" timed out");
                text.AppendLine();
                if (!check.Passed) text.AppendLine(check.Evidence.Length <= 2500 ? check.Evidence : check.Evidence[^2500..]);
            }
        }
        if (!string.IsNullOrWhiteSpace(result.FinalText))
        {
            text.AppendLine("Agent summary:");
            text.AppendLine(result.FinalText.Length <= 1500 ? result.FinalText : result.FinalText[..1500] + "...");
        }
        return text.ToString().TrimEnd();
    }
}
