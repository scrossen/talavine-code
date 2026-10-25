using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;

namespace SwarmCore.Chat.Components;

/// <summary>
/// Markdown to safe HTML for chat surfaces. Markdig runs with raw HTML disabled,
/// then a parser-based allowlist pass rebuilds the markup so the result can be
/// handed to a MarkupString even when the model output is untrusted.
/// </summary>
public static class ChatMarkdown
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
    /// Parses the HTML and rebuilds it from an allowlist: markup elements Markdig emits keep
    /// their allowlisted attributes; script-capable elements (script, style, frames, objects,
    /// forms, SVG/MathML, templates, metadata) are removed with their content; any other element
    /// is unwrapped to its text. Images become their alt text so rendering never triggers a remote
    /// load. Link URLs with a script or data scheme (after entity decoding) become <c>#</c>; other
    /// schemes are left for the link policy. Comments are dropped.
    /// </summary>
    public static string SanitizeHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument(string.Empty);
        var container = document.CreateElement("div");
        foreach (var node in parser.ParseFragment(html, document.Body!).ToList())
        {
            container.AppendChild(node);
        }

        Clean(container);
        return container.InnerHtml;
    }

    private static readonly HashSet<string> RemovedWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "noscript",
        "template", "meta", "link", "base", "svg", "math", "textarea", "select", "button", "audio", "video",
        "canvas", "noembed", "noframes", "xmp", "plaintext", "title", "head",
    };

    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "blockquote", "br", "cite", "code", "dd", "del", "details", "div", "dl", "dt", "em",
        "figcaption", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "input", "ins", "kbd", "li", "mark",
        "ol", "p", "pre", "q", "s", "small", "span", "strong", "sub", "summary", "sup", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "u", "ul",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "title", "class", "id", "align", "start", "colspan", "rowspan", "lang", "dir", "rel", "target",
    };

    private static void Clean(INode parent)
    {
        foreach (var node in parent.ChildNodes.ToList())
        {
            switch (node)
            {
                case IComment:
                    node.RemoveFromParent();
                    break;
                case IElement element:
                    var name = element.LocalName;
                    if (RemovedWithContent.Contains(name))
                    {
                        element.RemoveFromParent();
                    }
                    else if (string.Equals(name, "img", StringComparison.OrdinalIgnoreCase))
                    {
                        var alt = element.GetAttribute("alt");
                        parent.ReplaceChild(element.Owner!.CreateTextNode(alt ?? string.Empty), element);
                    }
                    else if (string.Equals(name, "input", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only a read-only task-list checkbox survives.
                        if (!string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase))
                        {
                            element.RemoveFromParent();
                            break;
                        }

                        var isChecked = element.HasAttribute("checked");
                        foreach (var attribute in element.Attributes.ToList()) element.RemoveAttribute(attribute.Name);
                        element.SetAttribute("type", "checkbox");
                        element.SetAttribute("disabled", "disabled");
                        if (isChecked) element.SetAttribute("checked", "checked");
                    }
                    else if (!AllowedElements.Contains(name) || element.NamespaceUri != NamespaceNames.HtmlUri)
                    {
                        Clean(element);
                        foreach (var child in element.ChildNodes.ToList()) parent.InsertBefore(child, element);
                        element.RemoveFromParent();
                    }
                    else
                    {
                        foreach (var attribute in element.Attributes.ToList())
                        {
                            if (!AllowedAttributes.Contains(attribute.Name)
                                || (string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "a", StringComparison.OrdinalIgnoreCase)))
                            {
                                element.RemoveAttribute(attribute.Name);
                            }
                            else if (string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase) && IsScriptOrDataUrl(attribute.Value))
                            {
                                element.SetAttribute("href", "#");
                            }
                        }

                        Clean(element);
                    }

                    break;
            }
        }
    }

    private static bool IsScriptOrDataUrl(string value)
    {
        // The parser has already decoded entities; browsers also ignore embedded whitespace,
        // control and zero-width characters in a scheme.
        var compact = new string(value.Where(c => c > ' ' && c != '\u00a0' && c != '\u200b' && c != '\ufeff').ToArray());
        return compact.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("livescript:", StringComparison.OrdinalIgnoreCase);
    }
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
