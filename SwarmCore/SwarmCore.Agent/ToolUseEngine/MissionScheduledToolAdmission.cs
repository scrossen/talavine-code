namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Revalidates the host's current mission fence before dispatch or replay and
/// delegates configured reservations to the existing budget owner. Hosts supply
/// the authority callback and ledger; unlimited work needs no reservation.
/// </summary>
public sealed class MissionScheduledToolAdmission(AgentSession session,
    IScheduledToolBudgetReservation? budgets = null) : IScheduledToolAdmission
{
    public Task ValidateReplayAsync(ScheduledToolRecord call, CancellationToken ct)
        => ValidateExecutionAsync(ct);

    public async Task<IAsyncDisposable> AdmitAsync(ScheduledToolRecord call, CancellationToken ct)
    {
        await ValidateExecutionAsync(ct);
        if (!string.IsNullOrWhiteSpace(session.TurnContext?.MissionBudgetReference))
            return await (budgets ?? throw new InvalidOperationException("Configured mission budget requires its reservation adapter."))
                .ReserveAsync(call, session.TurnContext.MissionBudgetReference, ct);
        return new UnlimitedReservation();
    }

    private Task ValidateExecutionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.TurnContext?.MissionObjectiveId is not null)
            return (session.TurnContext.ValidateMissionExecution
                ?? throw new InvalidOperationException("Mission requires the host execution fence."))(ct);
        return Task.CompletedTask;
    }

    private sealed class UnlimitedReservation : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
