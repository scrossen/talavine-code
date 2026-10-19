using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.Routing;

/// <summary>
/// Runs one user request on a pinned lane and hands the remaining work to the
/// next lane when the first declines, stalls, or its route is unavailable. Each
/// hand-off is a continuation turn in the same conversation, so the next model sees
/// the earlier transcript and tool receipts and does not repeat completed work.
/// <para>
/// Host-neutral: lanes come from the host's <see cref="ICodingLaneSource"/>, the repository
/// from the request or the host's <see cref="ICodingWorkspaceResolver"/>, and authority from
/// <see cref="CodingTurnRequest.CommunicationScope"/>. Both seams are resolved inside a scope,
/// so a host may register them scoped. One instance serves any number of conversations and
/// repositories.
/// </para>
/// </summary>
public sealed partial class CodingTurnOrchestrator(
    IServiceScopeFactory scopes,
    IOptions<CodingOptions> codingOptions,
    ILogger<CodingTurnOrchestrator> logger,
    LaneHealthTracker? health = null,
    ActiveCodingWorkspaces? activeWorkspaces = null,
    Prompting.CodingCredentialIntakeContext? credentialIntake = null,
    Permissions.ActiveCodingPermissions? activePermissions = null) : ICodingTurnOrchestrator
{
    /// <summary>Tool ids whose successful completion means the lane changed the workspace (engine and native agent names).</summary>
    public static readonly HashSet<string> MutatingToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "fs.write_file", "code.edit_file", "git.commit", "shell.exec", Tools.NotebookEditTool.ToolId,
        "Edit", "Write", "MultiEdit", "NotebookEdit", "Bash", "PowerShell",
        // SwarmCode's bridge notebook edit as Claude Code and Gemini CLI name it (Codex: IsMutating below).
        Native.NativeToolSideEffects.ClaudeBridgePrefix + Native.NativeToolSideEffects.BridgeNotebookEdit,
        Native.NativeToolSideEffects.BridgeNotebookEdit,
        Native.CodexNativeLaneRunner.CommandToolName, Native.CodexNativeLaneRunner.FileChangeToolName,
        "write_file", "replace", "run_shell_command",
    };

    /// <summary>Text the next lane reads in place of a previous lane's refusal.</summary>
    public const string NeutralStopNote =
        "[A previous attempt stopped here without finishing the request. Tool receipts above show any work it completed.]";

    /// <summary>Why a native lane is not used for a turn the host did not grant user-initiated authority.</summary>
    public const string NativeLaneNeedsUserAuthority =
        "native lanes bypass approval prompts (their agent runs its own tools without the host's permission broker), so they only run for a user-initiated request";

    /// <summary>Why a native lane is not used for an unattended turn or a request the host does not admit native lanes for.</summary>
    public const string NativeLaneNotAdmitted =
        "native lanes run only on a person's own direct request in a profile that admits them; this turn continues on a non-native lane";

    /// <summary>Why a native lane is skipped when its CLI cannot be held to the request's workspace containment.</summary>
    public const string NativeLaneCannotBeConfined =
        "this native agent cannot be confined to the workspace on this machine, so the request continues on another lane";

    /// <summary>Name of the lane built from <see cref="CodingTurnRequest.RouteProvider"/>/<see cref="CodingTurnRequest.RouteModel"/>.</summary>
    public const string SelectedRouteLane = "selected";

    private readonly LaneHealthTracker _health = health ?? new LaneHealthTracker();
    private readonly ActiveCodingWorkspaces _active = activeWorkspaces ?? new ActiveCodingWorkspaces();
    private readonly Permissions.ActiveCodingPermissions _permissions = activePermissions ?? new Permissions.ActiveCodingPermissions();

    public IReadOnlyList<CodingLane> Lanes => CurrentLanes().Lanes;

    public CodingLane? ResolveLane(string? name) => CurrentLanes().Resolve(name);

    private CodingLaneSet CurrentLanes()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ICodingLaneSource>()?.GetLanes() ?? CodingLaneSet.Empty;
    }

    private async IAsyncEnumerable<TurnEvent> RunCoreAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var coding = codingOptions.Value;
        var startTurnId = NewId();

        CodingLaneSet options;
        CodingWorkspace? workspace;
        AgentConversationExecutionGate executions;
        var containment = request.NativeContainment
            ?? (string.Equals(coding.NativeAgents.CodexSandbox, "workspace-write", StringComparison.OrdinalIgnoreCase)
                ? NativeLaneContainment.WorkspaceConfined : coding.NativeAgents.DefaultContainment);
        var unconfinable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canPlan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var requestScope = scopes.CreateScope())
        {
            var source = requestScope.ServiceProvider.GetService<ICodingLaneSource>();
            options = source is null ? CodingLaneSet.Empty : await source.GetLanesAsync(request.ConversationId, ct).ConfigureAwait(false);
            // A native agent whose CLI cannot be held to the requested containment is never used for it.
            var nativeRunners = requestScope.ServiceProvider.GetServices<INativeLaneRunner>().ToList();
            foreach (var native in options.Lanes.Where(l => l.IsNative))
                if (!nativeRunners.Any(r => r.Supports(native) && r.SupportsContainment(containment)))
                    unconfinable.Add(native.Name);
            // Plan mode is host state: only a native agent with a verified read-only plan mode runs a plan turn.
            foreach (var native in options.Lanes.Where(l => l.IsNative))
                if (nativeRunners.Any(r => r.Supports(native) && r.SupportsPlanMode))
                    canPlan.Add(native.Name);
            // The host's shared conversation ownership (the same gate AgentTurnRunner takes for engine turns).
            executions = requestScope.ServiceProvider.GetService<AgentConversationExecutionGate>() ?? AgentConversationExecutionGate.Shared;
            workspace = request.Workspace;
            if (workspace is null && requestScope.ServiceProvider.GetService<ICodingWorkspaceResolver>() is { } resolver)
                workspace = await resolver.ResolveAsync(request.ConversationId, ct).ConfigureAwait(false);
        }

        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RepositoryRoot))
        {
            yield return new TurnErrored(startTurnId, "No repository is selected for this coding conversation. Choose a workspace and retry.");
            yield break;
        }
        var root = workspace.FullRoot;
        if (!Directory.Exists(root))
        {
            yield return new TurnErrored(startTurnId, $"The coding workspace '{root}' does not exist.");
            yield break;
        }
        workspace = workspace with { RepositoryRoot = root };
        using var activeWorkspace = _active.Enter(request.ConversationId, workspace, request.PlanMode, Agents.CodingAgentTools.Patterns(request.Metadata), request.UnattendedAgentTurn);

        // Authority is the host's decision; the harness only narrows it (native lanes bypass the broker):
        // never for a turn nobody is attending, never when the host does not admit them for this request.
        var scopeAuthority = request.CommunicationScope ?? AgentCommunicationScope.None;
        var nativeAllowed = CodingLaneRequestFactory.AllowsNativeScope(request);
        var toolsWithheld = request.AuthenticatedOriginalUserMessage is { } original
            ? UserToolUseRestriction.WithholdsAuthenticatedOriginal(original)
            : UserToolUseRestriction.WithholdsAllTools(request.UserMessage, scopeAuthority.UserInitiated);
        var nativeDenial = scopeAuthority.UserInitiated ? NativeLaneNotAdmitted : NativeLaneNeedsUserAuthority;
        // The request's permission policy holds for the turn's tools; native lanes cannot be held to one that asks or refuses.
        using var activePermissions = request.Permissions is { } policy ? _permissions.Enter(request.ConversationId, policy) : null;
        var permissionBlock = request.Permissions?.NativeLaneBlock;
        if (nativeAllowed && permissionBlock is not null) nativeDenial = permissionBlock;
        string? NativeBlock(CodingLane candidate) => !candidate.IsNative ? null
            : toolsWithheld ? "the authenticated request withholds all tools; native file and command tools are not admitted"
            : !nativeAllowed ? nativeDenial
            : permissionBlock is not null ? permissionBlock
            : unconfinable.Contains(candidate.Name) && containment != NativeLaneContainment.Bypass ? NativeLaneCannotBeConfined
            : request.PlanMode && !canPlan.Contains(candidate.Name) ? Planning.CodingPlanMode.NativeLaneCannotPlan
            : null;
        if (!string.IsNullOrWhiteSpace(request.RouteProvider) && !string.IsNullOrWhiteSpace(request.RouteModel)
            && string.IsNullOrWhiteSpace(request.LaneName))
        {
            var selected = new CodingLane(SelectedRouteLane, request.RouteProvider.Trim(), request.RouteModel.Trim())
            {
                Description = "The model chosen for this conversation",
                Priority = int.MinValue,
                HealthKey = "selected:" + request.ConversationId + ":" + request.RouteProvider.Trim() + "/" + request.RouteModel.Trim(),
            };
            options = options with { Lanes = [selected, .. options.Lanes.Where(l => !string.Equals(l.Name, SelectedRouteLane, StringComparison.OrdinalIgnoreCase))] };
        }
        var profileId = request.ProfileId ?? coding.ProfileId;
        var responseMemoryContext = new CodingFallbackContext(profileId.ToString("N"),
            ModelResponseLearning.Fingerprint(request.AuthenticatedOriginalUserMessage ?? request.UserMessage),
            request.RememberModelResponses && coding.FallbackDiscovery.RememberResponses
                && (!request.IsContinuation || request.AuthenticatedOriginalUserMessage is not null));
        var intake = request.CredentialIntake;
        if (intake is not null && (!intake.Matches(request.ConversationId, profileId, intake.ObjectiveId)
            || request.Metadata?.GetValueOrDefault("continualObjectiveId") != intake.ObjectiveId.ToString("D"))) intake = null;
        using var intakeScope = credentialIntake?.Enter(intake);
        var acceptanceCommands = workspace.AcceptanceChecks ?? coding.Acceptance.Commands;
        var requestedLane = !string.IsNullOrWhiteSpace(request.LaneName) ? request.LaneName
            : options.Find(SelectedRouteLane) is not null && !string.IsNullOrWhiteSpace(request.RouteProvider) ? SelectedRouteLane
            : workspace.LaneName;
        var limits = request.ExecutionLimits ?? coding.ExecutionLimits;
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limits.Timeout > TimeSpan.Zero && limits.Timeout != Timeout.InfiniteTimeSpan)
            requestBudget.CancelAfter(limits.Timeout);
        var requestStarted = System.Diagnostics.Stopwatch.StartNew();
        var completedTools = 0;
        long? reportedTokens = null;
        var repairAttempts = 0;
        var workspaceRepairAttempts = 0;

        // An explicitly named lane is honoured (even while cooling down) unless it is a native lane the
        // host's authority does not allow; then it is skipped visibly and the default pick takes over.
        CodingLane? lane = null;
        var namedLaneUsable = false;
        // Why the requested (else the first) lane was skipped, for the message when no lane is left.
        string? blockReason = null;
        if (!string.IsNullOrWhiteSpace(requestedLane))
        {
            lane = options.Resolve(requestedLane);
            if (lane is not null && NativeBlock(lane) is { } blockedNamed)
            {
                yield return new CodingLaneSkipped(startTurnId, lane, blockedNamed);
                blockReason = blockedNamed;
                lane = null;
            }
            else namedLaneUsable = true;
        }
        if (!namedLaneUsable && !request.SelectionPinned)
        {
            var skippedNamed = !string.IsNullOrWhiteSpace(requestedLane);
            lane = null;
            foreach (var candidate in DefaultOrder(options))
            {
                if (NativeBlock(candidate) is { } blockedDefault)
                {
                    if (!(skippedNamed && string.Equals(candidate.Name, requestedLane, StringComparison.OrdinalIgnoreCase)))
                        yield return new CodingLaneSkipped(startTurnId, candidate, blockedDefault);
                    blockReason ??= blockedDefault;
                    continue;
                }
                if (_health.IsUnavailable(candidate.HealthKeyOrName, out var why, options.HealthScope))
                {
                    yield return new CodingLaneSkipped(startTurnId, candidate, why);
                    continue;
                }
                lane = candidate;
                break;
            }
            // Every eligible lane is cooling down: try the first eligible one anyway rather than refuse to work.
            lane ??= DefaultOrder(options).FirstOrDefault(l => NativeBlock(l) is null);
        }

        if (lane is null)
        {
            yield return new CodingLanesExhausted(startTurnId, namedLaneUsable || (!string.IsNullOrWhiteSpace(requestedLane) && options.Find(requestedLane) is null)
                ? $"Unknown lane '{requestedLane}'."
                : options.Lanes.Count == 0 ? "No coding lanes are configured."
                : $"No coding lane is available for this request: {blockReason ?? nativeDenial}.");
            yield break;
        }

        // A model chosen for a lane that was skipped does not carry over to the fallback lane.
        if (!string.IsNullOrWhiteSpace(request.ModelOverride) && (namedLaneUsable || string.IsNullOrWhiteSpace(requestedLane)))
            lane = lane with { Model = request.ModelOverride.Trim() };

        var handOff = CodingRoutingPolicy.CanHandOff(request, options.HandOffOnRefusal);
        var tried = new List<CodingLane> { lane };
        var message = request.PlanMode && !request.ReviewOnly ? Planning.CodingPlanMode.FrameRequest(request.UserMessage) : request.UserMessage;
        var attempt = 1;
        var fallbackCatalogLoaded = false;
        var continuation = request.IsContinuation;
        NativeCommitScope? finishingCommit = null;

        while (true)
        {
            var orchestratorTurnId = NewId();
            // The previous-worker check runs under the conversation's execution ownership: otherwise a second message
            // sent while a native attempt is running sees that attempt's live worker and is rejected instead of
            // waiting its turn. A native attempt checks once it owns the conversation (below); an engine lane's runner
            // takes ownership itself, so the check runs here under a brief acquisition.
            if (!lane.IsNative)
            {
                bool? stopped = null;
                var probe = await TryAcquireOwnershipAsync(executions, request.ConversationId, requestBudget.Token).ConfigureAwait(false);
                if (probe is not null)
                {
                    try { stopped = await Native.NativeLaneSupport.EnsurePreviousWorkerStoppedAsync(root, request.ConversationId, logger, requestBudget.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (requestBudget.IsCancellationRequested) { }
                    finally { await probe.DisposeAsync().ConfigureAwait(false); }
                }
                if (stopped is null)
                {
                    if (!ct.IsCancellationRequested)
                        yield return new CodingTurnLimitReached(orchestratorTurnId, "The configured job deadline was reached.");
                    yield break;
                }
                if (stopped == false)
                {
                    yield return new TurnErrored(orchestratorTurnId, PreviousWorkerNotStopped);
                    yield break;
                }
            }
            yield return new CodingLaneSelected(orchestratorTurnId, lane, attempt);

            string? finalText = null;
            string? resolvedModel = null;
            var answerCompleted = false;
            string? error = null;
            LlmFailureCategory? category = null;
            DeclineSignal? decline = null;
            var mutated = false;
            var codexBridgeFileChanges = new HashSet<string>(StringComparer.Ordinal);
            var cancelled = false;
            var stuck = false;
            var blockedCalls = 0;
            var completionFailed = false;
            var inspectionProgress = new CodingInspectionProgress(request.RequireWorkspaceChange && !request.PlanMode
                ? coding.NoProgressReadLimit : 0);
            string? limitReason = null;
            NativeCommitSnapshot? nativeCommitSnapshot = null;
            TurnCompleted? deferredNativeAnswer = null;
            var confinedClaude = lane.Provider.Equals(Native.ClaudeCodeNativeLaneRunner.ProviderId, StringComparison.OrdinalIgnoreCase)
                                 && containment == NativeLaneContainment.WorkspaceConfined;

            using (var scope = scopes.CreateScope())
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(requestBudget.Token))
            {
                var attemptCt = attemptCts.Token;
                IAsyncEnumerable<TurnEvent> stream;
                if (lane.IsNative)
                {
                    string? attachmentError = null;
                    try
                    {
                        Files.CodingAttachments.ValidateBatch(request.Attachments, codingOptions.Value.AttachmentLimits);
                        foreach (var attachment in request.Attachments)
                            await Files.CodingAttachments.VerifyAsync(attachment, attemptCt, codingOptions.Value.AttachmentLimits).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { attachmentError = ex.Message; }
                    if (attachmentError is not null) { yield return new TurnErrored(orchestratorTurnId, attachmentError); yield break; }
                    var native = scope.ServiceProvider.GetServices<INativeLaneRunner>().FirstOrDefault(r => r.Supports(lane));
                    // Native CLIs run their own loop, so the host's opted-in injectors (memory digest, repository memory
                    // index) are computed here and framed as reference data in the CLI prompt.
                    var hostContext = native is null ? [] : await Native.NativeHostContext.BuildAsync(scope.ServiceProvider, codingOptions.Value,
                        new ContextInjectionRequest(request.ConversationId, request.AuthenticatedOriginalUserMessage ?? request.UserMessage, profileId,
                            TurnId: null, WorkspaceRoot: root) { AuthenticatedOriginalUserMessage = request.AuthenticatedOriginalUserMessage },
                        logger, attemptCt).ConfigureAwait(false);
                    stream = native is null
                        ? Single(new TurnErrored(orchestratorTurnId, $"No native runner is registered for provider {lane.Provider}; the lane is unavailable.")
                            { FailureCategory = LlmFailureCategory.ModelNotFound })
                        : native.RunAsync(new NativeLaneTurn(request.ConversationId, message, lane, root, scopeAuthority.Authority, continuation)
                            { ExecutionLimits = limits, Attachments = request.Attachments, ProfileId = profileId, CredentialIntake = intake,
                                ProfileName = request.ProfileName, Metadata = request.Metadata,
                                Containment = containment, HostContext = hostContext, PlanMode = request.PlanMode }, attemptCt);
                }
                else
                {
                    var runner = scope.ServiceProvider.GetRequiredService<IAgentTurnRunner>();
                    string? attachmentError = null;
                    IReadOnlyList<TurnImageAttachment> images = [];
                    IReadOnlyList<TurnFileAttachment> files = [];
                    try
                    {
                        if (request.Attachments.Any(a => a.IsImage)
                            && scope.ServiceProvider.GetService<ILlmCapabilitiesService>()?.SupportsVision(lane.Provider, lane.Model) != true)
                            throw new InvalidDataException($"{lane.Model} does not support image input. Choose a vision model or a native provider; your images will not be silently omitted.");
                        (images, files) = await Files.CodingAttachments.ForApiAsync(request.Attachments, attemptCt, codingOptions.Value.AttachmentLimits).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { attachmentError = ex.Message; }
                    if (attachmentError is not null) { yield return new TurnErrored(orchestratorTurnId, attachmentError); yield break; }
                    var engineRequest = CodingLaneRequestFactory.CreateEngine(request, lane, root,
                        scopeAuthority, profileId, message, continuation, limits, reportedTokens, images, files,
                        finishingCommit is not null ? NativeCommitFinishing.Metadata(request.Metadata)
                            : request.PlanMode ? Planning.CodingPlanMode.Metadata(request.Metadata) : request.Metadata);
                    using (credentialIntake?.Enter(intake))
                        stream = finishingCommit is null ? runner.RunAsync(engineRequest, attemptCt)
                            : NativeCommitFinishing.RunAsync(runner, engineRequest, finishingCommit, attemptCt);
                }

                yield return new CodingTurnProgress(orchestratorTurnId, lane.Name, requestStarted.Elapsed, completedTools, reportedTokens, "working");
                IAsyncEnumerator<TurnEvent> events;
                using (credentialIntake?.Enter(intake))
                    events = stream.GetAsyncEnumerator(attemptCt);
                IAsyncDisposable? ownership = null;
                try
                {
                    // A native lane runs outside AgentTurnRunner, so it takes the conversation's execution ownership
                    // here: it waits while a chat turn, a background continuation or another client owns the
                    // conversation, and holds it until the attempt ends. An engine lane's runner takes it itself.
                    if (lane.IsNative)
                    {
                        ownership = await TryAcquireOwnershipAsync(executions, request.ConversationId, attemptCt).ConfigureAwait(false);
                        if (ownership is null)
                        {
                            if (ct.IsCancellationRequested) cancelled = true;
                            else limitReason ??= "The configured job deadline was reached.";
                        }
                        // While this attempt owns the conversation no live turn here can have a worker running, so a
                        // worker still recorded for it belongs to a crashed or abandoned process and must be proven
                        // stopped before another one starts in the repository.
                        else if (!await Native.NativeLaneSupport.EnsurePreviousWorkerStoppedAsync(root, request.ConversationId, logger, attemptCt).ConfigureAwait(false))
                        {
                            yield return new TurnErrored(orchestratorTurnId, PreviousWorkerNotStopped);
                            yield break;
                        }
                        if (ownership is not null && confinedClaude)
                            nativeCommitSnapshot = await NativeCommitSnapshot.CaptureAsync(root, attemptCt).ConfigureAwait(false);
                    }
                    while (!lane.IsNative || ownership is not null)
                    {
                        TurnEvent evt;
                        Exception? fault = null;
                        // Async iterators restore the caller's ExecutionContext at each yield. Re-enter
                        // host evidence for every move so a later finisher/fallback sees the same receipt.
                        Task<bool> nextEvent;
                        using (credentialIntake?.Enter(intake))
                            nextEvent = events.MoveNextAsync().AsTask();
                        while (!nextEvent.IsCompleted)
                        {
                            var pulse = Task.Delay(TimeSpan.FromSeconds(2), attemptCt);
                            await Task.WhenAny(nextEvent, pulse).ConfigureAwait(false);
                            if (nextEvent.IsCompleted) break;
                            if (attemptCt.IsCancellationRequested)
                            {
                                break; // the await below observes cancellation and joins owned work
                            }
                            yield return new CodingTurnProgress(orchestratorTurnId, lane.Name, requestStarted.Elapsed,
                                completedTools, reportedTokens, "working");
                        }
                        try
                        {
                            if (!await nextEvent.ConfigureAwait(false)) break;
                            evt = events.Current;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; break; }
                        catch (OperationCanceledException) when (requestBudget.IsCancellationRequested)
                        { limitReason = "The configured job deadline was reached."; break; }
                        catch (OperationCanceledException) when (stuck) { break; }
                        catch (Exception ex) { fault = ex; evt = new TurnErrored(orchestratorTurnId, ex.Message, ex.GetType().Name); }

                        if (fault is not null)
                        {
                            logger.LogWarning(fault, "Lane {Lane} turn faulted", lane.Name);
                            error = fault.Message;
                            yield return evt;
                            break;
                        }

                        if (evt is ToolCallCompleted) completedTools++;
                        if (nativeCommitSnapshot is not null)
                            await nativeCommitSnapshot.ObserveAsync(evt, attemptCt).ConfigureAwait(false);
                        if (evt is TurnUsageReported usage)
                        {
                            reportedTokens = (reportedTokens ?? 0) + Math.Max(0, usage.InputTokens) + Math.Max(0, usage.OutputTokens);
                            if (usage.Provider.Equals(lane.Provider, StringComparison.OrdinalIgnoreCase)
                                && ModelResponseLearning.IsExactModel(usage.Model)) resolvedModel = usage.Model;
                        }
                        switch (evt)
                        {
                            case TurnCompleted done:
                                answerCompleted = true;
                                finalText = done.FinalText;
                                break;
                            case TurnErrored err:
                                error = err.Message;
                                category = err.FailureCategory;
                                break;
                            case TurnAbandoned:
                                cancelled = true;
                                break;
                            case ToolCallStarted started when string.Equals(started.ToolName, HarnessDeclineTool.ToolId, StringComparison.OrdinalIgnoreCase):
                                decline = HarnessDeclineTool.FromInput(started.Input) ?? decline;
                                break;
                            case ToolCallStarted codexMcp when Native.NativeToolSideEffects.IsCodexBridgeFileChange(codexMcp.ToolName, codexMcp.Input):
                                codexBridgeFileChanges.Add(codexMcp.ToolCallId);
                                break;
                            case ToolCallCompleted { IsError: false } tool when MutatingToolIds.Contains(tool.ToolName) || codexBridgeFileChanges.Contains(tool.ToolCallId):
                                mutated = true;
                                break;
                            case ToolCallCompleted { IsError: true } blocked when RepeatedToolCallGuard.IsRecoveryBlock(blocked.Result):
                                blockedCalls++;
                                if (blockedCalls >= Math.Max(1, coding.StuckAfterBlockedCalls))
                                {
                                    stuck = true;
                                    logger.LogInformation("Lane {Lane} kept repeating a failing call; stopping it for a hand-off.", lane.Name);
                                    yield return evt;
                                    attemptCts.Cancel();
                                    continue;
                                }
                                break;
                        }
                        if (!stuck && inspectionProgress.Observe(evt))
                        {
                            stuck = true;
                            error = "Repeated repository inspections returned no new evidence while the requested file changes remain unfinished.";
                            yield return evt; // preserve the last completed read receipt before cancelling
                            yield return new CodingLaneNotice(orchestratorTurnId, lane.Name,
                                "This model is repeating repository inspections without making progress.");
                            attemptCts.Cancel();
                            break;
                        }
                        // A native answer cannot claim completion before any requested host-side commit and audit.
                        if (confinedClaude && evt is TurnCompleted nativeDone) deferredNativeAnswer = nativeDone;
                        else yield return evt;
                        if (evt is ToolCallCompleted or TurnUsageReported)
                            yield return new CodingTurnProgress(orchestratorTurnId, lane.Name, requestStarted.Elapsed,
                                completedTools, reportedTokens, "working");
                        if (limits.MaxToolCalls > 0 && completedTools >= limits.MaxToolCalls)
                            limitReason = "The configured completed-tool budget was reached.";
                        if (limits.MaxCumulativeTokens > 0 && reportedTokens >= limits.MaxCumulativeTokens)
                            limitReason = "The configured reported-token budget was reached.";
                        if (limitReason is not null) { attemptCts.Cancel(); break; }
                    }
                    // A budget or deadline stop cancelled the attempt token and ends in CodingTurnLimitReached below,
                    // so only an attempt that ran to its end proves its evidence, on the request's own budget.
                    if (nativeCommitSnapshot?.HasSuccessfulWrites == true && !cancelled && !stuck && error is null
                        && limitReason is null && !requestBudget.IsCancellationRequested)
                    {
                        try { finishingCommit = await nativeCommitSnapshot.FinishAsync(request.ConversationId, requestBudget.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
                        catch (OperationCanceledException) when (requestBudget.IsCancellationRequested)
                        { limitReason = "The configured job deadline was reached."; }
                    }
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(false);
                    if (ownership is not null) await ownership.DisposeAsync().ConfigureAwait(false);
                }

                if (cancelled || ct.IsCancellationRequested) yield break;
                if (limitReason is not null || requestBudget.IsCancellationRequested)
                {
                    yield return new CodingTurnLimitReached(orchestratorTurnId, limitReason ?? "The configured job deadline was reached.");
                    yield break;
                }

                if (!answerCompleted && error is null && !stuck && decline is null)
                    error = "The lane stopped without completing an answer; task completion is unverified.";
                var outcome = RefusalDetector.Classify(finalText, error, mutated, category, decline, stuck);
                if (outcome == LaneOutcomeKind.Delivered && request.RequireWorkspaceChange && !request.PlanMode && !mutated)
                {
                    if (!requestBudget.IsCancellationRequested && workspaceRepairAttempts < 1)
                    {
                        workspaceRepairAttempts++;
                        message = "[Workspace repair] The user asked for repository file changes, but no mutating tool completed. "
                            + "Your previous prose or code block did not edit a file. Inspect the repository and use the available file tools "
                            + "to apply the requested change. If this model cannot invoke those tools, say so plainly. "
                            + "Do not claim that files were changed without a successful tool receipt.\nOriginal request:\n"
                            + (request.AuthenticatedOriginalUserMessage ?? request.UserMessage);
                        continuation = true;
                        yield return new CodingLaneNotice(orchestratorTurnId, lane.Name,
                            "The answer did not change repository files; asking the selected model to apply the edit with tools.");
                        continue;
                    }
                    error = "The requested repository changes are unfinished: no file-changing tool completed after a repair attempt.";
                    completionFailed = true;
                    outcome = LaneOutcomeKind.Stuck;
                    if (!handOff)
                    {
                        yield return new TurnErrored(orchestratorTurnId, error) { ErrorKind = TurnErrorKind.CompletionValidation };
                        yield break;
                    }
                }
                var observationId = orchestratorTurnId + ":" + attempt + ":" + repairAttempts;
                var observedModel = resolvedModel ?? lane.Model;
                var evidenceKind = decline is not null ? "harness-decline" : category == LlmFailureCategory.ContentPolicy
                    ? "provider-policy" : "host-classifier-v2";
                if (outcome != LaneOutcomeKind.Delivered)
                {
                    var responseOutcome = outcome switch
                    {
                        LaneOutcomeKind.Refused => ModelResponseOutcome.Declined,
                        LaneOutcomeKind.PartialRefusal => ModelResponseOutcome.PartiallyDeclined,
                        _ => ModelResponseOutcome.RuntimeFailed,
                    };
                    if (await RememberResponseAsync(scope.ServiceProvider, responseMemoryContext, observationId, lane.Provider,
                        observedModel, responseOutcome, evidenceKind).ConfigureAwait(false) is { } warning)
                        yield return new CodingLaneNotice(orchestratorTurnId, lane.Name, warning);
                }
                if (CodingRoutingPolicy.StopsOnRefusal(request, outcome))
                {
                    if (deferredNativeAnswer is not null) yield return deferredNativeAnswer;
                    yield return new CodingLanesExhausted(orchestratorTurnId,
                        $"{lane.Name} declined the request. Automatic routing stopped; the selected model and any completed work are retained.")
                        { ReassessModel = false };
                    yield break;
                }
                if (outcome is LaneOutcomeKind.Refused or LaneOutcomeKind.PartialRefusal && !string.IsNullOrWhiteSpace(finalText))
                {
                    var rewriteExpired = false;
                    try { await NeutralizeAsync(scope.ServiceProvider, request.ConversationId, finalText, requestBudget.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
                    catch (OperationCanceledException) when (requestBudget.IsCancellationRequested) { rewriteExpired = true; }
                    if (rewriteExpired)
                    {
                        yield return new CodingTurnLimitReached(orchestratorTurnId, "The configured job deadline was reached.");
                        yield break;
                    }
                }

                if (confinedClaude && nativeCommitSnapshot?.HasSuccessfulWrites == true && finishingCommit is not null
                    && outcome is LaneOutcomeKind.Delivered or LaneOutcomeKind.PartialRefusal)
                {
                    var engine = !request.SelectionPinned && attempt < Math.Max(1, options.MaxLanesPerRequest)
                        ? options.Lanes.FirstOrDefault(candidate => !candidate.IsNative && !CodingRoutingPolicy.WasAttempted(candidate, tried)
                            && !_health.IsUnavailable(candidate.HealthKeyOrName, out _, options.HealthScope)) : null;
                    if (engine is null)
                    {
                        yield return new CodingLaneNotice(orchestratorTurnId, lane.Name,
                            "Native edits are saved, but no admitted engine lane is available to finish the original local-git request. No automatic commit was made.");
                        yield return new TurnErrored(orchestratorTurnId, "Native edits require a governed finishing step; completion has not been verified.")
                        { ErrorKind = TurnErrorKind.CompletionValidation };
                        yield break;
                    }
                    yield return new CodingLaneHandOff(orchestratorTurnId, lane, engine,
                        "saved edits; finishing the original request with governed local git tools") { Outcome = LaneOutcomeKind.PartialRefusal };
                    message = NativeCommitFinishing.Prompt(request.UserMessage, outcome == LaneOutcomeKind.PartialRefusal ? NeutralStopNote : finalText ?? "", finishingCommit);
                    tried.Add(engine); lane = engine; attempt++; continuation = true;
                    continue;
                }
                if (deferredNativeAnswer is not null) yield return deferredNativeAnswer;
                if (outcome == LaneOutcomeKind.Delivered)
                {
                    _health.Clear(lane.HealthKeyOrName, options.HealthScope);
                    // A plan-mode turn ends with the proposed plan: nothing changed, so there is nothing to verify yet.
                    if (request.PlanMode) yield break;
                    yield return new CodingTurnProgress(orchestratorTurnId, lane.Name, requestStarted.Elapsed, completedTools, reportedTokens, "verifying");
                    var verifier = scope.ServiceProvider.GetService<ICodingAcceptanceVerifier>();
                    CodingAcceptanceResult acceptance;
                    try
                    {
                        acceptance = verifier is null
                            ? acceptanceCommands.Count == 0 ? CodingAcceptanceResult.Unverified
                                : new(CodingAcceptanceStatus.Failed, "Configured acceptance checks have no registered verifier.", [])
                            : await VerifyInWorkspaceAsync(verifier, new(request.ConversationId, root, request.UserMessage,
                                finalText ?? "", lane) { Commands = workspace.AcceptanceChecks }, scopeAuthority, profileId, executions, requestBudget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
                    catch (OperationCanceledException) { acceptance = new(CodingAcceptanceStatus.Failed, "Verification did not finish before the job deadline.", []); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Host acceptance could not verify the coding turn");
                        acceptance = new(CodingAcceptanceStatus.Failed, "Host acceptance could not verify completion.", []);
                    }
                    yield return new CodingAcceptanceEvaluated(orchestratorTurnId, acceptance);
                    var responseOutcome = acceptance.Status switch
                    {
                        CodingAcceptanceStatus.Passed => ModelResponseOutcome.Verified,
                        CodingAcceptanceStatus.Failed => ModelResponseOutcome.AcceptanceFailed,
                        _ => ModelResponseOutcome.AnsweredUnverified,
                    };
                    if (await RememberResponseAsync(scope.ServiceProvider, responseMemoryContext, observationId, lane.Provider,
                        observedModel, responseOutcome, "host-acceptance:" + acceptance.Status).ConfigureAwait(false) is { } warning)
                        yield return new CodingLaneNotice(orchestratorTurnId, lane.Name, warning);
                    if (acceptance.Status != CodingAcceptanceStatus.Failed) yield break;
                    if (!requestBudget.IsCancellationRequested && repairAttempts < Math.Max(0, coding.Acceptance.MaxRepairAttempts))
                    {
                        repairAttempts++;
                        message = "[Acceptance repair] The host verification failed. Inspect the saved evidence, repair the remaining issue and rerun the required checks. Do not repeat completed external actions.\n"
                            + acceptance.Summary + "\n" + string.Join("\n", acceptance.Checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Evidence))
                            + "\nOriginal request:\n" + request.UserMessage;
                        continuation = true;
                        yield return new CodingTurnProgress(orchestratorTurnId, lane.Name, requestStarted.Elapsed, completedTools, reportedTokens, "repairing");
                        continue;
                    }
                    error = "Task completion failed host acceptance: " + acceptance.Summary;
                    completionFailed = true;
                    if (!handOff)
                    {
                        yield return new TurnErrored(orchestratorTurnId, error) { ErrorKind = TurnErrorKind.CompletionValidation };
                        yield break;
                    }
                    outcome = LaneOutcomeKind.Failed;
                }

                if (outcome == LaneOutcomeKind.RouteUnavailable)
                    _health.MarkUnavailable(lane.HealthKeyOrName, Short(error) is var e && e != "unknown error" ? e : "route unavailable", options.UnavailableLaneCooldown, options.HealthScope);

                var reason = DescribeOutcome(outcome, error, decline);

                if (!fallbackCatalogLoaded && handOff && finishingCommit is null
                    && (outcome is LaneOutcomeKind.RouteUnavailable or LaneOutcomeKind.Refused or LaneOutcomeKind.PartialRefusal
                        or LaneOutcomeKind.Stuck or LaneOutcomeKind.Exhausted or LaneOutcomeKind.Failed)
                    && attempt < Math.Max(1, options.MaxLanesPerRequest))
                {
                    fallbackCatalogLoaded = true;
                    IReadOnlyList<CodingLane> discovered = [];
                    string? discoveryFailure = null;
                    string? historyWarning = null;
                    try
                    {
                        using var discoveryScope = scopes.CreateScope();
                        using var discoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(requestBudget.Token);
                        discoveryDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                        if (discoveryScope.ServiceProvider.GetService<ICodingFallbackDiscovery>() is { } discovery)
                        {
                            var discoveryResult = await discovery.DiscoverAsync(options.Lanes, tried, responseMemoryContext, discoveryDeadline.Token).ConfigureAwait(false);
                            discovered = discoveryResult.Candidates;
                            historyWarning = discoveryResult.HistoryWarning;
                        }
                    }
                    catch (OperationCanceledException) when (requestBudget.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Fallback catalog discovery failed"); discoveryFailure = "Fallback model discovery is unavailable; continuing with configured lanes."; }
                    if (discoveryFailure is not null) yield return new CodingLaneNotice(orchestratorTurnId, lane.Name, discoveryFailure);
                    if (historyWarning is not null) yield return new CodingLaneNotice(orchestratorTurnId, lane.Name, historyWarning);
                    if (discovered.Count > 0)
                    {
                        options = options with { Lanes = [.. tried, .. discovered, .. options.Lanes.Where(l => !tried.Contains(l)
                            && !discovered.Any(d => d.Provider == l.Provider && d.Model == l.Model))] };
                        yield return new CodingLaneNotice(orchestratorTurnId, lane.Name,
                            "Discovered alternative catalog models: " + string.Join(", ", discovered.Select(l => l.Route))
                            + (responseMemoryContext.UseMemory && historyWarning is null ? ". Recent response history is considered; unknown models remain candidates." : ". Response history is off or unavailable for this turn.")
                            + " Acceptance will be checked from their actual results.");
                    }
                }

                CodingLane? next = null;
                if (handOff && attempt < Math.Max(1, options.MaxLanesPerRequest))
                {
                    foreach (var candidate in CodingRoutingPolicy.HandOffCandidates(options.Lanes, tried, outcome))
                    {
                        if (finishingCommit is not null && candidate.IsNative) continue;
                        if (NativeBlock(candidate) is { } blockedNext)
                        {
                            yield return new CodingLaneSkipped(orchestratorTurnId, candidate, blockedNext);
                            continue;
                        }
                        if (_health.IsUnavailable(candidate.HealthKeyOrName, out var why, options.HealthScope))
                        {
                            yield return new CodingLaneSkipped(orchestratorTurnId, candidate, why);
                            continue;
                        }
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    if (completionFailed)
                        yield return new TurnErrored(orchestratorTurnId, error!) { ErrorKind = TurnErrorKind.CompletionValidation };
                    yield return new CodingLanesExhausted(orchestratorTurnId, $"{lane.Name} {reason}; "
                        + (request.SelectionPinned ? "your explicit model choice is retained; automatic switching is disabled."
                            : attempt >= Math.Max(1, options.MaxLanesPerRequest) ? "the configured lane-attempt limit was reached." : "no other lane available."));
                    yield break;
                }

                yield return new CodingLaneHandOff(orchestratorTurnId, lane, next, reason) { Outcome = outcome };
                logger.LogInformation("Handing off from lane {From} to {To}: {Reason}", lane.Name, next.Name, reason);
                // A finishing lane can be unavailable before doing any work. Its own mutation flag does not
                // describe the native work already saved. Keep the finishing evidence (or acceptance repair)
                // with the restricted commit scope rather than rebuilding an original-request-only hand-off.
                if (finishingCommit is null)
                    message = BuildHandOffMessage(request.UserMessage, lane, next, outcome, mutated, decline?.Remaining)
                        + (error is null || outcome is LaneOutcomeKind.Refused or LaneOutcomeKind.PartialRefusal
                            ? "" : "\nPrevious attempt's host evidence:\n" + error);
                tried.Add(next);
                lane = next;
                attempt++;
                continuation = true;
            }
        }
    }

    private async Task<string?> RememberResponseAsync(IServiceProvider services, CodingFallbackContext context,
        string id, string provider, string model, ModelResponseOutcome outcome, string evidenceKind)
    {
        if (!context.UseMemory || !ModelResponseLearning.IsExactModel(model)
            || services.GetService<IModelResponseMemory>() is not { } memory) return null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await memory.AppendAsync(new ModelResponseObservation(id, context.ScopeId, provider, model,
                context.TaskFingerprint, outcome, DateTimeOffset.UtcNow, evidenceKind), deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save model response history for {Provider}/{Model}", provider, model);
            return "Model response history could not be saved. The current result is retained; no action will be repeated to recover history.";
        }
    }

    /// <summary>
    /// Host acceptance runs after the agent turn has ended, so no turn context is ambient. Run it inside
    /// one that carries the workspace root (the same mechanism tools use), so sandbox-resolving tools such
    /// as <c>shell.exec</c> treat the workspace, not the host's configured sandbox root, as their root.
    /// The context lives only in this async method's flow and is gone when it returns.
    /// </summary>
    public static async Task<CodingAcceptanceResult> VerifyInWorkspaceAsync(ICodingAcceptanceVerifier verifier,
        CodingAcceptanceContext context, AgentCommunicationScope scope, Guid profileId, CancellationToken ct)
    {
        new AsyncLocalCurrentTurnContextAccessor().Current = new CurrentTurnContext(context.ConversationId, null, profileId,
            context.Lane.Provider, context.Lane.Model,
            CommunicationScopeKind: scope.Kind, CommunicationScopeAuthority: scope.Authority,
            CommunicationScopeUserInitiated: scope.UserInitiated)
        { WorkspaceRoot = context.RepositoryRoot };
        return await verifier.VerifyAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Host acceptance under the conversation's execution ownership: the checks run while no other turn (chat,
    /// background continuation, another client) executes in the conversation, and the ownership is released
    /// when they finish or are cancelled. Callers must not already hold it.
    /// </summary>
    public static async Task<CodingAcceptanceResult> VerifyInWorkspaceAsync(ICodingAcceptanceVerifier verifier,
        CodingAcceptanceContext context, AgentCommunicationScope scope, Guid profileId, AgentConversationExecutionGate? executions,
        CancellationToken ct)
    {
        await using var owned = await (executions ?? AgentConversationExecutionGate.Shared).AcquireAsync(context.ConversationId, ct).ConfigureAwait(false);
        return await VerifyInWorkspaceAsync(verifier, context, scope, profileId, ct).ConfigureAwait(false);
    }

    internal const string PreviousWorkerNotStopped =
        "A previous native worker for this conversation could not be proven stopped. Inspect the existing work before continuing.";

    /// <returns>The ownership lease, or null when the attempt was cancelled while waiting for it.</returns>
    private static async Task<IAsyncDisposable?> TryAcquireOwnershipAsync(AgentConversationExecutionGate executions, string conversationId,
        CancellationToken ct)
    {
        try { return await executions.AcquireAsync(conversationId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Default lane first, then the rest in priority order.</summary>
    private static IEnumerable<CodingLane> DefaultOrder(CodingLaneSet set)
    {
        var def = set.Resolve(null);
        if (def is not null) yield return def;
        foreach (var lane in set.Lanes)
            if (!ReferenceEquals(lane, def) && !lane.Equals(def)) yield return lane;
    }

    private async Task NeutralizeAsync(IServiceProvider services, string conversationId, string refusalText, CancellationToken ct)
    {
        var rewriter = services.GetService<ITranscriptRewriter>();
        if (rewriter is null) return;
        try
        {
            var executions = services.GetService<AgentConversationExecutionGate>() ?? AgentConversationExecutionGate.Shared;
            await using var ownership = await executions.AcquireAsync(conversationId, ct).ConfigureAwait(false);
            await rewriter.ReplaceLastAssistantTextAsync(conversationId, refusalText, NeutralStopNote, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not neutralize the previous lane's reply in the transcript for {Conversation}", conversationId);
        }
    }

    /// <summary>Human-readable, neutral wording: never tells the next model that a policy was involved.</summary>
    internal static string DescribeOutcome(LaneOutcomeKind outcome, string? error, DeclineSignal? decline) => outcome switch
    {
        LaneOutcomeKind.Refused => "stopped without completing the request",
        LaneOutcomeKind.PartialRefusal => "completed part of the request and stopped",
        LaneOutcomeKind.RouteUnavailable => "is unavailable: " + Short(error),
        LaneOutcomeKind.Exhausted => "ran out of time or context: " + Short(error),
        LaneOutcomeKind.Stuck => "kept repeating a failing action",
        _ => "failed: " + Short(error),
    };

    internal static string BuildHandOffMessage(string original, CodingLane from, CodingLane to, LaneOutcomeKind outcome, bool mutated, string? remaining)
    {
        var progress = mutated
            ? to.IsNative
                ? "Earlier tool calls succeeded. Inspect their receipts to establish what completed; if repository edits are relevant, inspect git status and git diff before changing files. Do not repeat completed actions."
                : "Earlier tool receipts in this conversation show work already completed; build on it and do not redo it."
            : "No successful workspace-changing tool call was recorded. Inspect existing receipts before repeating an action.";
        var scope = remaining is not null
            ? $"Remaining work: {remaining}\n"
            : outcome == LaneOutcomeKind.PartialRefusal
                ? "Finish only what is still undone from the original request below.\n"
                : "";
        return $"[Hand-off] You are {to.Name} ({to.Route}), continuing a request that a previous attempt did not finish.\n"
             + $"{progress}\n{scope}"
             + "A tool call without a saved result has an uncertain outcome. Do not repeat an uncertain external action; verify it through a read-only check first.\n"
             + "Evaluate the original request on its own terms. Prior model assumptions are not user requirements or verified facts. Do not substitute an unrelated task for the requested outcome.\n"
             + "If you also cannot do part of it, call harness.decline (or say so in one plain sentence first) and complete the rest.\n\n"
             + "Original request:\n" + original;
    }

    private static async IAsyncEnumerable<TurnEvent> Single(TurnEvent evt)
    {
        await Task.Yield();
        yield return evt;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Short(string? s) => string.IsNullOrWhiteSpace(s) ? "unknown error" : s.Length <= 200 ? s.Trim() : s[..200].Trim() + "...";
}
