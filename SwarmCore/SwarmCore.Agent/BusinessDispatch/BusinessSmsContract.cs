using System.Text.RegularExpressions;

namespace SwarmCore.Agent.BusinessDispatch;

/// <summary>Exact international identities; formatting cleanup never invents a country or account.</summary>
public static class BusinessSmsContract
{
    public static string InternationalNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An explicit international phone number is required.");
        var normalized = value.Trim();
        if (!normalized.StartsWith('+')) throw new ArgumentException("Enter the explicit international phone number beginning with + and its country code. Existing ambiguous national numbers are not guessed or backfilled.");
        if (normalized[1..].Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c) && c is not ('(' or ')' or '-')))
            throw new ArgumentException("A phone number may contain digits and ordinary formatting only; extensions and inferred identities are unsupported.");
        normalized = "+" + new string(normalized[1..].Where(char.IsAsciiDigit).ToArray());
        if (!Regex.IsMatch(normalized, @"^\+[1-9][0-9]{6,14}$")) throw new ArgumentException("An exact international phone number with 7-15 digits is required.");
        return normalized;
    }
    public static bool IsExactNumber(string value)
    { try { return InternationalNumber(value) == value; } catch (ArgumentException) { return false; } }
    public static bool IsSid(string value, string prefix) => Regex.IsMatch(value, "^" + Regex.Escape(prefix) + "[0-9a-fA-F]{32}$");
    // Twilio's Messages/Media API declares SM or MM message identities. Never accept an
    // MMS receipt for a plain SMS action or infer an identity by removing a prefix.
    public static bool IsMessageSid(string value, bool hasMedia) => IsSid(value, "SM") || hasMedia && IsSid(value, "MM");
}
