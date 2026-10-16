namespace SwarmCore.Services.Commerce;

/// <summary>
/// Provider-neutral, read-only view of a business's own payment account (customers, invoices,
/// payments, subscriptions). Hosts own credential storage, persistence, contact matching and UI;
/// a reader only performs GET requests with the key it is handed and never keeps it.
/// </summary>
public interface IPaymentProviderReader
{
    /// <summary>Stable provider id stored with mirrored rows, e.g. "stripe".</summary>
    string Provider { get; }

    /// <summary>Rejects a key before any network call when it cannot be a read-only credential.</summary>
    PaymentKeyCheck CheckKey(string? apiKey);

    /// <summary>Proves the key can read customers and returns what the account discloses about itself.</summary>
    Task<PaymentAccountInfo> VerifyAsync(string apiKey, CancellationToken ct);

    /// <summary>Reads one bounded snapshot. A truncated list is never treated as complete by the host.</summary>
    Task<PaymentProviderSnapshot> ReadSnapshotAsync(string apiKey, PaymentSnapshotLimits limits, CancellationToken ct);
}

public sealed record PaymentKeyCheck(bool Accepted, bool IsLiveMode, string? Problem, string? KeyHint);

public sealed record PaymentAccountInfo(string? AccountId, string? DisplayName, bool IsLiveMode);

public sealed record PaymentSnapshotLimits(int MaxPerKind = 2000)
{
    public static PaymentSnapshotLimits Default { get; } = new();
}

/// <summary>One kind of provider record and whether the provider had more than the limit allowed.</summary>
public sealed record PaymentRecordList<T>(IReadOnlyList<T> Items, bool Truncated);

public sealed record PaymentProviderSnapshot(
    PaymentRecordList<PaymentCustomerRecord> Customers,
    PaymentRecordList<PaymentInvoiceRecord> Invoices,
    PaymentRecordList<PaymentChargeRecord> Payments,
    PaymentRecordList<PaymentSubscriptionRecord> Subscriptions,
    DateTime ReadAtUtc);

public sealed record PaymentCustomerRecord(
    string ExternalId, string? Email, string? Name, string? Currency, bool Delinquent, DateTime CreatedAtUtc);

public sealed record PaymentInvoiceRecord(
    string ExternalId, string? CustomerExternalId, string? Number, string? Status, string Currency,
    long TotalMinor, long AmountDueMinor, long AmountPaidMinor, long AmountRemainingMinor,
    DateTime CreatedAtUtc, DateTime? DueAtUtc, DateTime? PaidAtUtc);

public sealed record PaymentChargeRecord(
    string ExternalId, string? CustomerExternalId, string? InvoiceExternalId, string Status, string Currency,
    long AmountMinor, long AmountRefundedMinor, bool Paid, bool Refunded, string? Description, DateTime CreatedAtUtc);

public sealed record PaymentSubscriptionRecord(
    string ExternalId, string? CustomerExternalId, string Status, string? Currency, long? AmountMinor,
    string? Interval, int? IntervalCount, string? PlanName, bool CancelAtPeriodEnd,
    DateTime CreatedAtUtc, DateTime? CurrentPeriodEndUtc, DateTime? CanceledAtUtc);

/// <summary>A provider refusal or failure whose message is safe to show an owner (never contains the key).</summary>
public sealed class PaymentProviderException(string message, PaymentProviderFailure failure) : Exception(message)
{
    public PaymentProviderFailure Failure { get; } = failure;
}

public enum PaymentProviderFailure
{
    InvalidKey,
    MissingPermission,
    RateLimited,
    Unavailable,
    UnexpectedResponse,
}

/// <summary>Formats provider minor units for display. Zero-decimal currencies follow Stripe's published list.</summary>
public static class PaymentAmounts
{
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
    };

    public static decimal ToMajor(long minor, string? currency)
        => currency is not null && ZeroDecimal.Contains(currency) ? minor : minor / 100m;

    public static string Format(long minor, string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? "" : currency.Trim().ToUpperInvariant();
        var major = ToMajor(minor, currency);
        var digits = currency is not null && ZeroDecimal.Contains(currency) ? "N0" : "N2";
        return code.Length == 0
            ? major.ToString(digits, System.Globalization.CultureInfo.InvariantCulture)
            : $"{major.ToString(digits, System.Globalization.CultureInfo.InvariantCulture)} {code}";
    }
}
