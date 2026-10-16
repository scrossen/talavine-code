namespace SwarmCore.Abstractions.Crm;

// ---------------------------------------------------------------------------------------------
// Read models added for the desktop "frictionless CRM" bridge: per-record briefing (the CRM's
// deterministic CustomerBriefing projected onto a flat wire shape), pending decisions raised by
// CRM agents, and the connection's scope inventory. Like the original connector models these are
// deliberately flat and string-heavy so the wire shape can evolve behind <c>Extra</c>.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A source-grounded briefing for one CRM record (contact, company, deal, or ticket): summary,
/// highlight facts, open commitments (with overdue flags), and a recent timeline. Mirrors the
/// CRM's <c>CustomerBriefing</c>; every list item carries the CRM citation keys it was built from.
/// </summary>
public sealed record CrmCustomerBriefing
{
    public required string RecordKind { get; init; }
    public required string RecordId { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? CompanyName { get; init; }

    /// <summary>Relative CRM route for the subject record (join with <see cref="CrmRecordLinks.Absolute"/>).</summary>
    public string? Link { get; init; }

    public DateTime GeneratedAtUtc { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<CrmBriefingFact> Highlights { get; init; } = [];
    public IReadOnlyList<CrmBriefingCommitment> OpenCommitments { get; init; } = [];
    public IReadOnlyList<CrmBriefingTimelineItem> Timeline { get; init; } = [];

    /// <summary>Related records the caller may want to link to (open deals, open tickets, company).</summary>
    public IReadOnlyList<CrmRelatedRecord> Related { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Extra { get; init; } = new Dictionary<string, string?>();
}

public sealed record CrmBriefingFact
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public IReadOnlyList<string> CitationKeys { get; init; } = [];
}

public sealed record CrmBriefingCommitment
{
    /// <summary>task | tickler | ticket | deal_next_step | scheduled_call</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public DateTime? DueAtUtc { get; init; }
    public bool IsOverdue { get; init; }
    public string? RecordId { get; init; }
    public string? Link { get; init; }
    public IReadOnlyList<string> CitationKeys { get; init; } = [];
}

public sealed record CrmBriefingTimelineItem
{
    /// <summary>email | note | call | deal | ticket | meeting</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public string? RecordId { get; init; }
    public string? Link { get; init; }
    public IReadOnlyList<string> CitationKeys { get; init; } = [];
}

/// <summary>A related CRM record reference (e.g. an open deal on a contact briefing).</summary>
public sealed record CrmRelatedRecord
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Status { get; init; }
    public string? Link { get; init; }
}

/// <summary>
/// A decision a CRM agent raised for a human (approve a proposed reply, confirm a deal-stage move,
/// hire/decline a suggestion). Read from <c>crm.list_decisions</c> / <c>GET api/crm/decisions</c>.
/// </summary>
public sealed record CrmDecision
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? RaisedByAgent { get; init; }
    public DateTime? RaisedAtUtc { get; init; }
    public string? Priority { get; init; }
    public string? Status { get; init; }

    /// <summary>Related record (contact/deal/ticket) when the decision is about one.</summary>
    public string? RecordKind { get; init; }
    public string? RecordId { get; init; }

    /// <summary>Relative CRM route (normally <c>/decisions</c> or a deep link into it).</summary>
    public string? Link { get; init; }

    /// <summary>Resolution choices the CRM accepts for this decision (e.g. approve, reject).</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Extra { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// Privacy-bounded website and Talavine product adoption summary for a connected CRM tenant.
/// It contains counts only: no visitor identity, path history, installation identifier, email,
/// or contact record crosses the desktop bridge.
/// </summary>
public sealed record CrmTelemetrySummary
{
    public int SinceHours { get; init; }
    public int WebsiteEvents { get; init; }
    public int OpenAlerts { get; init; }
    public CrmProductAdoptionSummary? ProductAdoption { get; init; }
}

public sealed record CrmProductAdoptionSummary
{
    public int ActivatedInstalls { get; init; }
    public int ActiveInstalls7Days { get; init; }
    public int ActiveInstalls30Days { get; init; }
    public int RegistrationSubmissions { get; init; }
    public int RegistrationsPendingConfirmation { get; init; }
    public int ContactableRegistrations { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Write requests. Every write is a narrow, typed CRM command (never a generic record mutation)
// so the CRM can apply its own domain invariants, audit rows, and action receipts.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Log an email the user already sent/received from the desktop against the matching CRM contact
/// (matched by the external party's address; the CRM creates the contact when
/// <see cref="CreateContactIfMissing"/> is set). Idempotent on <see cref="MessageId"/>.
/// </summary>
public sealed record CrmEmailLogRequest
{
    /// <summary>RFC 5322 Message-ID (or the desktop's stable message key) — dedupe key on the CRM.</summary>
    public required string MessageId { get; init; }

    public required string Subject { get; init; }
    public string? BodyText { get; init; }
    public required string FromEmail { get; init; }
    public string? FromName { get; init; }
    public IReadOnlyList<string> ToEmails { get; init; } = [];
    public IReadOnlyList<string> CcEmails { get; init; } = [];

    /// <summary>inbound | outbound, from the desktop user's point of view.</summary>
    public required string Direction { get; init; }
    public DateTime OccurredAtUtc { get; init; }

    /// <summary>The desktop user's own address, so the CRM can tell "us" from "them" without a hardcoded domain.</summary>
    public required string OwnerEmail { get; init; }

    /// <summary>
    /// The desktop user's other own addresses (mailbox aliases). The CRM treats them exactly like
    /// <see cref="OwnerEmail"/> when it derives the external party, so mail sent from (or copied to) an
    /// alias is never logged against — or creates — a contact for the user's own address. Older CRMs
    /// ignore the field.
    /// </summary>
    public IReadOnlyList<string> OwnerAliases { get; init; } = [];

    /// <summary>
    /// A <c>talavine://message/{accountId}/{messageId}</c> reference the CRM stores so its UI can
    /// offer "Open in Talavine". Opaque to the CRM.
    /// </summary>
    public string? DesktopReference { get; init; }

    /// <summary>Preferred contact to attach to when the caller already resolved one.</summary>
    public string? ContactId { get; init; }
    public bool CreateContactIfMissing { get; init; }
}

public sealed record CrmTicketCreateRequest
{
    public required string Subject { get; init; }
    public required string Description { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactName { get; init; }
    public string? ContactId { get; init; }
    public string? Priority { get; init; }
    public string? Category { get; init; }

    /// <summary>Message-ID of the originating email, for dedupe and cross-linking.</summary>
    public string? SourceMessageId { get; init; }
    public string? DesktopReference { get; init; }
}

public sealed record CrmNoteRequest
{
    public required string Body { get; init; }
    public string? ContactId { get; init; }
    public string? ContactEmail { get; init; }
    public string? CompanyId { get; init; }
    public string? DealId { get; init; }
    public string? Category { get; init; }
    public string? DesktopReference { get; init; }
}

public sealed record CrmContactCreateRequest
{
    public required string Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
    public string? Title { get; init; }
    public string? CompanyName { get; init; }
    public string? Source { get; init; }
}

/// <summary>
/// A proposal from a local desktop agent for the CRM's decision inbox ("move Acme to Proposal",
/// "log these three emails"). The CRM never executes it directly; a human resolves it in the CRM
/// or from the desktop through <see cref="ICrmWriteConnector.ResolveDecisionAsync"/>.
/// </summary>
public sealed record CrmProposalRequest
{
    public required string Title { get; init; }
    public required string Rationale { get; init; }

    /// <summary>The typed CRM action kind the proposal maps to (as advertised by the CRM), e.g. <c>deal.move_stage</c>.</summary>
    public string? ActionKind { get; init; }

    /// <summary>Action arguments, opaque to the desktop.</summary>
    public IReadOnlyDictionary<string, string?> Arguments { get; init; } = new Dictionary<string, string?>();

    public string? RecordKind { get; init; }
    public string? RecordId { get; init; }
    public string? ProposedByAgent { get; init; }
    public string? DesktopReference { get; init; }
}

/// <summary>Result of any CRM write: what record it produced/touched and where it lives.</summary>
public sealed record CrmWriteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>The request may have committed, but its acknowledgement was lost.
    /// Read the CRM's retained record or receipt before any new write.</summary>
    public bool OutcomeUnknown { get; init; }

    /// <summary>True when the write was refused for lack of scope/role (the UI should offer reconnect, not retry).</summary>
    public bool Forbidden { get; init; }

    /// <summary>True when the CRM found an existing record and did not create a duplicate.</summary>
    public bool AlreadyExisted { get; init; }

    public string? RecordKind { get; init; }
    public string? RecordId { get; init; }

    /// <summary>Canonical citation reference (<c>crm:ticket:17</c>).</summary>
    public string? Reference { get; init; }

    /// <summary>Relative CRM route for the affected record.</summary>
    public string? Link { get; init; }

    /// <summary>The CRM's action receipt id, when it issued one.</summary>
    public string? ReceiptId { get; init; }

    public static CrmWriteResult Fail(string error, bool forbidden = false) =>
        new() { Success = false, Error = error, Forbidden = forbidden };

    public static CrmWriteResult Unknown() => new()
    {
        Success = false,
        OutcomeUnknown = true,
        Error = "The CRM may have saved this, but its confirmation was lost. Check the CRM record before trying again."
    };
}

/// <summary>A one-time URL that opens the CRM already signed in as the connected user.</summary>
public sealed record CrmHandoffResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the connection's key lacks the handoff scope; callers require a scoped reconnect.</summary>
    public bool NotGranted { get; init; }

    /// <summary>Absolute, single-use, short-lived URL. Never log it.</summary>
    public string? Url { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }

    public static CrmHandoffResult Fail(string error, bool notGranted = false) =>
        new() { Success = false, Error = error, NotGranted = notGranted };
}

/// <summary>
/// Scope names the CRM stamps on a desktop-connector key. Kept here so the desktop can ask for
/// them at connect time and explain them in its UI; the CRM remains the authority on which roles
/// may hold which scope.
/// </summary>
public static class CrmConnectorScopes
{
    /// <summary>Read contacts, companies, deals, tickets, agenda, briefings, decisions.</summary>
    public const string DataRead = "data:read";

    /// <summary>The curated MCP write surface (ticket lifecycle, campaign drafting) for write-granting roles.</summary>
    public const string DataWrite = "data:write";

    /// <summary>Log emails, add notes, create contacts, and raise proposals — the "activity" writes an individual rep needs.</summary>
    public const string ActivityWrite = "activity:write";

    /// <summary>Create (not manage) support tickets.</summary>
    public const string TicketCreate = "ticket:create";

    /// <summary>Mint a one-time signed-in handoff URL so "Open in CRM" lands on the page without a login prompt.</summary>
    public const string SessionHandoff = "session:handoff";
    /// <summary>Explicitly opted-in governed business commands. Independent of CRM data scopes.</summary>
    public const string BusinessDispatch = "business:dispatch";
}
