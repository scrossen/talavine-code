using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.CodingTasks;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Native;

/// <summary>
/// <see cref="ICodingTaskAgentRunner"/> over the harness's <see cref="INativeLaneRunner"/>s: a
/// CompatibilityAttempt coding task runs Claude Code or Codex exactly as a native coding lane does,
/// inside the task's isolated worktree. The transcript is recorded in its own hidden conversation
/// (<see cref="CodingTaskConversationIds.Prefix"/>), never in the calling chat. Like the
/// orchestrator, a native agent runs outside the host's approval broker, so it only runs for a person's
/// own direct request (and, when the host registers one, an <see cref="ICodingNativeLaneAdmission"/> that
/// admits it); otherwise the run fails visibly rather than widen authority. It always runs
/// <see cref="NativeLaneContainment.WorkspaceConfined"/>, like the headless attempt it replaces
/// (Claude <c>acceptEdits</c>, Codex <c>workspace-write</c>).
/// </summary>
public sealed class NativeLaneCodingTaskAgentRunner(
    IServiceScopeFactory scopes,
    IOptions<CodingOptions> coding,
    ILogger<NativeLaneCodingTaskAgentRunner> logger,
    ICurrentTurnContextAccessor? turns = null) : ICodingTaskAgentRunner
{
    /// <summary>Instruction appended to the task prompt: the service, not the agent, owns the branch.</summary>
    public const string WorktreeInstruction =
        "You are working in an isolated git worktree created for this task. Make the change and run any relevant checks. " +
        "Do not commit, merge, push, rebase, switch branches, or publish anything: the host commits your changes on the task branch for human review.";

    public bool Supports(string cli) => LaneFor(cli) is not null;

    public async Task<CodingTaskProcessResult> RunAsync(CodingTaskAgentRun run, CancellationToken ct = default)
    {
        var lane = LaneFor(run.Cli) ?? throw new ArgumentException($"Unknown coding CLI '{run.Cli}'.", nameof(run));
        var turn = turns?.Current ?? AsyncLocalCurrentTurnContextAccessor.Ambient;
        // A native agent runs outside the host's approval broker, so only a person's own direct request
        // may start one: never a repair/system, mission, employee, scheduled or other unattended turn.
        if (turn is null || !turn.CommunicationScopeUserInitiated || turn.UnattendedAgentTurn
            || turn.RepairAuthority is not null || turn.CodingRepairGrant is not null || turn.MissionObjectiveId is not null
            || !string.IsNullOrWhiteSpace(turn.ActingAgentId))
            return new(1, "", "The coding task was not started: " + CodingTurnOrchestrator.NativeLaneNeedsUserAuthority + ".", TimedOut: false);
        await using (var admissionScope = scopes.CreateAsyncScope())
        {
            if (admissionScope.ServiceProvider.GetService<ICodingNativeLaneAdmission>() is { } admission
                && !await admission.AdmitsAsync(turn, ct).ConfigureAwait(false))
                return new(1, "", "The coding task was not started: " + CodingTurnOrchestrator.NativeLaneNotAdmitted + ".", TimedOut: false);
        }

        var profileId = turn?.ProfileId ?? coding.Value.ProfileId;
        var conversationId = CodingTaskConversationIds.Prefix + Guid.NewGuid().ToString("N");
        await using var scope = scopes.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetServices<INativeLaneRunner>().FirstOrDefault(r => r.Supports(lane))
            ?? throw new InvalidOperationException($"No native runner is registered for {lane.Provider}.");
        if (!runner.SupportsContainment(NativeLaneContainment.WorkspaceConfined))
            return new(1, "", "The coding task was not started: " + CodingTurnOrchestrator.NativeLaneCannotBeConfined + ".", TimedOut: false);
        await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .GetOrCreateAsync(conversationId, "Coding task: " + run.Title, profileId, ct).ConfigureAwait(false);

        var nativeTurn = new NativeLaneTurn(conversationId, run.Prompt + "\n\n" + WorktreeInstruction, lane,
            run.WorktreePath, turn?.CommunicationScopeAuthority ?? "coding-task", IsContinuation: false)
        { ProfileId = profileId, ProfileName = await ProfileNameAsync(scope.ServiceProvider, conversationId, profileId, ct).ConfigureAwait(false),
            Containment = NativeLaneContainment.WorkspaceConfined };
        var output = new StringBuilder();
        var errors = new StringBuilder();
        string? finalText = null;
        var timedOut = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The inactivity lease the old headless runner had: every agent event renews it.
        using var watchdog = new CancellationTokenSource();
        var lastActivity = DateTime.UtcNow;
        var monitor = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(run.InactivityTimeout.TotalSeconds / 4, 0.05, 15)), watchdog.Token).ConfigureAwait(false);
                    if (DateTime.UtcNow - lastActivity < run.InactivityTimeout) continue;
                    timedOut = true;
                    await lifetime.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        try
        {
            await foreach (var evt in runner.RunAsync(nativeTurn, lifetime.Token).ConfigureAwait(false))
            {
                lastActivity = DateTime.UtcNow;
                switch (evt)
                {
                    case TextChunk text:
                        output.Append(text.Delta);
                        break;
                    case ToolCallCompleted tool:
                        output.AppendLine().Append("[tool ").Append(tool.ToolName).Append(tool.IsError ? " failed]" : " ok]").AppendLine();
                        break;
                    case TurnCompleted done:
                        finalText = done.FinalText;
                        break;
                    case TurnErrored error:
                        errors.AppendLine(error.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (timedOut && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Coding task agent {Cli} was silent for {Timeout}; stopped.", run.Cli, run.InactivityTimeout);
        }
        finally
        {
            await watchdog.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }

        // The service summarizes the head of standard output: lead with the agent's answer.
        var stdout = (finalText is null ? output.ToString() : finalText.Trim() + "\n\n" + output).TrimEnd()
            + "\n" + DescribeContainment(nativeTurn, coding.Value.NativeAgents);
        var exitCode = timedOut || finalText is null ? 1 : 0;
        if (finalText is null && errors.Length == 0 && !timedOut) errors.Append("The coding agent stopped without completing an answer.");
        return new(exitCode, stdout.Trim(), errors.ToString().Trim(), timedOut);
    }

    /// <summary>
    /// The containment-relevant launch arguments, built by the same code the runner launches with (including
    /// the installed CLI's capability probe), so the coding-task receipt states exactly how the agent was
    /// confined. Reported at the end of the output, where the transcript tail keeps it.
    /// </summary>
    internal static string DescribeContainment(NativeLaneTurn turn, NativeAgentOptions options, Func<string, string?>? executableOverride = null)
    {
        var containment = turn.EffectiveContainment(options);
        var confined = containment == NativeLaneContainment.WorkspaceConfined;
        List<string> args;
        if (turn.Lane.Provider == ClaudeCodeNativeLaneRunner.ProviderId)
        {
            var exe = executableOverride?.Invoke("claude") ?? ClaudeCodeNativeLaneRunner.ResolveExecutable();
            args = ClaudeCodeNativeLaneRunner.BuildStartInfo(exe ?? "claude", turn, null, null, containment,
                confined && exe is not null && NativeCliCapabilities.Supports(exe, ClaudeCodeNativeLaneRunner.RestrictedFlag)).ArgumentList.ToList();
        }
        else if (turn.Lane.Provider == CodexNativeLaneRunner.ProviderId)
        {
            var exe = executableOverride?.Invoke("codex") ?? CodexNativeLaneRunner.ResolveExecutable();
            if (confined && (exe is null || !CodexNativeLaneRunner.HasRequiredConfinementCapabilities(exe)))
                return $"[native agent {turn.Lane.Name}, {containment}: {CodexNativeLaneRunner.ConfinementUnavailableReason}]";
            args = CodexNativeLaneRunner.BuildStartInfo(exe ?? "codex", turn, null, options,
                confined && exe is not null && NativeCliCapabilities.Supports(exe, CodexNativeLaneRunner.IgnoreUserConfigFlag, "exec", "--help"),
                confined && exe is not null && NativeCliCapabilities.Supports(exe, CodexNativeLaneRunner.IgnoreRulesFlag, "exec", "--help")).ArgumentList.ToList();
        }
        else args = [];

        // Drop the transport/session plumbing; keep every flag that decides what the agent may do. (A Windows shim's
        // cmd.exe bridge is applied only at launch, so the argument list is the CLI's own.)
        var plumbing = new HashSet<string>(StringComparer.Ordinal) { "-p", "--verbose", "exec", "--json", "--skip-git-repo-check", "-" };
        var pairedPlumbing = new HashSet<string>(StringComparer.Ordinal) { "--output-format", "-C", "--model", "-m", "--resume" };
        var shown = new List<string>();
        for (var k = 0; k < args.Count; k++)
        {
            if (pairedPlumbing.Contains(args[k])) { k++; continue; }
            if (plumbing.Contains(args[k])) continue;
            if (args[k] == "-c" && k + 1 < args.Count && args[k + 1].StartsWith("model_reasoning_effort", StringComparison.Ordinal)) { k++; continue; }
            shown.Add(args[k].Contains(' ') ? "\"" + args[k] + "\"" : args[k]);
        }
        return $"[native agent {turn.Lane.Name}, {containment}: {string.Join(' ', shown)}]";
    }

    /// <summary>The host's name for the task's profile (its resolver), or null to fall back to the profile identity.</summary>
    private async Task<string?> ProfileNameAsync(IServiceProvider services, string conversationId, Guid profileId, CancellationToken ct)
    {
        if (profileId == coding.Value.ProfileId) return coding.Value.ProfileName;
        if (services.GetService<IProfileResolver>() is not { } resolver) return null;
        try { return (await resolver.ResolveAsync(conversationId, profileId, ct).ConfigureAwait(false)).Snapshot.Name; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve the coding task's profile name");
            return null;
        }
    }

    private static CodingLane? LaneFor(string cli) => cli switch
    {
        CodingTaskService.ClaudeCli => new("claude", ClaudeCodeNativeLaneRunner.ProviderId, CodingLaneDiscovery.SubscriptionModel(SubscriptionCliCatalog.ClaudeCodeProvider)),
        CodingTaskService.CodexCli => new("codex", CodexNativeLaneRunner.ProviderId, CodingLaneDiscovery.SubscriptionModel(SubscriptionCliCatalog.CodexCliProvider)),
        _ => null,
    };
}

/// <summary>
/// Optional host policy for native coding agents started outside a coding conversation (CompatibilityAttempt
/// coding tasks). Talavine admits them only for a profile that lists its native-lane capability.
/// </summary>
public interface ICodingNativeLaneAdmission
{
    ValueTask<bool> AdmitsAsync(CurrentTurnContext turn, CancellationToken ct = default);
}
