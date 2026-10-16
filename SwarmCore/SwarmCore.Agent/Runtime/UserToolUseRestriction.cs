using System.Text.RegularExpressions;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// A user's explicit global tool prohibition narrows an existing turn; it never
/// grants authority. Only authenticated original request text is an input.
/// Quoted/pasted evidence and partial restrictions are not global prohibitions.
/// </summary>
public static class UserToolUseRestriction
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    public const string Guidance = "Answer without tools or automatic retrieval of personal-data context, using the supplied conversation content and standing instructions. "
        + "The authenticated request withholds tool use. If the requested answer cannot be established from that content, say so honestly.";

    public static bool WithholdsAllTools(string? message, bool authenticated = true)
    {
        if (!authenticated || string.IsNullOrWhiteSpace(message)) return false;
        // The host envelope's original objective is authoritative, never its
        // status, prior output or receipt sections. A malformed envelope does
        // not establish this restriction; existing host scope checks still
        // reject communication retrieval without a proven original objective.
        var original = ContinualObjectivePromptEnvelope.ExtractObjectiveOrOriginal(message);
        if (ContinualObjectivePromptEnvelope.IsHostEnvelope(message) && string.IsNullOrWhiteSpace(original)) return false;
        return WithholdsAuthenticatedOriginal(original);
    }

    /// <summary>Evaluates exact authored text; authored headings cannot manufacture host-wrapper provenance.</summary>
    public static bool WithholdsAuthenticatedOriginal(string? original, bool authenticated = true)
    {
        if (!authenticated || string.IsNullOrWhiteSpace(original)) return false;
        var authored = AuthoredClauses(original);
        var matches = Regex.Matches(authored,
            @"(?:^|[.!?;:\n]\s*)(?:please\s+)?(?:do\s+not|don't|don’t|never)\s+(?:use|call|invoke|run)\s+(?:any\s+)?tools(?:\s+at\s+all)?(?=\s*(?:$|[.!?;,\n]))"
            + @"|(?:^|[.!?;:\n]\s*)(?:please\s+)?(?:answer|reply|respond)\s+without\s+(?:using\s+)?(?:any\s+)?tools(?=\s*(?:$|[.!?;,\n]))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        foreach (Match match in matches)
        {
            var tail = authored[(match.Index + match.Length)..];
            // A comma followed by an exception limits this particular clause;
            // it is not a global prohibition. An additional independently
            // stated full ban can still narrow the request.
            if (!Regex.IsMatch(tail, @"^\s*(?:,\s*)?(?:except|unless|but|when|while|only|for|to)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout)) return true;
        }
        return false;
    }

    public static string OriginalUserMessage(AgentTurnRequest request)
    {
        if (request.MissionObjectiveId is not null)
            return request.MissionOriginalUserIntent ?? throw new InvalidOperationException("A mission turn requires its original authenticated intent.");
        if (request.AuthenticatedOriginalUserMessage is not null) return request.AuthenticatedOriginalUserMessage;
        if (request.IsContinuation)
        {
            var original = ContinualObjectivePromptEnvelope.ExtractObjectiveOrOriginal(request.UserMessage);
            // Keep malformed envelopes visible to the existing authority guard.
            if (!string.IsNullOrWhiteSpace(original)) return original;
        }
        return request.UserMessage;
    }

    public static bool WithholdsAllTools(AgentTurnRequest request)
        => request.MissionObjectiveId is not null || request.AuthenticatedOriginalUserMessage is not null
            ? WithholdsAuthenticatedOriginal(OriginalUserMessage(request))
            : WithholdsAllTools(OriginalUserMessage(request), request.CommunicationScopeUserInitiated);

    private static string AuthoredClauses(string original)
    {
        // Replace evidence with a non-boundary token, not an empty string:
        // removing a quote must not join two fragments into a new command.
        var text = Regex.Replace(original, @"```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)", " quoted ", RegexOptions.CultureInvariant, MatchTimeout);
        text = Regex.Replace(text, @"<(?:untrusted|document|tool-result|context-pack)\b[^>]*>[\s\S]*?(?:</(?:untrusted|document|tool-result|context-pack)>|$)", " quoted ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        text = Regex.Replace(text, @"(?m)^\s*>[^\n]*(?:\n|$)|(?m)^(?: {4}|\t)[^\n]*(?:\n|$)", " quoted\n", RegexOptions.CultureInvariant, MatchTimeout);
        text = Regex.Replace(text, "\"[^\"]*(?:\"|$)|“[^”]*(?:”|$)|`[^`]*(?:`|$)|(?<![\\p{L}\\p{N}])'[^'\\n]*'(?![\\p{L}\\p{N}])", " quoted ", RegexOptions.CultureInvariant, MatchTimeout);
        // Ordinary instruction lists remain authored text. This runs only
        // after quoted/indented evidence has been removed.
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}(?:(?:[-*+] |\d+[.)] )(?=\S))?", "", RegexOptions.CultureInvariant, MatchTimeout);
        // Paired emphasis preserves authored contents, including data-heading
        // boundaries. Unpaired markers and intra-word underscores are not
        // formatting; quoted/fenced/indented evidence was excluded above.
        text = Regex.Replace(text,
            @"(?<![\p{L}\p{N}_*])(?<marker>\*\*|__|\*|_)(?=\S)(?<content>[^\r\n]*?\S)\k<marker>(?![\p{L}\p{N}_*])",
            "${content}", RegexOptions.CultureInvariant, MatchTimeout);
        // Unfenced content commonly follows an explicit data heading. It is
        // evidence, not an additional user instruction. No model parsing.
        var dataStart = Regex.Match(text, @"(?im)^\s*(?:(?:here (?:is|are)(?: the)?|the)\s+)?(?:pasted (?:text|message|content)|text to (?:review|summarize|translate)|(?:tool|model) (?:output|reply)|quoted (?:text|message)|(?:review|summarize|translate|explain) (?:this|the following)(?: (?:text|message|document|content))?)\s*:", RegexOptions.CultureInvariant, MatchTimeout);
        return dataStart.Success ? text[..dataStart.Index] : text;
    }
}
