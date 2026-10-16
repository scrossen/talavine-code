using System.Net;
using System.Text.RegularExpressions;

namespace SwarmCore.Chat;

/// <summary>
/// A product-owned feature destination and the phrases that identify it in an
/// assistant answer. Routes come from the host, never from model output.
/// </summary>
public sealed record ChatKnownFeatureLink(
    string Label,
    string Route,
    IReadOnlyList<string> Terms)
{
    public ChatKnownFeatureLink(string label, string route, params string[] terms)
        : this(label, route, (IReadOnlyList<string>)terms)
    {
    }
}

/// <summary>
/// Finds known product features mentioned in assistant-authored Markdown so a
/// host can render verified navigation beside the answer.
/// </summary>
public static class ChatKnownFeatureLinks
{
    public static IReadOnlyList<ChatKnownFeatureLink> FindReferenced(
        string? markdown,
        IEnumerable<ChatKnownFeatureLink>? features)
    {
        if (string.IsNullOrWhiteSpace(markdown) || features is null)
        {
            return [];
        }

        return features
            .Where(IsValid)
            .Where(feature => feature.Terms.Any(term =>
                !string.IsNullOrWhiteSpace(term)
                && markdown.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(static feature => feature.Route, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ChatKnownFeatureLink> FindMissing(
        string? markdown,
        IEnumerable<ChatKnownFeatureLink>? features)
    {
        var text = markdown ?? string.Empty;
        return FindReferenced(text, features)
            .Where(feature => !ContainsRoute(text, feature.Route))
            .ToArray();
    }

    public static string AppendMarkdown(
        string? markdown,
        string productLabel,
        IEnumerable<ChatKnownFeatureLink>? features)
    {
        var text = markdown ?? string.Empty;
        var links = FindMissing(text, features);
        if (links.Count == 0)
        {
            return text;
        }

        return $"{text.TrimEnd()}\n\nOpen in {productLabel}: "
               + string.Join(" · ", links.Select(static link => $"[{link.Label}]({link.Route})"));
    }

    private static bool IsValid(ChatKnownFeatureLink feature) =>
        !string.IsNullOrWhiteSpace(feature.Label)
        && IsSafeNavigationUrl(feature.Route)
        && feature.Terms.Count > 0;

    private static bool ContainsRoute(string markdown, string route) =>
        markdown.Contains($"]({route})", StringComparison.OrdinalIgnoreCase)
        || markdown.Contains($"href=\"{route}\"", StringComparison.OrdinalIgnoreCase)
        || markdown.Contains($"href='{route}'", StringComparison.OrdinalIgnoreCase);

    /// <param name="hostDeepLinks">
    /// Exact links the host itself handles (for example a product URI its own protocol handler opens). They are the
    /// host's, never a general custom-protocol escape hatch: only an exact, case-insensitive match is allowed.
    /// </param>
    internal static bool IsSafeNavigationUrl(string href, IReadOnlyCollection<string>? hostDeepLinks = null)
    {
        if (hostDeepLinks is { Count: > 0 } && hostDeepLinks.Contains(href, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (href.StartsWith("/", StringComparison.Ordinal) && !href.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (href.StartsWith("#", StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps
                   || uri.Scheme == Uri.UriSchemeHttp
                   || uri.Scheme == Uri.UriSchemeMailto);
    }
}

/// <summary>
/// Applies the browser contract used by Swarm chat surfaces: safe links open
/// separately and cannot retain a reference to their opener.
/// </summary>
public static partial class ChatLinkHtmlPolicy
{
    /// <param name="hostDeepLinks">Exact links the host handles itself, allowed beside the safe web/relative/mail links.</param>
    public static string OpenLinksSeparately(string? html, IReadOnlyCollection<string>? hostDeepLinks = null)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html ?? string.Empty;
        }

        return AnchorRegex().Replace(html, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (!ChatKnownFeatureLinks.IsSafeNavigationUrl(href, hostDeepLinks))
            {
                return "<a href=\"#\" aria-disabled=\"true\"";
            }

            return $"<a href=\"{match.Groups["href"].Value}\" target=\"_blank\" rel=\"noopener noreferrer\"";
        });
    }

    [GeneratedRegex("<a href=\"(?<href>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorRegex();
}
