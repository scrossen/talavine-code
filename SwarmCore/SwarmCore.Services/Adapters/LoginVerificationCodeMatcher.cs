using System.Text.RegularExpressions;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Local-only, conservative extraction of a one-time login code from an email or a text
/// message. Sender identity is the authoritative signal and is enforced by the caller (its
/// mailbox query, or <see cref="SmsLoginChallengeStore.MatchCode"/>'s site check); this
/// matcher only extracts a code when the text reads like a sign-in challenge and never when
/// it reads like a payment, credential-change, enrollment, or marketing message. Neither the
/// text nor the returned code belongs in model context or logs.
/// </summary>
public static class LoginVerificationCodeMatcher
{
    private static readonly Regex CodePhrase = new(
        @"\b(sign[ -]?(in|on)|log[ -]?(in|on)|verification|one[ -]?time|two[ -]?factor|2fa|access|security)\b[^.\n]{0,40}?\b(code|passcode|pin|token)\b"
        + @"|\b(code|passcode|pin|token)\b[^.\n]{0,24}?\b(sign|log|verification|one[ -]?time|login)\b",
        RegexOptions.IgnoreCase);

    // Text messages are terse ("Use 123456 to sign in to Example"), so the sign-in verb or a
    // one-time password is enough there; an email that merely mentions signing in is not.
    private static readonly Regex ShortMessagePhrase = new(
        @"\b(sign[ -]?(in|on)|log[ -]?(in|on)|one[ -.]?time (code|password))\b",
        RegexOptions.IgnoreCase);

    // Both channels refuse the same non-login purposes.
    private static readonly Regex SuspiciousContext = new(
        @"\b(payment|purchase|transfer|wire|transaction|reset|recover|change|activate|enroll)\w*\b",
        RegexOptions.IgnoreCase);

    // Marketing email offers codes next to an unsubscribe link; carriers append opt-out
    // wording ("Reply STOP to unsubscribe") to ordinary sign-in texts, so this is email-only.
    private static readonly Regex MarketingEmail = new(@"\bunsubscribe\w*\b", RegexOptions.IgnoreCase);

    private static readonly Regex CodeCandidate = new(@"(?<![\w])\d{4,8}(?![\w])");

    /// <summary>Returns the single 4–8 digit login code in an email, or null when absent or ambiguous.</summary>
    public static string? MatchLoginCode(string? subject, string? textBody)
    {
        var text = Join(subject, textBody);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!CodePhrase.IsMatch(text) || MarketingEmail.IsMatch(text)) return null;
        return SingleCode(text);
    }

    /// <summary>Returns the single 4–8 digit login code in a text message, or null when absent or ambiguous.</summary>
    public static string? MatchShortMessageCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!CodePhrase.IsMatch(text) && !ShortMessagePhrase.IsMatch(text)) return null;
        return SingleCode(text);
    }

    private static string? SingleCode(string text)
    {
        if (SuspiciousContext.IsMatch(text)) return null;
        var codes = CodeCandidate.Matches(text)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return codes.Length == 1 ? codes[0] : null;
    }

    private static string Join(params string?[] parts)
    {
        var joined = new System.Text.StringBuilder();
        foreach (var part in parts)
            if (!string.IsNullOrWhiteSpace(part))
            {
                if (joined.Length > 0) joined.Append(' ');
                joined.Append(part.Trim());
            }
        return joined.ToString();
    }
}
