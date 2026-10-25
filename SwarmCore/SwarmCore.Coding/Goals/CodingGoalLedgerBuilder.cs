using System.Runtime.CompilerServices;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Coding.Goals;

/// <summary>
/// Builds a goal's ledger incrementally. Receipts are immutable records, so a completed receipt is described once
/// per instance and reused while the host keeps serving the same snapshot instances; open calls and host
/// verification depend on the rest of the goal and are evaluated on every build.
/// </summary>
public sealed class CodingGoalLedgerBuilder
{
    private readonly ConditionalWeakTable<ContinualToolReceipt, Described> _described = new();

    public IReadOnlyList<CodingGoalLedgerEntry> Build(ContinualObjectiveSnapshot goal, bool includeObservations = false)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var entries = new List<CodingGoalLedgerEntry>(goal.Receipts.Count);
        foreach (var receipt in goal.Receipts)
        {
            if (!includeObservations && receipt.SideEffect == ContinualSideEffectClass.ReadOnly) continue;
            CodingGoalLedgerEntry? entry;
            if (receipt.State == ContinualToolReceiptState.Started) entry = CodingGoalLedger.Describe(goal, receipt, includeObservations: true);
            else if (_described.TryGetValue(receipt, out var known)) entry = known.Entry;
            else
            {
                entry = CodingGoalLedger.Describe(goal, receipt, includeObservations: true);
                _described.AddOrUpdate(receipt, new Described(entry));
            }
            if (entry is null) continue;
            var verified = receipt.HostResolution is not null && ContinualReceiptResolutionPolicy.IsResolved(receipt, goal.Receipts);
            entries.Add(entry.HostVerified == verified ? entry : entry with { HostVerified = verified });
        }
        return CodingGoalLedger.Order(entries);
    }

    private sealed record Described(CodingGoalLedgerEntry? Entry);
}
