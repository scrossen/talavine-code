namespace SwarmCore.Abstractions.Budgets;

/// <summary>Host-owned authority inherited by worker calls, retries and verification.</summary>
public sealed record MissionBudgetExecutionContext(
    IMissionBudgetLedger Ledger, Guid ProfileId, Guid MissionId, string Reference);

public static class MissionBudgetExecutionScope
{
    private static readonly AsyncLocal<MissionBudgetExecutionContext?> Ambient = new();
    public static MissionBudgetExecutionContext? Current => Ambient.Value;

    public static IDisposable Enter(IMissionBudgetLedger ledger, Guid profile, Guid mission, string reference)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var previous = Ambient.Value;
        Ambient.Value = new(ledger, profile, mission, reference);
        return new Restore(previous);
    }

    private sealed class Restore(MissionBudgetExecutionContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
