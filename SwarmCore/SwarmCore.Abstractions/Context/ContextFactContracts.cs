namespace SwarmCore.Abstractions.Context;

public interface IContextFactIngestionService
{
    Task<ContextFact> RecordAsync(
        ContextFactDraft draft,
        CancellationToken ct = default);
}

public sealed record ContextFactDraft
{
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public string? NormalizedKey { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public string? StateKey { get; init; }
    public double Confidence { get; init; } = 0.5;
    public bool IsUserConfirmed { get; init; }
    public bool IsSensitive { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required ContextSource Source { get; init; }
}

public interface IContextFactRepository
{
    /// <summary>Bounded owner-fact candidates in stable ID order. Callers must validate current lifecycle and source evidence before exposing them.</summary>
    Task<IReadOnlyList<ContextFact>> BrowseCandidatesAsync(int skip, int take, bool includeSensitive, CancellationToken ct = default)
        => throw new NotSupportedException("This fact store does not support bounded browsing.");

    Task<ContextFact> UpsertAsync(ContextFact fact, CancellationToken ct = default);

    Task<IReadOnlyList<ContextFact>> SearchAsync(
        ContextFactQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyList<ContextFact>> GetByIdsAsync(
        IReadOnlyCollection<Guid> factIds,
        CancellationToken ct = default);

    /// <summary>
    /// Every stored fact extracted FROM a named set of source records, oldest
    /// observation first — sensitive ones included, expiry ignored.
    ///
    /// A provenance read, not retrieval. The caller already holds these records
    /// and is already showing them, so the relevance ranking, the sensitivity
    /// gate, and the agent-partition exclusion that <see cref="SearchAsync"/>
    /// applies would all be answering a question nobody asked, and would make
    /// "what did we learn from this message" depend on wording. Anything
    /// assembling a prompt or a broad recall keeps using <see cref="SearchAsync"/>.
    ///
    /// Bounded by the id list the caller passes. Default returns nothing so
    /// lightweight doubles need not implement it.
    /// </summary>
    Task<IReadOnlyList<ContextFact>> ListBySourceRecordIdsAsync(
        IReadOnlyCollection<string> sourceRecordIds,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContextFact>>([]);

    Task<bool> ConfirmAsync(
        Guid factId,
        string? correctedText = null,
        CancellationToken ct = default);

    Task<bool> ForgetAsync(
        Guid factId,
        CancellationToken ct = default);

    Task<int> MergeAsync(
        ContextFact mergedWinner,
        IReadOnlyCollection<Guid> loserFactIds,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
        => Task.FromResult(0);

    Task<int> ForgetBySourceRecordIdsAsync(
        IReadOnlyCollection<Guid> sourceRecordIds,
        CancellationToken ct = default);

    Task<int> ForgetBySourceMessageRefsAsync(
        IReadOnlyCollection<(Guid AccountId, Guid MessageId)> sourceMessages,
        CancellationToken ct = default)
        => ForgetBySourceRecordIdsAsync(sourceMessages.Select(x => x.MessageId).ToArray(), ct);

    /// <summary>
    /// Source re-projection: removes the facts derived from these exact
    /// messages but keeps user-owned rows (a user's confirmation or correction
    /// and the pre-assertion copy retained for knowledge-time reads), so a
    /// re-extraction never deletes and re-creates them under new ids.
    /// </summary>
    Task<int> ForgetDerivedBySourceMessageRefsAsync(
        IReadOnlyCollection<(Guid AccountId, Guid MessageId)> sourceMessages,
        CancellationToken ct = default)
        => throw new NotSupportedException("This context store cannot preserve user-owned facts during source re-projection.");

    Task<int> ForgetBySourceAccountIdAsync(
        Guid sourceAccountId,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently retire an explicitly removed source account and remove its
    /// facts atomically. Later producers must not recreate that account's facts.
    /// Ordinary forgetting/reprojection does not imply permanent retirement.
    /// </summary>
    Task<int> RetireSourceAccountAsync(Guid sourceAccountId, CancellationToken ct = default)
        => throw new NotSupportedException("This context store does not support durable source-account retirement.");

    /// <summary>
    /// Retire every fact emitted by a given source kind (e.g. "calendar",
    /// "task", "contact"). Used by source-agnostic knowledge contributors that
    /// own all facts of their kind and reconcile with a retire-then-emit pass.
    /// Default no-op so lightweight test doubles need not implement it.
    /// </summary>
    Task<int> ForgetBySourceKindAsync(
        string sourceKind,
        CancellationToken ct = default)
        => Task.FromResult(0);

    Task<int> ClearAsync(CancellationToken ct = default);
}

public interface IContextFactSemanticService
{
    Task IndexAsync(ContextFact fact, CancellationToken ct = default);

    Task RemoveAsync(ContextFact fact, CancellationToken ct = default)
        => Task.CompletedTask;

    Task<int> IndexExistingAsync(int limit = 2000, CancellationToken ct = default);

    Task<IReadOnlyList<ContextFact>> SearchAsync(
        ContextFactQuery query,
        CancellationToken ct = default);
}

public sealed record ContextFactQuery
{
    public string? QueryText { get; init; }
    public string? Kind { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IncludeSensitive { get; init; }
    public bool IncludeExpired { get; init; }
    /// <summary>
    /// Include facts a newer same-state observation has replaced. Current-state
    /// retrieval leaves this false; timeline and provenance reads set it.
    /// </summary>
    public bool IncludeSuperseded { get; init; }
    /// <summary>Optional exact source-account allowlist for provenance-safe retrieval.</summary>
    public IReadOnlySet<Guid>? AllowedSourceAccountIds { get; init; }
    /// <summary>
    /// Evaluation instant. When supplied this is an as-of read: evidence
    /// observed after it is not yet available, so it cannot supersede the
    /// state that was current at that instant.
    /// </summary>
    public DateTimeOffset? Now { get; init; }
    /// <summary>State that applied at this instant; default is Now. Due dates are not effective dates.</summary>
    public DateTimeOffset? EffectiveAt { get; init; }
    /// <summary>Only evidence already recorded by this instant. Null means all knowledge currently stored.</summary>
    public DateTimeOffset? KnownAt { get; init; }
    /// <summary>Bounded candidate identities, e.g. semantic hits; state resolution still includes their siblings.</summary>
    public IReadOnlyCollection<Guid>? CandidateFactIds { get; init; }
    public int Limit { get; init; } = 12;
}
