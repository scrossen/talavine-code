namespace SwarmCore.Abstractions.Learning;

/// <summary>
/// A single observation handed to the store by an observer. The store — not
/// the caller — decides whether it becomes, updates, or deletes a
/// <see cref="CorrectionRecord"/> (verdict pinning + flip-back deletion).
/// </summary>
/// <param name="Domain">One of <see cref="CorrectionDomains"/>.</param>
/// <param name="ItemKey">Stable identity of the corrected item.</param>
/// <param name="SubjectKey">Normalized grouping key (sender identifier / action signature).</param>
/// <param name="MachineVerdict">The machine's verdict at the moment of the flip, normalized.</param>
/// <param name="UserVerdict">The user's verdict, normalized.</param>
/// <param name="EvidenceJson">Compact JSON envelope snapshot.</param>
/// <param name="Scope">Empty for profile-wide; "agent:{id}" / "tenant:{id}" otherwise.</param>
public sealed record CorrectionObservation(
    string Domain,
    string ItemKey,
    string SubjectKey,
    string MachineVerdict,
    string UserVerdict,
    string EvidenceJson,
    string Scope = "");

/// <summary>Outcome of recording an observation.</summary>
public enum CorrectionOutcome
{
    /// <summary>New disagreement stored.</summary>
    Recorded,

    /// <summary>Existing record updated with a new user verdict (machine verdict stays pinned).</summary>
    Updated,

    /// <summary>User flipped back to the pinned machine verdict — record deleted, nothing to learn.</summary>
    DroppedFlipBack,

    /// <summary>User verdict equals machine verdict and no record existed — no disagreement, nothing stored.</summary>
    NoDisagreement,
}

/// <summary>
/// Durable store for harvested corrections. Implementations enforce:
/// one record per (Domain, ItemKey); machine-verdict pinning (first observed
/// machine verdict survives later flips); flip-back deletion; and a bounded
/// per-domain cap (<see cref="MaxPerDomain"/>, oldest evicted) so the store
/// is a rolling window of recent signal, not an unbounded log.
/// </summary>
public interface ICorrectionStore
{
    /// <summary>Rolling cap per domain; oldest records evicted beyond this.</summary>
    const int MaxPerDomain = 500;

    /// <summary>Apply an observation (see semantics on <see cref="ICorrectionStore"/>).</summary>
    Task<CorrectionOutcome> RecordAsync(CorrectionObservation observation, CancellationToken ct = default);

    /// <summary>
    /// Most recent corrections for a domain (newest first), for few-shot
    /// prompt injection and, later, distillation. Excludes consumed records.
    /// </summary>
    Task<IReadOnlyList<CorrectionRecord>> ListRecentAsync(
        string domain, int take, string scope = "", CancellationToken ct = default);

    /// <summary>Unconsumed correction count for a domain (distillation trigger, phase 2).</summary>
    Task<int> CountUnconsumedAsync(string domain, string scope = "", CancellationToken ct = default);

    /// <summary>
    /// Stamp corrections as consumed by a distilled rule proposal. Consumed
    /// records leave the few-shot window and are never re-proposed. Ids not
    /// present are ignored. Returns the number of records stamped. A consumed
    /// record revives (consumption cleared) if the user disagrees again on the
    /// same item, and may still be deleted by a flip-back or retraction —
    /// <see cref="LearnedRule.SourceCorrectionIds"/> is advisory provenance,
    /// not a foreign key.
    /// </summary>
    Task<int> MarkConsumedAsync(
        IReadOnlyCollection<Guid> correctionIds, Guid proposalId, CancellationToken ct = default);

    /// <summary>
    /// Remove a correction because the user RETRACTED the action that created
    /// it (e.g. unflagging), without asserting any verdict of their own.
    /// Distinct from a flip-back: no user verdict is synthesized, so a
    /// retraction can never fabricate a disagreement. No-op when absent.
    /// </summary>
    Task<bool> RetractAsync(string domain, string itemKey, CancellationToken ct = default);
}
