using System.Text;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Coding.Prompting;

/// <summary>
/// Renders a conversation's durable transcript as Markdown (a host's <c>/export</c>): user and assistant text, tool
/// calls with their inputs and bounded results, and reasoning when the host asks for it. Secrets never reach the
/// durable transcript (they are stored as <c>{{secret:name}}</c> references), so the export carries none.
/// </summary>
public static class TranscriptExporter
{
    public const int MaxToolResultCharacters = 4_000;
    public const int MaxToolInputCharacters = 2_000;
    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToMarkdown(IReadOnlyList<ChatMessage> messages, string? title = null, bool includeReasoning = false, DateTime? exportedUtc = null)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(title) ? "Talavine Code session" : title.Trim());
        sb.Append("Exported ").Append((exportedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm 'UTC'")).Append(" · ")
          .Append(messages.Count).AppendLine(messages.Count == 1 ? " message" : " messages").AppendLine();
        foreach (var message in messages)
        {
            var heading = message.Role switch
            {
                ChatRole.User => "## You",
                ChatRole.Assistant => "## Assistant",
                ChatRole.System => "## System",
                _ => "## " + message.Role,
            };
            var wroteHeading = false;
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart text when !string.IsNullOrWhiteSpace(text.Text):
                        Heading();
                        sb.AppendLine(text.Text.Trim()).AppendLine();
                        break;
                    case ReasoningPart reasoning when includeReasoning && !string.IsNullOrWhiteSpace(reasoning.Text):
                        Heading();
                        sb.AppendLine("<details><summary>Reasoning</summary>").AppendLine().AppendLine(reasoning.Text.Trim()).AppendLine().AppendLine("</details>").AppendLine();
                        break;
                    case ToolUsePart use:
                        Heading();
                        var input = use.Input.ToJsonString(PrettyJson);
                        sb.Append("**Tool call** `").Append(use.Name).AppendLine("`").AppendLine();
                        sb.AppendLine(Fenced(Clip(input, MaxToolInputCharacters), "json")).AppendLine();
                        break;
                    case ToolResultPart result:
                        Heading();
                        sb.Append(result.IsError ? "**Tool error**" : "**Tool result**").AppendLine().AppendLine();
                        sb.AppendLine(Fenced(Clip(result.Content, MaxToolResultCharacters), "text")).AppendLine();
                        break;
                }
            }

            void Heading()
            {
                if (wroteHeading) return;
                // Tool results arrive as user-role messages; label them as what they are.
                var isToolResult = message.Role == ChatRole.User && message.Parts.All(p => p is ToolResultPart);
                sb.AppendLine(isToolResult ? "## Tool results" : heading).AppendLine();
                wroteHeading = true;
            }
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>A file name for the export: the title, made path-safe, with the date.</summary>
    public static string SuggestFileName(string? title, DateTime? utc = null)
    {
        var stamp = (utc ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmm");
        var safe = new StringBuilder();
        foreach (var c in (title ?? "session").Trim())
            safe.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c is ' ' or '-' or '_' ? '-' : '\0');
        var name = safe.ToString().Replace("\0", "").Trim('-');
        while (name.Contains("--", StringComparison.Ordinal)) name = name.Replace("--", "-");
        if (name.Length > 48) name = name[..48].TrimEnd('-');
        return (name.Length == 0 ? "session" : name) + "-" + stamp + ".md";
    }

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…[truncated]";

    private static string Fenced(string body, string language)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in body)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + language + "\n" + body.TrimEnd('\n') + "\n" + fence;
    }
}

/// <summary>The request a host sends for <c>/init</c>: write or improve the repository's agent instruction file.</summary>
public static class CodingInitPrompt
{
    public const string Text =
        "Set up this repository's agent instructions. First look for an existing AGENTS.md or CLAUDE.md at the repository root "
        + "and read it; if one exists, improve it rather than replacing it, keeping every existing rule. Then examine the repository "
        + "(README, build files, project layout, test projects, scripts, CI configuration) and write a concise AGENTS.md at the root "
        + "that tells a coding agent: what the project is and how it is laid out; the exact commands to build, run and test "
        + "(and which test lanes matter); code style and conventions that are not obvious from the code; where configuration "
        + "and secrets come from, without recording any secret; and the rules an agent must follow before committing. Keep it "
        + "under about 150 lines, use headings and bullet lists, state only what you verified in the repository, and do not "
        + "invent commands. If CLAUDE.md exists and AGENTS.md does not, create AGENTS.md and leave CLAUDE.md as a one-line "
        + "pointer to it only if you were asked to; otherwise do not touch CLAUDE.md. Finish by listing what you wrote and "
        + "anything you could not verify.";
}
