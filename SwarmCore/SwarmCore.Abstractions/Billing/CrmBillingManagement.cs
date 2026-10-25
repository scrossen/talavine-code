namespace SwarmCore.Abstractions.Billing;

/// <summary>
/// The actions a CRM can ask the billing authority (the website, which owns the Stripe account) to
/// take on one workspace's subscription. The CRM decides who may ask; the billing authority checks
/// the request is signed, names a real signup, and that operator-only actions came from an operator.
/// </summary>
public static class CrmBillingActions
{
    /// <summary>Read the subscription, payment method, next charge, and recent invoices.</summary>
    public const string Overview = "overview";

    /// <summary>A one-time Stripe-hosted session for updating the card or reading invoices.</summary>
    public const string PortalSession = "portal-session";

    /// <summary>Change the number of paid human seats (prorated on the next invoice).</summary>
    public const string SetSeats = "set-seats";

    /// <summary>Stop renewing: service continues until the end of the paid period (or the trial).</summary>
    public const string CancelAtPeriodEnd = "cancel-at-period-end";

    /// <summary>Undo <see cref="CancelAtPeriodEnd"/> before the period ends.</summary>
    public const string Resume = "resume";

    /// <summary>Operator: end the free trial now. Stripe invoices and charges the card immediately.</summary>
    public const string EndTrial = "end-trial";

    /// <summary>Operator: cancel immediately, without proration or a final invoice.</summary>
    public const string CancelNow = "cancel-now";

    /// <summary>
    /// Operator: fully refund the most recent successful payment. The request names the payment the
    /// operator confirmed (<see cref="CrmBillingManagementRequest.ChargeId"/>); if that is no longer
    /// the newest refundable payment, nothing is refunded.
    /// </summary>
    public const string RefundLatestPayment = "refund-latest-payment";

    public static IReadOnlyList<string> All { get; } =
        [Overview, PortalSession, SetSeats, CancelAtPeriodEnd, Resume, EndTrial, CancelNow, RefundLatestPayment];

    public static bool IsKnown(string? action) => action is not null && All.Contains(action, StringComparer.Ordinal);

    /// <summary>Actions that move money or end service at once; workspace administrators cannot request them.</summary>
    public static bool IsOperatorOnly(string? action) =>
        action is EndTrial or CancelNow or RefundLatestPayment;

    /// <summary>Actions that change the subscription. These carry an idempotency key through to Stripe.</summary>
    public static bool IsMutation(string? action) =>
        action is SetSeats or CancelAtPeriodEnd or Resume or EndTrial or CancelNow or RefundLatestPayment;
}

/// <summary>Signed body of <c>POST /api/crm/billing/manage</c>.</summary>
public sealed class CrmBillingManagementRequest
{
    /// <summary>The website signup id (the CRM's provisioning request id), in "N" format.</summary>
    public string RequestId { get; set; } = string.Empty;

    public string TenantSlug { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;

    /// <summary>New seat count for <see cref="CrmBillingActions.SetSeats"/>.</summary>
    public int? Quantity { get; set; }

    /// <summary>
    /// For <see cref="CrmBillingActions.RefundLatestPayment"/>: the payment the operator saw and
    /// confirmed (<see cref="CrmBillingOverview.RefundableChargeId"/>). Binding the refund to it means
    /// a retried or stale confirmation can never refund an older payment as well.
    /// </summary>
    public string? ChargeId { get; set; }

    /// <summary>Where the Stripe-hosted portal returns to; must be on the workspace's own host.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>Who asked, for the billing authority's log. Never used for authorization.</summary>
    public string ActorEmail { get; set; } = string.Empty;

    /// <summary>The CRM vouches the actor is a platform operator, not a workspace administrator.</summary>
    public bool ActorIsOperator { get; set; }

    public DateTime IssuedAtUtc { get; set; }
}

public sealed class CrmBillingManagementResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>The subscription after the action (every action returns it, so screens refresh at once).</summary>
    public CrmBillingOverview? Overview { get; set; }

    /// <summary>For <see cref="CrmBillingActions.PortalSession"/>: the hosted session to redirect to.</summary>
    public string? Url { get; set; }

    /// <summary>One sentence describing what happened, for the confirmation banner and audit log.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Set by the caller (never sent on the wire) when the request may have reached the billing service but no
    /// answer came back — a timeout or an unreachable endpoint after sending. The change may have been applied.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OutcomeUnknown { get; set; }

    public static CrmBillingManagementResponse Fail(string error) => new() { Ok = false, Error = error };

    /// <summary>A failure whose effect on the subscription is not known.</summary>
    public static CrmBillingManagementResponse Unknown(string error) => new() { Ok = false, Error = error, OutcomeUnknown = true };
}

public sealed class CrmBillingOverview
{
    /// <summary>Billed in the Stripe sandbox: nothing is ever charged.</summary>
    public bool IsSandbox { get; set; }

    /// <summary>Normalized state: Trialing, Active, PastDue, Unpaid, Canceled, Incomplete.</summary>
    public string SubscriptionState { get; set; } = string.Empty;

    public int Seats { get; set; }

    /// <summary>"month" or "year".</summary>
    public string BillingInterval { get; set; } = string.Empty;

    public long UnitAmountCents { get; set; }
    public string Currency { get; set; } = "usd";
    public DateTime? TrialEndsAtUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? CanceledAtUtc { get; set; }

    /// <summary>"Visa •••• 4242", "Link", or empty when none is on file.</summary>
    public string PaymentMethodLabel { get; set; } = string.Empty;

    public long? NextInvoiceAmountCents { get; set; }
    public DateTime? NextInvoiceAtUtc { get; set; }

    public List<CrmBillingInvoice> Invoices { get; set; } = [];

    /// <summary>Amount of the newest payment that can still be refunded; null when there is none.</summary>
    public long? RefundableAmountCents { get; set; }

    /// <summary>The payment <see cref="RefundableAmountCents"/> belongs to; operators only.</summary>
    public string? RefundableChargeId { get; set; }

    /// <summary>Stripe dashboard link for operators; never sent to workspace administrators.</summary>
    public string? DashboardUrl { get; set; }
}

public sealed class CrmBillingInvoice
{
    public string Number { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public long TotalCents { get; set; }
    public long AmountPaidCents { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? HostedUrl { get; set; }
    public string? PdfUrl { get; set; }
}

/// <summary>Pure rules shared by both ends, so a request the CRM would refuse is refused the same way at the billing authority.</summary>
public static class CrmBillingManagementRules
{
    /// <summary>Upper bound on self-serve seats; larger teams talk to a person.</summary>
    public const int MaxSeats = 1000;

    public static string? Validate(CrmBillingManagementRequest? request)
    {
        if (request is null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.TenantSlug))
        {
            return "Request id and tenant slug are required.";
        }

        if (!CrmBillingActions.IsKnown(request.Action))
        {
            return "Unknown billing action.";
        }

        if (CrmBillingActions.IsOperatorOnly(request.Action) && !request.ActorIsOperator)
        {
            return "Only a platform operator can do that.";
        }

        if (request.Action == CrmBillingActions.SetSeats
            && (request.Quantity is not int quantity || quantity < 1 || quantity > MaxSeats))
        {
            return $"Seats must be between 1 and {MaxSeats}.";
        }

        if (request.Action == CrmBillingActions.RefundLatestPayment && string.IsNullOrWhiteSpace(request.ChargeId))
        {
            return "Refresh the subscription and confirm which payment to refund.";
        }

        return null;
    }

    /// <summary>
    /// A seat count a workspace can move to: never below the people who can already sign in, so a
    /// reduction cannot strand users the seat gate would then refuse to re-enable.
    /// </summary>
    public static string? ValidateSeatChange(int requestedSeats, int activeHumanUsers)
    {
        if (requestedSeats < 1 || requestedSeats > MaxSeats)
        {
            return $"Seats must be between 1 and {MaxSeats}.";
        }

        if (requestedSeats < activeHumanUsers)
        {
            var users = activeHumanUsers == 1 ? "1 person" : $"{activeHumanUsers} people";
            return $"{users} can sign in today. Disable users before reducing seats below that.";
        }

        return null;
    }

    /// <summary>
    /// A portal return address must be https on the workspace's own host, so a signed request cannot
    /// turn the billing authority into an open redirect. A loopback address (http or https) is
    /// admitted only when the host says it is a development deployment.
    /// </summary>
    public static bool IsAllowedReturnUrl(string? returnUrl, string? tenantHostname, bool allowLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(tenantHostname)
            || !Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var local = allowLoopback && uri.IsLoopback && uri.Scheme is "http" or "https";
        return (uri.Scheme == Uri.UriSchemeHttps || local)
            && (string.Equals(uri.Host, tenantHostname.Trim(), StringComparison.OrdinalIgnoreCase) || local);
    }

    /// <summary>"$20.00" style money for screens and messages.</summary>
    public static string FormatMoney(long cents, string? currency = "usd")
    {
        var amount = (cents / 100m).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(currency)
            ? "$" + amount
            : amount + " " + currency.ToUpperInvariant();
    }
}
