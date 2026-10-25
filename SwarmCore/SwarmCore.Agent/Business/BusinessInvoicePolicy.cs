using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Business;

/// <summary>One owner-stated invoice line. Amounts are what the owner stated; no tax is computed.</summary>
public sealed record BusinessInvoiceLine(string Description, decimal Amount);

/// <summary>
/// The exact customer-facing invoice message bound to one issued invoice. Purpose is "invoice" for the
/// issue send or "reminder" for a payment reminder. Hosts rebuild this from the issued record at
/// transport time and refuse any body that no longer matches.
/// </summary>
public sealed record BusinessInvoiceIssue(Guid InvoiceId, string Number, string ContentHash, string Purpose, string Subject, string PlainText);

/// <summary>
/// A signed provider attestation that money arrived for an invoice. It is evidence for the owner to
/// review; it never marks an invoice paid on its own.
/// </summary>
public sealed record BusinessPaymentReceipt(string EventId, Guid InvoiceId, string InvoiceNumber, decimal Amount,
    string Currency, DateTimeOffset ReceivedAtUtc, string ProviderReceiptId);

/// <summary>
/// Invoice rules shared by hosts: number format, deposit validation, reminder offsets, the content hash
/// that makes an issued invoice immutable, and the list of fields a writer may never change after issue.
/// Accounting (ledger, tax computation) stays outside; amounts and tax lines are what the owner states.
/// </summary>
public static class BusinessInvoicePolicy
{
    public const string ServiceKind = "Service";
    public const string DepositKind = "Deposit";
    public const string IssuePurpose = "invoice";
    public const string ReminderPurpose = "reminder";
    public const string NumberPrefix = "INV-";
    public const int MaximumNumberLength = 40;
    public const int DefaultPaymentTermsDays = 14;
    public const int MaximumPaymentTermsDays = 365;
    public const int MaximumReminders = 10;
    public const int MaximumInstructionsLength = 2000;

    /// <summary>Default reminders: three days before due, on the due day, and one and two weeks after.</summary>
    public static IReadOnlyList<int> DefaultReminderOffsetDays { get; } = [-3, 0, 7, 14];

    /// <summary>Commercial fields fixed at issue. Corrections void the invoice and issue a replacement.</summary>
    public static IReadOnlyList<string> ImmutableFields { get; } =
        ["Kind", "Number", "CaseId", "ProposalId", "ServiceInvoiceDraftId", "SnapshotHash", "Currency", "Amount",
         "LineItemsJson", "IssuedAtUtc", "DueAtUtc", "PaymentInstructions", "SellerName", "ContentHash", "IssuedBy"];

    /// <summary>States an issued invoice moves through. Paid and Void are terminal.</summary>
    public static IReadOnlyList<string> States { get; } = ["Issued", "Sent", "Paid", "Void"];

    public static bool IsKind(string? kind) => kind is ServiceKind or DepositKind;
    public static bool IsOpen(string? state) => state is "Issued" or "Sent";
    public static bool IsTerminal(string? state) => state is "Paid" or "Void";

    /// <summary>Allowed state moves. Paid and Void never change again.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        ("Issued", "Sent") => true,
        ("Issued" or "Sent", "Paid") => true,
        ("Issued" or "Sent", "Void") => true,
        _ => false,
    };

    /// <summary>A tenant's sequential number, for example INV-000042. Sequence values start at one.</summary>
    public static string FormatNumber(long sequence)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence), "Invoice sequences start at one.");
        var number = NumberPrefix + sequence.ToString("D6", CultureInfo.InvariantCulture);
        return number.Length <= MaximumNumberLength ? number : throw new ArgumentOutOfRangeException(nameof(sequence));
    }

    /// <summary>A whole deposit percent between 1 and 100.</summary>
    public static bool ValidDepositPercent(decimal? percent) => percent is >= 1 and <= 100 && decimal.Truncate(percent.Value) == percent.Value;

    /// <summary>The deposit amount, rounded half away from zero to cents.</summary>
    public static decimal DepositAmount(decimal value, decimal percent)
    {
        if (!ValidDepositPercent(percent)) throw new ArgumentException("A deposit is a whole percent from 1 to 100.");
        if (value < 0) throw new ArgumentException("A deposit needs a non-negative accepted value.");
        return Math.Round(value * percent / 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Due date from issue time and payment terms; defaults to <see cref="DefaultPaymentTermsDays"/>.</summary>
    public static DateTime DueAt(DateTime issuedAtUtc, DateTime? requestedDueAtUtc)
    {
        var due = requestedDueAtUtc ?? issuedAtUtc.AddDays(DefaultPaymentTermsDays);
        if (due < issuedAtUtc.Date || due > issuedAtUtc.AddDays(MaximumPaymentTermsDays))
            throw new ArgumentException("Choose a due date from today up to one year after issue.");
        return due;
    }

    /// <summary>Validates reminder offsets: distinct, ascending, at most ten, within thirty days before and ninety after due.</summary>
    public static int[] NormalizeOffsets(IReadOnlyList<int>? offsets)
    {
        var values = (offsets is { Count: > 0 } ? offsets : DefaultReminderOffsetDays).ToArray();
        if (values.Length > MaximumReminders || values.Any(x => x is < -30 or > 90) || values.Distinct().Count() != values.Length)
            throw new ArgumentException("Use up to ten distinct reminder days from 30 before to 90 after the due date.");
        Array.Sort(values);
        return values;
    }

    /// <summary>Reminder offsets whose send time is still ahead; earlier ones are skipped, never sent late in a burst.</summary>
    public static int[] PendingOffsets(IReadOnlyList<int> offsets, DateTime dueAtUtc, DateTime nowUtc)
        => offsets.Where(x => dueAtUtc.AddDays(x) >= nowUtc.AddHours(-12)).ToArray();

    public static DateTime ReminderAt(DateTime dueAtUtc, int offsetDays) => dueAtUtc.AddDays(offsetDays);

    public static string ReminderObjective(Guid scheduleId) => "invoice-reminder:" + scheduleId.ToString("N");
    public static string ReminderSlot(int index) => "reminder-" + index.ToString(CultureInfo.InvariantCulture);
    public static string IssueObjective(Guid invoiceId) => "invoice:" + invoiceId.ToString("N");
    public const string IssueSlot = "issue";

    /// <summary>Bounded owner-stated payment instructions, or null when absent.</summary>
    public static string? NormalizeInstructions(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return null;
        var value = instructions.Trim();
        if (value.Length > MaximumInstructionsLength || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("Payment instructions must be plain text of up to 2,000 characters.");
        return value;
    }

    /// <summary>Validates owner-stated lines and returns their total.</summary>
    public static decimal Total(IReadOnlyList<BusinessInvoiceLine> lines)
    {
        if (lines is not { Count: > 0 and <= 50 } || lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.Description.Length > 1000))
            throw new ArgumentException("An invoice needs one to fifty bounded lines.");
        var total = lines.Sum(x => x.Amount);
        if (total < 0) throw new ArgumentException("An invoice total cannot be negative.");
        return total;
    }

    /// <summary>Hash over every immutable field. Any change after issue fails verification.</summary>
    public static string ContentHash(Guid invoiceId, string kind, string number, Guid caseId, Guid proposalId, string snapshotHash,
        string currency, decimal amount, IReadOnlyList<BusinessInvoiceLine> lines, DateTime issuedAtUtc, DateTime dueAtUtc,
        string? paymentInstructions, string issuedBy, string sellerName)
    {
        if (string.IsNullOrWhiteSpace(sellerName) || sellerName.Length > 200) throw new ArgumentException("An invoice needs the seller's bounded name.");
        if (!IsKind(kind) || string.IsNullOrWhiteSpace(number) || number.Length > MaximumNumberLength || currency is not { Length: 3 }
            || !currency.All(char.IsAsciiLetterUpper) || amount < 0 || amount != Total(lines) || dueAtUtc < issuedAtUtc.Date)
            throw new ArgumentException("Issued invoice content is incomplete or inconsistent.");
        return Hash(new
        {
            Id = invoiceId, Kind = kind, Number = number, Case = caseId, Proposal = proposalId, Snapshot = snapshotHash, Currency = currency,
            Amount = amount.ToString("0.00", CultureInfo.InvariantCulture),
            Lines = lines.Select(x => new { x.Description, Amount = x.Amount.ToString("0.00", CultureInfo.InvariantCulture) }).ToArray(),
            Issued = Instant(issuedAtUtc), Due = Instant(dueAtUtc),
            Instructions = paymentInstructions ?? "", IssuedBy = issuedBy, Seller = sellerName,
        });
    }

    /// <summary>Plain-text customer message. Content comes only from the issued record.</summary>
    public static BusinessInvoiceIssue Message(Guid invoiceId, string purpose, string sellerName, string number, string contentHash,
        string currency, decimal amount, decimal paidAmount, IReadOnlyList<BusinessInvoiceLine> lines, DateTime dueAtUtc, string? paymentInstructions)
    {
        if (purpose is not (IssuePurpose or ReminderPurpose)) throw new ArgumentException("Unknown invoice message purpose.");
        string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;
        var text = new StringBuilder();
        if (purpose == ReminderPurpose) text.Append("Payment reminder for invoice ").Append(number).Append('\n');
        else text.Append("Invoice ").Append(number).Append('\n');
        text.Append("From: ").Append(sellerName).Append('\n');
        foreach (var line in lines) text.Append(line.Description).Append(": ").Append(Money(line.Amount)).Append('\n');
        text.Append("Total: ").Append(Money(amount)).Append('\n');
        if (purpose == ReminderPurpose && paidAmount > 0) text.Append("Received so far: ").Append(Money(paidAmount)).Append('\n');
        if (purpose == ReminderPurpose && paidAmount > 0) text.Append("Balance: ").Append(Money(amount - paidAmount)).Append('\n');
        text.Append("Due: ").Append(dueAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        if (paymentInstructions is { Length: > 0 }) text.Append("How to pay:\n").Append(paymentInstructions).Append('\n');
        var subject = purpose == ReminderPurpose ? "Payment reminder: invoice " + number : "Invoice " + number + " from " + sellerName;
        if (subject.Length > 200) subject = subject[..200];
        return new(invoiceId, number, contentHash, purpose, subject, text.ToString().TrimEnd('\n'));
    }

    /// <summary>The month boundary (UTC instant) of <paramref name="nowUtc"/> in the owner's time zone.</summary>
    public static DateTime MonthStartUtc(DateTime nowUtc, string? timeZoneId)
    {
        TimeZoneInfo zone;
        try { zone = string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var start = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(start, zone);
    }

    // Stores round-trip UTC instants without their DateTimeKind; the hash depends only on the instant's ticks.
    private static string Instant(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
