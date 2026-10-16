namespace SwarmCore.Knowledge;

/// <summary>
/// Combines all summaries in source order through a balanced tree. Pairing bounds
/// the number of child summaries in each request without dropping source sections
/// or setting a limit on document size, reduction depth, or total work.
/// Hosts own model calls, durable node caching and the definition of a failed step.
/// </summary>
public static class OrderedSummaryReduction
{
    public static async Task<T> ReduceAsync<T>(
        IReadOnlyList<T> summaries,
        Func<T, T, CancellationToken, Task<T>> combine,
        Func<T, bool> isFailure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(combine);
        ArgumentNullException.ThrowIfNull(isFailure);
        if (summaries.Count == 0) throw new ArgumentException("At least one summary is required.", nameof(summaries));
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<T> current = summaries.ToArray();
        foreach (var summary in current)
        {
            if (isFailure(summary)) return summary;
        }

        while (current.Count > 1)
        {
            var next = new List<T>();
            for (var index = 0; index < current.Count; index += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index + 1 == current.Count)
                {
                    next.Add(current[index]);
                    continue;
                }

                var combined = await combine(current[index], current[index + 1], cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (isFailure(combined)) return combined;
                next.Add(combined);
            }

            current = next;
        }

        return current[0];
    }
}
