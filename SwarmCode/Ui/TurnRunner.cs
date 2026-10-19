using SwarmCode.Hosting.Secrets;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Ui;

/// <summary>Runs one user request through the orchestrator and feeds the renderer.</summary>
public sealed class TurnRunner
{
    private readonly ICodingTurnOrchestrator _orchestrator;
    private readonly ConsoleRenderer _renderer;
    private readonly bool? _allowHandOff;
    private readonly SecretIntake? _secrets;

    /// <summary>Personal-memory turn contexts (null: memory not composed).</summary>
    public SwarmCode.Hosting.Memory.SwarmCodeMemoryTurns? MemoryTurns { get; init; }

    /// <summary>Post-turn capture; null skips learning (non-interactive runs).</summary>
    public SwarmCode.Hosting.Memory.SwarmCodeMemoryCapture? MemoryCapture { get; init; }

    /// <summary>Temporary session: memory is neither read nor written.</summary>
    public bool Temporary { get; init; }
    public SwarmCode.Hosting.DecisionSessionCoordinator? Decisions { get; init; }
    public SwarmCode.Hosting.WorkspaceSessionCatalog? Sessions { get; init; }
    public string? RepositoryRoot { get; init; }

    /// <summary>Agents: marks the console turn as running so its lanes may start agents, and settles them afterwards.</summary>
    public SwarmCode.Hosting.Agents.SwarmCodeAgentHost? Agents { get; init; }

    /// <summary>The allow/deny rules from settings.json (<c>Coding:Permissions</c>). The console runs in Auto mode; deny rules still hold.</summary>
    public SwarmCore.Coding.Permissions.CodingPermissionRules? PermissionRules { get; init; }
    /// <summary>The workspace's services, for UserPromptSubmit and Stop hooks and repository permission rules (null: neither).</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>
    /// Context from SessionStart hooks, placed in front of the next lane message once (the console shows what the
    /// person typed). The REPL and the -p path set it; <see cref="RunAsync"/> consumes it.
    /// </summary>
    public string? PendingContext { get; set; }
    /// <summary>Working-tree checkpoints for <c>/undo</c>; null takes none (non-interactive runs).</summary>
    public SwarmCore.Coding.Checkpoints.TurnCheckpointService? Checkpoints { get; init; }

    /// <summary>Permission mode for the turns (<c>--permission-mode</c>, <c>/mode</c>); the console defaults to Auto. Deny rules always hold.</summary>
    public SwarmCore.Coding.Permissions.CodingPermissionMode PermissionMode { get; set; } = SwarmCore.Coding.Permissions.CodingPermissionMode.Auto;

    /// <summary>Plan mode (<c>--plan</c>, <c>/plan</c>): turns investigate read-only and end with a plan to approve.</summary>
    public bool PlanMode { get; set; }

    /// <summary>True after a plan-mode turn delivered a plan: <c>/approve</c> implements it.</summary>
    public bool PlanAwaitingApproval { get; private set; }
    public void ClearPlanApproval() => PlanAwaitingApproval = false;

    /// <summary>Exact model id for the lane (<c>--model</c>, <c>/model</c>); null uses the lane's configured model.</summary>
    public string? ModelOverride { get; set; }

    /// <summary>Set by <c>/compact</c>: the next turn compacts the working context before its first model call.</summary>
    public bool CompactNextTurn { get; set; }

    /// <summary>Model iterations per lane attempt (<c>--max-turns</c>); 0 keeps the configured limit.</summary>
    public int MaxIterations { get; init; }

    /// <summary>Tokens and cost the providers reported for this process, by turn and in total (null: nothing reported).</summary>
    public long? SessionTokens => _renderer.SessionTokens;
    public decimal? SessionCostUsd => _renderer.SessionCostUsd;

    private readonly List<(string Conversation, SwarmCore.Coding.Checkpoints.TurnCheckpointRecord Record)> _turnChanges = [];
    private string? _checkpointNotice;

    /// <summary>The newest turn of <paramref name="conversationId"/> whose file changes have not been undone.</summary>
    public SwarmCore.Coding.Checkpoints.TurnCheckpointRecord? LastUndoable(string conversationId)
        => _turnChanges.LastOrDefault(t => t.Conversation == conversationId).Record;

    /// <summary>Forgets an undone turn so the next <c>/undo</c> reaches the one before it.</summary>
    public void MarkUndone(string checkpointId) => _turnChanges.RemoveAll(t => t.Record.CheckpointId == checkpointId);

    /// <summary>Deletes a finished console conversation's checkpoints (its refs serve only this process).</summary>
    public async Task ForgetConversationAsync(string conversationId)
    {
        _turnChanges.RemoveAll(t => t.Conversation == conversationId);
        if (Checkpoints is null || RepositoryRoot is null) return;
        try { await Checkpoints.DeleteConversationAsync(RepositoryRoot, conversationId).ConfigureAwait(false); }
        catch (Exception) { /* best effort: retention bounds what is left */ }
    }

    public TurnRunner(ICodingTurnOrchestrator orchestrator, ConsoleRenderer renderer, bool? allowHandOff, SecretIntake? secrets = null)
    {
        _orchestrator = orchestrator;
        _renderer = renderer;
        _allowHandOff = allowHandOff;
        _secrets = secrets;
    }

    public async Task<TurnOutcome> RunAsync(string conversationId, string message, string? lane, CancellationToken ct, bool explicitLane = true)
    {
        // An explicit lane is the starting preference; --no-handoff separately controls retries.
        explicitLane &= !string.IsNullOrWhiteSpace(lane);
        _renderer.Reset();

        // Credential values typed into the prompt go to the vault, never to a lane or the transcript.
        if (_secrets is not null)
        {
            try
            {
                var intake = await _secrets.ScrubAsync(message, ct).ConfigureAwait(false);
                if (intake.StoredNames.Count > 0)
                {
                    message = intake.Text;
                    Console.Error.WriteLine($"[vault] {intake.Message}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                Console.Error.WriteLine("[vault] Could not protect credentials. The message was not sent. Retry or save credentials in the vault first.");
                return TurnOutcome.Errored;
            }
        }

        var laneMessage = message;
        if (Services is not null)
        {
            SwarmCode.Hosting.Extensions.PromptHookResult prompt;
            try { prompt = await SwarmCode.Hosting.Extensions.SwarmCodeTurnHooks.BeforePromptAsync(Services, conversationId, message, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Console.Error.WriteLine("(cancelled)"); return TurnOutcome.Abandoned; }
            foreach (var warning in prompt.Warnings) Console.Error.WriteLine($"[hooks] {warning}");
            if (prompt.Blocked)
            {
                Console.Error.WriteLine($"[hooks] A UserPromptSubmit hook blocked this message: {prompt.Reason}");
                return TurnOutcome.Errored;
            }
            laneMessage = prompt.LaneMessage;
        }

        // @path mentions attach the named files to the lane message; the typed text is what memory and titles see.
        if (RepositoryRoot is not null && laneMessage.Contains('@'))
        {
            var mentions = SwarmCore.Coding.Prompting.FileMentions.Expand(laneMessage, RepositoryRoot);
            laneMessage = mentions.LaneMessage;
            foreach (var m in mentions.Mentions) Console.Error.WriteLine($"[@] attached {m.RelativePath}{(m.IsDirectory ? "/" : "")}");
            foreach (var note in mentions.Notes) Console.Error.WriteLine($"[@] {note}");
        }

        // SessionStart hook context goes in front of the first lane message of the session, once.
        var sessionContext = PendingContext;
        PendingContext = null;
        laneMessage = SwarmCore.Coding.Extensions.CodingHooks.WithSessionContext(laneMessage, sessionContext);

        // The settings rules plus the repository's own (deny always; allow once approved), under the chosen mode.
        var policy = Services is null
            ? SwarmCore.Coding.Permissions.CodingPermissionPolicy.From(PermissionMode, PermissionRules)
            : await SwarmCode.Hosting.SwarmCodePermissions.PolicyAsync(PermissionMode, Services, PermissionRules, RepositoryRoot).ConfigureAwait(false);

        var planTurn = PlanMode;
        var compactFirst = CompactNextTurn;
        CompactNextTurn = false;
        PlanAwaitingApproval = false;
        var request = SwarmCode.Hosting.SwarmCodeTurnPolicy.Apply(new CodingTurnRequest
        {
            ConversationId = conversationId,
            UserMessage = laneMessage,
            // Hook context and @file contents travel with the lane message; the person's authenticated words stay the typed text.
            AuthenticatedOriginalUserMessage = laneMessage != message ? message : null,
            LaneName = lane,
            ModelOverride = ModelOverride,
            AllowHandOff = _allowHandOff,
            RememberModelResponses = !Temporary,
            PlanMode = planTurn,
            CompactWorkingContextFirst = compactFirst,
            ExecutionLimits = MaxIterations > 0 ? new SwarmCore.Coding.CodingExecutionLimits { MaxIterations = MaxIterations } : null,
        }, policy, "swarmcode-cli");

        var memoryTurn = MemoryTurns?.Create(conversationId, Temporary, message, Guid.NewGuid().ToString("N"));
        var checkpoint = await BeginCheckpointAsync(conversationId, ct).ConfigureAwait(false);
        long? revision = null;
        var reassessModel = true;
        var agentTurn = Agents?.BeginExternalTurn(conversationId);
        try
        {
            if (Decisions is not null && Sessions is not null && RepositoryRoot is not null)
            {
                await Sessions.EnsureAsync(conversationId, RepositoryRoot, ct).ConfigureAwait(false);
                if (explicitLane && lane is not null && _orchestrator.ResolveLane(lane) is { } selected)
                    await Sessions.SetUserLaneAsync(conversationId, selected.Name, selected.Model, ct).ConfigureAwait(false);
                var selection = await Decisions.PrepareAsync(conversationId, message, lane, ModelOverride, _orchestrator.Lanes, false, ct).ConfigureAwait(false);
                request = request with { LaneName = selection.Lane, ModelOverride = selection.Model };
                revision = selection.Revision;
                if (selection.Notice is { } notice) Console.Error.WriteLine(notice);
            }
            using (memoryTurn is null ? null : MemoryTurns!.Begin(memoryTurn))
                await foreach (var evt in _orchestrator.RunAsync(request, ct).ConfigureAwait(false))
                {
                    _renderer.Render(evt);
                    if (evt is CodingLanesExhausted { ReassessModel: false }) reassessModel = false;
                    if (Decisions is not null && evt is CodingLaneSelected selected)
                        revision = await Decisions.ObserveAsync(conversationId, revision, selected.Lane, selected.Attempt > 1, ct).ConfigureAwait(false);
                }
            // A plan turn ends with the plan and no acceptance event; the delivered plan is the completed result.
            if (planTurn) _renderer.FinishWithoutAcceptance();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("(cancelled)");
            return TurnOutcome.Abandoned;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"error: {ex.Message}");
            return TurnOutcome.Errored;
        }
        finally
        {
            if (Services is not null)
                foreach (var warning in await SwarmCode.Hosting.Extensions.SwarmCodeTurnHooks.AfterTurnAsync(Services, conversationId,
                             _renderer.Outcome == TurnOutcome.Completed, ct.IsCancellationRequested, _renderer.Outcome == TurnOutcome.Errored ? "turn failed" : null).ConfigureAwait(false))
                    Console.Error.WriteLine($"[hooks] {warning}");
            if (checkpoint is not null) await CompleteCheckpointAsync(conversationId, checkpoint).ConfigureAwait(false);
            agentTurn?.Dispose();
            if (Agents is not null)
                await Agents.Team.ReportTurnFinishedAsync(conversationId, new SwarmCore.Coding.Agents.AgentTurnOutcome(
                    _renderer.Outcome == TurnOutcome.Completed, ct.IsCancellationRequested, null, null, 0), CancellationToken.None).ConfigureAwait(false);
        }

        if (planTurn && _renderer.Outcome == TurnOutcome.Completed)
        {
            PlanAwaitingApproval = true;
            Console.Error.WriteLine("[plan] Plan delivered. Type /approve to implement it, or keep discussing (plan mode stays on; /plan turns it off).");
        }
        if (memoryTurn is not null && _renderer.Outcome == TurnOutcome.Completed) MemoryCapture?.Schedule(memoryTurn);
        if (Decisions is not null && reassessModel && _renderer.Outcome != TurnOutcome.Completed)
            await Decisions.MarkFailedAsync(conversationId, revision, ct).ConfigureAwait(false);
        return _renderer.Outcome == TurnOutcome.None ? TurnOutcome.Errored : _renderer.Outcome;
    }

    private async Task<SwarmCore.Coding.Checkpoints.TurnCheckpoint?> BeginCheckpointAsync(string conversationId, CancellationToken ct)
    {
        if (Checkpoints is null || RepositoryRoot is null) return null;
        try
        {
            var start = await Checkpoints.BeginAsync(RepositoryRoot, conversationId, ct).ConfigureAwait(false);
            if (start.Checkpoint is null && !start.NotARepository && start.UnavailableReason is { } reason && reason != _checkpointNotice)
            {
                _checkpointNotice = reason;
                Console.Error.WriteLine($"[undo] This turn cannot be undone: {reason}");
            }
            return start.Checkpoint;
        }
        catch (Exception) { return null; } // never blocks the turn
    }

    private async Task CompleteCheckpointAsync(string conversationId, SwarmCore.Coding.Checkpoints.TurnCheckpoint checkpoint)
    {
        try
        {
            var record = await Checkpoints!.CompleteAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
            if (record.UnavailableReason is { } reason) { Console.Error.WriteLine($"[undo] This turn cannot be undone: {reason}"); return; }
            if (record.Changes.Count == 0) return;
            _turnChanges.Add((conversationId, record));
            Console.Error.WriteLine($"[{(record.Changes.Count == 1 ? "1 file" : $"{record.Changes.Count} files")} changed · /undo reverts them]");
        }
        catch (Exception) { /* the turn's outcome stands; it just has no undo */ }
    }
}
