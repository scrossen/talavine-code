using System.Net;
using System.Text.RegularExpressions;
using Markdig;

namespace SwarmCore.Chat.Components;

/// <summary>
/// Markdown to safe HTML for chat surfaces. Markdig runs with raw HTML disabled,
/// then a second, regex-based pass strips anything executable so the result can
/// be handed to a MarkupString even when the model output is untrusted.
/// </summary>
public static partial class ChatMarkdown
{
    private static readonly MarkdownPipeline Pipeline = WithoutGenericAttributes(new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml())
        .Build();

    private static readonly MarkdownPipeline RawHtmlPipeline = WithoutGenericAttributes(new MarkdownPipelineBuilder()
        .UseAdvancedExtensions())
        .Build();

    /// <summary>
    /// The advanced set includes generic attributes, which read any <c>{...}</c> after an inline element as
    /// attributes and drop it from the text (<c>mcp_servers={}</c> rendered as <c>mcp_servers=</c>). Chat text
    /// is prose and code, never attribute syntax, so literal braces stay text.
    /// </summary>
    private static MarkdownPipelineBuilder WithoutGenericAttributes(MarkdownPipelineBuilder builder)
    {
        builder.Extensions.RemoveAll(extension => extension is Markdig.Extensions.GenericAttributes.GenericAttributesExtension);
        return builder;
    }

    /// <summary>Renders markdown to sanitized HTML. Never throws.</summary>
    public static string ToHtml(string? markdown) => ToHtml(markdown, ChatMarkdownOptions.Default);

    /// <summary>
    /// Renders markdown to sanitized HTML with host-supplied pipeline choices
    /// (raw HTML passthrough, sanitizer, block decoration, link policy). Never throws.
    /// </summary>
    public static string ToHtml(string? markdown, ChatMarkdownOptions? options)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        options ??= ChatMarkdownOptions.Default;
        try
        {
            var rendered = Markdown.ToHtml(markdown, options.AllowRawHtml ? RawHtmlPipeline : Pipeline);
            var html = options.Sanitizer is { } sanitize ? sanitize(rendered) : SanitizeHtml(rendered);
            if (options.DecorateBlocks)
            {
                html = html.Replace("<pre><code", "<pre class=\"chat-code\"><code", StringComparison.Ordinal)
                    .Replace("<table>", "<div class=\"chat-table-wrap\"><table>", StringComparison.Ordinal)
                    .Replace("</table>", "</table></div>", StringComparison.Ordinal);
            }

            return options.LinkPolicy is { } linkPolicy
                ? linkPolicy(html)
                : ChatLinkHtmlPolicy.OpenLinksSeparately(html, options.HostDeepLinks);
        }
        catch
        {
            return WebUtility.HtmlEncode(markdown);
        }
    }

    /// <summary>
    /// Removes script/style/iframe/object/embed/form (and similar) elements, event-handler
    /// and style attributes, and javascript:/vbscript:/data: URLs. Images are replaced by
    /// their alt text so rendering never triggers a remote load.
    /// </summary>
    public static string SanitizeHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var current = html;
        // Repeat until stable so fragments reassembled by a removal (<scr<script>ipt>) are caught.
        for (var pass = 0; pass < 6; pass++)
        {
            var next = current;
            next = DangerousPairedTag().Replace(next, string.Empty);
            next = DangerousLooseTag().Replace(next, string.Empty);
            next = ImageTag().Replace(next, ImageToAltText);
            next = EventHandlerAttribute().Replace(next, " ");
            next = StyleAttribute().Replace(next, " ");
            next = UrlAttribute().Replace(next, NeutralizeUrlAttribute);
            if (string.Equals(next, current, StringComparison.Ordinal))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static string ImageToAltText(Match match)
    {
        var alt = AltAttribute().Match(match.Value);
        if (!alt.Success)
        {
            return string.Empty;
        }

        // Already entity-encoded source text; keep as is.
        return alt.Groups["d"].Success ? alt.Groups["d"].Value
            : alt.Groups["s"].Success ? alt.Groups["s"].Value
            : alt.Groups["u"].Value;
    }

    private static string NeutralizeUrlAttribute(Match match)
    {
        var value = match.Groups["d"].Success ? match.Groups["d"].Value
            : match.Groups["s"].Success ? match.Groups["s"].Value
            : match.Groups["u"].Value;

        // WebUtility only knows HTML4 entities; browsers also decode these HTML5 ones.
        var normalized = value
            .Replace("&colon;", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("&Tab;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("&NewLine;", string.Empty, StringComparison.OrdinalIgnoreCase);
        var decoded = WebUtility.HtmlDecode(WebUtility.HtmlDecode(normalized));
        var compact = new string(decoded.Where(c => c > ' ' && c != ' ' && c != '​').ToArray());
        if (compact.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("livescript:", StringComparison.OrdinalIgnoreCase))
        {
            return $"{match.Groups["name"].Value}=\"#\"";
        }

        return match.Value;
    }

    private const string DangerousNames =
        "script|style|iframe|frame|frameset|object|embed|applet|form|noscript|template|meta|link|base";

    [GeneratedRegex("<(?<t>" + DangerousNames + @")\b[^>]*>.*?</\k<t>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousPairedTag();

    [GeneratedRegex("</?(?:" + DangerousNames + @")\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousLooseTag();

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex("""\balt\s*=\s*(?:"(?<d>[^"]*)"|'(?<s>[^']*)'|(?<u>[^\s>]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AltAttribute();

    [GeneratedRegex("""[\s/"']on[a-z0-9_-]+\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex("""\sstyle\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("""(?<name>\b(?:href|src|xlink:href|action|formaction|poster|background))\s*=\s*(?:"(?<d>[^"]*)"|'(?<s>[^']*)'|(?<u>[^\s>]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlAttribute();
}

/// <summary>
/// Host choices for <see cref="ChatMarkdown.ToHtml(string?, ChatMarkdownOptions?)"/>.
/// The defaults are the strict shared pipeline: raw HTML disabled, the built-in
/// sanitizer, code/table decoration for the shared stylesheet, and
/// <see cref="ChatLinkHtmlPolicy.OpenLinksSeparately"/>.
/// </summary>
public sealed record ChatMarkdownOptions
{
    public static ChatMarkdownOptions Default { get; } = new();

    /// <summary>
    /// Lets Markdig pass raw HTML through to the sanitizer instead of escaping it.
    /// The sanitizer (built-in or <see cref="Sanitizer"/>) still runs on the result.
    /// </summary>
    public bool AllowRawHtml { get; init; }

    /// <summary>
    /// Replaces <see cref="ChatMarkdown.SanitizeHtml"/>. A host sanitizer must remove
    /// executable markup itself; its output is handed to a MarkupString.
    /// </summary>
    public Func<string, string>? Sanitizer { get; init; }

    /// <summary>
    /// Adds the <c>chat-code</c> class to code blocks (copy button) and wraps tables in
    /// <c>chat-table-wrap</c>. Hosts styling markdown with their own stylesheet turn this off.
    /// </summary>
    public bool DecorateBlocks { get; init; } = true;

    /// <summary>
    /// Final anchor policy over the sanitized HTML. Null applies
    /// <see cref="ChatLinkHtmlPolicy.OpenLinksSeparately"/> with <see cref="HostDeepLinks"/>.
    /// </summary>
    public Func<string, string>? LinkPolicy { get; init; }

    /// <summary>
    /// Exact links this host handles itself (a product URI its own protocol handler opens), kept by the default link
    /// policy beside the safe web, relative and mail links. Empty: no custom-scheme link survives.
    /// </summary>
    public IReadOnlyList<string> HostDeepLinks { get; init; } = [];
}
