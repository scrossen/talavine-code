namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Phases an embedding pin migration moves through. The normal path is
/// Pending → InProgress → ReadyToFlip → Flipped. Canonical content can change after
/// validation, so ReadyToFlip may durably reopen to InProgress. Cancelled/Failed are terminal.
///
/// During <see cref="InProgress"/>, target vectors are written to a device-local shadow
/// generation while exact source-generation sidecars remain searchable. The canonical
/// row is the mutable synced logical record/cache and may change while source/target
/// generations are repaired from that content. At <see cref="ReadyToFlip"/> the target
/// corpus has been validated;
/// <see cref="IEmbeddingRebuildOrchestrator.ApplyAsync"/> flips the search pin so the
/// complete target generation becomes visible in one logical step. The retained exact
/// source sidecars remain on disk for rollback until generation retention removes them.
/// </summary>
public enum EmbeddingRebuildPhase
{
    Pending = 1,
    InProgress = 2,
    ReadyToFlip = 3,
    Flipped = 4,
    Cancelled = 5,
    Failed = 6,
}

/// <summary>
/// Snapshot of an embedding rebuild. Persisted in settings so the migration survives
/// app restarts — a 50k-row local re-embed is hours of work and shouldn't lose progress
/// on a routine reboot.
/// </summary>
public sealed record EmbeddingRebuildState(
    EmbeddingModelIdentity TargetPin,
    EmbeddingModelIdentity SourcePin,
    EmbeddingRebuildPhase Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int TotalRowsEstimate,
    int RowsRebuilt,
    int RowsFailed,
    string? StatusMessage,
    Guid? RebuildId = null,
    Guid? SourceGenerationId = null,
    Guid? TargetGenerationId = null)
{
    public double Progress => TotalRowsEstimate > 0
        ? Math.Min(1.0, (double)RowsRebuilt / TotalRowsEstimate)
        : 0;

    public bool IsActive => Phase is EmbeddingRebuildPhase.Pending
        or EmbeddingRebuildPhase.InProgress
        or EmbeddingRebuildPhase.ReadyToFlip;

    /// <summary>Exact candidate generation; falls back to the older episode id on WIP profiles.</summary>
    public Guid EffectiveTargetGenerationId => TargetGenerationId ?? RebuildId ?? Guid.Empty;

    /// <summary>Exact generation that remains active until apply.</summary>
    public Guid EffectiveSourceGenerationId => SourceGenerationId ?? Guid.Empty;
}

/// <summary>
/// Coordinates an embedding model switch: from the user picking a new model, through
/// the corpus re-embed, to the atomic flip of the active pin. Built around the contract
/// "search keeps using the old pin until the new pin's corpus is complete" — so the
/// user never sees a half-built semantic index.
///
/// The orchestrator owns the state machine; the actual re-embedding work runs in a
/// hosted backfill service that consults the orchestrator for the current target.
/// </summary>
public interface IEmbeddingRebuildOrchestrator
{
    /// <summary>Return the active rebuild, or null when nothing is in flight.</summary>
    Task<EmbeddingRebuildState?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Queue a rebuild to a new pin. Validates that no other rebuild is in flight,
    /// captures the current pin as the source, and transitions to <see cref="EmbeddingRebuildPhase.Pending"/>.
    /// </summary>
    Task<EmbeddingRebuildState> StartAsync(EmbeddingModelIdentity target, int totalRowsEstimate, CancellationToken ct = default);

    /// <summary>Backfill worker advances the state machine. Returns the new state.</summary>
    Task<EmbeddingRebuildState> UpdateProgressAsync(int rowsRebuilt, int rowsFailed, string? statusMessage, CancellationToken ct = default);

    /// <summary>Backfill worker transitions InProgress → ReadyToFlip after exact coverage validation.</summary>
    Task<EmbeddingRebuildState> MarkReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically flip the active pin to the rebuild target. Only valid in
    /// <see cref="EmbeddingRebuildPhase.ReadyToFlip"/>. The user typically calls this
    /// from the rebuild UI after reviewing the completed state.
    /// </summary>
    Task<EmbeddingRebuildState> ApplyAsync(CancellationToken ct = default);

    /// <summary>
    /// Cancel an in-flight rebuild. The old pin stays active. Terminal intent is durable
    /// before target-shadow cleanup, which is best-effort and otherwise left to generation GC.
    /// </summary>
    Task<EmbeddingRebuildState> CancelAsync(string reason, CancellationToken ct = default);

    /// <summary>
    /// Pin to use for ordinary live writes. This always remains the active/search pin;
    /// the background rebuild worker alone writes the target generation. Hot-path; cached.
    /// </summary>
    EmbeddingModelIdentity GetWritePin();

    /// <summary>
    /// Compatibility signal for stores that support staged writes. Live writes remain
    /// canonical under the active pin, so the production orchestrator returns false.
    /// </summary>
    bool ShouldStageWrites { get; }

    /// <summary>
    /// Notify the rebuild state machine that canonical source content changed. A rebuild
    /// that had reached ReadyToFlip returns durably to InProgress so the worker resumes.
    /// </summary>
    Task NotifySourceMutationAsync(CancellationToken ct = default);

    /// <summary>
    /// Serialize canonical semantic mutations with the validate-and-flip boundary,
    /// including sibling processes that share the profile. Acquiring the lease refreshes
    /// durable pin and rebuild state. A mutation commits and notifies while holding it;
    /// Apply takes the same lease before final corpus validation so no write lands in the gap.
    /// </summary>
    ValueTask<IAsyncDisposable> EnterMutationAsync(CancellationToken ct = default);

    /// <summary>
    /// Take the same cross-process exclusion lease for source-verified metadata-only
    /// repair, without resolving a model or changing generation authority. Callers
    /// must preserve vectors, content and generation identity, and keep any matching
    /// sidecar source-version changes atomic with the canonical metadata edit.
    /// </summary>
    ValueTask<IAsyncDisposable> EnterMetadataMutationAsync(CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support model-independent metadata repair.");
}
