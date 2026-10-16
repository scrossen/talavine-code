namespace SwarmCore.Abstractions.Scheduling;

/// <summary>
/// Records unfinished evidence transitions. A repeated transition supplies no
/// new progress; distinct transitions remain eligible without an attempt cap.
/// Hosts define evidence identity and decide how to report non-convergence.
/// Call only after a fresh observation still requires another pass, so a
/// revisited state can settle normally when that observation succeeds.
/// </summary>
public sealed class ConvergenceProgress<T> where T : notnull
{
    private readonly HashSet<(T Before, T After)> _transitions = [];

    public bool TryAdvance(T before, T after) => _transitions.Add((before, after));
}
