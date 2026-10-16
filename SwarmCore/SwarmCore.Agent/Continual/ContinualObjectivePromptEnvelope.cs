using System.Globalization;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Formats and reads the host-owned objective section of a continual prompt.
/// The character count is the authority boundary: objective Markdown may use
/// arbitrary headings, and later failure/output/receipt text cannot extend or
/// truncate the objective by repeating one of the host's headings.
/// </summary>
public static class ContinualObjectivePromptEnvelope
{
    private const string EnvelopePrefix = "Continue the durable objective below.";
    private const string OriginalObjectiveHeading = "## Original objective";
    private const string OriginalUserObjectiveHeading = "## Original user objective";
    private const string LengthMarker = " [characters:";
    private const string LegacyNextHostHeading = "## Why it remains active";
    public const string TaskCorrectionsHeading =
        "## Subsequent user task corrections (ordered; later corrections refine earlier work)";

    public static string FormatOriginalObjectiveSection(string objective)
        => FormatObjectiveSection(OriginalObjectiveHeading, objective);

    public static string FormatOriginalUserObjectiveSection(string objective)
        => FormatObjectiveSection(OriginalUserObjectiveHeading, objective);

    public static bool IsHostEnvelope(string? message)
        => TryGetEnvelopeStart(message, out _);

    /// <summary>
    /// Returns the authenticated objective for a host envelope, the original
    /// message for an ordinary turn, or an empty value for a malformed host
    /// envelope. Malformed envelopes fail closed so incidental wrapper text
    /// can never become retrieval authority.
    /// </summary>
    public static string ExtractObjectiveOrOriginal(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;

        if (!TryGetEnvelopeStart(message, out var envelopeStart))
        {
            return message;
        }

        if (!TryFindFirstObjectiveHeading(message, envelopeStart, out var headingIndex, out var heading))
        {
            return string.Empty;
        }

        if (TryExtractLengthDelimited(message, headingIndex, heading, out var objective)
            || TryExtractLegacy(message, headingIndex, heading, out objective))
        {
            return objective.Trim();
        }

        return string.Empty;
    }

    private static bool TryGetEnvelopeStart(string? message, out int envelopeStart)
    {
        envelopeStart = 0;
        if (string.IsNullOrWhiteSpace(message)) return false;

        while (envelopeStart < message.Length && char.IsWhiteSpace(message[envelopeStart]))
        {
            envelopeStart++;
        }

        return message.AsSpan(envelopeStart).StartsWith(
            EnvelopePrefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatObjectiveSection(string heading, string objective)
    {
        ArgumentNullException.ThrowIfNull(objective);
        return $"{heading}{LengthMarker}{objective.Length}]\n{objective}";
    }

    private static bool TryExtractLengthDelimited(
        string message,
        int headerIndex,
        string heading,
        out string objective)
    {
        objective = string.Empty;
        var headerPrefix = heading + LengthMarker;
        if (!message.AsSpan(headerIndex).StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lengthStart = headerIndex + headerPrefix.Length;
        var closingBracket = message.IndexOf(']', lengthStart);
        if (closingBracket < 0) return false;

        var lengthText = message.AsSpan(lengthStart, closingBracket - lengthStart);
        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length < 0
            || length > message.Length)
        {
            return false;
        }

        var objectiveStart = SkipSingleLineEnding(message, closingBracket + 1);
        if (objectiveStart < 0 || objectiveStart > message.Length - length) return false;

        var objectiveEnd = objectiveStart + length;
        var nextContent = SkipLineEndings(message, objectiveEnd);
        if (!StartsWithHeadingLine(message, TaskCorrectionsHeading, nextContent)
            && !StartsWithHeadingLine(message, LegacyNextHostHeading, nextContent))
        {
            return false;
        }

        objective = message.Substring(objectiveStart, length);
        return true;
    }

    private static bool TryExtractLegacy(
        string message,
        int headingIndex,
        string heading,
        out string objective)
    {
        objective = string.Empty;
        var objectiveStart = SkipSingleLineEnding(message, headingIndex + heading.Length);
        if (objectiveStart < 0) return false;

        // Legacy prompts had no explicit length. Use the first exact host
        // heading, never the last: later output and receipts are untrusted.
        // Newly generated prompts always take the length-delimited path.
        var nextHostSection = FindHeadingLine(message, LegacyNextHostHeading, objectiveStart);
        if (nextHostSection < objectiveStart) return false;

        objective = message[objectiveStart..nextHostSection];
        return !string.IsNullOrWhiteSpace(objective);
    }

    private static bool TryFindFirstObjectiveHeading(
        string message,
        int envelopeStart,
        out int headingIndex,
        out string heading)
    {
        var searchStart = envelopeStart + EnvelopePrefix.Length;
        var userHeadingIndex = FindHeadingLine(message, OriginalUserObjectiveHeading, searchStart);
        var genericHeadingIndex = FindHeadingLine(message, OriginalObjectiveHeading, searchStart);
        headingIndex = EarliestIndex(userHeadingIndex, genericHeadingIndex);
        heading = headingIndex == userHeadingIndex
            ? OriginalUserObjectiveHeading
            : OriginalObjectiveHeading;
        return headingIndex >= 0;
    }

    private static int EarliestIndex(int first, int second)
        => first < 0 ? second : second < 0 ? first : Math.Min(first, second);

    private static int FindHeadingLine(string message, string headingPrefix, int startIndex)
    {
        var searchIndex = Math.Max(0, startIndex);
        while (searchIndex < message.Length)
        {
            var index = message.IndexOf(headingPrefix, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            if ((index == 0 || message[index - 1] == '\n')) return index;
            searchIndex = index + headingPrefix.Length;
        }

        return -1;
    }

    private static bool StartsWithHeadingLine(string message, string heading, int index)
    {
        if (index < 0 || index + heading.Length > message.Length) return false;
        if (!message.AsSpan(index).StartsWith(heading, StringComparison.OrdinalIgnoreCase)) return false;

        var after = index + heading.Length;
        return after == message.Length || message[after] is '\r' or '\n';
    }

    private static int SkipSingleLineEnding(string message, int index)
    {
        if (index >= message.Length) return -1;
        if (message[index] == '\r')
        {
            return index + 1 < message.Length && message[index + 1] == '\n'
                ? index + 2
                : -1;
        }

        return message[index] == '\n' ? index + 1 : -1;
    }

    private static int SkipLineEndings(string message, int index)
    {
        while (index < message.Length && message[index] is '\r' or '\n')
        {
            index++;
        }

        return index;
    }
}
