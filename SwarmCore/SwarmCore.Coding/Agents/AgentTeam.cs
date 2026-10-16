using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SwarmCore.Coding.Agents;

/// <summary>
/// Host-neutral coordinator for agents that coding turns start. An agent is its own conversation, run by the host
/// (<see cref="IAgentTeamHost"/>) with the same authority the person gave the parent session; the team never widens it.
/// The team owns the tree (who started whom, depth, fan-out), the running-slot queue, results, and the hand-back:
/// when every agent a conversation started has settled while that conversation is idle, the results go back to it as
/// one continuation turn, so a parent can start background work, finish its turn, and pick the results up later.
/// Ownership is always the trusted current conversation; an agent can see and steer only the agents it started.
/// </summary>
public sealed class AgentTeam : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> LiveInstances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentRecord> _records = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IAgentTeamStore _store;
    private readonly Func<AgentTeamOptions> _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _instance;
    private readonly SemaphoreSlim _init = new(1, 1);
    // Starting an agent's turn belongs to the team, not to the tool call or turn end that happened to trigger the pump.
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _signal = NewSignal();
    private Task _saves = Task.CompletedTask;
    private bool _loaded;
    private IAgentTeamHost? _host;

    public AgentTeam(IAgentTeamStore store, IOptionsMonitor<AgentTeamOptions> options, ILogger<AgentTeam>? logger = null, TimeProvider? time = null,
        IAgentDefinitionSource? definitions = null)
        : this(store, () => options.CurrentValue, logger, time, definitions)
    {
    }

    /// <summary>For hosts that build one team outside a service provider (shared across workspaces).</summary>
    public AgentTeam(IAgentTeamStore store, Func<AgentTeamOptions> options, ILogger<AgentTeam>? logger = null, TimeProvider? time = null,
        IAgentDefinitionSource? definitions = null)
    {
        _store = store;
        _options = options;
        Definitions = definitions;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        using var self = Process.GetCurrentProcess();
        _instance = $"{Environment.ProcessId}:{self.StartTime.ToUniversalTime().Ticks}:{Guid.NewGuid():N}";
        LiveInstances[_instance] = 0;
    }

    public AgentTeamOptions Options => _options();

    /// <summary>Named agent roles (<c>agents/*.md</c>); null when the host offers none.</summary>
    public IAgentDefinitionSource? Definitions { get; set; }

    /// <summary>Raised on any thread after a record changes. Hosts marshal to their UI.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised once each time an agent this instance owns goes from working (queued or running) to a terminal state
    /// (idle, failed, cancelled or interrupted), with the record as written. Handlers run outside the team's lock, one
    /// after another; a handler that throws is logged and does not stop the others. A host uses it for its own
    /// lifecycle hooks; the team itself does nothing with it.
    /// </summary>
    public event Func<AgentRecord, Task>? AgentSettled;

    // Settlements noticed under the lock, raised by Notify() outside it.
    private readonly List<AgentRecord> _settled = [];

    /// <summary>Queues a settlement when <paramref name="previous"/> was working and <paramref name="current"/> is terminal. Callers hold the lock.</summary>
    private void MarkSettledUnlocked(AgentRecord previous, AgentRecord current)
    {
        if (!previous.IsTerminal && current.IsTerminal) _settled.Add(current);
    }

    /// <summary>Binds the host that runs agent conversations. A host rebinds when its workspace changes.</summary>
    public void AttachHost(IAgentTeamHost? host)
    {
        lock (_gate) _host = host;
    }

    /// <summary>Unbinds <paramref name="host"/> if it is still the bound host (a newer workspace may already have replaced it).</summary>
    public void DetachHost(IAgentTeamHost host, bool interruptRunning = false)
    {
        var changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_host, host)) return;
            _host = null;
            if (!interruptRunning) return;
            foreach (var record in _records.Values.Where(r => r.Owner == _instance && r.State is AgentState.Running or AgentState.Queued).ToList())
            {
                var stopped = record with
                {
                    State = AgentState.Interrupted, Activity = null, Pending = [], FinishedUtc = Now, UpdatedUtc = Now, ResultDelivered = false,
                    Error = "Stopped when the workspace closed. Send it a message to continue.",
                };
                _records[record.Id] = stopped;
                SaveUnlocked(stopped);
                MarkSettledUnlocked(record, stopped);
                changed = true;
            }
        }
        if (changed) Notify();
    }

    /// <summary>True when a host is bound (a workspace is open).</summary>
    public bool HasHost
    {
        get { lock (_gate) return _host is not null; }
    }

    /// <summary>True while <paramref name="host"/> is the bound host (a team shared across workspaces has one at a time).</summary>
    public bool IsAttached(IAgentTeamHost host)
    {
        lock (_gate) return ReferenceEquals(_host, host);
    }

    /// <summary>Loads saved agents once. Running agents whose owner process is gone become <see cref="AgentState.Interrupted"/>.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _init.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var record in loaded)
                {
                    var current = record;
                    if (!OwnerAlive(record.Owner))
                    {
                        current = record with { Owner = _instance };
                        if (record.State is AgentState.Running or AgentState.Queued)
                        {
                            current = current with
                            {
                                State = AgentState.Interrupted, Activity = null, Pending = [], UpdatedUtc = Now,
                                Error = "Talavine Code stopped while this agent was working. Send it a message to continue.",
                                ResultDelivered = false,
                            };
                            SaveUnlocked(current);
                        }
                    }
                    _records[current.Id] = current;
                }
                _loaded = true;
            }
        }
        finally { _init.Release(); }
        Notify();
    }

    /// <summary>Every agent, newest first (dashboards).</summary>
    public IReadOnlyList<AgentRecord> Snapshot()
    {
        lock (_gate) return _records.Values.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    public AgentRecord? Get(string id)
    {
        lock (_gate) return _records.GetValueOrDefault(id);
    }

    /// <summary>True when the conversation is an agent's (not a person's session).</summary>
    public bool IsAgent(string conversationId)
    {
        lock (_gate) return _records.ContainsKey(conversationId);
    }

    public int RunningCount
    {
        get { lock (_gate) return _records.Values.Count(r => r.State == AgentState.Running); }
    }

    /// <summary>The agents a conversation started, newest first.</summary>
    public IReadOnlyList<AgentRecord> Children(string parentId, bool includeDelivered = true)
    {
        lock (_gate)
            return _records.Values
                .Where(r => r.ParentId == parentId && (includeDelivered || !(r.IsTerminal && r.ResultDelivered)))
                .OrderByDescending(r => r.CreatedUtc).ToList();
    }

    /// <summary>True while the agent or any agent below it is queued or running.</summary>
    public bool IsActive(string id)
    {
        lock (_gate) return IsActiveUnlocked(id, 0);
    }

    /// <summary>Starts an agent for <paramref name="parentId"/>; returns immediately with the record.</summary>
    public async Task<AgentRecord> SpawnAsync(string parentId, AgentSpawnRequest request, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(request);
        if (!AgentIds.IsValid(parentId)) throw new InvalidOperationException("Agents can be started only from a conversation turn.");
        var options = Options;
        if (!options.Enabled) throw new InvalidOperationException("Agents are turned off in settings.");
        var task = Bound(request.Task, 32_000) ?? throw new ArgumentException("Describe the agent's task.");
        var host = RequireHost();

        string rootId; int depth;
        lock (_gate)
        {
            var parent = _records.GetValueOrDefault(parentId);
            rootId = parent?.RootId ?? parentId;
            depth = (parent?.Depth ?? 0) + 1;
            if (depth > Math.Max(1, options.MaxDepth))
                throw new InvalidOperationException($"Agents may nest only {Math.Max(1, options.MaxDepth)} level(s) deep. Do this part yourself.");
            if (_records.Values.Count(r => r.RootId == rootId) >= Math.Max(1, options.MaxAgentsPerSession))
                throw new InvalidOperationException($"This session already started {options.MaxAgentsPerSession} agents. Reuse one with agents.send, or do the work yourself.");
        }

        AgentDefinition? definition = null;
        if (Bound(request.Agent, 64) is { } roleName)
        {
            definition = Definitions is null ? null : await Definitions.FindAsync(parentId, roleName, ct).ConfigureAwait(false);
            if (definition is null)
            {
                var known = Definitions is null ? [] : await Definitions.ListAsync(parentId, ct).ConfigureAwait(false);
                throw new ArgumentException(known.Count == 0
                    ? $"No agent role named \"{roleName}\" is defined (add agents/<name>.md). Start the agent without a role."
                    : $"No agent role named \"{roleName}\". Known roles: {string.Join(", ", known.Select(d => d.Name))}.");
            }
        }
        var title = Bound(request.Title, 80) ?? (definition is null ? TitleFrom(task) : $"{definition.Name}: {TitleFrom(task)}");
        var lane = Bound(request.Lane, 100) ?? definition?.Lane;
        var model = Bound(request.Model, 200) ?? definition?.Model;
        var isolation = AgentIsolation.Normalize(request.Isolation ?? definition?.Isolation);
        var conversation = await host.CreateAgentConversationAsync(new AgentConversationRequest(parentId, title, lane, model, isolation), ct).ConfigureAwait(false);
        var id = conversation.Id;
        if (!AgentIds.IsValid(id)) throw new InvalidOperationException("The host returned an invalid conversation id.");
        if (isolation is not null && conversation.Worktree is null)
            throw new InvalidOperationException("The host did not create the requested worktree; the agent was not started.");
        var now = Now;
        var record = new AgentRecord
        {
            Id = id, ParentId = parentId, RootId = rootId, Depth = depth, Title = title, Task = task,
            Lane = lane, Model = model, State = AgentState.Queued, CreatedUtc = now, UpdatedUtc = now,
            Pending = [AgentTeamPrompts.Brief(title, task, depth, depth < options.MaxDepth, conversation.Worktree, definition, request.Independent)], Owner = _instance,
            Worktree = conversation.Worktree,
            Agent = definition?.Name, AllowedTools = definition?.Tools ?? [], Independent = request.Independent,
        };
        lock (_gate)
        {
            _records[id] = record;
            SaveUnlocked(record);
        }
        Notify();
        await PumpAsync().ConfigureAwait(false);
        return Get(id) ?? record;
    }

    /// <summary>Sends a follow-up to one of the owner's agents; a finished agent starts a new turn.</summary>
    public async Task<AgentRecord> SendAsync(string ownerId, string agentId, string message, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var text = Bound(message, 32_000) ?? throw new ArgumentException("Write the message to send.");
        var host = RequireHost();
        AgentRecord record;
        bool running;
        lock (_gate)
        {
            record = Owned(ownerId, agentId);
            running = record.State == AgentState.Running;
            if (!running)
            {
                record = record with { State = AgentState.Queued, Pending = [.. record.Pending, text], Error = null, UpdatedUtc = Now, ResultDelivered = false };
                _records[agentId] = record;
                SaveUnlocked(record);
            }
        }
        if (running)
        {
            if (!await host.StartTurnAsync(agentId, text, record.Lane, record.Model, ct).ConfigureAwait(false))
                throw new InvalidOperationException("The agent could not take the message. Check its session.");
            return record;
        }
        Notify();
        await PumpAsync().ConfigureAwait(false);
        return Get(agentId) ?? record;
    }

    /// <summary>
    /// Stops an agent and every agent below it. <paramref name="ownerId"/> null is the person (a dashboard); otherwise
    /// the agent must belong to that conversation.
    /// </summary>
    public async Task<AgentRecord> CancelAsync(string? ownerId, string agentId, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var stop = new List<string>();
        AgentRecord record;
        lock (_gate)
        {
            record = ownerId is null
                ? _records.GetValueOrDefault(agentId) ?? throw new InvalidOperationException("No agent with that id.")
                : Owned(ownerId, agentId);
            if (record.Owner != _instance) throw new InvalidOperationException("That agent is running in another Talavine Code window.");
            Collect(agentId, stop);
            foreach (var id in stop)
            {
                var item = _records[id];
                if (item.State == AgentState.Queued)
                {
                    var queued = item;
                    item = item with { State = AgentState.Cancelled, Pending = [], Activity = null, FinishedUtc = Now, UpdatedUtc = Now, ResultDelivered = false };
                    _records[id] = item;
                    SaveUnlocked(item);
                    MarkSettledUnlocked(queued, item);
                }
            }
            record = _records[agentId];
        }
        var host = _host;
        foreach (var id in stop) host?.Cancel(id);
        Notify();
        return record;
    }

    /// <summary>
    /// Removes a finished agent's record (its conversation stays with the host). <paramref name="force"/> removes it
    /// whatever its state, for a host deleting the agent's conversation after stopping it.
    /// </summary>
    public async Task<bool> ForgetAsync(string agentId, CancellationToken ct = default, bool force = false)
        => (await ForgetWithCleanupAsync(agentId, ct, force).ConfigureAwait(false)).Forgotten;

    /// <summary>
    /// <see cref="ForgetAsync"/>, also removing an isolated agent's worktree when nothing would be lost (no uncommitted
    /// changes, branch merged into the parent's HEAD). Otherwise the worktree and branch stay and the result says why.
    /// </summary>
    public async Task<AgentForgetResult> ForgetWithCleanupAsync(string agentId, CancellationToken ct = default, bool force = false)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        AgentWorktree? worktree;
        AgentRecord removed;
        Task<bool> deleted;
        lock (_gate)
        {
            if (!_records.TryGetValue(agentId, out var record) || record.Owner != _instance
                || (!force && (!record.IsTerminal || IsActiveUnlocked(agentId, 0))))
                return new AgentForgetResult(false, null);
            removed = record;
            worktree = record.Worktree;
            _records.Remove(agentId);
            var store = _store;
            var logger = _logger;
            deleted = _saves.ContinueWith(async _ =>
            {
                try { await store.DeleteAsync(agentId, CancellationToken.None).ConfigureAwait(false); return true; }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not delete agent {Agent} ({Error}).", agentId, ex.GetType().Name);
                    return false;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _saves = deleted;
        }
        Notify();
        if (!await deleted.ConfigureAwait(false))
        {
            // The saved record is still on disk and would return next session: keep it (and its worktree) here too.
            lock (_gate) _records.TryAdd(agentId, removed);
            Notify();
            return new AgentForgetResult(false, null);
        }
        if (worktree is null) return new AgentForgetResult(true, null);
        AgentWorktreeCleanup cleanup;
        try { cleanup = await AgentWorktrees.TryRemoveAsync(worktree, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cleanup = new AgentWorktreeCleanup(false, $"Kept the agent's worktree {worktree.Path} (branch {worktree.Branch}): {ex.Message}");
        }
        if (!cleanup.Removed) _logger.LogInformation("{Cleanup}", cleanup.Message);
        return new AgentForgetResult(true, cleanup);
    }

    /// <summary>
    /// Waits until the named agents (default: every agent the owner started that has not delivered its result)
    /// have finished: any of them, or all when <paramref name="all"/>. Returned finished results count as delivered.
    /// </summary>
    public async Task<IReadOnlyList<AgentRecord>> WaitAsync(string ownerId, IReadOnlyList<string>? agentIds, bool all, TimeSpan timeout, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var max = TimeSpan.FromSeconds(Math.Max(1, Options.MaxWaitSeconds));
        if (timeout <= TimeSpan.Zero || timeout > max) timeout = max;
        IReadOnlyList<string> ids;
        lock (_gate)
        {
            ids = agentIds is { Count: > 0 }
                ? agentIds.Select(id => Owned(ownerId, id).Id).Distinct().ToList()
                : _records.Values.Where(r => r.ParentId == ownerId && !r.Independent && !(r.IsTerminal && r.ResultDelivered)).Select(r => r.Id).ToList();
        }
        if (ids.Count == 0) return [];

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                signal = _signal.Task;
                var done = ids.Count(id => !IsActiveUnlocked(id, 0));
                if (all ? done == ids.Count : done > 0) break;
            }
            try { await signal.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        return MarkDelivered(ids);
    }

    /// <summary>Returns the agent and, when it has settled, counts its result as delivered.</summary>
    public AgentRecord Status(string ownerId, string agentId)
    {
        lock (_gate) Owned(ownerId, agentId);
        return MarkDelivered([agentId])[0];
    }

    /// <summary>Host progress report for an agent's running turn.</summary>
    public void ReportActivity(string conversationId, string? activity, int toolCalls)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(conversationId, out var record) || record.Owner != _instance || record.State != AgentState.Running) return;
            var text = Bound(activity, 120);
            if (record.Activity == text && record.ToolCalls == toolCalls) return;
            // Activity is presentation: kept in memory, saved with the next state change.
            _records[conversationId] = record with { Activity = text, ToolCalls = toolCalls, UpdatedUtc = Now };
        }
        Notify();
    }

    /// <summary>The host reports that a turn started in a conversation (also a follow-up it had queued).</summary>
    public void ReportTurnStarted(string conversationId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(conversationId, out var record) || record.Owner != _instance || record.State == AgentState.Running) return;
            record = record with
            {
                State = AgentState.Running, Turns = record.Turns + 1, StartedUtc = Now, FinishedUtc = null, UpdatedUtc = Now,
                Activity = "Working", Error = null, ResultDelivered = false,
            };
            _records[conversationId] = record;
            SaveUnlocked(record);
        }
        Notify();
    }

    /// <summary>
    /// The host reports that a conversation's turn ended, for every conversation (person or agent): it settles an
    /// agent's turn, starts queued agents, and hands settled results back to any idle parent.
    /// </summary>
    public async Task ReportTurnFinishedAsync(string conversationId, AgentTurnOutcome outcome, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        string? parentId = null;
        // An isolated agent's report says whether its worktree still holds uncommitted work.
        AgentWorktree? inspected = null;
        if (Get(conversationId)?.Worktree is { } worktree)
        {
            try
            {
                var status = await AgentWorktrees.InspectAsync(worktree, ct).ConfigureAwait(false);
                inspected = worktree with { HasUncommittedChanges = status.HasUncommittedChanges, CommitsAhead = status.CommitsAhead };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not inspect agent {Agent}'s worktree ({Error}).", conversationId, ex.GetType().Name);
            }
        }
        lock (_gate)
        {
            if (_records.TryGetValue(conversationId, out var record) && record.Owner == _instance
                && !(record.State == AgentState.Interrupted && outcome.Cancelled))
            {
                parentId = record.ParentId;
                var before = record;
                var max = Math.Max(500, Options.MaxResultCharacters);
                var result = outcome.FinalText is { Length: > 0 } text ? (text.Length > max ? text[..max] + "\n[result truncated]" : text) : record.Result;
                var state = outcome.Cancelled ? AgentState.Cancelled
                    : outcome.MoreQueued ? AgentState.Running
                    : outcome.Error is not null && !outcome.Completed ? AgentState.Failed
                    : AgentState.Idle;
                record = record with
                {
                    State = state, Result = result, Error = state == AgentState.Idle ? null : outcome.Error ?? (outcome.Cancelled ? "Stopped." : null),
                    Activity = state == AgentState.Running ? "Working" : null, FinishedUtc = state == AgentState.Running ? null : Now,
                    Turns = state == AgentState.Running ? record.Turns + 1 : record.Turns, UpdatedUtc = Now, ToolCalls = Math.Max(record.ToolCalls, outcome.ToolCalls),
                    ResultDelivered = false, Worktree = inspected ?? record.Worktree,
                };
                _records[conversationId] = record;
                SaveUnlocked(record);
                MarkSettledUnlocked(before, record);
            }
        }
        Notify();
        await PumpAsync().ConfigureAwait(false);
        // The conversation itself became idle: its own settled agents may report now. Then its parent may be waiting on it.
        await TryHandBackAsync(conversationId, ct).ConfigureAwait(false);
        if (parentId is not null) await TryHandBackAsync(parentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts queued agents while running slots are free. It runs on the team's lifetime: a cancelled caller (a stopped
    /// spawn tool call, another conversation's turn end) must not abandon an agent it already marked Running.
    /// </summary>
    private async Task PumpAsync()
    {
        var ct = _lifetime.Token;
        while (true)
        {
            AgentRecord next;
            string message;
            IAgentTeamHost host;
            lock (_gate)
            {
                if (_host is null) return;
                host = _host;
                if (_records.Values.Count(r => r.State == AgentState.Running && r.Owner == _instance) >= Math.Max(1, Options.MaxRunning)) return;
                var queued = _records.Values.Where(r => r.State == AgentState.Queued && r.Owner == _instance && r.Pending.Count > 0)
                    .OrderBy(r => r.UpdatedUtc).FirstOrDefault();
                if (queued is null) return;
                message = queued.Pending[0];
                next = queued with
                {
                    State = AgentState.Running, Pending = queued.Pending.Skip(1).ToList(), Turns = queued.Turns + 1,
                    StartedUtc = Now, FinishedUtc = null, UpdatedUtc = Now, Activity = "Starting", ResultDelivered = false,
                };
                _records[next.Id] = next;
                SaveUnlocked(next);
            }
            Notify();
            bool started;
            try
            {
                started = await host.StartTurnAsync(next.Id, message, next.Lane, next.Model, ct).ConfigureAwait(false);
                // Later follow-ups queue behind the running turn in the host.
                foreach (var extra in next.Pending)
                    started &= await host.StartTurnAsync(next.Id, extra, next.Lane, next.Model, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The team is closing while the host was starting the turn: do not leave the agent Running with no
                // turn behind it (it would hold a slot and block its parent's hand-back until a restart).
                lock (_gate)
                {
                    if (_records.TryGetValue(next.Id, out var current) && current.State == AgentState.Running)
                    {
                        var stopped = current with
                        {
                            State = AgentState.Interrupted, Activity = null, Pending = [], FinishedUtc = Now, UpdatedUtc = Now, ResultDelivered = false,
                            Error = "Stopped while the agent was starting. Send it a message to continue.",
                        };
                        _records[next.Id] = stopped;
                        SaveUnlocked(stopped);
                        MarkSettledUnlocked(current, stopped);
                    }
                }
                Notify();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Agent {Agent} could not start ({Error}).", next.Id, ex.GetType().Name);
                started = false;
            }
            lock (_gate)
            {
                if (_records.TryGetValue(next.Id, out var current))
                {
                    var before = current;
                    current = started
                        ? current with { Pending = [] }
                        : current with { State = AgentState.Failed, Activity = null, Error = "The agent could not start. Open its session for details.", FinishedUtc = Now, UpdatedUtc = Now };
                    _records[next.Id] = current;
                    SaveUnlocked(current);
                    MarkSettledUnlocked(before, current);
                }
            }
            Notify();
        }
    }

    /// <summary>Hands settled, undelivered results to an idle conversation once nothing it started is still working.</summary>
    private async Task TryHandBackAsync(string conversationId, CancellationToken ct)
    {
        IAgentTeamHost? host;
        List<AgentRecord> ready;
        // The host answers IsBusy from its own runner state (and its own locks): asked outside _gate, like every other
        // host call, so a host that calls back into the team can never deadlock against it.
        lock (_gate) host = _host;
        if (host is null || host.IsBusy(conversationId)) return;
        lock (_gate)
        {
            if (!ReferenceEquals(_host, host)) return;
            // The parent is an agent that was stopped or failed: nothing to continue.
            if (_records.TryGetValue(conversationId, out var self) && self.State is AgentState.Cancelled or AgentState.Queued) return;
            // Independent follow-up work is the person's to review: it neither holds back nor joins the hand-back.
            var children = _records.Values.Where(r => r.ParentId == conversationId && r.Owner == _instance && !r.Independent).ToList();
            if (children.Count == 0 || children.Any(c => IsActiveUnlocked(c.Id, 0))) return;
            ready = children.Where(c => c.IsTerminal && !c.ResultDelivered && c.State != AgentState.Cancelled).OrderBy(c => c.CreatedUtc).ToList();
            if (ready.Count == 0) return;
            foreach (var child in ready)
            {
                var delivered = _records[child.Id] with { ResultDelivered = true };
                _records[child.Id] = delivered;
                SaveUnlocked(delivered);
            }
        }
        Notify();
        bool ok;
        try { ok = await host.DeliverAsync(conversationId, AgentTeamPrompts.HandBack(ready), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not hand agent results back to {Conversation} ({Error}).", conversationId, ex.GetType().Name);
            ok = false;
        }
        if (ok) return;
        // Not delivered: leave the results for agents.wait / agents.list or the next idle moment.
        lock (_gate)
            foreach (var child in ready)
                if (_records.TryGetValue(child.Id, out var current))
                {
                    current = current with { ResultDelivered = false };
                    _records[child.Id] = current;
                    SaveUnlocked(current);
                }
        Notify();
    }

    private IReadOnlyList<AgentRecord> MarkDelivered(IReadOnlyList<string> ids)
    {
        var result = new List<AgentRecord>();
        lock (_gate)
            foreach (var id in ids)
            {
                if (!_records.TryGetValue(id, out var record)) continue;
                if (record.IsTerminal && !record.ResultDelivered && !IsActiveUnlocked(id, 0) && record.Owner == _instance)
                {
                    record = record with { ResultDelivered = true };
                    _records[id] = record;
                    SaveUnlocked(record);
                }
                result.Add(record);
            }
        Notify();
        return result;
    }

    private AgentRecord Owned(string ownerId, string agentId)
    {
        if (!AgentIds.IsValid(agentId) || !_records.TryGetValue(agentId, out var record) || record.ParentId != ownerId)
            throw new InvalidOperationException("No agent with that id was started by this conversation. Use agents.list.");
        if (record.Owner != _instance) throw new InvalidOperationException("That agent is running in another Talavine Code window.");
        return record;
    }

    private bool IsActiveUnlocked(string id, int guard)
    {
        if (guard > 16 || !_records.TryGetValue(id, out var record)) return false;
        if (record.State is AgentState.Running or AgentState.Queued) return true;
        // Independent follow-up work does not keep the agent that started it busy.
        return _records.Values.Any(r => r.ParentId == id && !r.Independent && IsActiveUnlocked(r.Id, guard + 1));
    }

    private void Collect(string id, List<string> into)
    {
        if (into.Contains(id) || into.Count > 256) return;
        into.Add(id);
        foreach (var child in _records.Values.Where(r => r.ParentId == id).Select(r => r.Id).ToList()) Collect(child, into);
    }

    private IAgentTeamHost RequireHost()
    {
        lock (_gate) return _host ?? throw new InvalidOperationException("Agents need an open workspace.");
    }

    private void SaveUnlocked(AgentRecord record)
    {
        var store = _store;
        var logger = _logger;
        _saves = _saves.ContinueWith(async _ =>
        {
            try { await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning("Could not save agent {Agent} ({Error}).", record.Id, ex.GetType().Name); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Completes once every queued save has reached the store (tests, shutdown).</summary>
    public Task FlushAsync()
    {
        lock (_gate) return _saves;
    }

    private void Notify()
    {
        TaskCompletionSource signal;
        List<AgentRecord>? settled = null;
        lock (_gate)
        {
            signal = _signal;
            _signal = NewSignal();
            if (_settled.Count > 0) { settled = [.. _settled]; _settled.Clear(); }
        }
        signal.TrySetResult();
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning("An agent change listener failed ({Error}).", ex.GetType().Name); }
        if (settled is not null && AgentSettled is { } handlers)
            _ = RaiseSettledAsync(handlers, settled);
    }

    // Settlement handlers run off the caller's path (they may run hook processes); each failure is logged, never thrown.
    private async Task RaiseSettledAsync(Func<AgentRecord, Task> handlers, List<AgentRecord> settled)
    {
        await Task.Yield();
        foreach (var record in settled)
            foreach (var handler in handlers.GetInvocationList().Cast<Func<AgentRecord, Task>>())
            {
                try { await handler(record).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning("An agent settlement listener failed for {Agent} ({Error}).", record.Id, ex.GetType().Name); }
            }
    }

    private bool OwnerAlive(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        if (owner == _instance) return true;
        var parts = owner.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var started)) return false;
        if (pid == Environment.ProcessId) return LiveInstances.ContainsKey(owner);
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? Bound(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    internal static string TitleFrom(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "...";
    }

    public async ValueTask DisposeAsync()
    {
        if (!LiveInstances.TryRemove(_instance, out _)) return;
        _lifetime.Cancel();
        try { await FlushAsync().ConfigureAwait(false); } catch { /* logged per save */ }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
