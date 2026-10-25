using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Business;

/// <summary>
/// One client identity rule across Talavine, CRM and Code: exact normalized email or E.164 phone,
/// scoped to a tenant. Normalization only trims and lowercases; it never strips Gmail dots,
/// folds plus addresses, or guesses a country code.
/// </summary>
public static class ClientIdentity
{
    private static readonly Regex E164 = new(@"^\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant);

    /// <summary>The exact target rule: trim and lowercase invariant. Throws on null, like the dispatch contract it backs.</summary>
    public static string NormalizeAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant();
    }

    /// <summary>Trimmed, lowercased email; null when blank. No dot, plus or provider-specific folding.</summary>
    public static string? NormalizeEmail(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeAddress(value);

    /// <summary>
    /// E.164 (+ and 7-15 digits) when the input already carries an explicit + country code and only
    /// ordinary formatting (spaces, parentheses, hyphens); otherwise null. A national number is never guessed.
    /// </summary>
    public static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text[0] != '+') return null;
        if (text[1..].Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c) && c is not ('(' or ')' or '-'))) return null;
        var normalized = "+" + new string(text[1..].Where(char.IsAsciiDigit).ToArray());
        return E164.IsMatch(normalized) ? normalized : null;
    }

    /// <summary>
    /// Stable key <c>client:v1:{escaped tenant}:{sha256}</c>, or null when neither identifier normalizes. Email wins
    /// when both are present so a contact seen with and without a phone keeps one key. The key never exposes the address.
    /// </summary>
    public static string? Key(string tenantScope, string? email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(tenantScope)) throw new ArgumentException("A tenant scope is required for a client identity.", nameof(tenantScope));
        var tenant = tenantScope.Trim();
        var normalizedEmail = NormalizeEmail(email);
        var (kind, identifier) = normalizedEmail is not null ? ("email", normalizedEmail)
            : NormalizePhone(phone) is { } normalizedPhone ? ("phone", normalizedPhone)
            : (null, null);
        if (kind is null) return null;
        var material = "client-identity-v1\n" + tenant.Length + ":" + tenant + "\n" + kind + "\n" + identifier;
        return "client:v1:" + Uri.EscapeDataString(tenant) + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
