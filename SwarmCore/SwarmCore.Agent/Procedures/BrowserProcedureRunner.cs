using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Procedures;

/// <summary>Host proof that a browser procedure reached its goal (signed in, data read, ...).</summary>
public sealed record BrowserProcedureProof(bool Succeeded, string Status, string? Answer = null);

/// <summary>The host browser operations a learned browser procedure drives, bound to one host scope and credential.</summary>
public sealed record BrowserProcedureSurfaceAccess(
    Func<CancellationToken, Task<BrowserProcedureBindingResult>> Bind,
    Func<string, CancellationToken, Task<BrowserProcedureObservation>> Observe,
    Func<string, BrowserProcedureStep, CancellationToken, Task<BrowserProcedureStepResult>> Execute,
    Func<string, CancellationToken, Task<bool>> Complete);

/// <summary>
/// What a browser procedure is for. The host owns every value here: the tool names, the
/// proof (<see cref="Verify"/>), the prompt and the identities bound into the saved procedure.
/// </summary>
public sealed record BrowserProcedureTask
{
    /// <summary>Tool-id prefix, e.g. "login" gives login.observe / login.step / login.verify.</summary>
    public required string ToolPrefix { get; init; }
    public required string OwnerId { get; init; }
    public required string ProfileName { get; init; }
    public required string SystemPrompt { get; init; }
    public required string Request { get; init; }
    public required string ObserveDescription { get; init; }
    public required string StepDescription { get; init; }
    public required string VerifyDescription { get; init; }
    /// <summary>Text bound (with the scope) into the procedure's binding fingerprint.</summary>
    public required string BindingMaterial { get; init; }
    /// <summary>Verifier id and evidence text recorded when <see cref="Verify"/> proves success.</summary>
    public required string VerifierId { get; init; }
    public required string EvidenceMaterial { get; init; }
    /// <summary>Host proof, read-only: it may never act on the page.</summary>
    public required Func<CancellationToken, Task<BrowserProcedureProof>> Verify { get; init; }
    public required string LayoutChangedStatus { get; init; }
    public required string LearningUnavailableStatus { get; init; }
    public required string SuccessMessage { get; init; }
    public IReadOnlyCollection<string> ExtraTerminalStatuses { get; init; } = [];
    public int MaxIterations { get; init; } = 18;
    public TimeSpan HardTimeout { get; init; } = TimeSpan.FromMinutes(6);
}

/// <summary>
/// The one browser adapter for the shared learned-procedure engine: bind the host browser,
/// resolve an uncertain earlier attempt only through read-only proof (or the exact retained
/// attempt's confirmed actions), learn the page sequence with the model once, replay the saved
/// procedure without model calls, and accept a revision only when the host proof succeeds.
/// Sign-in and site tasks both use it, so recovery semantics live in one place.
/// </summary>
public sealed class BrowserProcedureRunner(IDurableAgentSessionRunner runner)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ContinuedAttemptVerifier = "continued-browser-attempt-v1";

    public sealed record EmptyInput;
    public sealed record StepInput(BrowserProcedureActionKind Kind, BrowserProcedureTarget? Target = null);
    public sealed record StepEvidence(BrowserProcedureStepResult Outcome) : IAgentProcedureEffectEvidence
    {
        public bool Succeeded => Outcome.Success && !Outcome.OutcomeUncertain;
        public bool NoPhysicalAction => !Outcome.Dispatched;
        public bool LocalProcessCompleted => false;
        public bool OutcomeUncertain => Outcome.OutcomeUncertain;
    }

    private sealed class TraceCompiler : IAgentProcedurePlanner
    {
        public Task<AgentProcedureCandidate?> CreateCandidateAsync(AgentProcedureContext context,
            AgentProcedureRunEvidence evidence, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Semantic targets are learned constants, not array offsets into a later page's controls.
            return Task.FromResult<AgentProcedureCandidate?>(new(evidence.Receipts.Where(receipt => !receipt.IsError).Select(receipt =>
                new AgentProcedureStep(receipt.ToolId, receipt.Arguments.DeepClone().AsObject(), [])).ToArray()));
        }
    }

    public async Task<BrowserProcedureProof> RunAsync(
        BrowserProcedureSurfaceAccess browser,
        BrowserProcedureScope scope,
        AgentProcedureKey key,
        IAgentProcedureStore store,
        BrowserProcedureTask task,
        CancellationToken ct = default)
    {
        bool Terminal(string status) => IsTerminal(status) || task.ExtraTerminalStatuses.Contains(status);
        AgentProcedureVerification Proven() => new(task.VerifierId, Hash(task.EvidenceMaterial), DateTimeOffset.UtcNow);
        var stepTool = task.ToolPrefix + ".step";
        var verifyTool = task.ToolPrefix + ".verify";

        var fingerprint = Hash(task.BindingMaterial + "\n" + JsonSerializer.Serialize(scope, Json));
        var previous = await store.GetAsync(key, ct).ConfigureAwait(false);
        if (previous is { ActiveRevisionId: null } && previous.Revisions.LastOrDefault() is
            { FailureCode: "outcome_uncertain" or "effect_checkpoint_required" } unresolved
            && unresolved.BindingFingerprint != fingerprint)
            // A different account or consent contract cannot inspect the old action's outcome.
            // Keep the stable job's barrier before opening or binding another browser attempt.
            return new(false, "OutcomeUncertain");

        var binding = await browser.Bind(ct).ConfigureAwait(false);
        if (!binding.Success || binding.BindingId is null)
        {
            if (binding.Status != "OutcomeUncertain") return new(false, binding.Status);
            // A retained browser may know a dispatch was uncertain before it can issue a new binding.
            // Read-only inspection can resolve it; never reopen or resubmit to obtain proof.
            var inspected = await task.Verify(ct).ConfigureAwait(false);
            if (!inspected.Succeeded) return new(false, "OutcomeUncertain");
            var pendingHistory = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (pendingHistory is { ActiveRevisionId: null }
                && pendingHistory.Revisions.LastOrDefault()?.BindingFingerprint == fingerprint)
                await AgentProcedureRuntime.ResolveUncertaintyAsync(store, key, pendingHistory.Version, Proven(), ct).ConfigureAwait(false);
            if (binding.BindingId is not null)
                await browser.Complete(binding.BindingId, ct).ConfigureAwait(false);
            return inspected;
        }

        var bindingId = binding.BindingId;
        var initial = binding.Observation ?? await browser.Observe(bindingId, ct).ConfigureAwait(false);
        if (Terminal(initial.Status) && initial.Status != "OutcomeUncertain") return new(false, initial.Status);
        var already = await task.Verify(ct).ConfigureAwait(false);
        var history = await store.GetAsync(key, ct).ConfigureAwait(false);
        IReadOnlyList<BrowserProcedureConfirmedAction> confirmedPrefix = [];
        AgentProcedureRecovery? recovered = null;
        if (history is { ActiveRevisionId: null } && history.Revisions.LastOrDefault() is
            { FailureCode: "outcome_uncertain" or "effect_checkpoint_required" or "outcome_inspected" } pending
            && pending.BindingFingerprint == fingerprint
            && (pending.FailureCode != "outcome_inspected" || pending.Verification?.VerifierId == ContinuedAttemptVerifier))
        {
            // Confirmed partial work can continue in the exact retained browser attempt. Its action
            // reservations stay intact. A new browser/process, unknown receipt, changed binding, or
            // crash checkpoint can never acquire this continuation proof.
            var continued = binding is { Status: "Resumed", Recovery: { } recovery }
                && (pending.FailureCode == "effect_checkpoint_required"
                    // A later proven-no-action failure can checkpoint the earlier confirmed effects.
                    // The retained attempt must predate that checkpoint; a fresh process cannot qualify.
                    && pending.CreatedAt >= recovery.StartedAt && pending.CreatedAt <= DateTimeOffset.UtcNow
                    || pending.FailureCode == "outcome_inspected"
                    && pending.Verification?.EvidenceHash == recovery.ReceiptFingerprint);
            if (!already.Succeeded && !continued) return new(false, "OutcomeUncertain");
            if (continued) confirmedPrefix = binding.Recovery!.Actions;
            var inspection = pending.FailureCode == "outcome_inspected" ? pending.Verification! : already.Succeeded
                ? Proven()
                : new AgentProcedureVerification(ContinuedAttemptVerifier, binding.Recovery!.ReceiptFingerprint, DateTimeOffset.UtcNow);
            var inspectedVersion = history.Version;
            if (pending.FailureCode != "outcome_inspected")
            {
                if (!await AgentProcedureRuntime.ResolveUncertaintyAsync(store, key, history.Version, inspection, ct).ConfigureAwait(false))
                    return new(false, "OutcomeUncertain");
                inspectedVersion++;
            }
            if (!continued && !already.Succeeded)
                return new(false, "OutcomeUncertain");
            if (continued && !already.Succeeded)
                recovered = new(inspectedVersion, fingerprint, inspection, confirmedPrefix.Select(action =>
                    new AgentProcedureToolReceipt(stepTool,
                        JsonSerializer.SerializeToNode(new StepInput(action.Kind, action.Target), Json)!.AsObject(), null,
                        IsError: false, WasDispatched: true, MayHaveSideEffects: true,
                        HostCompletionVerified: true, OutcomeUncertain: false)).ToArray());
        }
        if (already.Succeeded)
        {
            await browser.Complete(bindingId, ct).ConfigureAwait(false);
            return already;
        }
        if (Terminal(already.Status)) return already;
        if (initial.Status == "OutcomeUncertain") return new(false, initial.Status);

        // The opaque credential binding stays with the host, outside model arguments and persisted steps.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(task.HardTimeout);
        string? stopped = null;
        var latestProof = new BrowserProcedureProof(false, task.LearningUnavailableStatus);
        void CheckStatus(string status)
        {
            if (!Terminal(status)) return;
            stopped = status;
            budget.Cancel();
            throw new OperationCanceledException(budget.Token);
        }
        async Task<BrowserProcedureObservation> Observe(CancellationToken token)
        {
            var observation = await browser.Observe(bindingId, token).ConfigureAwait(false);
            CheckStatus(observation.Status);
            return observation;
        }
        async Task<BrowserProcedureObservation> ObserveReady(StepInput input, CancellationToken token)
        {
            var observation = await Observe(token).ConfigureAwait(false);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var matches = observation.Controls.Where(c => c.Target == input.Target).ToArray();
                var transient = observation.Status == "PageChanged" || matches is [{ Enabled: false }]
                    || input.Target is not null && matches.Length == 0;
                if (!transient) break;
                // Waiting is read-only. Never repeat a physical fill or click to test readiness.
                await Task.Delay(400, token).ConfigureAwait(false);
                observation = await Observe(token).ConfigureAwait(false);
            }
            return observation;
        }
        async Task<BrowserProcedureProof> VerifyReady(CancellationToken token)
        {
            var proof = await task.Verify(token).ConfigureAwait(false);
            for (var attempt = 0; attempt < 10 && !proof.Succeeded && !Terminal(proof.Status); attempt++)
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                proof = await task.Verify(token).ConfigureAwait(false);
            }
            CheckStatus(proof.Status);
            return proof;
        }

        var tools = new[]
        {
            ToolDescriptor.Create<EmptyInput, BrowserProcedureObservation>(task.ToolPrefix + ".observe", task.OwnerId,
                task.ObserveDescription, ToolCostClass.External, ToolSideEffect.ReadOnly, (_, token) => Observe(token)),
            ToolDescriptor.Create<StepInput, StepEvidence>(stepTool, task.OwnerId,
                task.StepDescription, ToolCostClass.External, ToolSideEffect.ExternalSend, async (input, token) =>
                {
                    var observation = await ObserveReady(input, token).ConfigureAwait(false);
                    var result = await browser.Execute(bindingId,
                        new() { ObservationId = observation.ObservationId, Kind = input.Kind, Target = input.Target }, token).ConfigureAwait(false);
                    // Return the physical receipt before stopping so restart recovery retains its outcome.
                    if (Terminal(result.Status)) stopped = result.Status;
                    if (result.OutcomeUncertain) stopped = "OutcomeUncertain";
                    if (result.Observation is { } next && Terminal(next.Status)) stopped = next.Status;
                    return new StepEvidence(result);
                }),
            ToolDescriptor.Create<EmptyInput, BrowserProcedureProof>(verifyTool, task.OwnerId,
                task.VerifyDescription, ToolCostClass.External, ToolSideEffect.ReadOnly, async (_, token) =>
                {
                    latestProof = await VerifyReady(token).ConfigureAwait(false);
                    return latestProof;
                })
        };
        var context = new AgentProcedureContext
        {
            Key = key, Store = store, Inputs = new(), Planner = new TraceCompiler(), Recovery = recovered,
            BindingFingerprint = fingerprint,
            AllowTool = (tool, _) => tools.Any(t => t.ToolId == tool.ToolId),
            AllowLiteral = (tool, pointer, value) => tool == stepTool &&
                (pointer == "/kind" || pointer == "/target" && value is null || pointer.StartsWith("/target/", StringComparison.Ordinal)) &&
                (value is null || value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length <= 160),
            CheckPreconditionAsync = async (step, args, token) =>
            {
                if (step.ToolId != stepTool) return true;
                var input = args.Deserialize<StepInput>(Json);
                if (input is null) return false;
                var observation = await ObserveReady(input, token).ConfigureAwait(false);
                return input.Kind is BrowserProcedureActionKind.Wait or BrowserProcedureActionKind.NavigateStart
                    || observation.Controls.Count(c => c.Enabled && !c.ReadOnly && c.Target == input.Target && c.Actions.Contains(input.Kind)) == 1;
            },
            VerifyAsync = async (evidence, token) =>
            {
                if (evidence.Receipts.LastOrDefault()?.ToolId != verifyTool
                    || evidence.Receipts[^1].IsError
                    || !latestProof.Succeeded) return null;
                latestProof = await VerifyReady(token).ConfigureAwait(false);
                return latestProof.Succeeded ? Proven() : null;
            },
            FormatResult = _ => latestProof.Answer ?? task.SuccessMessage
        };
        var session = new AgentSession
        {
            ConversationId = Guid.NewGuid().ToString("N"), UserRequestAuthorized = true,
            Profile = new()
            {
                Id = Guid.NewGuid(), Name = task.ProfileName, EnabledToolIds = tools.Select(t => t.ToolId).ToArray(),
                MaxIterations = task.MaxIterations, HardTimeout = task.HardTimeout,
                SystemPrompt = task.SystemPrompt
            },
            Transcript = [], Tools = tools, ProcedureContext = context
        };
        try
        {
            var request = task.Request;
            if (confirmedPrefix.Count > 0) request += " Host-confirmed actions in this retained attempt (do not repeat): "
                + JsonSerializer.Serialize(confirmedPrefix, Json);
            await foreach (var item in runner.RunAsync(session, request,
                DurableAgentSessionTurn.Create(session.ConversationId), budget.Token).ConfigureAwait(false))
            {
                if (item is ToolCallCompletedEvent && stopped is not null)
                {
                    budget.Cancel();
                    return new(false, stopped);
                }
                if (item is ErroredEvent) break;
                if (item is FinishedEvent)
                {
                    latestProof = await VerifyReady(budget.Token).ConfigureAwait(false);
                    if (latestProof.Succeeded) await context.AcceptAsync(budget.Token).ConfigureAwait(false);
                    else context.Reject();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { context.Reject(); return new(false, stopped ?? task.LayoutChangedStatus); }
        finally { context.Reject(); }
        if (latestProof.Succeeded)
            await browser.Complete(bindingId, ct).ConfigureAwait(false);
        return latestProof;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Statuses that stop a browser procedure: a host limitation, not a page to retry.</summary>
    public static bool IsTerminal(string status) => status is "LoginTemporarilyLimited" or "VerificationRequired"
        or "RecoveryBlocked" or "ScopeChanged" or "AccountMismatch" or "OutcomeUncertain"
        or "SiteTermsNeedUserAction" or "SiteTermsChangedNeedUserAction" or "ConsentRequired" or "ConsentChanged"
        or "BindingChangeNeedsNewSession" or "StepLimitReached" or "BindingUnavailable" or "BrowserUnavailable" or "Denied";
}
