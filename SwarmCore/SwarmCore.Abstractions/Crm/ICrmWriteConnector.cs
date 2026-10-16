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
