namespace SwarmCore.Abstractions.Learning;

/// <summary>
/// One observed disagreement between a machine verdict and the user's verdict,
/// harvested passively from an ordinary user action (flipping a message's
/// attention, moving it out of the stream the pipeline chose, deciding a
/// worker proposal). Raw material for the unified correction-learning
/// pipeline (docs/unified-correction-learning-design-2026-08.md): recent
/// corrections are injected as few-shot ground truth into the relevant
/// classifier prompt, and accumulated corrections are later distilled and
/// compiled into deterministic rules.
///
/// Verdicts are deliberately plain normalized strings, not enums: the axes
/// that describe a message (kind / domains / attention) are product-owned and
/// evolve; this table must survive taxonomy changes untouched. Observers
/// normalize through the taxonomy helpers before recording.
///
/// Semantics owned by <see cref="ICorrectionStore"/>, not by callers:
/// one record per (Domain, ItemKey); the FIRST machine verdict is pinned as
/// the stable "before" across repeated flips; a flip back to the pinned
/// machine verdict deletes the record (no disagreement left — self-corrections
/// are noise, not signal).
/// </summary>
public sealed class CorrectionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Which learning axis this correction belongs to — one of
    /// <see cref="CorrectionDomains"/>. Axes are independent: an attention
    /// correction never implies anything about kind, retention, or trust.
    /// </summary>
    public string Domain { get; set; } = "";

    /// <summary>
    /// Learning scope: empty = profile-wide; "agent:{id}" / "tenant:{id}" for
    /// agent- or tenant-bound corrections (which must never leak into the
    /// personal scope — see the agent knowledge partition invariant).
    /// </summary>
    public string Scope { get; set; } = "";

    /// <summary>
    /// Stable identity of the corrected item (message Guid, proposal id) —
    /// the flip-back/upsert key together with <see cref="Domain"/>.
    /// </summary>
    public string ItemKey { get; set; } = "";

    /// <summary>
    /// Normalized grouping key for later distillation — lowercased sender
    /// email / digits-only phone for messages, action signature for agent
    /// corrections. Matches <c>ContactIdentifier.NormalizedValue</c>
    /// conventions where applicable.
    /// </summary>
    public string SubjectKey { get; set; } = "";

    /// <summary>What the machine said, normalized (pinned at first flip).</summary>
    public string MachineVerdict { get; set; } = "";

    /// <summary>What the user says it actually is, normalized (latest flip wins).</summary>
    public string UserVerdict { get; set; } = "";

    /// <summary>
    /// Compact JSON evidence snapshot taken at observation time (sender,
    /// subject, stream, account) — enough to render a few-shot example and to
    /// justify a distilled rule later, without re-reading the message.
    /// </summary>
    public string EvidenceJson { get; set; } = "{}";

    /// <summary>When the user action that produced this correction happened (latest).</summary>
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional persistent writer identity supplied by the replication host.</summary>
    public Guid? WriterDeviceId { get; set; }
    public long? WriterMonotonicTicks { get; set; }

    /// <summary>
    /// Set when a distilled rule proposal consumed this correction (phase 2+).
    /// Consumed corrections are excluded from re-proposal but retained as
    /// provenance for the rule they produced.
    /// </summary>
    public Guid? ConsumedByProposalId { get; set; }
}

/// <summary>
/// Well-known correction domains. String-typed and open — consumers may add
/// domains without touching this contract; these constants exist so the
/// message-pipeline observers and the few-shot renderer agree on spelling.
/// </summary>
public static class CorrectionDomains
{
    /// <summary>
    /// Message utility corrections use useful_no_action | low_value_no_action.
    /// User importance actions never teach action_required; that remains
    /// grounded in the current message.
    /// </summary>
    public const string MessageAttention = "message.attention";

    /// <summary>Kind flips: correspondence | transaction | notification | newsletter | promotion | other.</summary>
    public const string MessageKind = "message.kind";

    /// <summary>User moved a message to a different stream/folder than the pipeline routed it to.</summary>
    public const string MessageStream = "message.stream";

    /// <summary>Boss decisions on worker/agent proposals (phase 4).</summary>
    public const string AgentAction = "agent.action";
}
