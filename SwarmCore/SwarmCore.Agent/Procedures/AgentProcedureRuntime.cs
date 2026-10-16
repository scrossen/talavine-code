using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Learning;

namespace SwarmCore.Agent.Procedures;

/// <summary>
/// Learns parameterized traces and supplies calls to the ordinary engine dispatch. It never invokes
/// a descriptor, saves an answer, or treats a model's claim of success as verification.
/// </summary>
public sealed class AgentProcedureRuntime
{
    private readonly AgentProcedureContext _context;
    private readonly JsonObject _inputs;
    private readonly IReadOnlyDictionary<string, ToolDescriptor> _tools;
    private readonly string _toolFingerprint;
    private AgentProcedureHistory? _history;
    private AgentProcedureRevision? _replay;
    private readonly List<AgentProcedureToolReceipt> _receipts = [];
    private int _cursor;
    private bool _disabled;
    private bool _completed;
    private string? _stopReason;
    private bool _hasEffectCheckpoint;
    private bool _hasIntentHistory;
    private bool _hasUncertainOutcome;
    private Guid? _intentCheckpointId;
    private readonly HashSet<Guid> _ownedCheckpoints = [];
    private readonly HashSet<string> _pendingDispatches = new(StringComparer.Ordinal);
    private bool _replayFinished;
    private readonly SemaphoreSlim _receiptGate = new(1, 1);

    private AgentProcedureRuntime(AgentProcedureContext context, IReadOnlyList<ToolDescriptor> tools)
    {
        _context = context;
        _inputs = context.Inputs.DeepClone().AsObject();
        _tools = tools.GroupBy(t => t.ToolId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _toolFingerprint = FingerprintTools(_tools.Values);
        context.Reject();
        context.PendingInvalidation = async (reason, ct) =>
        {
            _disabled = true;
            _completed = true;
            if (_replay is not null) await RejectActiveAsync(reason, ct).ConfigureAwait(false);
        };
    }

    public static async Task<AgentProcedureRuntime> StartAsync(AgentProcedureContext context,
        IReadOnlyList<ToolDescriptor> tools, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.BindingFingerprint);
        var run = new AgentProcedureRuntime(context, tools);
        if (context.RoutineLearningContext is { } learningContext)
        {
            AgentRoutineLearningPolicy.ValidateContext(learningContext);
            if (learningContext.Key != context.Key
                || learningContext.BindingFingerprint != context.BindingFingerprint
                || learningContext.ToolFingerprint != run._toolFingerprint)
                throw new InvalidDataException("Routine learning is not bound to the actual procedure context and routed tool set.");
        }
        run._history = await context.Store.GetAsync(context.Key, ct).ConfigureAwait(false);
        if (run._history is { ActiveRevisionId: null } && run._history.Revisions.LastOrDefault() is
            { State: AgentProcedureRevisionState.Rejected, FailureCode: "outcome_uncertain" or "effect_checkpoint_required" })
            run._stopReason = "A previous procedure action requires outcome inspection before this task can continue. No action has been repeated.";
        var active = run._history?.Revisions.SingleOrDefault(r => r.Id == run._history.ActiveRevisionId);
        if (active is { State: AgentProcedureRevisionState.Active, Verification: not null }
            && active.BindingFingerprint == context.BindingFingerprint && active.ToolFingerprint == run._toolFingerprint)
            run._replay = active;
        if (context.Recovery is { } recovery)
        {
            var inspected = run._history?.Revisions.LastOrDefault();
            if (run._stopReason is not null || context.Mode != AgentProcedureMode.VerifiedCompletion
                || run._history?.Version != recovery.HistoryVersion || run._history.ActiveRevisionId is not null
                || recovery.BindingFingerprint != context.BindingFingerprint
                || inspected is not { FailureCode: "outcome_inspected", Verification: not null }
                || inspected.BindingFingerprint != context.BindingFingerprint || inspected.ToolFingerprint != run._toolFingerprint
                || inspected.Verification != recovery.Inspection || recovery.Receipts.Count is 0 or > 256
                || recovery.Receipts.Any(r => r.IsError || !r.WasDispatched || !r.MayHaveSideEffects
                    || !r.HostCompletionVerified || r.OutcomeUncertain != false
                    || !run._tools.TryGetValue(r.ToolId, out var tool) || !context.AllowTool(tool, r.Arguments)))
                run._stopReason = "The retained action receipts do not match the current host inspection. No action has been repeated.";
            else
            {
                run._receipts.AddRange(recovery.Receipts.Select(r => r with
                {
                    Arguments = r.Arguments.DeepClone().AsObject(), Result = r.Result?.DeepClone()
                }));
                run._hasEffectCheckpoint = true;
                run.RepairReason = "confirmed_continuation";
            }
        }
        return run;
    }

    public bool IsReplaying => _replay is not null && !_disabled && !_replayFinished;
    public bool ProtectsEffects => _context.Mode == AgentProcedureMode.VerifiedCompletion;
    public string? StopReason => _stopReason;
    public string? RepairReason { get; private set; }

    /// <summary>Repair continues after confirmed work but cannot repeat an identical completed mutation.</summary>
    public bool MayDispatch(string toolId, JsonObject arguments) => _stopReason is null && (!ProtectsEffects || RepairReason is null
        || !_receipts.Any(r => r.ToolId == toolId && !r.IsError && r.WasDispatched && r.MayHaveSideEffects
            && JsonNode.DeepEquals(r.Arguments, arguments)));

    /// <summary>Re-resolves every argument against current inputs and earlier fresh receipts.</summary>
    public async Task<(string ToolId, JsonObject Arguments)?> NextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsReplaying || _completed || _cursor >= _replay!.Steps.Count) return null;
        var step = _replay.Steps[_cursor];
        JsonObject arguments;
        try { arguments = Materialize(step, _inputs, _receipts); }
        catch (InvalidDataException) { await FailReplayAsync("binding_unavailable", ct).ConfigureAwait(false); return null; }
        if (!_tools.TryGetValue(step.ToolId, out var descriptor) || !_context.AllowTool(descriptor, arguments))
        {
            await FailReplayAsync("authority_changed", ct).ConfigureAwait(false);
            return null;
        }
        if (_context.CheckPreconditionAsync is not null
            && !await _context.CheckPreconditionAsync(step, arguments, ct).ConfigureAwait(false))
        {
            await FailReplayAsync("precondition_failed", ct).ConfigureAwait(false);
            return null;
        }
        return (step.ToolId, arguments);
    }

    public async Task RecordAsync(AgentProcedureToolReceipt receipt, CancellationToken ct = default)
    {
        await _receiptGate.WaitAsync(ct).ConfigureAwait(false);
        try { await RecordCoreAsync(receipt, ct).ConfigureAwait(false); }
        finally { _receiptGate.Release(); }
    }

    /// <summary>Called inside the guarded physical dispatch boundary. A crash after this write leaves
    /// an inspection barrier, even if no tool receipt ever comes back.</summary>
    public async Task BeforeDispatchAsync(string invocationId, CancellationToken ct = default)
    {
        if (!ProtectsEffects) return;
        await _receiptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopReason is not null) throw new InvalidOperationException("Procedure outcome inspection is required before dispatch.");
            await WriteIntentCheckpointAsync("outcome_uncertain", ct).ConfigureAwait(false);
            _pendingDispatches.Add(invocationId);
            _hasIntentHistory = true;
        }
        finally { _receiptGate.Release(); }
    }

    private async Task WriteIntentCheckpointAsync(string code, CancellationToken ct)
    {
        var history = _history ?? new(_context.Key, 0, null, []);
        var existing = history.Revisions.SingleOrDefault(r => r.Id == _intentCheckpointId);
        if (existing?.FailureCode == code) return; // The durable barrier already covers outstanding work.
        var id = existing?.Id ?? Guid.NewGuid();
        var revisions = existing is null
            ? history.Revisions.Select(r => r.Id == history.ActiveRevisionId
                ? r with { State = AgentProcedureRevisionState.Rejected, FailureCode = code } : r)
                .Append(new(id, history.ActiveRevisionId, _context.BindingFingerprint, _toolFingerprint, [],
                    DateTimeOffset.UtcNow, AgentProcedureRevisionState.Rejected, FailureCode: code)).ToArray()
            : history.Revisions.Select(r => r.Id == id ? r with { FailureCode = code } : r).ToArray();
        var replacement = history with { Version = history.Version + 1, ActiveRevisionId = null, Revisions = revisions };
        if (!await _context.Store.CompareExchangeAsync(_context.Key, history.Version, replacement, ct).ConfigureAwait(false))
        {
            _stopReason = "The procedure changed before dispatch. No new action was started; inspect the current task state before continuing.";
            throw new InvalidOperationException("Procedure dispatch checkpoint was not committed.");
        }
        _history = replacement;
        _intentCheckpointId = id;
        _ownedCheckpoints.Add(id);
    }

    private async Task ResolveNoActionIntentAsync(CancellationToken ct)
    {
        if (_intentCheckpointId is not { } id || _history is null) return;
        var proof = new AgentProcedureVerification("physical-outcome-known-v1",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_receipts)))), DateTimeOffset.UtcNow);
        var replacement = _history with
        {
            Version = _history.Version + 1,
            Revisions = _history.Revisions.Select(r => r.Id == id
                ? r with { FailureCode = "outcome_inspected", Verification = proof } : r).ToArray()
        };
        if (!await _context.Store.CompareExchangeAsync(_context.Key, _history.Version, replacement, ct).ConfigureAwait(false))
        {
            _stopReason = "The physical action receipt could not be reconciled with the current procedure. Inspect its outcome before continuing.";
            return;
        }
        _history = replacement;
        _intentCheckpointId = null;
    }

    private async Task RecordCoreAsync(AgentProcedureToolReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _receipts.Add(receipt);
        if (receipt.InvocationId is { } invocationId) _pendingDispatches.Remove(invocationId);
        if (ProtectsEffects && !receipt.IsError && receipt.WasDispatched && receipt.MayHaveSideEffects) _hasEffectCheckpoint = true;
        if (ProtectsEffects && receipt.IsError && receipt.WasDispatched && receipt.MayHaveSideEffects && receipt.OutcomeUncertain != false)
        {
            _stopReason = "A tool may have changed external or local state without a confirmed result. Inspect that outcome before continuing; the procedure will not repeat it.";
            _hasUncertainOutcome = true;
            _disabled = true;
            _context.Reject();
            await WriteIntentCheckpointAsync("outcome_uncertain", ct).ConfigureAwait(false);
        }
        else
        {
            if (ProtectsEffects && !_hasUncertainOutcome && _intentCheckpointId is not null && _pendingDispatches.Count == 0)
            {
                if (_hasEffectCheckpoint) await WriteIntentCheckpointAsync("effect_checkpoint_required", ct).ConfigureAwait(false);
                else await ResolveNoActionIntentAsync(ct).ConfigureAwait(false);
            }
            if (IsReplaying)
            {
                if (receipt.IsError) await FailReplayAsync("tool_failed", ct).ConfigureAwait(false);
                else _cursor++;
            }
        }
    }

    /// <summary>Steering changes the host task mid-turn. No existing plan or candidate survives it.</summary>
    public void Abandon()
    {
        _disabled = true;
        _completed = true;
        _context.Reject();
    }

    /// <summary>
    /// The verifier decides completion from fresh evidence. A candidate remains ephemeral until the
    /// host calls Context.AcceptAsync after its output guard and persistence succeed.
    /// </summary>
    public async Task<string?> TryCompleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_completed || _stopReason is not null || _receipts.Count == 0) return null;
        if (_context.Mode == AgentProcedureMode.EvidencePreparation)
        {
            if (IsReplaying && _cursor >= _replay!.Steps.Count)
            {
                var prepared = new AgentProcedureRunEvidence(_receipts.ToArray(), true, _replay.Id);
                if (await _context.VerifyAsync(prepared, ct).ConfigureAwait(false) is null)
                    await FailReplayAsync("verification_failed", ct).ConfigureAwait(false);
                else _replayFinished = true;
            }
            return null; // Read evidence is not an answer; synthesis always resumes.
        }
        if (IsReplaying && _cursor < _replay!.Steps.Count) return null;
        var successful = _receipts.Where(r => !r.IsError).ToArray();
        if (successful.Length == 0 || successful.Any(r => !_tools.TryGetValue(r.ToolId, out var tool) || !_context.AllowTool(tool, r.Arguments))) return null;
        var evidence = new AgentProcedureRunEvidence(_receipts.ToArray(), IsReplaying, _replay?.Id);
        var proof = await _context.VerifyAsync(evidence, ct).ConfigureAwait(false);
        if (proof is null)
        {
            if (IsReplaying) await FailReplayAsync("verification_failed", ct).ConfigureAwait(false);
            return null;
        }
        if (string.IsNullOrWhiteSpace(proof.VerifierId) || string.IsNullOrWhiteSpace(proof.EvidenceHash))
            throw new InvalidDataException("A procedure verifier must issue an identified receipt.");
        var formatted = _context.FormatResult(evidence);
        if (string.IsNullOrWhiteSpace(formatted)) return null;
        var expected = _history;
        if (IsReplaying)
        {
            if (_hasIntentHistory)
                StageEffectsAcceptance(proof, expected!, _replay!.Id);
        }
        else
        {
            var candidate = _context.Planner is null
                ? Compile(_context, _inputs, successful)
                : await _context.Planner.CreateCandidateAsync(_context, evidence, ct).ConfigureAwait(false);
            if (candidate is not null && ValidateCandidate(candidate, successful))
            {
                // Clone before storing: delegates, JsonNodes and host code cannot mutate accepted plan content.
                var frozen = CloneSteps(candidate.Steps);
                StagePromotion(frozen, proof, expected);
            }
            else if (_hasIntentHistory)
                StageEffectsAcceptance(proof, expected!, null);
        }
        _completed = true;
        return formatted;
    }

    /// <summary>Stages only a verified, parameterized read prefix after normal model completion.
    /// The final answer remains subject to the caller's ordinary output guards and acceptance.</summary>
    public async Task PrepareModelCompletionAsync(CancellationToken ct = default)
    {
        if (_context.Mode != AgentProcedureMode.EvidencePreparation || _completed || _stopReason is not null) return;
        var prefix = new List<AgentProcedureToolReceipt>();
        foreach (var receipt in _receipts)
        {
            if (receipt.MayHaveSideEffects || !_tools.TryGetValue(receipt.ToolId, out var tool)
                || !_context.AllowTool(tool, receipt.Arguments)) break;
            if (receipt.IsError) continue; // A safe read failure may be replaced by a corrected model step.
            if (!receipt.HostCompletionVerified) break;
            prefix.Add(receipt);
        }
        AgentProcedureCandidate? candidate = null;
        var used = 0;
        for (var count = 1; count <= prefix.Count; count++)
        {
            var possible = Compile(_context, _inputs, prefix.Take(count).ToArray());
            if (possible is null) break;
            candidate = possible; used = count;
        }
        if (candidate is null || _replayFinished && !_disabled && used <= _replay!.Steps.Count) return;
        var receipts = prefix.Take(used).ToArray();
        var proof = await _context.VerifyAsync(new(receipts, false, _replay?.Id), ct).ConfigureAwait(false);
        if (proof is null || !ValidateCandidate(candidate, receipts)) return;
        var frozen = CloneSteps(candidate.Steps);
        var expected = _history;
        StagePromotion(frozen, proof, expected);
        _completed = true;
    }

    private void StagePromotion(IReadOnlyList<AgentProcedureStep> steps, AgentProcedureVerification proof,
        AgentProcedureHistory? expected)
    {
        var revision = new AgentProcedureRevision(Guid.NewGuid(), expected?.ActiveRevisionId ?? _replay?.Id, _context.BindingFingerprint,
            _toolFingerprint, steps, DateTimeOffset.UtcNow, AgentProcedureRevisionState.Active, proof);
        var revisions = CompleteEffectCheckpoints(expected?.Revisions ?? [], proof)
            .Select(r => r.State == AgentProcedureRevisionState.Active
                ? r with { State = AgentProcedureRevisionState.Superseded } : r).Append(revision).ToArray();
        StageAcceptance(AgentProcedureAcceptanceCandidateKind.PromoteRevision, expected,
            new(_context.Key, (expected?.Version ?? 0) + 1, revision.Id, revisions), proof);
    }

    private void StageEffectsAcceptance(AgentProcedureVerification proof, AgentProcedureHistory expected,
        Guid? restoreRevision)
    {
        var revisions = CompleteEffectCheckpoints(expected.Revisions, proof)
            .Select(r => r.Id == restoreRevision ? r with { State = AgentProcedureRevisionState.Active, FailureCode = null } : r).ToArray();
        StageAcceptance(AgentProcedureAcceptanceCandidateKind.ReconcileEffects, expected,
            expected with { Version = expected.Version + 1, ActiveRevisionId = restoreRevision, Revisions = revisions }, proof);
    }

    private void StageAcceptance(AgentProcedureAcceptanceCandidateKind kind, AgentProcedureHistory? expected,
        AgentProcedureHistory replacement, AgentProcedureVerification proof)
    {
        var candidate = AgentProcedureAcceptanceCandidate.Freeze(kind, expected, replacement,
            _context.BindingFingerprint, _toolFingerprint, proof);
        _context.PendingCandidate = candidate;
        _context.PendingAcceptance = token => candidate.ApplyAsync(_context.Store, token);
    }

    private IEnumerable<AgentProcedureRevision> CompleteEffectCheckpoints(IReadOnlyList<AgentProcedureRevision> revisions,
        AgentProcedureVerification proof)
        => revisions.Select(r => _ownedCheckpoints.Contains(r.Id)
            && r is { Steps.Count: 0, FailureCode: "effect_checkpoint_required", Verification: null }
                ? r with { FailureCode = "outcome_inspected", Verification = proof } : r);

    private async Task FailReplayAsync(string reason, CancellationToken ct)
    {
        RepairReason = reason;
        _disabled = true;
        await RejectActiveAsync(reason, ct).ConfigureAwait(false);
    }

    private async Task RejectActiveAsync(string code, CancellationToken ct)
    {
        var history = _history ?? new(_context.Key, 0, null, []);
        var active = history.ActiveRevisionId;
        if (active is null && code is not ("outcome_uncertain" or "effect_checkpoint_required")) return;
        var revisions = history.Revisions.Select(r => r.Id == active
            ? r with { State = AgentProcedureRevisionState.Rejected, FailureCode = code } : r).ToArray();
        // A checkpoint is appended even after rollback to an older active revision. The final revision
        // then unambiguously records the inspection barrier without relying on original creation order.
        if (code is "outcome_uncertain" or "effect_checkpoint_required")
            revisions = revisions.Append(new(Guid.NewGuid(), active, _context.BindingFingerprint, _toolFingerprint,
                [], DateTimeOffset.UtcNow, AgentProcedureRevisionState.Rejected, FailureCode: code)).ToArray();
        var replacement = history with
        {
            Version = history.Version + 1, ActiveRevisionId = null,
            Revisions = revisions
        };
        if (await _context.Store.CompareExchangeAsync(_context.Key, history.Version, replacement, ct).ConfigureAwait(false))
            _history = replacement;
        else
            _stopReason = "The procedure changed while its replay was being invalidated. Inspect the current procedure before continuing.";
    }

    /// <summary>Only a host outcome inspector may release an uncertainty barrier; model text is not proof.</summary>
    public static async Task<bool> ResolveUncertaintyAsync(IAgentProcedureStore store, AgentProcedureKey key,
        long expectedVersion, AgentProcedureVerification inspection, CancellationToken ct = default)
    {
        var history = await store.GetAsync(key, ct).ConfigureAwait(false);
        if (history?.Version != expectedVersion || history.ActiveRevisionId is not null
            || string.IsNullOrWhiteSpace(inspection.VerifierId) || string.IsNullOrWhiteSpace(inspection.EvidenceHash)) return false;
        var blocked = history.Revisions.LastOrDefault();
        if (blocked?.FailureCode is not ("outcome_uncertain" or "effect_checkpoint_required")) return false;
        var resolved = new AgentProcedureRevision(Guid.NewGuid(), blocked.Id, blocked.BindingFingerprint,
            blocked.ToolFingerprint, [], DateTimeOffset.UtcNow, AgentProcedureRevisionState.Rejected,
            inspection, "outcome_inspected");
        return await store.CompareExchangeAsync(key, expectedVersion,
            history with { Version = expectedVersion + 1, Revisions = history.Revisions.Append(resolved).ToArray() }, ct).ConfigureAwait(false);
    }

    /// <summary>Host-only rollback. Current binding/tool schema must still match the previously verified plan.</summary>
    public static async Task<bool> RollbackAsync(IAgentProcedureStore store, AgentProcedureKey key, long expectedVersion,
        Guid revisionId, string bindingFingerprint, string toolFingerprint, CancellationToken ct = default)
    {
        var history = await store.GetAsync(key, ct).ConfigureAwait(false);
        if (history?.Version != expectedVersion) return false;
        if (history.Revisions.LastOrDefault()?.FailureCode is "outcome_uncertain" or "effect_checkpoint_required")
            return false;
        var target = history.Revisions.SingleOrDefault(r => r.Id == revisionId);
        if (target?.Verification is null || target.Steps.Count == 0 || target.BindingFingerprint != bindingFingerprint
            || target.ToolFingerprint != toolFingerprint) return false;
        var replacement = history with
        {
            Version = expectedVersion + 1, ActiveRevisionId = target.Id,
            Revisions = history.Revisions.Select(r => r.Id == target.Id ? r with { State = AgentProcedureRevisionState.Active, FailureCode = null }
                : r.State == AgentProcedureRevisionState.Active ? r with { State = AgentProcedureRevisionState.Superseded } : r).ToArray()
        };
        return await store.CompareExchangeAsync(key, expectedVersion, replacement, ct).ConfigureAwait(false);
    }

    private bool ValidateCandidate(AgentProcedureCandidate candidate, IReadOnlyList<AgentProcedureToolReceipt> receipts)
    {
        if (candidate.Steps.Count != receipts.Count) return false;
        try
        {
            for (var i = 0; i < candidate.Steps.Count; i++)
            {
                var step = candidate.Steps[i];
                if (step.ToolId != receipts[i].ToolId || !ValidateConstants(step, _context)) return false;
                if (!_context.Equivalent(Materialize(step, _inputs, receipts.Take(i).ToArray()), receipts[i].Arguments)) return false;
            }
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    public static string FingerprintTools(IEnumerable<ToolDescriptor> tools)
    {
        var text = JsonSerializer.Serialize(tools.OrderBy(t => t.ToolId, StringComparer.Ordinal).Select(t => new
        { t.ToolId, t.OwnerAgentId, t.Description, Input = t.InputType.FullName, Output = t.OutputType.FullName,
            t.InputSchema, t.SideEffect, t.DataAccess, t.SupportsProcedureEvidencePreparation,
            HasEffectResolver = t.ResolveSideEffect is not null, t.ImplementationFingerprint }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static IReadOnlyList<AgentProcedureStep> CloneSteps(IReadOnlyList<AgentProcedureStep> steps)
        => steps.Select(s => new AgentProcedureStep(s.ToolId, s.Arguments.DeepClone().AsObject(), s.Bindings.ToArray())).ToArray();

    private static AgentProcedureCandidate? Compile(AgentProcedureContext context, JsonObject inputs,
        IReadOnlyList<AgentProcedureToolReceipt> receipts)
    {
        var steps = new List<AgentProcedureStep>();
        var inputValues = Walk(inputs).ToArray();
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var args = receipt.Arguments.DeepClone().AsObject();
            var bindings = new List<AgentProcedureBinding>();
            bool Visit(JsonNode? node, string target)
            {
                var inputsMatching = inputValues.Where(p => p.Value is not null && context.Equivalent(p.Value, node)).ToArray();
                if (inputsMatching.Length == 1)
                {
                    bindings.Add(new(target, AgentProcedureValueSource.Input, inputsMatching[0].Pointer));
                    return true;
                }
                // The nearest unambiguous matching result wins; repeated values within it are not guessed.
                for (var previous = index - 1; previous >= 0; previous--)
                {
                    var matches = Walk(receipts[previous].Result).Where(p => p.Value is not null && context.Equivalent(p.Value, node)).ToArray();
                    if (matches.Length == 1)
                    {
                        bindings.Add(new(target, AgentProcedureValueSource.PriorResult, matches[0].Pointer, previous));
                        return true;
                    }
                    if (matches.Length > 1) break;
                }
                if (node is JsonObject obj) return obj.All(p => Visit(p.Value, target + "/" + Escape(p.Key)));
                if (node is JsonArray array) return array.Select((v, i) => (v, i)).All(p => Visit(p.v, target + "/" + p.i));
                return context.AllowLiteral?.Invoke(receipt.ToolId, target, node) == true;
            }
            if (!Visit(args, "")) return null;
            JsonNode? template = args;
            foreach (var binding in bindings) Set(ref template, binding.TargetPointer, null);
            // A whole input object can bind the argument root; keep an empty placeholder object.
            steps.Add(new(receipt.ToolId, template as JsonObject ?? new JsonObject(), bindings));
        }
        return new(steps);
    }

    private static bool ValidateConstants(AgentProcedureStep step, AgentProcedureContext context)
    {
        var bound = step.Bindings.Select(b => b.TargetPointer).ToArray();
        if (bound.Distinct(StringComparer.Ordinal).Count() != bound.Length) return false;
        foreach (var a in bound)
            foreach (var b in bound)
                if (a != b && (a.Length == 0 || b.StartsWith(a + "/", StringComparison.Ordinal))) return false;
        return Walk(step.Arguments).Where(p => p.Value is not JsonObject and not JsonArray)
            .All(p => bound.Any(b => b.Length == 0 || p.Pointer == b || p.Pointer.StartsWith(b + "/", StringComparison.Ordinal))
                ? p.Value is null : context.AllowLiteral?.Invoke(step.ToolId, p.Pointer, p.Value) == true);
    }

    private static JsonObject Materialize(AgentProcedureStep step, JsonObject inputs,
        IReadOnlyList<AgentProcedureToolReceipt> prior)
    {
        JsonNode? value = step.Arguments.DeepClone();
        foreach (var binding in step.Bindings)
        {
            var source = binding.Source switch
            {
                AgentProcedureValueSource.Input when binding.StepIndex is null => inputs,
                AgentProcedureValueSource.PriorResult when binding.StepIndex is >= 0 && binding.StepIndex < prior.Count => prior[binding.StepIndex.Value].Result,
                _ => throw new InvalidDataException("Invalid procedure binding.")
            };
            Set(ref value, binding.TargetPointer, Get(source, binding.SourcePointer)?.DeepClone());
        }
        return value as JsonObject ?? throw new InvalidDataException("Procedure arguments must be an object.");
    }

    private static IEnumerable<(string Pointer, JsonNode? Value)> Walk(JsonNode? value, string pointer = "")
    {
        yield return (pointer, value);
        if (value is JsonObject obj)
            foreach (var pair in obj)
                foreach (var child in Walk(pair.Value, pointer + "/" + Escape(pair.Key))) yield return child;
        if (value is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                foreach (var child in Walk(array[i], pointer + "/" + i)) yield return child;
    }
    private static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static string[] Parts(string pointer)
    {
        if (pointer == "") return [];
        if (!pointer.StartsWith('/')) throw new InvalidDataException("Invalid JSON pointer.");
        for (var i = 0; i < pointer.Length; i++)
            if (pointer[i] == '~' && (++i >= pointer.Length || pointer[i] is not ('0' or '1')))
                throw new InvalidDataException("Invalid JSON pointer escape.");
        return pointer[1..].Split('/').Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
    }
    private static JsonNode? Get(JsonNode? root, string pointer)
    {
        var node = root;
        foreach (var part in Parts(pointer))
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(part, out var value) => value,
                JsonArray array when int.TryParse(part, out var i) && i >= 0 && i < array.Count => array[i],
                _ => throw new InvalidDataException("Procedure source no longer exists.")
            };
        return node;
    }
    private static void Set(ref JsonNode? root, string pointer, JsonNode? value)
    {
        var parts = Parts(pointer);
        if (parts.Length == 0) { root = value; return; }
        var parent = parts.Length == 1 ? root : Get(root, "/" + string.Join('/', parts[..^1].Select(Escape)));
        switch (parent)
        {
            case JsonObject obj when obj.ContainsKey(parts[^1]): obj[parts[^1]] = value; break;
            case JsonArray array when int.TryParse(parts[^1], out var i) && i >= 0 && i < array.Count: array[i] = value; break;
            default: throw new InvalidDataException("Invalid procedure target.");
        }
    }
}
