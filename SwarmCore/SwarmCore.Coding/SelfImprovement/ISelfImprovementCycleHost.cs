namespace SwarmCore.Coding.SelfImprovement;

public enum SelfImprovementCycleOutcome
{
    /// <summary>A fix is committed on its own branch and the build and test checks passed in the worktree.</summary>
    Verified,
    /// <summary>The cycle ran but produced no verified fix (counts as an attempt).</summary>
    Failed,
    /// <summary>The cycle cannot be run at all here (no checks, no git repository); the item needs a person.</summary>
    Unverifiable,
    /// <summary>Stopped before an outcome (the person came back, the host shut down); not an attempt.</summary>
    Cancelled,
}

/// <summary>What one cycle produced. <paramref name="Note"/> is one plain sentence for the backlog row.</summary>
public sealed record SelfImprovementCycleResult(
    SelfImprovementCycleOutcome Outcome,
    string Note,
    string? Evidence = null,
    string? Branch = null,
    string? Commit = null,
    string? WorktreePath = null,
    string? SessionId = null,
    bool Merged = false)
{
    public static SelfImprovementCycleResult Cancelled(string note, string? sessionId = null) => new(SelfImprovementCycleOutcome.Cancelled, note, SessionId: sessionId);
    public static SelfImprovementCycleResult Unverifiable(string note) => new(SelfImprovementCycleOutcome.Unverifiable, note);
}

/// <summary>What the worker tells the host about the cycle it is starting.</summary>
public sealed record SelfImprovementCycleContext(string Owner, string RepositoryRoot, SelfImprovementOptions Options)
{
    /// <summary>The host reports the cycle's session as soon as it exists, so the UI can open it while it runs.</summary>
    public Action<string>? SessionStarted { get; init; }
}

/// <summary>
/// Runs one repair or feature cycle. The host owns isolation (a git worktree of the source checkout), the coding
/// session that does the work, the build and test checks that verify it, and the commit on the cycle's branch. The
/// worker owns when a cycle may start and what happens to the item afterwards.
/// </summary>
public interface ISelfImprovementCycleHost
{
    /// <summary>Recheck current host authority before starting a linked repair. No executor or lease is started by this check.</summary>
    Task<bool> CanRunAsync(SelfImprovementItem item, CancellationToken ct = default) => Task.FromResult(true);
    /// <summary>The product's source checkout cycles work on, or null when there is none here (an installed app without source).</summary>
    string? ResolveRepositoryRoot();

    Task<SelfImprovementCycleResult> RunAsync(SelfImprovementItem item, SelfImprovementCycleContext context, CancellationToken ct);
}
