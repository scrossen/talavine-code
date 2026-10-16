using SwarmCore.Abstractions.Budgets;

namespace SwarmCore.Agent.Continual;

/// <summary>Observe the shared deadline during direct host work as well as processor turns.</summary>
public sealed class MissionBudgetDeadlineMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _watch;
    public CancellationToken Token => _lifetime.Token;
    public bool DeadlineExpired { get; private set; }

    public MissionBudgetDeadlineMonitor(IMissionBudgetLedger? ledger, Guid profile, string? reference,
        TimeProvider time, CancellationToken ct)
    {
        if (reference is not null && ledger is null) throw new InvalidOperationException("Mission budget ledger unavailable.");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _watch = reference is null ? Task.CompletedTask : WatchAsync(
            ledger!, profile, reference, time);
    }

    private async Task WatchAsync(IMissionBudgetLedger ledger, Guid profile, string reference, TimeProvider time)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var budget = await ledger.ReadAsync(profile, reference, _lifetime.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Mission budget disappeared during execution.");
                var now = time.GetUtcNow();
                if (budget.Limits.DeadlineUtc <= now)
                {
                    DeadlineExpired = true;
                    await _lifetime.CancelAsync().ConfigureAwait(false);
                    return;
                }
                // The deadline is a stored timestamp that a host may move: sleep until the earlier of it
                // and the next refresh instead of re-reading the ledger continuously.
                var wait = budget.Limits.DeadlineUtc is { } deadline && deadline - now < ContinualObjectiveProcessor.DeadlineRefreshInterval
                    ? deadline - now : ContinualObjectiveProcessor.DeadlineRefreshInterval;
                await Task.Delay(wait, time, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { await _lifetime.CancelAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _watch.ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
}
