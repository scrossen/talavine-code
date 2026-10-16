namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// Selects the oldest pending operation for each dependency key, then gives
/// least-recently-attempted independent work its turn. Failed work remains
/// durable without starving other keys or allowing a dependent write to pass it.
/// </summary>
public static class DurableRetryOrder
{
    public static IReadOnlyList<T> SelectIndependentHeads<T, TKey>(
        IEnumerable<T> pending,
        Func<T, TKey> dependencyKey,
        Func<T, DateTime> createdAt,
        Func<T, DateTime?> lastAttemptAt)
        where TKey : notnull
        => pending.OrderBy(createdAt)
            .GroupBy(dependencyKey)
            .Select(group => group.First())
            .OrderBy(entry => lastAttemptAt(entry) ?? createdAt(entry))
            .ThenBy(createdAt)
            .ToArray();
}
