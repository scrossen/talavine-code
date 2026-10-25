using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Goals;

/// <summary>
/// Opt-in durable execution around the ordinary coding harness. The shared continual processor
/// owns leases, receipt checkpoints, acceptance, retries and backoff; a model turn is one attempt.
/// Hosts still supply current workspace, credentials, permissions and tool authority on each run.
/// </summary>
public sealed class CodingGoalOrchestrator
{
    public const string Partition = "talavine-code";
    public const string ExecutorKey = "coding.goal.v1";
    private readonly ICodingTurnOrchestrator _inner;
    private readonly ICodingGoalStore _store;
    private readonly IContinualObjectiveAcceptanceService _acceptance;
    private readonly CodingGoalRuntimeState _runtime;
    private readonly TimeProvider _time;
    private readonly ContinualObjectiveOptions _options;
    private readonly ICodingGoalRequestPolicy? _policy;
    private readonly Native.NativeHostToolReceiptClassifier _nativeReceipts;
    private readonly ICodingGoalReceiptVerifier? _receiptVerifier;
    private readonly SwarmCore.Agent.Communication.IGoalMailbox? _goalMailbox;
    private readonly IReadOnlyList<ICodingGoalOutcomeInspector> _outcomeInspectors;
    private readonly IContinualObjectiveTextSanitizer _sanitizer = new CredentialScrubbingContinualObjectiveTextSanitizer();

    public CodingGoalOrchestrator(ICodingTurnOrchestrator inner, ICodingGoalStore store,
        IContinualObjectiveAcceptanceService acceptance, CodingGoalRuntimeState? runtime = null,
        TimeProvider? time = null, ContinualObjectiveOptions? options = null, ICodingGoalRequestPolicy? policy = null,
        ICapabilityCatalog? tools = null, ICodingGoalReceiptVerifier? receiptVerifier = null,
        SwarmCore.Agent.Communication.IGoalMailbox? mailbox = null,
        IEnumerable<ICodingGoalOutcomeInspector>? outcomeInspectors = null)
    {
        _inner = inner; _store = store; _acceptance = acceptance;
        _runtime = runtime ?? new(); _time = time ?? TimeProvider.System;
        _options = options ?? new() { NoProgressAttentionThreshold = 3 };
        _policy = policy;
        _nativeReceipts = new(tools);
        _receiptVerifier = receiptVerifier;
        _goalMailbox = mailbox;
        _outcomeInspectors = outcomeInspectors?.ToArray() ?? [];
    }

    public async Task<ContinualObjectiveSnapshot> StartAsync(CodingTurnRequest request, CancellationToken ct = default)
    {
        if (request.PlanMode) throw new InvalidOperationException("Leave plan mode before starting a goal.");
        if (request.CommunicationScope?.UserInitiated != true)
            throw new UnauthorizedAccessException("A goal requires the person's authenticated request.");
        if (_policy is not null) request = await _policy.PrepareAsync(request, ct).ConfigureAwait(false);
        if (request.CommunicationScope?.UserInitiated != true)
            throw new UnauthorizedAccessException("Current host authority no longer permits starting this goal.");
        var objective = _sanitizer.Sanitize(request.AuthenticatedOriginalUserMessage ?? request.UserMessage, int.MaxValue);
        if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("A goal is required.", nameof(request));
        var id = Guid.NewGuid();
        return await _store.CreateOrJoinAsync(new()
        {
            Id = id, PartitionKey = Partition, Surface = "coding-goal", IdempotencyKey = id.ToString("N"),
            ConversationId = request.ConversationId, ExecutorKey = ExecutorKey,
            ObjectiveText = objective, ObjectiveSummary = objective.Length <= 300 ? objective : objective[..300],
            Authority = ContinualObjectiveAuthority.UserRequested(request.CommunicationScope.Authority),
            ExecutorPayloadJson = JsonSerializer.Serialize(new CodingGoalPayload(request.Attachments, request.Workspace?.RepositoryRoot)),
            Provider = request.RouteProvider, Model = request.RouteModel ?? request.ModelOverride,
        }, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public Task<ContinualObjectiveSnapshot?> GetAsync(string conversationId, CancellationToken ct = default)
        => _store.GetLatestForConversationAsync(Partition, conversationId, ct);
    public Task<ContinualObjectiveSnapshot?> GetLatestAsync(string conversationId, CancellationToken ct = default)
        => GetAsync(conversationId, ct);

    public async Task<ContinualObjectiveSnapshot?> ControlAsync(string conversationId, CodingGoalControl control, CancellationToken ct = default)
    {
        while (true)
        {
            var current = await GetAsync(conversationId, ct).ConfigureAwait(false);
            if (current is null || ContinualObjectiveStateMachine.IsTerminal(current.Status)) return current;
            if (control == CodingGoalControl.Resume && current.LeaseExpiresAtUtc > _time.GetUtcNow()) return current;
            if (await _store.ControlAsync(current.Id, current.Version, control, _time.GetUtcNow(), ct).ConfigureAwait(false))
            {
                if (control != CodingGoalControl.Resume) CancelActive(current.Id);
                return await _store.GetAsync(current.Id, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Only a fresh authenticated user correction reaches this method; generated continuation text must never do so.</summary>
    public async Task<ContinualObjectiveSnapshot?> UpdateAsync(string conversationId, string objective, CancellationToken ct = default,
        IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment>? attachments = null)
    {
        var safe = _sanitizer.Sanitize(objective, int.MaxValue) ?? throw new ArgumentException("A goal is required.", nameof(objective));
        while (true)
        {
            var current = await GetAsync(conversationId, ct).ConfigureAwait(false);
            if (current is null || ContinualObjectiveStateMachine.IsTerminal(current.Status)) return current;
            if (await _store.UpdateAsync(current.Id, current.Version, safe, _time.GetUtcNow(), ct, attachments).ConfigureAwait(false))
            {
                CancelActive(current.Id);
                return await _store.GetAsync(current.Id, ct).ConfigureAwait(false);
            }
        }
    }

    private void CancelActive(Guid id)
    {
        if (!_runtime.Active.TryGetValue(id, out var running)) return;
        try { running.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, Guid goalId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request.PlanMode) throw new InvalidOperationException("Leave plan mode before resuming a goal.");
        if (request.CommunicationScope?.UserInitiated != true)
            throw new UnauthorizedAccessException("The current host must revalidate user authority before resuming a goal.");
        var current = await _store.GetAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The saved goal no longer exists.");
        if (current.ConversationId != request.ConversationId || current.PartitionKey != Partition || current.ExecutorKey != ExecutorKey)
            throw new UnauthorizedAccessException("The goal belongs to another conversation or execution scope.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_runtime.Active.TryAdd(goalId, cancellation))
            throw new InvalidOperationException("This goal already has an active runner.");
        var events = Channel.CreateBounded<TurnEvent>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var work = Task.Run(async () =>
        {
            try { await PumpAsync(request, goalId, events.Writer, cancellation.Token).ConfigureAwait(false); events.Writer.TryComplete(); }
            catch (Exception ex) { events.Writer.TryComplete(ex); }
        }, CancellationToken.None);
        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return evt;
        }
        finally
        {
            cancellation.Cancel();
            await work.ConfigureAwait(false);
            _runtime.Active.TryRemove(goalId, out _);
        }
    }

    private async Task PumpAsync(CodingTurnRequest request, Guid id, ChannelWriter<TurnEvent> events, CancellationToken ct)
    {
        var owner = "coding-goal:" + Guid.NewGuid().ToString("N");
        var machine = new ContinualObjectiveStateMachine(_time, _options);
        var executor = new Executor(_inner, request, events, _time, _store, _policy, _nativeReceipts, _receiptVerifier, _goalMailbox, _outcomeInspectors);
        var processor = new ContinualObjectiveProcessor(_store, machine, new Acceptance(_acceptance, executor),
            new Registry(executor), new Activity(), _sanitizer, new ContinualObjectiveContinuationPromptBuilder(),
            new ContinualObjectiveExecutionControl(), _time, _options);
        string? lastVisibleState = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var goal = await _store.GetAsync(id, ct).ConfigureAwait(false);
                if (goal is null) return;
                if (goal.Status == ContinualObjectiveStatus.Completed)
                {
                    await events.WriteAsync(executor.Completed ?? new TurnCompleted(request.ConversationId,
                        goal.LastOutput ?? "Goal achieved.", TimeSpan.Zero), ct).ConfigureAwait(false);
                    await events.WriteAsync(new CodingGoalChanged(request.ConversationId, goal), ct).ConfigureAwait(false);
                    return;
                }
                var visibleState = $"{goal.Status}:{goal.AttemptCount}:{goal.LastFailure}:{goal.ObjectiveText}";
                if (visibleState != lastVisibleState)
                {
                    await events.WriteAsync(new CodingGoalChanged(request.ConversationId, goal), ct).ConfigureAwait(false);
                    lastVisibleState = visibleState;
                }
                if (ContinualObjectiveStateMachine.IsTerminal(goal.Status) || goal.Status is ContinualObjectiveStatus.Paused
                    or ContinualObjectiveStatus.WaitingForInput or ContinualObjectiveStatus.Uncertain)
                    return;
                if (!machine.IsDue(goal))
                {
                    var until = goal.NextAttemptAtUtc ?? goal.LeaseExpiresAtUtc ?? _time.GetUtcNow() + _options.PollInterval;
                    var wait = until - _time.GetUtcNow();
                    await Task.Delay(wait > _options.PollInterval ? _options.PollInterval : wait > TimeSpan.Zero ? wait : _options.PollInterval, _time, ct).ConfigureAwait(false);
                    continue;
                }
                var result = await processor.ProcessAsync(id, owner, ct).ConfigureAwait(false);
                if (result.Disposition == ContinualObjectiveProcessDisposition.ClaimLost)
                    await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Preserve an explicit Cancel/hold, and never pause another process's active claim.
            var goal = await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (goal is not null && (goal.LeaseOwner == owner || goal.LeaseToken is null)
                && goal.Status is ContinualObjectiveStatus.InProgress or ContinualObjectiveStatus.RetryScheduled or ContinualObjectiveStatus.VerificationPending)
                await _store.ControlAsync(id, goal.Version, CodingGoalControl.Pause, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class Registry(IContinualObjectiveExecutor executor) : IContinualObjectiveExecutorRegistry
    { public IContinualObjectiveExecutor? Get(string key) => key == ExecutorKey ? executor : null; }
    private sealed class Activity : IContinualObjectiveActivitySink
    { public Task PublishAsync(ContinualObjectiveActivity activity, CancellationToken ct = default) => Task.CompletedTask; }

    private sealed class Acceptance(IContinualObjectiveAcceptanceService inner, Executor executor) : IContinualObjectiveAcceptanceService
    {
        public bool RequiresRetainedVerificationCandidate => inner.RequiresRetainedVerificationCandidate;
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => executor.NeedsInput is { } reason
                ? Task.FromResult(new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.NeedsUserHelp, reason))
                : executor.AcceptanceResult is { Status: CodingAcceptanceStatus.Failed } failed
                ? Task.FromResult(new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry,
                    failed.Summary + "\n" + string.Join("\n", failed.Checks.Where(check => !check.Passed).Select(check => check.Evidence))))
                : CodingGoalAcceptancePolicy.EvaluateAsync(inner, request, ct);
    }

    private sealed class Executor(ICodingTurnOrchestrator inner, CodingTurnRequest original,
        ChannelWriter<TurnEvent> events, TimeProvider time, ICodingGoalStore store,
        ICodingGoalRequestPolicy? policy, Native.NativeHostToolReceiptClassifier nativeReceipts,
        ICodingGoalReceiptVerifier? receiptVerifier, SwarmCore.Agent.Communication.IGoalMailbox? goalMailbox,
        IReadOnlyList<ICodingGoalOutcomeInspector> outcomeInspectors)
        : IContinualObjectiveExecutor, IContinualObjectiveReceiptRecovery
    {
        public string ExecutorKey => CodingGoalOrchestrator.ExecutorKey;
        public TurnCompleted? Completed { get; private set; }
        public CodingAcceptanceResult? AcceptanceResult { get; private set; }
        public string? NeedsInput { get; private set; }
        private bool _opinionsObtained;

        public bool NeedsInspection(ContinualObjectiveSnapshot objective, ContinualToolReceipt receipt)
            => ContinualReceiptResolutionPolicy.NeedsInspection(receipt, objective.Receipts);

        public async Task ReconcileAsync(ContinualObjectiveExecutionRequest request,
            IContinualObjectiveExecutionReporter reporter, CancellationToken ct = default)
        {
            try
            {
                var current = await store.GetAsync(request.Objective.Id, ct).ConfigureAwait(false)
                    ?? throw new ContinualObjectiveLeaseLostException(request.Objective.Id);
                var targets = current.Receipts.Where(r => NeedsInspection(current, r)).ToArray();
                await events.WriteAsync(new CodingGoalChanged(original.ConversationId, current), ct).ConfigureAwait(false);
                await RecoveryProgress(targets.Length, 0, $"Inspecting {targets.Length} saved tool outcomes. No prior action will be repeated.").ConfigureAwait(false);
                // Revalidate current host scope before reading private state, just as an ordinary
                // attempt does. An inspector never grants itself permissions or another lease.
                var inspectionRequest = original with { GoalId = current.Id, PlanMode = true, ReviewOnly = true,
                    ReadOnlyInspection = true, AllowNativeLanes = false, RequireSelectedRoute = true,
                    AuthenticatedOriginalUserMessage = current.ObjectiveText };
                if (policy is not null) inspectionRequest = await policy.PrepareAsync(inspectionRequest, ct).ConfigureAwait(false);
                if (inspectionRequest.CommunicationScope?.UserInitiated != true) return;
                if (inspectionRequest.HostToolsWithheld || original.HostToolsWithheld
                    || UserToolUseRestriction.WithholdsAuthenticatedOriginal(current.ObjectiveText)) return;
                var root = CodingGoalPayload.Read(current).RepositoryRoot;
                if (root is not null && (inspectionRequest.Workspace?.RepositoryRoot is not { } actualRoot
                    || !string.Equals(Path.GetFullPath(root), Path.GetFullPath(actualRoot),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return;
                if (goalMailbox is SwarmCore.Agent.Communication.IGoalWorkerAuthority authority
                    && await authority.ToolsWithheldAsync(current.Id, original.ConversationId, request.LeaseToken, ct).ConfigureAwait(false)) return;
                var resolved = 0;
                foreach (var target in targets)
                {
                    foreach (var inspector in outcomeInspectors)
                    {
                        ct.ThrowIfCancellationRequested();
                        current = await store.GetAsync(current.Id, ct).ConfigureAwait(false)
                            ?? throw new ContinualObjectiveLeaseLostException(current.Id);
                        if (current.LeaseToken != request.LeaseToken || current.Status != ContinualObjectiveStatus.InProgress)
                            throw new ContinualObjectiveLeaseLostException(current.Id);
                        var exact = current.Receipts.Single(r => r.Attempt == target.Attempt && r.ToolCallId == target.ToolCallId);
                        if (ContinualReceiptResolutionPolicy.Fingerprint(exact) != ContinualReceiptResolutionPolicy.Fingerprint(target))
                            throw new InvalidDataException("The original outcome changed during inspection.");
                        if (!NeedsInspection(current, exact)) { resolved++; break; }
                        var started = time.GetUtcNow();
                        CodingGoalOutcomeInspection? result;
                        try { result = await inspector.InspectAsync(current, exact, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (ContinualObjectiveLeaseLostException) { throw; }
                        catch (Exception error)
                        {
                            // A failed readback is neither outcome evidence nor a reason to hide
                            // another independently registered host contract. Never expose raw errors.
                            await RecoveryProgress(targets.Length, resolved,
                                $"Host outcome inspector unavailable ({error.GetType().Name}); trying remaining read-only contracts.").ConfigureAwait(false);
                            continue;
                        }
                        if (result is null) continue;
                        if (string.IsNullOrWhiteSpace(inspector.Identity) || string.IsNullOrWhiteSpace(result.Evidence)
                            || !Enum.IsDefined(result.Outcome)) throw new InvalidDataException("The host inspector returned incomplete proof.");
                        var observation = new ContinualToolReceipt(current.Id, current.AttemptCount, current.LastEventSequence + 1,
                            "host-inspection-" + Guid.NewGuid().ToString("N"), "host.goal-outcome-inspection",
                            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ReadOnly,
                            JsonSerializer.Serialize(new { target.Attempt, target.ToolCallId,
                                fingerprint = ContinualReceiptResolutionPolicy.Fingerprint(target), inspector = inspector.Identity }),
                            result.Evidence, false, started, time.GetUtcNow()) { LocalProcessCompleted = true };
                        await reporter.ToolCompletedAsync(observation, ct).ConfigureAwait(false);
                        // Bind canonical persisted bytes after the host's credential scrubbing.
                        current = await store.GetAsync(current.Id, ct).ConfigureAwait(false)
                            ?? throw new ContinualObjectiveLeaseLostException(current.Id);
                        var saved = current.Receipts.Single(r => r.Attempt == observation.Attempt && r.ToolCallId == observation.ToolCallId);
                        var verified = time.GetUtcNow();
                        var proof = new ContinualReceiptResolution(current.Id, target.Attempt, target.ToolCallId,
                            ContinualReceiptResolutionPolicy.Fingerprint(target), result.Outcome, inspector.Identity, saved.Result ?? "",
                            [new(saved.Attempt, saved.ToolCallId, ContinualReceiptResolutionPolicy.Fingerprint(saved))], verified);
                        if (!await store.ResolveReceiptAsync(current.Id, request.LeaseToken, proof, verified, ct).ConfigureAwait(false))
                            throw new ContinualObjectiveLeaseLostException(current.Id);
                        resolved++;
                        await RecoveryProgress(targets.Length, resolved, $"Verified {resolved} of {targets.Length} saved outcomes without repeating actions.").ConfigureAwait(false);
                        break;
                    }
                }
                if (resolved == targets.Length)
                {
                    await RecoveryProgress(targets.Length, resolved, "Saved outcomes verified. Continuing remaining work from the checkpoint.").ConfigureAwait(false);
                    return;
                }
                await RecoveryProgress(targets.Length, resolved,
                    $"{targets.Length - resolved} saved outcomes still need a supported read-only inspection; completed actions remain protected from replay.").ConfigureAwait(false);
                current = await store.GetAsync(current.Id, ct).ConfigureAwait(false) ?? current;
                await ExecuteAsync(request with { Objective = current }, reporter, ct).ConfigureAwait(false);
            }
            finally { Completed = null; AcceptanceResult = null; NeedsInput = null; }

            ValueTask RecoveryProgress(int total, int resolved, string summary)
                => events.WriteAsync(new CodingGoalRecoveryProgress(original.ConversationId, request.Objective.Id, total, resolved, summary), ct);
        }

        public async Task<ContinualObjectiveExecutionResult> ExecuteAsync(ContinualObjectiveExecutionRequest execution,
            IContinualObjectiveExecutionReporter reporter, CancellationToken ct = default)
        {
            Completed = null; AcceptanceResult = null; NeedsInput = null;
            TurnErrored? error = null;
            string? limit = null;
            var objective = execution.Objective;
            var unresolved = objective.Receipts.Where(receipt => NeedsInspection(objective, receipt)).ToArray();
            var recover = unresolved.Length > 0;
            var hostToolsWithheld = original.HostToolsWithheld;
            if (goalMailbox is SwarmCore.Agent.Communication.IGoalWorkerAuthority authority)
                hostToolsWithheld |= await authority.ToolsWithheldAsync(objective.Id, original.ConversationId,
                    execution.LeaseToken, ct).ConfigureAwait(false);
            SwarmCore.Agent.Communication.GoalHeldInputBatch? heldInput = null;
            if (!recover && goalMailbox is SwarmCore.Agent.Communication.IGoalWorkerHeldInputReader heldReader)
            {
                try
                {
                    heldInput = await heldReader.GetHeldInputAsync(objective.Id, original.ConversationId,
                        execution.LeaseToken, ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    return Block("Retained goal input could not be inspected for this worker. Open the goal inbox and inspect its held messages before continuing; do not repeat earlier actions.");
                }
            }
            var payload = CodingGoalPayload.Read(objective);
            var metadata = (original.Metadata ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => pair.Value);
            metadata["coding.goal"] = objective.Id.ToString("D");
            var request = original with
            {
                GoalId = objective.Id, GoalMailbox = heldInput is null ? goalMailbox : null, GoalMessageLeaseToken = objective.LeaseToken,
                HostToolsWithheld = hostToolsWithheld,
                UserMessage = heldInput is not null
                    ? "This is a dedicated read-only inspection of previously claimed goal input. It is not an execution step. "
                        + "Do not carry out or replay the actions described in the retained messages. Read their full bodies and chronology, "
                        + "then acknowledge them using the explicit final-output goal-message reply envelope. "
                        + "The host will continue remaining work in a separate attempt only after this inspection.\n"
                        + FullObjective(objective) + "\n\n" + heldInput.ContextText
                    : recover ? RecoveryPrompt(execution.ContinuationPrompt + FullObjective(objective)
                    + "\n\nExact operations requiring host inspection (do not replay):\n"
                    + string.Join("\n", unresolved.Select(receipt => $"Attempt {receipt.Attempt}; call {receipt.ToolCallId}; {receipt.ToolName}; input={receipt.InputJson}")))
                    : objective.AttemptCount <= 1 ? Frame(original.UserMessage)
                    : Frame(execution.ContinuationPrompt + FullObjective(objective) + HostResolutionContext(objective)),
                AuthenticatedOriginalUserMessage = objective.ObjectiveText,
                IsContinuation = original.IsContinuation || objective.AttemptCount > 1,
                Assistance = recover || heldInput is not null ? new()
                    : _opinionsObtained ? original.Assistance with { GetOpinionsFirst = false } : original.Assistance,
                Attachments = payload.Attachments,
                PlanMode = recover || heldInput is not null,
                ReviewOnly = recover || heldInput is not null,
                ReadOnlyInspection = heldInput is not null,
                RequireSelectedRoute = original.RequireSelectedRoute || recover || heldInput is not null,
                AllowNativeLanes = !recover && original.AllowNativeLanes,
                Metadata = metadata,
            };
            request = request with { UserMessage = request.UserMessage
                + CodingGoalControlAuthority.Format(objective, payload, recover || heldInput is not null)
                + (recover || heldInput is not null ? "" : ResultsLedgerGuidance(objective)) };
            if (policy is not null) request = await policy.PrepareAsync(request, ct).ConfigureAwait(false);
            if (request.CommunicationScope?.UserInitiated != true)
                return Block("Current host authority no longer permits this goal.");
            if (payload.RepositoryRoot is not null && !string.Equals(Path.GetFullPath(payload.RepositoryRoot),
                request.Workspace?.RepositoryRoot is { } root ? Path.GetFullPath(root) : null,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Block("The conversation's project changed. Resume the goal in its original project.");
            await events.WriteAsync(new CodingGoalChanged(original.ConversationId, objective), ct).ConfigureAwait(false);
            await events.WriteAsync(new Thinking(original.ConversationId, $"Working toward your goal · attempt {objective.AttemptCount}"), ct).ConfigureAwait(false);
            var calls = new Dictionary<string, ContinualToolReceipt>(StringComparer.Ordinal);
            var observations = new List<ContinualToolReceipt>();
            var effects = new Dictionary<string, ToolSideEffect?>(StringComparer.Ordinal);
            var sequence = objective.LastEventSequence;
            await foreach (var raw in inner.RunAsync(request, ct).ConfigureAwait(false))
            {
                var evt = nativeReceipts.Enrich(raw, effects);
                switch (evt)
                {
                    case CodingLaneSelected:
                        // A recovered lane owns the candidate outcome; previous failures stay in receipts/history.
                        error = null; limit = null; Completed = null; AcceptanceResult = null;
                        break;
                    case ToolCallStarted started:
                        var receipt = new ContinualToolReceipt(objective.Id, objective.AttemptCount, ++sequence,
                            started.ToolCallId, started.ToolName, ContinualToolReceiptState.Started,
                            Effect(started.SideEffect), started.Input.ToJsonString(), null, false, time.GetUtcNow());
                        calls[started.ToolCallId] = receipt;
                        await reporter.ToolStartedAsync(receipt, ct).ConfigureAwait(false);
                        break;
                    case ToolCallCompleted finished:
                        var saved = calls.GetValueOrDefault(finished.ToolCallId) ?? new ContinualToolReceipt(objective.Id,
                            objective.AttemptCount, sequence, finished.ToolCallId, finished.ToolName,
                            ContinualToolReceiptState.Started, Effect(finished.SideEffect), null, null, false, time.GetUtcNow() - finished.Elapsed);
                        var completed = saved with { Sequence = ++sequence, State = ContinualToolReceiptState.Completed,
                            Result = finished.Result, IsError = finished.IsError, CompletedAtUtc = time.GetUtcNow(),
                            ElapsedMs = (long)finished.Elapsed.TotalMilliseconds, WasDispatched = finished.WasDispatched,
                            LocalProcessCompleted = finished.LocalProcessCompleted,
                            SideEffect = finished.SideEffect is null ? saved.SideEffect : Effect(finished.SideEffect) };
                        observations.Add(completed);
                        await reporter.ToolCompletedAsync(completed, ct).ConfigureAwait(false);
                        break;
                    case TurnCompleted done:
                        Completed = done;
                        var reply = SwarmCore.Agent.Communication.GoalFinalReplies.Parse(done.FinalText);
                        if (reply.Present)
                        {
                            if (!reply.Valid || reply.Envelope?.GoalId != objective.Id
                                || request.GoalId != objective.Id || request.GoalMessageLeaseToken != execution.LeaseToken
                                || goalMailbox is not SwarmCore.Agent.Communication.IGoalWorkerReplySink sink)
                            {
                                NeedsInput = "The explicit inbox reply could not be validated for this goal. The original output and pending messages were retained; no acknowledgement was invented.";
                                continue;
                            }
                            try
                            {
                                foreach (var item in reply.Envelope.Replies)
                                {
                                    var savedReply = await sink.ReplyForWorkerAsync(objective.Id, original.ConversationId,
                                        item.DeliveryId, item.MessageId, item.Text, item.Disposition,
                                        execution.LeaseToken, ct).ConfigureAwait(false);
                                    if (savedReply.GoalId != objective.Id || savedReply.MessageId != item.MessageId
                                        || savedReply.Disposition != item.Disposition || string.IsNullOrWhiteSpace(savedReply.Id))
                                        throw new InvalidDataException("The host did not return the exact persisted reply receipt.");
                                }
                                // Only persisted explicit worker replies permit removing the protocol wrapper.
                                var visible = reply.VisibleText.Trim();
                                Completed = done with { FinalText = visible.Length > 0 ? visible
                                    : string.Join("\n\n", reply.Envelope.Replies.Select(item => item.Text)) };
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch
                            {
                                NeedsInput = "The explicit inbox reply was not fully saved for the current worker. Its original output and pending input were retained. Inspect the inbox before continuing; completed actions must not be repeated.";
                            }
                        }
                        continue;
                    case TurnErrored failed: error = failed; continue;
                    case CodingAcceptanceEvaluated evaluated: AcceptanceResult = evaluated.Result; break;
                    case CodingOpinionsObtained: _opinionsObtained = true; break;
                    case CodingTurnLimitReached reached: limit = reached.Reason; break;
                }
                await events.WriteAsync(evt, ct).ConfigureAwait(false);
            }
            if (limit is not null) return Block(limit);
            if (error is not null)
                return error.FailureCategory is SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.AuthInvalid
                    or SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.QuotaExhausted
                    ? Block(error.Message) : new("", true, error.Message);
            if (heldInput is not null)
            {
                if (Completed is null) return Block("Retained input inspection produced no complete acknowledgement. Open the goal inbox before continuing; earlier actions must not be replayed.");
                try
                {
                    var remaining = await ((SwarmCore.Agent.Communication.IGoalWorkerHeldInputReader)goalMailbox!)
                        .GetHeldInputAsync(objective.Id, original.ConversationId, execution.LeaseToken, ct: ct).ConfigureAwait(false);
                    if (remaining?.Messages.Any(m => heldInput.Messages.Any(prior => prior.Id == m.Id)) == true)
                        NeedsInput ??= "Retained goal input still needs an explicit acknowledgement. Inspect the held messages in the goal inbox before continuing; no earlier action was repeated.";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { NeedsInput ??= "Retained input could not be reconciled for this worker. Inspect the goal inbox before continuing; do not repeat earlier actions."; }
                // Keep any earlier deliverable as the continuation baseline. Inspection replies are
                // saved in the inbox; they are neither a replacement artifact nor completion proof.
                return new(objective.LastOutput ?? Completed.FinalText, AwaitingOwnedWork: NeedsInput is null)
                    { VerifiedHostProgress = NeedsInput is null };
            }
            if (recover)
            {
                var current = await store.GetAsync(objective.Id, ct).ConfigureAwait(false) ?? objective;
                var observedKeys = observations.Select(receipt => (receipt.Attempt, receipt.ToolCallId)).ToHashSet();
                // The durable reporter may scrub secrets. Verifiers must bind the canonical
                // stored observation, never the pre-sanitization event's different bytes.
                var reads = current.Receipts.Where(receipt => observedKeys.Contains((receipt.Attempt, receipt.ToolCallId))
                    && receipt.State == ContinualToolReceiptState.Completed
                    && !receipt.IsError && receipt.SideEffect == ContinualSideEffectClass.ReadOnly).ToArray();
                if (Completed is null || reads.Length == 0)
                    return Block("A prior action has an unknown outcome. Read-only recovery could not verify its state; inspect it before resuming.");
                // Independent model agreement cannot settle an uncertain physical operation.
                // Only a host-owned verifier can supply exact, observation-bound proofs.
                if (receiptVerifier is not null)
                {
                    foreach (var proof in await receiptVerifier.VerifyAsync(current, reads, ct).ConfigureAwait(false))
                        if (!await store.ResolveReceiptAsync(objective.Id, execution.LeaseToken, proof, time.GetUtcNow(), ct).ConfigureAwait(false))
                            throw new ContinualObjectiveLeaseLostException(objective.Id);
                }
                return new(Completed.FinalText, AwaitingOwnedWork: true);
            }
            return Completed is null ? new("", true, "The coding turn ended without a completed output.")
                : new(Completed.FinalText);
        }

        private ContinualObjectiveExecutionResult Block(string reason)
        {
            NeedsInput = reason;
            return new(reason);
        }

        private static ContinualSideEffectClass Effect(ToolSideEffect? effect)
            => effect is null ? ContinualSideEffectClass.Unknown : (ContinualSideEffectClass)(int)effect.Value;
        private static string FullObjective(ContinualObjectiveSnapshot objective)
            => "\n\nFull authenticated goal (retain every constraint):\n"
                + ContinualObjectivePromptEnvelope.FormatOriginalObjectiveSection(objective.ObjectiveText);
        /// <summary>The owner reads the results ledger, not receipts: every ordinary continuation restates how to keep it current.</summary>
        private static string ResultsLedgerGuidance(ContinualObjectiveSnapshot objective)
            => objective.Mission is null && objective.PartitionKey == Partition
                ? "\n\nResults ledger for the owner:\n" + SwarmCore.Agent.Communication.GoalResults.WorkerGuidance
                    + "\n\nDaily report for the owner:\n" + SwarmCore.Agent.Communication.GoalReports.WorkerGuidance : "";
        private static string HostResolutionContext(ContinualObjectiveSnapshot objective)
        {
            var resolved = objective.Receipts.Where(receipt => ContinualReceiptResolutionPolicy.IsResolved(receipt, objective.Receipts)).ToArray();
            return resolved.Length == 0 ? "" : "\n\nHost-verified prior outcomes (original receipt errors/status remain historical facts):\n"
                + string.Join("\n", resolved.Select(receipt => $"Attempt {receipt.Attempt}; call {receipt.ToolCallId}: "
                    + JsonSerializer.Serialize(receipt.HostResolution)))
                + "\nThese exact outcomes are settled. Continue only remaining work from current state; never replay these operations to obtain a cleaner receipt.";
        }
        private static string RecoveryPrompt(string continuation) => """
            Inspect the prior attempt using read-only tools. One or more started or failed actions have unverified outcomes.
            Determine the actual observed state of each such action. Do not repeat, repair, send, mutate or execute
            commands during this recovery turn. Report exact supporting observations and any outcome you cannot verify.
            This is an inspection step; the host will resume remaining work only after independent verification.

            """ + continuation;
        private static string Frame(string message) => """
            You are working toward a persistent user goal. Keep working until the complete goal is achieved and verified.
            A model turn ending, a promise, or a partial implementation does not finish this goal. Diagnose failures,
            repair them, inspect actual results and try another authorized approach when needed. Agents you delegate to
            inherit this duty for their assigned work. Preserve the person's scope, current permissions, spending limits
            and stop controls. Ask only for indispensable input; do not ask for permission to continue ordinary work.
            Wait for every task agent you started, collect its result, and verify the combined work before claiming completion.
            Prefer the host's fs.read_json (MCP fs_read_json) for deterministic JSON/ledger/config inspection, using Pointer
            and Fields instead of ad-hoc Python or shell parsing. Mixed arrays can include strings and nulls; inspect their
            shape rather than assuming every element is an object. Use fs.read_file/fs.list_files for plain file inspection
            and code.grep/code.glob (MCP code_grep/code_glob) for deterministic search instead of raw shell search pipelines.
            These structured reads preserve read-only receipts even when input JSON is malformed. A failed raw shell command
            does not prove it had no side effects; never relabel or repeat an uncertain action merely to keep moving.

            """ + message;
    }
}
