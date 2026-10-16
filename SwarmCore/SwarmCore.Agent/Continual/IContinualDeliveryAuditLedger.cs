namespace SwarmCore.Agent.Continual;

/// <summary>
/// Host-owned durable budget and physical-call binding for one exact retained
/// verification candidate. A reservation is committed before inference and is
/// never refunded for an unknown outcome. Null means no further audit is admitted.
/// </summary>
public interface IContinualDeliveryAuditLedger
{
    /// <summary>Read-only adapter preflight before reservation. Positively prove the exact route honors this request's one-physical-call budget; set standard reasoning disabled when advertised.</summary>
    Task<bool> TryPreparePhysicalAuditRequestAsync(ContinualAcceptanceRequest request,
        SwarmCore.Abstractions.LlmRouting.LlmRouterOptions options, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>
    /// Positive host-owned scoped proof of committed correction intent, consumed
    /// physical pair, exact archived parent and atomic result/candidate linkage
    /// under current source/admission authority. A provenance label or fresh hash
    /// is never an audit allowance. Older hosts fail closed without inference.
    /// </summary>
    Task<bool> ValidateCorrectionProvenanceAsync(ContinualAcceptanceRequest request, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>
    /// Read-only inspection of this exact retained candidate's audit closure.
    /// Unknown is the fail-closed default: legacy ledgers cannot prove that an
    /// absent reservation means no prior physical call occurred.
    /// </summary>
    Task<ContinualDeliveryAuditClosure> InspectAsync(
        ContinualAcceptanceRequest request, CancellationToken ct)
        => Task.FromResult(new ContinualDeliveryAuditClosure(
            ContinualDeliveryAuditClosureKind.Unknown,
            ContinualDeliveryAuditClosureReason.MissingOrCorruptProof));

    Task<ContinualDeliveryAuditReservation?> ReserveAsync(
        ContinualAcceptanceRequest request, bool isRepair, CancellationToken ct);

    /// <summary>Enter the host's physical-inference scope across the awaited model call.</summary>
    IDisposable Enter(ContinualDeliveryAuditReservation reservation);

    /// <summary>
    /// Host flushes and verifies exactly one physical receipt, the current
    /// candidate and authority, then durably records this content-free shape.
    /// False is a hold, never permission to refund or repeat a physical call.
    /// </summary>
    Task<bool> RecordShapeAsync(ContinualAcceptanceRequest request,
        ContinualDeliveryAuditReservation reservation,
        ContinualDeliveryAuditResponseShape shape, CancellationToken ct);

    /// <summary>Seal a reserved call after any unknown/invalid physical outcome.</summary>
    Task SealAsync(ContinualAcceptanceRequest request,
        ContinualDeliveryAuditReservation reservation, CancellationToken ct);
}

public enum ContinualDeliveryAuditClosureKind { Open, ClosedRejected, Unknown }

/// <summary>Finite, content-free causes from a host's checked audit-ledger read.</summary>
public enum ContinualDeliveryAuditClosureReason
{
    None,
    InvalidReferences,
    IncompleteShape,
    SemanticRetry,
    NeedsUserHelp,
    UnknownPhysicalOutcome,
    MissingOrCorruptProof,
    BindingDrift,
}

/// <summary>
/// Read-only host proof state for the exact saved candidate. It carries no
/// model prose or source data. Identity fields bind a closure to the retained
/// candidate and the physical audit reservation that established it.
/// </summary>
public sealed record ContinualDeliveryAuditClosure(
    ContinualDeliveryAuditClosureKind Kind,
    ContinualDeliveryAuditClosureReason Reason,
    string? CandidateSha256 = null,
    Guid? AuditCallId = null,
    int? Ordinal = null)
{
    /// <summary>Stable host-neutral diagnostic for a finite closure reason.</summary>
    public static string Describe(ContinualDeliveryAuditClosureReason reason) => reason switch
    {
        ContinualDeliveryAuditClosureReason.InvalidReferences => "The retained audit rejected its receipt-reference roles; preserve the saved result for correction without repeating work.",
        ContinualDeliveryAuditClosureReason.IncompleteShape => "The retained audit response was incomplete; preserve the saved result for reconciliation without repeating work.",
        ContinualDeliveryAuditClosureReason.SemanticRetry => "The retained audit found the saved result incomplete; preserve completed effects and correct only the saved report.",
        ContinualDeliveryAuditClosureReason.NeedsUserHelp => "The retained audit identified an unresolved decision; preserve completed effects and request no automatic replay.",
        ContinualDeliveryAuditClosureReason.UnknownPhysicalOutcome => "The audit call's physical outcome is unknown; preserve the saved result and do not repeat it.",
        ContinualDeliveryAuditClosureReason.BindingDrift => "The retained audit no longer matches the exact saved candidate; preserve it for host reconciliation.",
        _ => "The audit closure proof is missing or corrupt; preserve the saved result for host reconciliation.",
    };
}

public sealed record ContinualDeliveryAuditReservation(Guid AuditCallId, int Ordinal, bool IsRepair);

public enum ContinualDeliveryAuditVerdictShape { Invalid, Delivered, Retry, NeedsUserHelp }
public enum ContinualDeliveryAuditDispositionShape { Missing, Invalid, Completed, WaitingForInput }

/// <summary>No source text, model prose, reason, or receipt identities leave the auditor in this ledger event.</summary>
public sealed record ContinualDeliveryAuditResponseShape(
    int SchemaVersion,
    ContinualDeliveryAuditVerdictShape Verdict,
    ContinualDeliveryAuditDispositionShape HostDisposition,
    bool EvidenceReferencesPresent,
    int EvidenceReferenceCount,
    bool ObservedReferencesPresent,
    int ObservedReferenceCount,
    bool ReferencesValid,
    bool RepairEligible)
{
    /// <summary>JSON field presence; neither the field nor its content is persisted.</summary>
    public bool ReasonPresent { get; init; }
    /// <summary>Present and nonblank, without retaining model prose.</summary>
    public bool ReasonValid { get; init; }
    /// <summary>All mandatory fields and reference roles passed structural validation.</summary>
    public bool Complete { get; init; }
}
