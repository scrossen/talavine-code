using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SwarmCore.Knowledge;

/// <summary>Stable evidence-content identity, intentionally excluding read/flag/sync bookkeeping.</summary>
public static class KnowledgeContentFingerprint
{
    /// <summary>
    /// Selects a bounded verbatim source span. Provider previews may be
    /// synthesized summaries, so they are evidence only when the body contains
    /// them. Missing bodies never turn an unverifiable preview into proof.
    /// </summary>
    public static string? MessageEvidenceExcerpt(string? text, string? html, string? preview = null)
    {
        var candidate = !string.IsNullOrWhiteSpace(preview) && ContainsEvidence(text, html, preview)
            ? preview
            : !string.IsNullOrWhiteSpace(text) ? text : HtmlText(html);
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        candidate = candidate.Trim();
        return candidate.Length <= KnowledgeContractLimits.SourceExcerptLength
            ? candidate : candidate[..KnowledgeContractLimits.SourceExcerptLength];
    }

    public static bool ContainsEvidence(string? text, string? html, string expected)
    {
        static string Normalize(string? value) => System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", " ").Trim();
        var needle = Normalize(expected);
        if (needle.Length == 0) return false;
        var htmlText = HtmlText(html);
        return Normalize(text).Contains(needle, StringComparison.OrdinalIgnoreCase)
            || Normalize(htmlText).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string HtmlText(string? html)
        => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html ?? "", "<[^>]+>", " "));

    public static string Compute(string content) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static string ForCalendar(string? title, string? description, string? location,
        DateTime start, DateTime end, bool allDay, string? recurrence, int status,
        string? organizer, string? attendees)
        => Compute(JsonSerializer.Serialize(new object?[] { title, description, location,
            start.Ticks, end.Ticks, allDay, recurrence, status, organizer, attendees }));

    public static string ForMessage(string? subject, string? sender, string? preview, string? summary,
        string? textBody, string? htmlBody, bool isFromMe, string? senderDisplayName = null, DateTime? receivedAt = null)
        => Compute(JsonSerializer.Serialize(new object?[] { subject, sender, preview, summary, textBody, htmlBody, isFromMe,
            senderDisplayName, receivedAt is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null }));
}
