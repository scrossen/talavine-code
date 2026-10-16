using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.ToolUseEngine;

public sealed record ToolScheduleRequest(string CallId, string ToolId, JsonObject Arguments,
    ToolSchedulingCapability Capability, Func<CancellationToken, Task<ScheduledToolResult>> Execute)
{
    public CancellationToken Cancellation { get; init; }
}

/// <summary>Unforgeable live lease for incremental, independent native reads.</summary>
public sealed class ToolSchedulerBatchLease : IAsyncDisposable
{
    internal DurableToolScheduler Owner { get; }
    internal string Profile { get; }
    private IAsyncDisposable? _lease;
    internal bool IsActive => _lease is not null;
    internal ToolSchedulerBatchLease(DurableToolScheduler owner, string profile, IAsyncDisposable lease)
    { Owner = owner; Profile = profile; _lease = lease; }
    public async ValueTask DisposeAsync()
    { if (Interlocked.Exchange(ref _lease, null) is { } lease) await lease.DisposeAsync(); }
}

/// <summary>
/// Host concurrency with a conventional result boundary. A batch owns every call until terminal;
/// nothing is detached optional work. Store records are durable before a result is observable.
/// Native provider async requires its own protocol gate in addition to this scheduler.
/// </summary>
public sealed class DurableToolScheduler
{
    private readonly IScheduledToolStore _store;
    private readonly ToolSchedulerOptions _options;
    private readonly SemaphoreSlim _capacity;
    private readonly SemaphoreSlim _workers;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profiles = new(StringComparer.Ordinal);
    private readonly IScheduledToolAdmission _admission;
    private readonly TimeProvider _time;

    public DurableToolScheduler(IScheduledToolStore store, ToolSchedulerOptions options,
        IScheduledToolAdmission admission, TimeProvider? time = null, ToolSchedulerCapacity? capacity = null)
    {
        options.Validate();
        _store = store; _options = options; _admission = admission; _time = time ?? TimeProvider.System;
        capacity ??= new(options);
        _capacity = capacity.Queue; _workers = capacity.Workers;
    }

    public async Task<IReadOnlyList<ScheduledToolRecord>> RunAsync(ToolScheduleIdentity identity,
        IReadOnlyList<ToolScheduleRequest> calls, CancellationToken ct = default, ToolSchedulerBatchLease? batchLease = null)
    {
        Validate(identity, calls);
        var orderedCalls = TopologicalOrder(calls);
        if (batchLease is not null && (batchLease.Owner != this || batchLease.Profile != identity.Profile || !batchLease.IsActive
            || calls.Any(c => !c.Capability.Reentrant || c.Capability.DependsOnCallIds.Count != 0 || c.Capability.ResourceKeys.Count != 0)))
            throw new InvalidOperationException("Incremental native batch only admits independent reviewed calls under its own live lease.");
        var reserved = 0;
        try
        {
            for (; reserved < calls.Count; reserved++)
                if (!_capacity.Wait(0)) throw new InvalidOperationException("Tool scheduler queue capacity exhausted; no new batch was dispatched.");
            var profile = _profiles.GetOrAdd(identity.Profile, _ => new(1));
            if (batchLease is null) await profile.WaitAsync(ct);
            try
            {
                await using var profileLease = batchLease is null ? await _store.AcquireProfileLeaseAsync(identity.Profile, ct) : null;
                var records = new Dictionary<string, ScheduledToolRecord>(StringComparer.Ordinal);
                foreach (var call in calls)
                {
                    var op = FileScheduledToolStore.Hash(JsonSerializer.Serialize(new object[] { identity with { AuthorityReference = null }, call.CallId }));
                    var record = new ScheduledToolRecord
                    {
                        OperationId = op, Identity = identity, CallId = call.CallId, ToolId = call.ToolId,
                        ArgumentsReference = FileScheduledToolStore.Hash(call.Arguments.ToJsonString()),
                        Capability = call.Capability, State = ScheduledToolState.Queued, UpdatedAtUtc = _time.GetUtcNow(),
                        DeadlineUtc = call.Capability.Timeout is { } timeout ? _time.GetUtcNow() + timeout : null
                    };
                    records.Add(call.CallId, await _store.RegisterAsync(record, call.Arguments, ct));
                }
                var jobs = new Dictionary<string, Task<ScheduledToolRecord>>(StringComparer.Ordinal);
                var prior = new List<Task<ScheduledToolRecord>>();
                Task<ScheduledToolRecord>? lastSerial = null;
                var resources = new Dictionary<string, Task<ScheduledToolRecord>>(StringComparer.Ordinal);
                // Host dependencies determine execution order, never the provider's event arrival order.
                foreach (var call in orderedCalls)
                {
                    var dependencies = call.Capability.DependsOnCallIds.Select(id => jobs[id]).ToArray();
                    var ordering = new List<Task<ScheduledToolRecord>>();
                    if (!call.Capability.Reentrant) ordering.AddRange(prior);
                    else if (lastSerial is not null) ordering.Add(lastSerial);
                    foreach (var key in call.Capability.ResourceKeys)
                        if (resources.TryGetValue(key, out var previous)) ordering.Add(previous);
                    var record = records[call.CallId];
                    var job = RunOneAsync(record, call, dependencies, ordering, ct);
                    jobs.Add(call.CallId, job); prior.Add(job);
                    if (!call.Capability.Reentrant) lastSerial = job;
                    foreach (var key in call.Capability.ResourceKeys) resources[key] = job;
                }
                // Preserve original call IDs/order, independent of physical completion order.
                return await Task.WhenAll(calls.Select(call => jobs[call.CallId]));
            }
            finally { if (batchLease is null) profile.Release(); }
        }
        finally { if (reserved > 0) _capacity.Release(reserved); }
    }

    public async Task<ToolSchedulerBatchLease> OpenNativeBatchAsync(string profile, CancellationToken ct)
        => new(this, profile, await _store.AcquireProfileLeaseAsync(profile, ct));

    private async Task<ScheduledToolRecord> RunOneAsync(ScheduledToolRecord record, ToolScheduleRequest call,
        Task<ScheduledToolRecord>[] dependencies, List<Task<ScheduledToolRecord>> ordering, CancellationToken ct)
    {
        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, call.Cancellation);
        ct = invocationCancellation.Token;
        if (IsTerminal(record.State))
        {
            await _admission.ValidateReplayAsync(record, ct);
            return record;
        }
        if (record.State == ScheduledToolState.Running)
            return await SaveAsync(record, ScheduledToolState.Uncertain,
                new("Prior dispatch has no terminal receipt; inspect its outcome before retrying.", true, true));
        var dispatched = false;
        try
        {
            await Task.WhenAll(ordering).WaitAsync(ct);
            var required = await Task.WhenAll(dependencies).WaitAsync(ct);
            if (required.Any(r => r.State != ScheduledToolState.Completed || r.Result?.IsError != false))
                return await SaveAsync(record, ScheduledToolState.Cancelled,
                    new("Required tool dependency did not complete successfully.", true, false));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (record.DeadlineUtc is { } at)
            {
                var remaining = at - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Tool deadline expired while queued.");
                deadline.CancelAfter(remaining);
            }
            await _workers.WaitAsync(deadline.Token);
            try
            {
                // Admission and execution run in a sibling ExecutionContext off the UI dispatcher.
                return await Task.Run(async () =>
                {
                    await using var reservation = await _admission.AdmitAsync(record, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (reservation is IScheduledToolBudgetLease budgetLease)
                        await budgetLease.MarkDispatchedAsync(deadline.Token);
                    record = await SaveAsync(record, ScheduledToolState.Running, null);
                    dispatched = true;
                    var result = await call.Execute(deadline.Token);
                    if (reservation is IScheduledToolBudgetLease completedBudget)
                        await completedBudget.CompleteAsync(result, CancellationToken.None);
                    var uncertain = result.WasDispatched && !result.LocalProcessCompleted;
                    // The caller stopped this call: a late result is quarantined and never used as evidence.
                    if (ct.IsCancellationRequested)
                        return await SaveAsync(record, uncertain ? ScheduledToolState.Uncertain : ScheduledToolState.Cancelled,
                            result with { IsError = true, Content = "Cancelled tool result quarantined; inspect any side effect." });
                    // Otherwise the executor's outcome is recorded as reported. A cancelled or timed-out call keeps
                    // its own reason (never a success); a call that completed even as its own deadline elapsed is a
                    // real completion, not a cancellation.
                    if (result.WasCancelled || result.TimedOut)
                        return await SaveAsync(record, uncertain ? ScheduledToolState.Uncertain : ScheduledToolState.Cancelled,
                            result with
                            {
                                IsError = true,
                                Content = uncertain ? result.Content + " The outcome is uncertain; inspect any side effect before retrying."
                                    : result.Content,
                            });
                    return await SaveAsync(record, result.IsError ? ScheduledToolState.Failed : ScheduledToolState.Completed, result);
                }, CancellationToken.None);
            }
            finally { _workers.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var cancelled = ex is OperationCanceledException;
            // A budget refusal carries its own reason string from the ledger (e.g.
            // "mission-budget-tool-calls"); persist that exact text instead of the exception's
            // type name so the stored receipt says why admission was refused, not just that it was.
            var budgetReason = !dispatched && ex is SwarmCore.Abstractions.Budgets.MissionBudgetExceededException budgetEx ? budgetEx.Message : null;
            return await SaveAsync(record,
                dispatched ? ScheduledToolState.Uncertain : cancelled ? ScheduledToolState.Cancelled : ScheduledToolState.Failed,
                new(dispatched ? "Tool dispatch did not produce a durable, confirmed outcome; inspection required."
                    : cancelled ? "Tool cancelled before dispatch."
                    : budgetReason is not null ? "Refused for configured mission budget: " + budgetReason
                    : "Tool admission or execution failed: " + ex.GetType().Name,
                    true, dispatched));
        }
    }

    public async Task<IReadOnlyList<ScheduledToolRecord>> ReconcileAsync(ToolScheduleIdentity identity,
        IScheduledToolOutcomeInspector inspector, CancellationToken ct = default)
    {
        await using var profileLease = await _store.AcquireProfileLeaseAsync(identity.Profile, ct);
        var records = await _store.ReadTurnAsync(identity, ct);
        var results = new List<ScheduledToolRecord>();
        foreach (var record in records)
        {
            if (record.State == ScheduledToolState.Queued)
            {
                results.Add(await SaveAsync(record, ScheduledToolState.Cancelled,
                    new("Queued call belonged to a stopped host turn and was not dispatched.", true, false)));
                continue;
            }
            if (record.State is not (ScheduledToolState.Running or ScheduledToolState.Uncertain)) { results.Add(record); continue; }
            var outcome = await inspector.InspectAsync(record, ct);
            results.Add(await SaveAsync(record, outcome is null ? ScheduledToolState.Uncertain
                : outcome.IsError ? ScheduledToolState.Failed : ScheduledToolState.Completed,
                outcome ?? new("Outcome inspection could not establish completion; automatic replay is prohibited.", true, true)));
        }
        return results;
    }

    private async Task<ScheduledToolRecord> SaveAsync(ScheduledToolRecord record, ScheduledToolState state, ScheduledToolResult? result)
    {
        var next = record with { State = state, Result = result, UpdatedAtUtc = _time.GetUtcNow() };
        if (!await _store.UpdateAsync(next, record.Version, CancellationToken.None))
            throw new InvalidOperationException("Tool journal ownership changed; stop execution.");
        return next with { Version = record.Version + 1 };
    }

    public static bool IsTerminal(ScheduledToolState state) => state is ScheduledToolState.Completed
        or ScheduledToolState.Failed or ScheduledToolState.Cancelled or ScheduledToolState.Uncertain;

    private void Validate(ToolScheduleIdentity identity, IReadOnlyList<ToolScheduleRequest> calls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Turn);
        if (calls.Count > _options.MaxQueuedCalls) throw new ArgumentException("Tool batch exceeds bounded queue capacity.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(call.CallId);
            ArgumentException.ThrowIfNullOrWhiteSpace(call.ToolId);
            if (!ids.Add(call.CallId)) throw new ArgumentException("Duplicate tool call ID in one batch.");
            if (call.Capability.Timeout is { } timeout && timeout <= TimeSpan.Zero)
                throw new ArgumentException("Tool timeout must be positive.");
            if (call.Capability.ResourceKeys.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Resource keys must be concrete host-owned names.");
        }
        if (calls.Any(call => call.Capability.DependsOnCallIds.Any(id => !ids.Contains(id))))
            throw new ArgumentException("Tool dependency must name a known call in this batch.");
    }

    private static IReadOnlyList<ToolScheduleRequest> TopologicalOrder(IReadOnlyList<ToolScheduleRequest> calls)
    {
        var pending = calls.ToList(); var done = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ToolScheduleRequest>(calls.Count);
        while (pending.Count > 0)
        {
            var index = pending.FindIndex(call => call.Capability.DependsOnCallIds.All(done.Contains));
            if (index < 0) throw new ArgumentException("Tool dependency graph contains a cycle.");
            var call = pending[index]; pending.RemoveAt(index); ordered.Add(call); done.Add(call.CallId);
        }
        return ordered;
    }
}
