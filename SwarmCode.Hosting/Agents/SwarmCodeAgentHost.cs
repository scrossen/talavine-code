using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.Agents;
using SwarmCore.Coding.Agents;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Agents;

/// <summary>Where a SwarmCode host keeps its per-session runners (the desktop app's app state; a pool for the console).</summary>
public interface IConversationRunnerSource
{
    /// <summary>The session's runner, created (and its history loaded) on first use; null without an open workspace.</summary>
    Task<ConversationRunner?> GetRunnerAsync(string conversationId, CancellationToken ct = default);

    ConversationRunner? TryGetRunner(string conversationId);
}

/// <summary>
/// Runs a SwarmCode <see cref="AgentTeam"/>: each agent is an ordinary session (child of the parent session, memory off)
/// whose turns run through its <see cref="ConversationRunner"/> with the person's own approval and hand-off preferences,
/// the same authority as the parent session. The host reports every runner's turn boundaries and progress to the team.
/// </summary>
public sealed class SwarmCodeAgentHost(
    AgentTeam team,
    WorkspaceSessionCatalog sessions,
    AppPreferencesStore preferences,
    IConversationRunnerSource runners,
    Microsoft.Extensions.Options.IOptions<SwarmCore.Coding.CodingOptions> coding,
    SwarmCodeDataPaths paths,
    DecisionSessionCoordinator? decisions = null,
    ILogger<SwarmCodeAgentHost>? logger = null) : IAgentTeamHost
{
    private readonly ConcurrentDictionary<ConversationRunner, RunnerObservation> _observed = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, int> _external = new(StringComparer.Ordinal);

    public AgentTeam Team => team;

    /// <summary>Reports this runner's turns to the team. Call once per runner the host creates; repeated calls are ignored.</summary>
    public void Observe(ConversationRunner runner)
    {
        var observation = new RunnerObservation(this, runner);
        if (!_observed.TryAdd(runner, observation)) return;
        runner.Changed += observation.OnChanged;
        runner.TurnFinished += observation.OnFinished;
        observation.OnChanged();
    }

    /// <summary>Stops reporting a runner the host disposed.</summary>
    public void Forget(ConversationRunner runner)
    {
        if (!_observed.TryRemove(runner, out var observation)) return;
        runner.Changed -= observation.OnChanged;
        runner.TurnFinished -= observation.OnFinished;
    }

    /// <summary>
    /// Marks a turn the host runs outside a <see cref="ConversationRunner"/> (the console) as running until disposed, so
    /// its native lane may use agents and results wait for it. Report the turn's end to the team after disposing.
    /// </summary>
    public IDisposable BeginExternalTurn(string conversationId)
    {
        _external.AddOrUpdate(conversationId, 1, (_, n) => n + 1);
        return new ExternalTurn(this, conversationId);
    }

    public async Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
        => (await CreateAgentConversationAsync(new AgentConversationRequest(parentConversationId, title, lane, model, null), ct).ConfigureAwait(false)).Id;

    /// <summary>
    /// An agent works in the parent's repository on the parent's route unless the request names another lane; a parent
    /// outside the session index (a console conversation) works in this workspace's repository. With
    /// <c>isolation: "worktree"</c> it gets its own git worktree on a new branch from the parent's current HEAD, under
    /// <see cref="SwarmCodeDataPaths.WorktreesDirectory"/>, recorded as the session's <see cref="WorkspaceSessionInfo.WorkingRoot"/>.
    /// The agent session inherits the parent's permission mode as a floor.
    /// </summary>
    public async Task<AgentConversation> CreateAgentConversationAsync(AgentConversationRequest request, CancellationToken ct)
    {
        var parent = await sessions.GetAsync(request.ParentConversationId, ct).ConfigureAwait(false);
        var laneName = request.Lane ?? parent?.LaneName;
        var modelName = request.Model ?? (laneName is null ? null : parent?.ModelChoices.GetValueOrDefault(laneName));
        var root = parent?.RepositoryRoot ?? coding.Value.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("The session starting this agent no longer exists.");
        var mode = SwarmCodePermissions.ForSession(preferences.Current.Mode, parent);

        AgentWorktree? worktree = null;
        if (request.Isolation == AgentIsolation.Worktree)
        {
            // Nested agents branch from their parent's own worktree when it has one.
            var source = parent?.WorkingRoot ?? root;
            worktree = await AgentWorktrees.CreateAsync(source, paths.WorktreesDirectory, Guid.NewGuid().ToString("N")[..8], ct).ConfigureAwait(false);
        }
        else if (request.Isolation is not null)
            throw new InvalidOperationException($"Unknown isolation \"{request.Isolation}\".");

        try
        {
            var session = await sessions.CreateAsync(root, request.Title, laneName, ct, modelName, request.ParentConversationId,
                workingRoot: worktree?.WorkingRoot ?? parent?.WorkingRoot, permissionMode: SwarmCore.Coding.Permissions.CodingPermissionPolicy.Name(mode)).ConfigureAwait(false);
            var schedulingOwner = await sessions.GetSchedulingOwnerIdAsync(session.Id, ct).ConfigureAwait(false);
            // Assessment is work owned by this new child, not the parent's ambient model call.
            using var runtimeOwner = LocalLlmRuntimeOwnerScope.Enter(session.Id, "Talavine Code", schedulingOwner);
            var selected = decisions is not null && !string.IsNullOrWhiteSpace(request.Task)
                ? await decisions.PrepareDelegatedAsync(session.Id, request.ParentConversationId, request.Task,
                    request.ExplicitModelSelection, ct).ConfigureAwait(false) : null;
            return new AgentConversation(session.Id, worktree, selected?.Lane ?? laneName,
                selected?.Model ?? modelName, selected?.Notice);
        }
        catch when (worktree is not null)
        {
            // Nothing has worked in the new worktree yet: remove it again (it is clean and its branch is at the parent's HEAD).
            try { await AgentWorktrees.TryRemoveAsync(worktree, CancellationToken.None).ConfigureAwait(false); } catch { /* reported by the original error */ }
            throw;
        }
    }

    public async Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
    {
        var runner = await runners.GetRunnerAsync(conversationId, ct).ConfigureAwait(false);
        if (runner is null) return false;
        Observe(runner);
        var session = await sessions.GetAsync(conversationId, ct).ConfigureAwait(false);
        // A fresh authenticated picker choice supersedes the route in an older durable team record.
        var userSelected = session?.Selection is { UserSelected: true } or { Origin: SwarmCore.Abstractions.LlmRouting.Decisions.ModelSelectionOrigin.User };
        var laneName = userSelected ? session!.Selection!.Route ?? session.LaneName : lane ?? session?.LaneName;
        var modelName = userSelected ? session!.Selection!.Model : model ?? (laneName is null ? null : session?.ModelChoices.GetValueOrDefault(laneName));
        var prefs = preferences.Current;
        // Agent messages are written by another agent: a continuation, never the person's own words.
        var mode = SwarmCodePermissions.ForSession(prefs.Mode, session);
        if (runner.TrySend(message, laneName, mode, prefs.HandOffOnRefusal, modelName, isContinuation: true, origin: AgentOrigin(conversationId))) return true;
        return await runner.EnqueueAsync(message, laneName, mode, prefs.HandOffOnRefusal, modelName).ConfigureAwait(false);
    }

    /// <summary>
    /// An agent turn runs under the person's own authority like any session turn; a role's tool allow list travels as
    /// request metadata (<see cref="SwarmCore.Coding.Agents.CodingAgentTools"/>) so engine lanes and the native bridge honour it.
    /// </summary>
    private TurnOrigin? AgentOrigin(string conversationId)
    {
        var record = team.Get(conversationId);
        if (record is null || record.AllowedTools.Count == 0) return null;
        return new TurnOrigin("swarmcode-app", Unattended: false, SwarmCore.Coding.Agents.CodingAgentTools.Metadata(null, record.AllowedTools));
    }

    public void Cancel(string conversationId)
    {
        if (runners.TryGetRunner(conversationId) is not { } runner) return;
        runner.Cancel();
        _ = Task.Run(async () =>
        {
            // Queued follow-ups belong to the stopped work.
            foreach (var queued in runner.QueuedMessages) await runner.RemoveQueuedAsync(queued.Id).ConfigureAwait(false);
        });
    }

    public bool IsBusy(string conversationId)
        => _external.ContainsKey(conversationId)
           || (runners.TryGetRunner(conversationId) is { } runner && (runner.IsRunning || (runner.QueuedMessages.Count > 0 && !runner.QueuePaused)));

    /// <summary>True only while a turn is running (the native bridge's admission check).</summary>
    public bool IsRunningTurn(string conversationId)
        => _external.ContainsKey(conversationId) || runners.TryGetRunner(conversationId)?.IsRunning == true;

    public async Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct)
    {
        // Only to a session this host runs: a console conversation collects results with agents.wait / agents.list.
        if (_external.ContainsKey(conversationId) || runners.TryGetRunner(conversationId) is not { } runner) return false;
        Observe(runner);
        var session = await sessions.GetAsync(conversationId, ct).ConfigureAwait(false);
        var prefs = preferences.Current;
        var lane = session?.LaneName;
        var model = lane is null ? null : session?.ModelChoices.GetValueOrDefault(lane);
        return runner.TrySendAgentReport(message, lane, SwarmCodePermissions.ForSession(prefs.Mode, session), prefs.HandOffOnRefusal,
            model, origin: AgentOrigin(conversationId));
    }

    /// <summary>Plain-language label for what an agent's runner is doing now.</summary>
    internal static string? DescribeActivity(ConversationRunner runner)
    {
        var entries = runner.Entries;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            switch (entries[i])
            {
                case PermissionEntry { IsPending: true }: return "Waiting for your approval";
                case ToolCallEntry { State: ToolCallState.Running } tool: return Label(tool.ToolName);
                case AssistantEntry { IsStreaming: true }: return "Writing";
                case ToolCallEntry or AssistantEntry or UserEntry: return runner.IsRunning ? "Thinking" : null;
            }
        }
        return runner.IsRunning ? "Working" : null;
    }

    private static string Label(string tool) => tool switch
    {
        _ when tool.StartsWith("browser", StringComparison.OrdinalIgnoreCase) => "Using Chrome",
        _ when tool.StartsWith("computer", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("desktop", StringComparison.OrdinalIgnoreCase) => "Using the desktop",
        _ when tool.StartsWith("agents", StringComparison.OrdinalIgnoreCase) => "Working with its agents",
        _ when tool.StartsWith("apps", StringComparison.OrdinalIgnoreCase) => "Using a connected app",
        "shell.exec" or "Bash" or "PowerShell" or "shell" => "Running a command",
        "fs.write_file" or "edit_file" or "Edit" or "Write" or "code.edit" => "Editing files",
        "grep" or "glob" or "Grep" or "Glob" or "Read" or "fs.read_file" => "Reading code",
        _ => AgentActivityText.Describe(tool, inProgress: true),
    };

    private sealed class ExternalTurn(SwarmCodeAgentHost host, string conversationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            while (host._external.TryGetValue(conversationId, out var n))
            {
                var done = n <= 1
                    ? host._external.TryRemove(new KeyValuePair<string, int>(conversationId, n))
                    : host._external.TryUpdate(conversationId, n - 1, n);
                if (done) break;
            }
        }
    }

    private sealed class RunnerObservation(SwarmCodeAgentHost host, ConversationRunner runner)
    {
        private int _wasRunning = -1;

        public void OnChanged()
        {
            var running = runner.IsRunning ? 1 : 0;
            if (Interlocked.Exchange(ref _wasRunning, running) != running && running == 1) host.Team.ReportTurnStarted(runner.ConversationId);
            if (running == 1 && host.Team.IsAgent(runner.ConversationId))
                host.Team.ReportActivity(runner.ConversationId, DescribeActivity(runner), runner.CompletedToolCalls);
        }

        public void OnFinished(TurnResult result)
        {
            Interlocked.Exchange(ref _wasRunning, 0);
            var moreQueued = !result.Cancelled && result.Error is null && runner.QueuedMessages.Count > 0 && !runner.QueuePaused;
            var outcome = new AgentTurnOutcome(result.Completed, result.Cancelled, result.Error, result.FinalText, result.ToolCalls, moreQueued);
            _ = Task.Run(() => host.ReportTurnFinishedAsync(runner.ConversationId, outcome));
        }
    }

    /// <summary>
    /// Records an agent's finished turn. The record must reach a settled state, or a parent in <c>agents.wait</c>
    /// waits out its own timeout: a failed write (store I/O, an invalid record) is logged and retried once as a
    /// failure report, and a second failure is logged rather than lost silently.
    /// </summary>
    internal async Task ReportTurnFinishedAsync(string conversationId, AgentTurnOutcome outcome)
    {
        try
        {
            await team.ReportTurnFinishedAsync(conversationId, outcome).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Recording agent {Agent}'s finished turn failed; reporting it as failed.", conversationId);
        }

        try
        {
            await team.ReportTurnFinishedAsync(conversationId, outcome with
            {
                Completed = false,
                Cancelled = false,
                MoreQueued = false,
                Error = "The agent's turn ended, but its result could not be saved.",
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not record agent {Agent}'s finished turn; its team record may not settle.", conversationId);
        }
    }
}

/// <summary>Runner pool for hosts without their own (the console): one runner per conversation over a workspace's services.</summary>
public sealed class ConversationRunnerPool(IServiceProvider services) : IConversationRunnerSource, IAsyncDisposable, IDisposable
{
    public void Dispose()
    {
        foreach (var lazy in _runners.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                lazy.Value.Result.Dispose();
        _runners.Clear();
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<ConversationRunner>>> _runners = new(StringComparer.Ordinal);

    public async Task<ConversationRunner?> GetRunnerAsync(string conversationId, CancellationToken ct = default)
    {
        var lazy = _runners.GetOrAdd(conversationId, id => new Lazy<Task<ConversationRunner>>(async () =>
        {
            var runner = new ConversationRunner(services, id);
            await runner.LoadHistoryAsync(CancellationToken.None).ConfigureAwait(false);
            return runner;
        }));
        try { return await lazy.Value.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _runners.TryRemove(new KeyValuePair<string, Lazy<Task<ConversationRunner>>>(conversationId, lazy));
            throw;
        }
    }

    public ConversationRunner? TryGetRunner(string conversationId)
        => _runners.TryGetValue(conversationId, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _runners.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                await lazy.Value.Result.DisposeAsync().ConfigureAwait(false);
        _runners.Clear();
    }
}
