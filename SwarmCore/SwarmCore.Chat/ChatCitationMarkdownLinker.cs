using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Chat;

/// <summary>
/// An answer-local citation alias and the host route that opens its source.
/// </summary>
public sealed record ChatCitationLink(string Alias, string Uri);

/// <summary>
/// Turns source aliases in assistant-authored Markdown into source links.
/// Models commonly emit both bracketed citations (<c>[S1]</c>) and bare
/// aliases in prose or table cells (<c>S1, S2</c>), so both forms are part of
/// the rendering contract.
/// </summary>
public static class ChatCitationMarkdownLinker
{
    private static readonly Regex CitationAliases = new(
        @"(?<![A-Za-z0-9])(?:\[\s*(?<bracketed>S\d+(?:\s*,\s*S\d+)*)\s*\]|(?<bare>S\d+(?:\s*,\s*S\d+)*))(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Linkifies known aliases while leaving code spans, code fences, existing
    /// Markdown links, and absolute URLs untouched.
    /// </summary>
    public static string Linkify(string? markdown, IEnumerable<ChatCitationLink>? citations)
    {
        if (string.IsNullOrEmpty(markdown) || citations is null)
        {
            return markdown ?? string.Empty;
        }

        var byAlias = citations
            .Where(static citation =>
                !string.IsNullOrWhiteSpace(citation.Alias)
                && !string.IsNullOrWhiteSpace(citation.Uri))
            .GroupBy(static citation => citation.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Uri.Trim(),
                StringComparer.OrdinalIgnoreCase);
        if (byAlias.Count == 0)
        {
            return markdown;
        }

        var output = new StringBuilder(markdown.Length + 64);
        var unprotected = new StringBuilder();
        var index = 0;

        while (index < markdown.Length)
        {
            if (TryReadCodeSpan(markdown, index, out var protectedEnd)
                || TryReadMarkdownLink(markdown, index, out protectedEnd)
                || TryReadAbsoluteUrl(markdown, index, out protectedEnd))
            {
                AppendLinkified(output, unprotected, byAlias);
                output.Append(markdown, index, protectedEnd - index);
                index = protectedEnd;
                continue;
            }

            unprotected.Append(markdown[index]);
            index++;
        }

        AppendLinkified(output, unprotected, byAlias);
        return output.ToString();
    }

    private static void AppendLinkified(
        StringBuilder output,
        StringBuilder markdown,
        IReadOnlyDictionary<string, string> byAlias)
    {
        if (markdown.Length == 0)
        {
            return;
        }

        output.Append(CitationAliases.Replace(
            markdown.ToString(),
            match =>
            {
                var aliases = (match.Groups["bracketed"].Success
                        ? match.Groups["bracketed"].Value
                        : match.Groups["bare"].Value)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (!aliases.Any(byAlias.ContainsKey))
                {
                    return match.Value;
                }

                return string.Join(
                    ", ",
                    aliases.Select(alias => byAlias.TryGetValue(alias, out var uri)
                        ? $"[{alias}]({uri})"
                        : alias));
            }));
        markdown.Clear();
    }

    private static bool TryReadCodeSpan(string markdown, int start, out int end)
    {
        end = start;
        var delimiterChar = markdown[start];
        if (delimiterChar is not '`' and not '~')
        {
            return false;
        }

        var delimiterLength = 1;
        while (start + delimiterLength < markdown.Length
               && markdown[start + delimiterLength] == delimiterChar)
        {
            delimiterLength++;
        }

        // Tildes are Markdown fences only at a run of three or more. Backticks
        // may be either an inline code span or a fence.
        if (delimiterChar == '~' && delimiterLength < 3)
        {
            return false;
        }

        var delimiter = markdown.Substring(start, delimiterLength);
        var closing = markdown.IndexOf(delimiter, start + delimiterLength, StringComparison.Ordinal);
        if (closing < 0)
        {
            return false;
        }

        end = closing + delimiterLength;
        return true;
    }

    private static bool TryReadMarkdownLink(string markdown, int start, out int end)
    {
        end = start;
        var labelStart = start;
        if (markdown[start] == '!')
        {
            if (start + 1 >= markdown.Length || markdown[start + 1] != '[')
            {
                return false;
            }
            labelStart++;
        }
        else if (markdown[start] != '[')
        {
            return false;
        }

        var labelEnd = FindUnescaped(markdown, ']', labelStart + 1);
        if (labelEnd < 0)
        {
            return false;
        }

        var destinationStart = labelEnd + 1;
        while (destinationStart < markdown.Length
               && markdown[destinationStart] is ' ' or '\t')
        {
            destinationStart++;
        }
        if (destinationStart >= markdown.Length || markdown[destinationStart] != '(')
        {
            return false;
        }

        var depth = 1;
        for (var index = destinationStart + 1; index < markdown.Length; index++)
        {
            if (markdown[index] == '\\')
            {
                index++;
                continue;
            }

            if (markdown[index] == '(')
            {
                depth++;
            }
            else if (markdown[index] == ')' && --depth == 0)
            {
                end = index + 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadAbsoluteUrl(string markdown, int start, out int end)
    {
        end = start;
        if (!markdown.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !markdown.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !markdown.AsSpan(start).StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        end = start;
        while (end < markdown.Length
               && !char.IsWhiteSpace(markdown[end])
               && markdown[end] is not '<' and not '>')
        {
            end++;
        }
        return end > start;
    }

    private static int FindUnescaped(string value, char target, int start)
    {
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                index++;
                continue;
            }

            if (value[index] == target)
            {
                return index;
            }
        }

        return -1;
    }
}
