namespace SwarmCore.PersonalMemory;

/// <summary>
/// Host-implemented persistence for personal memory. Talavine (SQLite + sync journal),
/// the hosted CRM (tenant SQL) and SwarmCode (local store) each implement this once.
///
/// <para><b>Writer contract (see agent-recipes/review-persistence-writers.md).</b></para>
/// <list type="bullet">
/// <item>Every method is scoped: rows whose scope differs from the argument are invisible
/// and must never be written. A store must reject a change whose embedded scope differs.</item>
/// <item><see cref="CommitAsync"/> is all-or-nothing. Every patch/delete/transition carries
/// the version the caller read; a mismatch rejects the whole change set with
/// <see cref="MemoryCommitStatus.VersionConflict"/> and mutates nothing.</item>
/// <item>Patches own only the fields they set (<see cref="MemoryField{T}.IsSet"/>), including
/// intentional null/false values. Never replace a row from a detached snapshot.</item>
/// <item>Rows and the host's sync/audit journal are written in the same transaction.</item>
/// <item>Each successful write increments the row version by exactly one.</item>
/// </list>
/// </summary>
public interface IPersonalMemoryStore
{
    /// <summary>
    /// Append a claim. Idempotent per (scope, <see cref="MemoryClaim.IdempotencyKey"/>): a repeat
    /// returns the stored claim with <see cref="MemoryClaimAppendResult.Created"/> = false and
    /// changes nothing.
    /// </summary>
    Task<MemoryClaimAppendResult> AppendClaimAsync(MemoryClaim claim, CancellationToken ct = default);

    /// <summary>Claims in the given statuses, oldest <see cref="MemoryClaim.CreatedAt"/> first, at most <paramref name="maxCount"/>.</summary>
    Task<IReadOnlyList<MemoryClaim>> GetClaimsAsync(
        MemoryScope scope,
        IReadOnlyCollection<MemoryClaimStatus> statuses,
        int maxCount,
        CancellationToken ct = default);

    Task<MemoryClaim?> GetClaimAsync(MemoryScope scope, Guid claimId, CancellationToken ct = default);

    /// <summary>Facts in scope matching <paramref name="query"/>.</summary>
    Task<IReadOnlyList<MemoryFact>> GetFactsAsync(MemoryScope scope, MemoryFactQuery query, CancellationToken ct = default);

    Task<MemoryFact?> GetFactAsync(MemoryScope scope, Guid factId, CancellationToken ct = default);

    /// <summary>Unexpired suppression rules in scope.</summary>
    Task<IReadOnlyList<MemorySuppression>> GetSuppressionsAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>Atomically apply a version-checked change set.</summary>
    Task<MemoryCommitResult> CommitAsync(MemoryScope scope, MemoryChangeSet changes, CancellationToken ct = default);

    /// <summary>Scopes that currently have <see cref="MemoryClaimStatus.Pending"/> claims (for the background consolidation job).</summary>
    Task<IReadOnlyList<MemoryScope>> GetScopesWithPendingClaimsAsync(int maxCount, CancellationToken ct = default);

    /// <summary>
    /// Scopes with a current fact that a consolidation pass changes on time alone (see
    /// <see cref="MemoryDueQuery.IsDue"/>): a plan whose date has passed, a stale non-user fact, an old
    /// conversation summary. Lets the background job update time-sensitive facts ("is going to Singapore"
    /// -> "went to Singapore") for people who have not used the assistant since. A store that cannot
    /// answer cheaply may return an empty list (only scopes with new claims are then consolidated).
    /// </summary>
    Task<IReadOnlyList<MemoryScope>> GetScopesWithDueFactsAsync(MemoryDueQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryScope>>([]);

    /// <summary>
    /// What a forget must touch, in scope: every fact stating one of <paramref name="statements"/>
    /// (normalized with <see cref="MemoryTextScreen.NormalizeForComparison"/>), every claim stating one
    /// of them or resulting in one of those facts or <paramref name="factIds"/>, and every fact that
    /// those facts superseded. A store with an index (see <see cref="MemoryForgetFingerprint.Statement"/>)
    /// overrides this; the default reads the scope's facts and claims.
    /// </summary>
    async Task<MemoryForgetCandidates> GetForgetCandidatesAsync(
        MemoryScope scope,
        IReadOnlyCollection<string> statements,
        IReadOnlyCollection<Guid> factIds,
        CancellationToken ct = default)
    {
        var texts = statements.ToHashSet(StringComparer.Ordinal);
        var facts = (await GetFactsAsync(scope, MemoryFactQuery.All with { MaxCount = 100_000 }, ct).ConfigureAwait(false))
            .Where(f => f.Scope == scope)
            .ToList();
        var sameStatement = facts.Where(f => texts.Contains(MemoryTextScreen.NormalizeForComparison(f.Text))).ToList();
        var ids = factIds.Concat(sameStatement.Select(f => f.Id)).ToHashSet();
        var claims = (await GetClaimsAsync(scope, Enum.GetValues<MemoryClaimStatus>(), 100_000, ct).ConfigureAwait(false))
            .Where(c => c.Scope == scope
                && ((c.ResultFactId is { } r && ids.Contains(r)) || texts.Contains(MemoryTextScreen.NormalizeForComparison(c.Text))))
            .ToList();
        var successorsOf = facts.Where(f => f.SupersededById is { } next && ids.Contains(next)).ToList();
        return new(sameStatement, claims, successorsOf);
    }
}

/// <summary>
/// The rows a forget reads: facts with the same statement, claims about it, and facts whose
/// <see cref="MemoryFact.SupersededById"/> points at one of the forgotten facts.
/// </summary>
public sealed record MemoryForgetCandidates(
    IReadOnlyList<MemoryFact> SameStatementFacts,
    IReadOnlyList<MemoryClaim> Claims,
    IReadOnlyList<MemoryFact> SupersededByForgotten);

/// <summary>Which facts are due for a time-only consolidation pass.</summary>
public sealed record MemoryDueQuery(DateTimeOffset Now, DateTimeOffset SummariesCreatedBefore, int MaxCount)
{
    /// <summary>The store-independent definition every implementation must match.</summary>
    public bool IsDue(MemoryFact fact)
    {
        if (fact.SupersededById is not null || (fact.ValidUntil is { } until && until <= Now)) return false;
        if (fact.Category == MemoryCategory.Plan && fact.EventAt is { } at && at <= Now) return true;
        if (fact.Category == MemoryCategory.ConversationSummary) return fact.CreatedAt <= SummariesCreatedBefore;
        return fact.StaleAfter is { } stale && stale <= Now && fact.Authority != MemorySourceAuthority.UserAuthored && !fact.UserConfirmed;
    }
}

public sealed record MemoryClaimAppendResult(MemoryClaim Claim, bool Created);

/// <summary>Which facts to read.</summary>
public sealed record MemoryFactQuery
{
    /// <summary>Only facts not superseded and not past <see cref="MemoryFact.ValidUntil"/> at <see cref="AsOf"/>.</summary>
    public bool CurrentOnly { get; init; } = true;
    public bool IncludeSuppressed { get; init; } = true;
    public DateTimeOffset? AsOf { get; init; }
    public IReadOnlyCollection<MemoryCategory>? Categories { get; init; }
    public string? Key { get; init; }
    public int MaxCount { get; init; } = 2000;

    public static MemoryFactQuery Current { get; } = new();
    public static MemoryFactQuery All { get; } = new() { CurrentOnly = false };
}

/// <summary>An explicitly-owned field in a patch. <c>default</c> means "not set / leave alone".</summary>
public readonly record struct MemoryField<T>(bool IsSet, T Value)
{
    public static MemoryField<T> Set(T value) => new(true, value);
    public static MemoryField<T> Unset => default;
}

/// <summary>Explicit-field update of one fact. Unset fields are untouched.</summary>
public sealed record MemoryFactPatch
{
    public MemoryField<string> Text { get; init; }
    public MemoryField<MemoryCategory> Category { get; init; }
    public MemoryField<MemorySourceAuthority> Authority { get; init; }
    public MemoryField<double> Confidence { get; init; }
    public MemoryField<DateTimeOffset?> ValidUntil { get; init; }
    public MemoryField<DateTimeOffset?> StaleAfter { get; init; }
    public MemoryField<DateTimeOffset?> EventAt { get; init; }
    public MemoryField<Guid?> SupersededById { get; init; }
    public MemoryField<bool> Suppressed { get; init; }
    public MemoryField<bool> UserConfirmed { get; init; }
    public MemoryField<bool> Sensitive { get; init; }

    /// <summary>Evidence appended (deduplicated by <see cref="MemoryEvidence.IdentityKey"/>).</summary>
    public IReadOnlyList<MemoryEvidence> AddEvidence { get; init; } = [];

    public bool IsEmpty => !Text.IsSet && !Category.IsSet && !Authority.IsSet && !Confidence.IsSet
        && !ValidUntil.IsSet && !StaleAfter.IsSet && !EventAt.IsSet && !SupersededById.IsSet
        && !Suppressed.IsSet && !UserConfirmed.IsSet && !Sensitive.IsSet && AddEvidence.Count == 0;
}

public abstract record MemoryChange;

/// <summary>Insert a new fact (its <see cref="MemoryFact.Version"/> must be 0; the store assigns 1).</summary>
public sealed record MemoryFactInsert(MemoryFact Fact) : MemoryChange;

public sealed record MemoryFactUpdate(Guid FactId, long ExpectedVersion, MemoryFactPatch Patch) : MemoryChange;

/// <summary>Hard delete (forget). Only the person's forget action produces this.</summary>
public sealed record MemoryFactDelete(Guid FactId, long ExpectedVersion) : MemoryChange;

public sealed record MemoryClaimTransition(
    Guid ClaimId,
    long ExpectedVersion,
    MemoryClaimStatus Status,
    Guid? ResultFactId,
    string? Reason) : MemoryChange;

/// <summary>Hard delete of a claim (forget removes the evidence that produced a fact).</summary>
public sealed record MemoryClaimDelete(Guid ClaimId, long ExpectedVersion) : MemoryChange;

public sealed record MemorySuppressionInsert(MemorySuppression Suppression) : MemoryChange;

/// <summary>An ordered set of changes applied atomically.</summary>
public sealed record MemoryChangeSet(IReadOnlyList<MemoryChange> Changes)
{
    public static MemoryChangeSet Of(params MemoryChange[] changes) => new(changes);
}

public enum MemoryCommitStatus
{
    Committed = 0,
    VersionConflict = 1,
    NotFound = 2,
    ScopeMismatch = 3,
    Invalid = 4,
}

public sealed record MemoryCommitResult(MemoryCommitStatus Status, string? Detail = null)
{
    public bool Succeeded => Status == MemoryCommitStatus.Committed;
    public static MemoryCommitResult Ok { get; } = new(MemoryCommitStatus.Committed);
}
