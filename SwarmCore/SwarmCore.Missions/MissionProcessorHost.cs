using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Bind the shared processor to this host's profile store without replacing another embedded runtime's store.</summary>
public sealed class MissionProcessorHost
{
    public IContinualObjectiveProcessor Processor { get; }
    public string ExecutorKey { get; }
    private readonly IMissionStore _store;
    private readonly IServiceProvider _services;
    public MissionProcessorHost(IServiceProvider services, IMissionStore store, MissionTurnExecutor executor, IContinualObjectiveTextSanitizer sanitizer)
        {
        _store = store; _services = services;
        ExecutorKey = executor.ExecutorKey;
        var processor = ActivatorUtilities.CreateInstance<ContinualObjectiveProcessor>(services, store, new Registry(executor), sanitizer);
        Processor = new BudgetedProcessor(processor, this, store, services.GetService<SwarmCore.Abstractions.Budgets.IMissionBudgetLedger>());
    }
    public async Task<IReadOnlyList<ContinualObjectiveProcessResult>> ProcessDueAsync(string owner, CancellationToken ct = default)
    {
        var limits = _services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MissionRuntimeOptions>>().Value.DelegationLimits;
        var clock = _services.GetRequiredService<TimeProvider>();
        var results = new List<ContinualObjectiveProcessResult>();
        var active = new List<Task<ContinualObjectiveProcessResult>>();
        var attempted = new Dictionary<Guid, (long Version, int Epoch, bool Claimed)>();
        var activeIds = new HashSet<Guid>();
        var admissions = 0;
        var progressEpoch = 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<ContinualObjectiveProcessResult> RunAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MissionProcessorHost>().Processor.ProcessAsync(id, owner, lifetime.Token);
        }
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (admissions < 64 && active.Count < Math.Max(1, limits.MaximumConcurrency))
                {
                    var due = await _store.GetDueAsync(clock.GetUtcNow(), 64, ct);
                    foreach (var item in due)
                    {
                        if (admissions >= 64 || active.Count >= Math.Max(1, limits.MaximumConcurrency)) break;
                        if (activeIds.Contains(item.Id)) continue;
                        if (attempted.TryGetValue(item.Id, out var prior)
                            && !(item.JoinWait is not null && item.Version != prior.Version)
                            && !(!prior.Claimed && progressEpoch > prior.Epoch)) continue;
                        attempted[item.Id] = (item.Version, progressEpoch, false);
                        activeIds.Add(item.Id);
                        admissions++;
                        active.Add(RunAsync(item.Id));
                    }
                }
                if (active.Count == 0) break;
                // A running parent may create children before it yields. Refresh vacant slots
                // without waiting for every unrelated long-running mission in this pass.
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var completed = active.Where(task => task.IsCompleted).ToArray();
                if (completed.Length == 0)
                {
                    var delay = Task.Delay(TimeSpan.FromSeconds(1), clock, refresh.Token);
                    await Task.WhenAny(active.Cast<Task>().Append(delay));
                    await refresh.CancelAsync();
                    completed = active.Where(task => task.IsCompleted).ToArray();
                }
                foreach (var task in completed)
                {
                    active.Remove(task);
                    var result = await task;
                    activeIds.Remove(result.ObjectiveId);
                    var prior = attempted[result.ObjectiveId];
                    attempted[result.ObjectiveId] = (prior.Version, prior.Epoch, result.Claimed);
                    if (result.Claimed) progressEpoch++;
                    results.Add(result);
                }
            }
            return results;
        }
        finally
        {
            await lifetime.CancelAsync();
            // Scope/provider disposal must never race still-running claimed work.
            await Task.WhenAll(active);
        }
    }
    private sealed class Registry(MissionTurnExecutor executor) : IContinualObjectiveExecutorRegistry
    {
        public IContinualObjectiveExecutor? Get(string executorKey) => executorKey == executor.ExecutorKey ? executor : null;
    }

    private sealed class BudgetedProcessor(IContinualObjectiveProcessor inner, MissionProcessorHost host,
        IMissionStore store, SwarmCore.Abstractions.Budgets.IMissionBudgetLedger? budgets) : IContinualObjectiveProcessor
    {
        public Task<ContinualObjectiveSnapshot> StartOrJoinAsync(ContinualObjectiveStart start, CancellationToken ct = default) => inner.StartOrJoinAsync(start, ct);
        public Task<bool> StopAsync(Guid objectiveId, string reason = "Stopped by the user.", CancellationToken ct = default) => inner.StopAsync(objectiveId, reason, ct);
        public Task<IReadOnlyList<ContinualObjectiveProcessResult>> ProcessDueAsync(string ownerId, CancellationToken ct = default) => host.ProcessDueAsync(ownerId, ct);
        public async Task<ContinualObjectiveProcessResult> ProcessAsync(Guid objectiveId, string ownerId, CancellationToken ct = default)
        {
            var mission = await store.GetAsync(objectiveId, ct);
            using var scope = mission?.Mission?.BudgetReference is { } reference
                ? SwarmCore.Abstractions.Budgets.MissionBudgetExecutionScope.Enter(budgets ?? throw new InvalidOperationException("Mission budget ledger unavailable."),
                    Guid.Parse(mission.PartitionKey), objectiveId, reference) : null;
            var result = await inner.ProcessAsync(objectiveId, ownerId, ct);
            if (mission?.Mission?.Delegation?.Spec.BudgetToolCalls is not null && mission.Mission.BudgetReference is { } childReference
                && budgets is not null && result.Status is ContinualObjectiveStatus.Completed or ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Failed)
                await budgets.ReleaseChildAsync(Guid.Parse(mission.PartitionKey), childReference, objectiveId, CancellationToken.None);
            return result;
        }
    }
}
