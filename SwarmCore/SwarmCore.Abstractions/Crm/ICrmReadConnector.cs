namespace SwarmCore.Abstractions.Crm;

/// <summary>
/// Shared, app-agnostic contract for reading a Talavine CRM tenant's data over
/// its authenticated, tenant-scoped read API (<c>api/crm/*</c>, <c>data:read</c>
/// scope). This is distinct from the Vibes data API (<c>api/vibes/data/*</c>),
/// which serves external Vibe apps through <c>SwarmCore.Vibes.Sharing.CrmDataClient</c>.
/// Visible to BOTH the desktop email client (which calls it to answer "one brain"
/// questions) and, in principle, any other SwarmCore consumer.
///
/// PRIVACY: this connector takes a fully-resolved <see cref="CrmConnection"/> and does
/// nothing on its own to decide WHICH account may call it. The fail-closed scoping —
/// only the single opted-in work email account can produce a connection — lives in the
/// caller (see Talavine's CrmConnectionResolver). The connector is the dumb pipe;
/// the gate is upstream.
/// </summary>
public interface ICrmReadConnector
{
    /// <summary>Verify the connection works and report the token's identity + scopes.</summary>
    Task<CrmPingResult> PingAsync(CrmConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Credential handshake: exchange the user's CRM email + password for a ROLE-SCOPED,
    /// least-privilege bearer key on the given tenant (<c>POST api/crm/connect</c>). Returns the raw
    /// <c>vk_…</c> key ONCE plus the resolved identity and granted scopes. The caller must store only
    /// the key and immediately discard the password. HTTPS is required; a non-HTTPS base URL fails.
    /// The password is never logged by the connector.
    /// </summary>
    Task<CrmConnectResult> ConnectAsync(
        string baseUrl, string email, string password, CancellationToken ct = default);

    /// <summary>Search contacts by free text (name, email, company). Read-only.</summary>
    Task<CrmReadResult<CrmContact>> SearchContactsAsync(
        CrmConnection connection, string query, int limit = 20, CancellationToken ct = default);

    /// <summary>Search companies/accounts by free text. Read-only.</summary>
    Task<CrmReadResult<CrmCompany>> SearchCompaniesAsync(
        CrmConnection connection, string query, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Search deals/opportunities by free text (deal name, stage, company). Used to
    /// answer "what's the status of the Acme deal?". Read-only.
    /// </summary>
    Task<CrmReadResult<CrmDeal>> SearchDealsAsync(
        CrmConnection connection, string query, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// The caller's due + upcoming CRM agenda (<c>GET api/crm/agenda</c>): overdue and
    /// soon-due scheduled calls, follow-up ticklers, tasks, deal next steps, and open
    /// high-priority tickets, ordered most urgent first. Scoped server-side to what the
    /// token's identity may see. Read-only; feeds "today" dashboards in sibling apps.
    /// </summary>
    /// <param name="horizonDays">How many days ahead to include (overdue items always included).</param>
    Task<CrmReadResult<CrmAgendaItem>> GetTodayAgendaAsync(
        CrmConnection connection, int horizonDays = 1, int limit = 25, CancellationToken ct = default);

    /// <summary>
    /// Credential handshake that also asks for extra scopes the user opted into on the desktop
    /// (for example <see cref="CrmConnectorScopes.SessionHandoff"/>). The CRM intersects the
    /// request with what the user's roles allow; it never grants more than the role permits.
    /// <para>
    /// A CRM account with two-factor sign-in gets no key on its password alone: the result's
    /// <see cref="CrmConnectResult.Status"/> is <see cref="CrmConnectStatus.TwoFactorRequired"/>, and
    /// the caller retries with <paramref name="twoFactorCode"/> (an authenticator or recovery code). A
    /// refused code yields <see cref="CrmConnectStatus.TwoFactorCodeRejected"/>. The code, like the
    /// password, is never logged or stored.
    /// </para>
    /// Default implementation ignores <paramref name="requestedScopes"/> and
    /// <paramref name="twoFactorCode"/> for older connectors.
    /// </summary>
    Task<CrmConnectResult> ConnectAsync(
        string baseUrl, string email, string password, IReadOnlyList<string> requestedScopes,
        string? twoFactorCode = null, CancellationToken ct = default)
        => ConnectAsync(baseUrl, email, password, ct);

    /// <summary>
    /// Exact-address contact lookup (<c>GET api/crm/contacts/by-email?email=</c>). Returns zero
    /// or more contacts (the CRM may hold duplicates); callers treat the first as primary.
    /// Read-only. Default returns a not-supported failure for older connectors.
    /// </summary>
    Task<CrmReadResult<CrmContact>> FindContactByEmailAsync(
        CrmConnection connection, string email, CancellationToken ct = default)
        => Task.FromResult(CrmReadResult<CrmContact>.Fail("Contact lookup by email is not supported by this connector."));

    /// <summary>
    /// The CRM's source-grounded briefing for one record
    /// (<c>GET api/crm/briefing?kind=&amp;id=</c>): summary, highlights, open commitments,
    /// timeline, related records. Read-only. Default returns a not-supported failure.
    /// </summary>
    Task<CrmReadResult<CrmCustomerBriefing>> GetCustomerBriefingAsync(
        CrmConnection connection, string recordKind, string recordId, CancellationToken ct = default)
        => Task.FromResult(CrmReadResult<CrmCustomerBriefing>.Fail("Briefings are not supported by this connector."));

    /// <summary>
    /// Decisions CRM agents raised that are still waiting on a human
    /// (<c>GET api/crm/decisions?limit=</c>), newest first. Read-only. Default returns a
    /// not-supported failure.
    /// </summary>
    Task<CrmReadResult<CrmDecision>> GetPendingDecisionsAsync(
        CrmConnection connection, int limit = 25, CancellationToken ct = default)
        => Task.FromResult(CrmReadResult<CrmDecision>.Fail("Decisions are not supported by this connector."));

    /// <summary>
    /// Read aggregate, privacy-bounded website telemetry and product-registration counts for the
    /// connected tenant. The endpoint never returns visitor rows, installation hashes, or contact
    /// identities. Succeeds with no item when the caller's role has no tenant-wide view (an
    /// owner-scoped rep). Default fails closed for older connectors.
    /// </summary>
    Task<CrmReadResult<CrmTelemetrySummary>> GetTelemetrySummaryAsync(
        CrmConnection connection, int sinceHours = 24, CancellationToken ct = default)
        => Task.FromResult(CrmReadResult<CrmTelemetrySummary>.Fail("Telemetry summaries are not supported by this connector."));

    /// <summary>
    /// Read the connected tenant's business-cycle counts (<c>GET api/crm/business/cycle</c>) for the
    /// Today strip. Counts follow the caller's CRM visibility; money totals are present only for a
    /// full-tenant role. Succeeds with no item when the CRM predates the endpoint (404). Default
    /// fails closed for older connectors.
    /// </summary>
    Task<CrmReadResult<CrmBusinessCycleSummary>> GetBusinessCycleAsync(
        CrmConnection connection, CancellationToken ct = default)
        => Task.FromResult(CrmReadResult<CrmBusinessCycleSummary>.Fail("Business cycle counts are not supported by this connector."));
}
