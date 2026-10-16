using System.Text.RegularExpressions;

namespace SwarmCore.Knowledge;

/// <summary>Identifies focused record-location work after the host has authorized retrieval.</summary>
public static class SourceRecordLookupPolicy
{
    /// <summary>
    /// A request to locate and report a record is informational work even
    /// when phrased as an imperative. A second requested action still needs
    /// ordinary objective auditing after the lookup.
    /// </summary>
    public static bool IsPureReadOnlyMessageLookup(string? text)
        => IsFocusedMessageLookup(text)
           && !Regex.IsMatch(text!,
               @"\b(?:delete|remove|send|forward|move|archive|edit|update|change|download|save|export|share|publish|create|write)\b",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsFocusedMessageLookup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // This chooses a retrieval strategy, never permission to read a store.
        if (Regex.IsMatch(text,
                @"\b(?:summarize|compare|analyze)\b|\b(?:email|message|receipt|invoice)\s+(?:template|sample|example)\b|\b(?:sample|example)\s+(?:email|message|receipt|invoice)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        return Regex.IsMatch(text.Trim(),
            @"^(?:(?:please|can you|could you|would you)\s+)*(?:find|locate|look up|search for)\s+(?:me\s+)?(?:(?:the|my|our|an?|latest|last)\s+)*(?:email|message|receipt|invoice|order confirmation)\b\s+(?:from|for|about|with|in|on|before|after|sent|dated|regarding|mentioning)\b.+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
