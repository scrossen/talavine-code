namespace SwarmCore.Abstractions.Crm;

/// <summary>
/// Shared contract for the narrow set of CRM writes a desktop email client makes on the user's
/// behalf: log an email, add a note, create a contact, create a ticket, raise a proposal, and
/// resolve a decision. Every call is a typed CRM command (the CRM applies its own invariants,
/// audit rows, and action receipts) — there is deliberately no generic "update record".
///
/// PRIVACY: like <see cref="ICrmReadConnector"/> this is the dumb pipe. It takes a fully
/// resolved <see cref="CrmConnection"/> and never decides which account may call it; the
/// fail-closed single-work-account gate lives upstream in the host's connection resolver.
/// Scope enforcement is the CRM's: a key without <see cref="CrmConnectorScopes.ActivityWrite"/>
/// (or <see cref="CrmConnectorScopes.TicketCreate"/> for tickets) receives a
/// <see cref="CrmWriteResult.Forbidden"/> result, never a silent success.
/// </summary>
public interface ICrmWriteConnector
{
    /// <summary><c>POST api/crm/emails</c> — idempotent on <see cref="CrmEmailLogRequest.MessageId"/>.</summary>
    Task<CrmWriteResult> LogEmailAsync(CrmConnection connection, CrmEmailLogRequest request, CancellationToken ct = default);

    /// <summary><c>POST api/crm/notes</c>.</summary>
    Task<CrmWriteResult> AddNoteAsync(CrmConnection connection, CrmNoteRequest request, CancellationToken ct = default);

    /// <summary><c>POST api/crm/contacts</c> — returns <see cref="CrmWriteResult.AlreadyExisted"/> on an address match.</summary>
    Task<CrmWriteResult> CreateContactAsync(CrmConnection connection, CrmContactCreateRequest request, CancellationToken ct = default);

    /// <summary><c>POST api/crm/tickets</c> — routes through the CRM's ticket service (numbering, contact match, audit); no autoresponse.</summary>
    Task<CrmWriteResult> CreateTicketAsync(CrmConnection connection, CrmTicketCreateRequest request, CancellationToken ct = default);

    /// <summary><c>POST api/crm/proposals</c> — raises a decision in the CRM's decision inbox; never executes the action.</summary>
    Task<CrmWriteResult> ProposeActionAsync(CrmConnection connection, CrmProposalRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>POST api/crm/decisions/{id}/resolve</c> — record the human's answer to a decision the
    /// CRM raised. <paramref name="resolution"/> must be one of <see cref="CrmDecision.Options"/>.
    /// </summary>
    Task<CrmWriteResult> ResolveDecisionAsync(CrmConnection connection, string decisionId, string resolution, string? note, CancellationToken ct = default);

    /// <summary>
    /// <c>POST api/crm/inquiries</c> — capture a message the user chose as a CRM inquiry plus an owned business
    /// case with a 24-hour follow-up task. Idempotent on <see cref="CrmInquiryCaptureRequest.ExternalReference"/>.
    /// Always user-initiated; the CRM sends nothing. Requires <see cref="CrmConnectorScopes.ActivityWrite"/>.
    /// </summary>
    Task<CrmInquiryCaptureResult> CaptureInquiryAsync(CrmConnection connection, CrmInquiryCaptureRequest request, CancellationToken ct = default);
}

/// <summary>
/// Body of <c>POST api/crm/inquiries</c>: one message the user chose to send to the CRM as an inquiry. The text is
/// stored as unconfirmed reference data; it never confirms intent or satisfies a qualification criterion.
/// </summary>
public sealed record CrmInquiryCaptureRequest
{
    /// <summary>The sender's email address (required by the CRM inquiry record).</summary>
    public string? SenderEmail { get; init; }
    public string? SenderName { get; init; }

    /// <summary>Optional E.164 phone (explicit + country code); never guessed.</summary>
    public string? SenderPhone { get; init; }
    public string? Company { get; init; }
    public string? Subject { get; init; }
    public string? Message { get; init; }
    public DateTime? ReceivedAtUtc { get; init; }

    /// <summary>
    /// The <c>talavine://message/{accountId}/{messageId}</c> reference; the CRM hashes it into the intake key so a
    /// second capture of the same message returns the first result.
    /// </summary>
    public string? ExternalReference { get; init; }

    /// <summary>Who referred this client, as the user stated it. Attribution only.</summary>
    public string? ReferredBy { get; init; }

    /// <summary>The exact CRM contact to attach to, when an earlier attempt reported several candidates.</summary>
    public long? ContactId { get; init; }
}

/// <summary>A CRM contact that matches the sender identity, offered when the match is ambiguous.</summary>
public sealed record CrmInquiryContactCandidate(long ContactId, string? DisplayName, string? Email, string? Company);

/// <summary>Result of <c>POST api/crm/inquiries</c>.</summary>
public sealed record CrmInquiryCaptureResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool Forbidden { get; init; }

    /// <summary>The CRM may have committed; capture again (it is idempotent) or inspect the CRM before anything else.</summary>
    public bool OutcomeUnknown { get; init; }

    /// <summary>True when this message was already captured; the earlier case is returned.</summary>
    public bool AlreadyExisted { get; init; }

    public string? CaseId { get; init; }
    public string? ContactId { get; init; }
    public string? InquiryId { get; init; }

    /// <summary>The owned follow-up task (due within 24 hours of receipt), like a website inquiry's.</summary>
    public string? TaskId { get; init; }
    public DateTime? FollowUpDueUtc { get; init; }

    /// <summary>Canonical citation for the contact (<c>crm:contact:17</c>).</summary>
    public string? Reference { get; init; }

    /// <summary>Relative CRM route (the contact page).</summary>
    public string? Link { get; init; }

    /// <summary>Set when several contacts match the sender; capture again with one <see cref="CrmInquiryCaptureRequest.ContactId"/>.</summary>
    public IReadOnlyList<CrmInquiryContactCandidate> Candidates { get; init; } = [];

    /// <summary>Names of details the CRM still needs (for example <c>clientEmail</c>).</summary>
    public IReadOnlyList<string> MissingFields { get; init; } = [];

    public static CrmInquiryCaptureResult Fail(string error, bool forbidden = false) => new() { Success = false, Error = error, Forbidden = forbidden };
    public static CrmInquiryCaptureResult Unknown() => new()
    {
        Success = false,
        OutcomeUnknown = true,
        Error = "The CRM may have saved this inquiry, but its reply was lost. Sending it again is safe; it will not duplicate.",
    };
}

/// <summary>
/// Mints a one-time, short-lived signed-in URL (<c>POST api/crm/handoff</c>) so "Open in CRM"
/// lands on the target page without a login prompt. Requires the key to carry
/// <see cref="CrmConnectorScopes.SessionHandoff"/>; otherwise returns
/// <see cref="CrmHandoffResult.NotGranted"/> so the caller can require reconnection with that scope.
/// </summary>
public interface ICrmSessionHandoffConnector
{
    Task<CrmHandoffResult> CreateHandoffAsync(CrmConnection connection, string? relativeTargetPath, CancellationToken ct = default);
}
