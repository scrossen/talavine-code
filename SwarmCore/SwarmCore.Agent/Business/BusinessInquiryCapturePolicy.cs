using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Agent.Business;

/// <summary>
/// H2 inquiry capture rules shared by hosts: the stable intake key for a captured message and the
/// evidence kind of submitted inquiry text. Text a sender wrote (a website form, an email the owner
/// sent to the CRM) is attributed reference data. It is never customer confirmation and never satisfies
/// an owner's qualification criterion, even when its literal text repeats one.
/// </summary>
public static class BusinessInquiryCapturePolicy
{
    /// <summary>Source identity prefix of evidence holding a sender's submitted inquiry text.</summary>
    public const string SubmittedTextSourcePrefix = "inquiry:";

    /// <summary>Prefix of the desktop message reference a capture is keyed on.</summary>
    public const string MessageReferencePrefix = "talavine://message/";

    /// <summary>Prefix of every intake key minted by <see cref="IntakeKey"/>.</summary>
    public const string IntakeKeyPrefix = "inquiry-capture:v1:";

    /// <summary>Maximum stored length of an external reference.</summary>
    public const int MaximumReferenceLength = 2000;

    /// <summary>True for evidence that carries a sender's submitted inquiry text.</summary>
    public static bool IsSubmittedText(BusinessEvidenceFact fact)
        => fact.SourceIdentity.StartsWith(SubmittedTextSourcePrefix, StringComparison.Ordinal);

    /// <summary>Source identity of the submitted text of one inquiry row.</summary>
    public static string SubmittedTextSourceIdentity(Guid inquiryId) => SubmittedTextSourcePrefix + inquiryId;

    /// <summary>
    /// Canonical external reference: a <c>talavine://message/{account}/{message}</c> link is rewritten with
    /// lowercase D-format GUIDs; any other bounded reference is trimmed. Null when blank, oversized or
    /// containing control characters.
    /// </summary>
    public static string? NormalizeReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > MaximumReferenceLength || text.Any(char.IsControl)) return null;
        if (text.StartsWith(MessageReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = text[MessageReferencePrefix.Length..].Split('/');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var account) || !Guid.TryParse(parts[1], out var message)
                || account == Guid.Empty || message == Guid.Empty) return null;
            return $"{MessageReferencePrefix}{account:D}/{message:D}";
        }
        return text;
    }

    /// <summary>
    /// Stable tenant-scoped intake key. With a message reference the key is the SHA-256 of that reference, so the
    /// same message captured twice is one inquiry and one case. Without one it hashes the normalized sender identity,
    /// subject, and the received time (or, when no time is known, the message text). Null when neither a reference
    /// nor a sender identity is available.
    /// </summary>
    public static string? IntakeKey(string tenantScope, string? externalReference, string? senderEmail, string? senderPhone,
        string? subject, DateTimeOffset? receivedAtUtc, string? messageText)
    {
        if (string.IsNullOrWhiteSpace(tenantScope)) throw new ArgumentException("A tenant scope is required for an intake key.", nameof(tenantScope));
        var tenant = tenantScope.Trim();
        string material;
        if (NormalizeReference(externalReference) is { } reference)
        {
            material = "reference\n" + reference;
        }
        else
        {
            var identity = ClientIdentity.NormalizeEmail(senderEmail) is { } email ? "email:" + email
                : ClientIdentity.NormalizePhone(senderPhone) is { } phone ? "phone:" + phone : null;
            if (identity is null) return null;
            var when = receivedAtUtc is { } at
                ? "at:" + at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                : "text:" + Hex(messageText?.Trim() ?? "");
            material = "sender\n" + identity + "\n" + (subject?.Trim() ?? "") + "\n" + when;
        }
        return IntakeKeyPrefix + Hex("inquiry-capture-v1\n" + tenant.Length + ":" + tenant + "\n" + material);
    }

    private static string Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
