using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Web;

/// <summary>
/// Turns an HTML page into readable text: drops scripts, styles, navigation, headers, footers, forms and other
/// non-content elements, prefers the page's <c>main</c> or <c>article</c>, keeps paragraph and list structure, and
/// decodes entities. Linear-time scanning (no backtracking regex over the whole page).
/// </summary>
public static partial class ReadableHtml
{
    private static readonly string[] RemovedElements =
        ["script", "style", "noscript", "template", "svg", "iframe", "object", "canvas", "head", "nav", "header", "footer", "aside", "form", "select", "button", "dialog"];

    public static (string? Title, string Text) Extract(string html)
    {
        var title = TitleOf(html);
        var s = RemoveComments(html);
        foreach (var tag in RemovedElements) s = RemoveElement(s, tag);
        s = InnerOf(s, "main") ?? InnerOf(s, "article") ?? InnerOf(s, "body") ?? s;

        s = BlockBreak().Replace(s, "\n");
        s = ListItem().Replace(s, "\n- ");
        s = Heading().Replace(s, "\n\n");
        s = AnyTag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);

        var sb = new StringBuilder(s.Length);
        var blank = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = Spaces().Replace(raw, " ").Trim();
            if (line.Length == 0) { if (++blank == 1 && sb.Length > 0) sb.Append('\n'); continue; }
            blank = 0;
            sb.Append(line).Append('\n');
        }
        return (title, sb.ToString().Trim());
    }

    internal static string? TitleOf(string html)
    {
        // The document's title is in its head; inline SVG icons carry <title>s of their own further down. A title
        // cannot nest, so it ends at the first </title> after it.
        var headEnd = html.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
        var scope = headEnd > 0 ? html[..headEnd] : html;
        var start = IndexOfTag(scope, "<title", 0);
        if (start < 0) return null;
        var gt = scope.IndexOf('>', start);
        if (gt < 0) return null;
        var end = scope.IndexOf("</title", gt + 1, StringComparison.OrdinalIgnoreCase);
        var inner = end > gt ? scope[(gt + 1)..end] : scope[(gt + 1)..];
        var t = Spaces().Replace(WebUtility.HtmlDecode(AnyTag().Replace(inner, " ")), " ").Trim();
        return t.Length == 0 ? null : t;
    }

    private static string RemoveComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = s.IndexOf("<!--", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, start - i);
            var end = s.IndexOf("-->", start + 4, StringComparison.Ordinal);
            i = end < 0 ? s.Length : end + 3;
        }
        return sb.ToString();
    }

    /// <summary>Removes every <c>&lt;tag ...&gt;...&lt;/tag&gt;</c> (an unclosed one runs to the end of the document).</summary>
    private static string RemoveElement(string s, string tag)
    {
        var open = "<" + tag;
        var close = "</" + tag;
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = IndexOfTag(s, open, i);
            if (start < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, start - i).Append('\n');
            var end = IndexOfTag(s, close, start + open.Length);
            if (end < 0) { i = s.Length; break; }
            var gt = s.IndexOf('>', end);
            i = gt < 0 ? s.Length : gt + 1;
        }
        return sb.ToString();
    }

    /// <summary>The content between the first <c>&lt;tag&gt;</c> and its last closing tag, or null when absent.</summary>
    private static string? InnerOf(string s, string tag)
    {
        var start = IndexOfTag(s, "<" + tag, 0);
        if (start < 0) return null;
        var gt = s.IndexOf('>', start);
        if (gt < 0) return null;
        var end = s.LastIndexOf("</" + tag, StringComparison.OrdinalIgnoreCase);
        return end > gt ? s[(gt + 1)..end] : s[(gt + 1)..];
    }

    /// <summary>Index of <paramref name="marker"/> (such as "&lt;nav") followed by a tag-name boundary.</summary>
    private static int IndexOfTag(string s, string marker, int from)
    {
        while (from < s.Length)
        {
            var i = s.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            var next = i + marker.Length;
            if (next >= s.Length || !char.IsLetterOrDigit(s[next]) && s[next] != '-') return i;
            from = next;
        }
        return -1;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/table|/section|/article|/blockquote|/pre|/ul|/ol|/dl|/dd|/dt|hr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();
    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ListItem();
    [GeneratedRegex(@"<\s*(h[1-6]|p|tr|table|section|blockquote|pre)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Heading();
    [GeneratedRegex("<[^>]*>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"[ \t\r\f\v ]+")] private static partial Regex Spaces();
}
