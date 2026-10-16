using System.Text;

namespace SwarmCore.Mcp.Core;

/// <summary>
/// Hygiene for server-authored text that ends up in prompts: tool descriptions,
/// parameter descriptions, and tool results. Strips C0 control characters (other
/// than tab / newline / carriage return) and enforces size caps with an explicit
/// truncation marker so downstream consumers can tell the text was cut.
/// </summary>
public static class McpTextSanitizer
{
    /// <summary>Maximum length of a tool or parameter description (~2 KB).</summary>
    public const int MaxDescriptionLength = 2048;

    /// <summary>Maximum length of a tool result (~128 KB).</summary>
    public const int MaxResultLength = 128 * 1024;

    /// <summary>
    /// Sanitises a description: strips control characters and caps the length.
    /// Returns null when the input is null.
    /// </summary>
    public static string? SanitizeDescription(string? text, int maxLength = MaxDescriptionLength)
    {
        if (text is null) return null;
        var cleaned = StripControlCharacters(text);
        if (cleaned.Length <= maxLength) return cleaned;
        return string.Concat(cleaned.AsSpan(0, maxLength), "…");
    }

    /// <summary>
    /// Sanitises a tool result: strips control characters and caps the length,
    /// appending a marker that states how many characters were dropped.
    /// </summary>
    public static string SanitizeResult(string? text, int maxLength = MaxResultLength)
    {
        var cleaned = StripControlCharacters(text ?? string.Empty);
        return Truncate(cleaned, maxLength);
    }

    /// <summary>
    /// Truncates to <paramref name="maxLength"/> characters and appends
    /// <c>[truncated: N characters omitted]</c> when anything was dropped.
    /// </summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var omitted = text.Length - maxLength;
        return $"{text.AsSpan(0, maxLength)}\n[truncated: {omitted} characters omitted]";
    }

    /// <summary>
    /// Removes C0 control characters (U+0000–U+001F) and DEL (U+007F) except
    /// tab, line feed, and carriage return.
    /// </summary>
    public static string StripControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var needsWork = false;
        foreach (var ch in text)
        {
            if (IsDisallowedControl(ch)) { needsWork = true; break; }
        }
        if (!needsWork) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!IsDisallowedControl(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool IsDisallowedControl(char ch)
        => (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') || ch == 0x7F;
}
