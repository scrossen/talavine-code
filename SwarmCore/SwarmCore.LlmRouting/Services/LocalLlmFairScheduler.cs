using System.Globalization;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Pure election over current waiting metadata. The caller validates liveness and records service under the physical mutex.</summary>
internal static class LocalLlmFairScheduler
{
    internal static LocalLlmRuntimeQueueItem? Select(IReadOnlyList<LocalLlmRuntimeQueueItem> items,
        IReadOnlyDictionary<string, long> lastServed, DateTimeOffset now, TimeSpan starvationAge)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(lastServed);
        if (starvationAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(starvationAge));

        var waiting = items.Where(row => row.Phase is LocalLlmRuntimeQueuePhase.WaitingInApp
            or LocalLlmRuntimeQueuePhase.WaitingForDevice).ToArray();
        if (waiting.Length == 0) return null;

        // An old waiter gets a turn even under a continuous stream of higher-priority arrivals.
        // This never interrupts the running holder, which is deliberately absent from this election.
        var aged = waiting.Where(row => now - row.QueuedUtc >= starvationAge).ToArray();
        var priority = waiting.Min(Priority);
        var eligible = aged.Length > 0 ? aged : waiting.Where(row => Priority(row) == priority);
        return eligible.GroupBy(OwnerKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(row => row.QueuedUtc).ThenBy(row => row.Id, StringComparer.Ordinal).First())
            .OrderBy(row => lastServed.TryGetValue(OwnerKey(row), out var sequence) ? sequence : 0)
            .ThenBy(row => row.QueuedUtc)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Trusted root scope groups a goal and its children. Unknown owners share their exact process instance's turn.</summary>
    internal static string OwnerKey(LocalLlmRuntimeQueueItem row)
    {
        var application = row.Application ?? "";
        var owner = !string.IsNullOrWhiteSpace(row.SchedulingOwnerId) ? row.SchedulingOwnerId : row.OwnerId;
        var identity = !string.IsNullOrWhiteSpace(owner)
            ? "owner:" + Part(owner)
            : "process:" + row.ProcessId.ToString(CultureInfo.InvariantCulture) + ":"
                + row.ProcessStartedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        return Part(application) + identity;
    }

    private static string Part(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static int Priority(LocalLlmRuntimeQueueItem row) => row.SchedulingClass == LocalLlmSchedulingClass.RoutingDecision ? 0
        : row.Interactivity == LlmInteractivity.UserWaiting ? 1 : 2;
}
