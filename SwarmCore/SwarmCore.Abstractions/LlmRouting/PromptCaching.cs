namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Shared sentinel for opt-in prompt caching. When a prompt string contains
/// <see cref="CacheBoundaryMarker"/>, the caller is declaring: "everything
/// before this marker is a stable prefix that providers can cache across
/// calls; everything after is per-call content that changes."
///
/// Providers that support explicit cache control (Anthropic) split the
/// prompt on the marker, tag the prefix with <c>cache_control: ephemeral</c>,
/// and submit it as a two-block content array. Providers with automatic
/// caching (OpenAI) strip the marker — their cache infrastructure only
/// needs a stable prefix at the start of the prompt, which the marker
/// guarantees is preserved. Providers without caching support strip the
/// marker silently.
///
/// Callers should emit the marker at the natural split between "shared
/// instructions/policy" and "per-message data". Keep the prefix &gt;= 1024
/// tokens for OpenAI's automatic cache threshold; Anthropic has a lower
/// floor but the same principle holds.
/// </summary>
public static class PromptCaching
{
    /// <summary>
    /// Sentinel string that separates cacheable prefix from per-call content.
    /// Chosen to be unlikely to appear in natural text. An earlier revision
    /// used <c>&lt;&lt;&lt;CACHE_BOUNDARY&gt;&gt;&gt;</c>, which a former router
    /// HTML-cleaning regex silently removed. The router now preserves prompts
    /// verbatim; cache-marker handling remains an explicit provider protocol.
    /// Equals signs aren't HTML-ish, aren't shell-ish, and don't appear in
    /// our prompt templates.
    /// </summary>
    public const string CacheBoundaryMarker = "===SWARM_CACHE_BOUNDARY===";

    /// <summary>
    /// Split a prompt on the boundary marker, returning (prefix, suffix).
    /// If the marker isn't present, the prefix is empty and the whole
    /// prompt is the suffix (non-cached payload).
    /// </summary>
    public static (string Prefix, string Suffix) Split(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return (string.Empty, prompt ?? string.Empty);

        var idx = prompt.IndexOf(CacheBoundaryMarker, StringComparison.Ordinal);
        if (idx < 0) return (string.Empty, prompt);

        var prefix = prompt[..idx];
        var suffix = prompt[(idx + CacheBoundaryMarker.Length)..];
        return (prefix, suffix);
    }

    /// <summary>
    /// Strip the boundary marker from a prompt without splitting — useful
    /// for providers that don't do explicit cache-control.
    /// </summary>
    public static string Strip(string prompt) =>
        string.IsNullOrEmpty(prompt)
            ? string.Empty
            : prompt.Replace(CacheBoundaryMarker, string.Empty, StringComparison.Ordinal);
}
