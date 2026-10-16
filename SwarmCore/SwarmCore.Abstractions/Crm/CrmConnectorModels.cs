namespace SwarmCore.Abstractions.Crm;

/// <summary>
/// Everything a CRM read call needs to reach exactly one tenant. Carries the
/// resolved base URL and the raw bearer token (already retrieved from the secret
/// vault by the caller — the connector itself never touches secret storage). This
/// is the single value an opted-in work email account contributes to the "one
/// brain" flow; an account that is not CRM-connected can never produce one, which
/// is how the privacy invariant is enforced upstream.
/// </summary>
public sealed record CrmConnection
{
    /// <summary>Tenant base URL, e.g. <c>https://crm.example.com</c> (no trailing /api).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Tenant-scoped <c>data:read</c> VibesShare bearer token.</summary>
    public required string Token { get; init; }

    /// <summary>Optional CRM login identity this connection is tied to (for citations/UI).</summary>
    public string? Identity { get; init; }
}

/// <summary>A contact returned from the CRM read API.</summary>
public sealed record CrmContact
{
    public string? Id { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? CompanyName { get; init; }
    public string? Title { get; init; }

    /// <summary>Any additional whitelisted fields the CRM returned, verbatim.</summary>
    public IReadOnlyDictionary<string, string?> Extra { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>A company/account returned from the CRM read API.</summary>
public sealed record CrmCompany
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Domain { get; init; }
    public string? Industry { get; init; }

    public IReadOnlyDictionary<string, string?> Extra { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>A deal/opportunity returned from the CRM read API.</summary>
public sealed record CrmDeal
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Stage { get; init; }
    public string? Status { get; init; }
    public string? Amount { get; init; }
    public string? CompanyName { get; init; }
    public string? OwnerName { get; init; }
    public string? CloseDate { get; init; }

    public IReadOnlyDictionary<string, string?> Extra { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>
/// A due or upcoming CRM work item for "today"-style surfaces (scheduled calls, follow-up
/// ticklers, tasks, deal next steps, open tickets). Deliberately flat and string-heavy like the
/// other connector models so the wire shape can evolve behind <see cref="Extra"/>.
/// </summary>
public sealed record CrmAgendaItem
{
    public string? Id { get; init; }

    /// <summary>
    /// Item kind: <c>scheduled_call</c> | <c>tickler</c> | <c>task</c> | <c>deal_next_step</c> | <c>ticket</c>.
    /// </summary>
    public string? Kind { get; init; }

    public string? Title { get; init; }

    /// <summary>When the item is due, UTC. Null for undated items (e.g. open tickets).</summary>
    public DateTime? DueUtc { get; init; }

    /// <summary>True when the item was due before the request time and is not completed.</summary>
    public bool Overdue { get; init; }

    public string? ContactName { get; init; }
    public string? CompanyName { get; init; }
    public string? Owner { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }

    /// <summary>Relative path into the CRM web UI for this item (joined onto the tenant base URL).</summary>
    public string? Link { get; init; }

    public IReadOnlyDictionary<string, string?> Extra { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>Generic result envelope for a CRM read call.</summary>
public sealed record CrmReadResult<T>
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<T> Items { get; init; } = [];

    public static CrmReadResult<T> Fail(string error) => new() { Success = false, Error = error };
    public static CrmReadResult<T> Ok(IReadOnlyList<T> items) => new() { Success = true, Items = items };
}

/// <summary>Liveness/identity probe result for a CRM connection.</summary>
public sealed record CrmPingResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? User { get; init; }
    /// <summary>Authoritative host-resolved tenant; null for older servers or non-tenant scope.</summary>
    public int? TenantId { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
}

/// <summary>Outcome of the credential handshake (<c>POST api/crm/connect</c>).</summary>
public enum CrmConnectStatus
{
    /// <summary>No key: wrong credentials, a refused or unreachable CRM, or a malformed reply.</summary>
    Failed = 0,

    /// <summary>The CRM issued a key.</summary>
    Connected,

    /// <summary>
    /// The password was accepted, but the account uses two-factor sign-in and the request carried no
    /// code. No key was issued; retry the same handshake with an authenticator or recovery code.
    /// </summary>
    TwoFactorRequired,

    /// <summary>
    /// The password was accepted, but the supplied two-factor code was refused (wrong, expired, or an
    /// already-used recovery code). No key was issued; retry with a new code. Failures count toward
    /// the CRM account's lockout.
    /// </summary>
    TwoFactorCodeRejected,
}

/// <summary>
/// Result of the credential handshake (<c>POST api/crm/connect</c>). On success carries the raw,
/// one-time <c>vk_…</c> key the caller must store, the resolved CRM identity, and the role-granted
/// scopes stamped on the key. Never carries the password or a two-factor code. On failure carries
/// only a generic message; <see cref="Status"/> says whether the CRM is waiting for a second factor.
/// </summary>
public sealed record CrmConnectResult
{
    public CrmConnectStatus Status { get; init; }

    public bool Success => Status == CrmConnectStatus.Connected;

    /// <summary>True when the password was accepted and the CRM now needs an authenticator or recovery code.</summary>
    public bool RequiresTwoFactorCode => Status is CrmConnectStatus.TwoFactorRequired or CrmConnectStatus.TwoFactorCodeRejected;

    public string? Error { get; init; }

    /// <summary>The raw bearer key — returned once, must be stored by the caller and never logged.</summary>
    public string? Key { get; init; }

    /// <summary>The resolved CRM login identity the key is bound to (for citations/UI).</summary>
    public string? Identity { get; init; }

    /// <summary>The least-privilege scopes the key was stamped with (may be empty for a no-read-role user).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    public static CrmConnectResult Fail(string error) => new() { Status = CrmConnectStatus.Failed, Error = error };

    /// <summary>The CRM accepted the password and is waiting for (or refused) a second factor.</summary>
    public static CrmConnectResult NeedsTwoFactorCode(bool codeRejected, string error) => new()
    {
        Status = codeRejected ? CrmConnectStatus.TwoFactorCodeRejected : CrmConnectStatus.TwoFactorRequired,
        Error = error,
    };
}
