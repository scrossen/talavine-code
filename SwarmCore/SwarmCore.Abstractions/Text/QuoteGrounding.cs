using System.Globalization;
using System.Text;

namespace SwarmCore.Abstractions.Text;

/// <summary>
/// Deterministic "did the model actually quote this source text?" check.
///
/// The message pipeline asks models for a verbatim <c>quote</c> alongside every
/// extracted fact and observation, then uses this predicate as a hard filter: a
/// quote that cannot be found in the source text means the artifact is dropped
/// before it reaches durable storage. That makes the predicate's tolerance a
/// yield decision, not a scoring nicety.
///
/// The tolerance is exactly typographic and presentational. Mail composed in
/// Apple Mail, Outlook, or Word auto-substitutes smart punctuation (curly
/// apostrophes, en/em dashes, a single-character ellipsis, non-breaking spaces),
/// and models normalize that punctuation back to ASCII when they copy text out.
/// A quote that differs from the source only by that substitution is verbatim in
/// every sense the check cares about, so it must ground. Measured on a real
/// inbox corpus, curly-vs-straight apostrophes and stripped markdown emphasis
/// accounted for every grounding failure — none were fabrications.
///
/// The same is true of the reply markers that quoted email history carries.
/// When a model quotes a sentence out of a "&gt; " block it reproduces the
/// sentence, not the markers the mail client painted down the left margin, and a
/// hard-wrapped quoted paragraph puts a marker in the middle of the sentence.
/// Measured across 1,300 labelled business and inbox messages, that accounted
/// for 246 of the 284 quotes still failing after typography normalization, and
/// every one was a contiguous passage of the source rather than two unrelated
/// quoted lines run together.
///
/// The tolerance deliberately stops there. There is no case/accent folding
/// beyond the pre-existing ordinal case-insensitivity, no stemming, no token
/// overlap, and no edit-distance fallback: the point of the check is that the
/// model copied real source text, so a paraphrase or an invention must still
/// fail. Every transform below is either a context-free per-character map or the
/// removal of a line's leading margin decoration, which is what keeps that
/// guarantee — it can turn a near-verbatim quote into a match, but it cannot
/// manufacture one out of text the source does not contain.
/// </summary>
public static class QuoteGrounding
{
    /// <summary>
    /// True when <paramref name="quote"/> appears in <paramref name="source"/>,
    /// tolerating only typographic substitution, whitespace/markdown-emphasis
    /// formatting, and the reply markers quoted email history carries. Blank
    /// quotes never ground.
    /// </summary>
    public static bool SourceContainsQuote(string? source, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return false;
        if (string.IsNullOrEmpty(source)) return false;

        var trimmed = quote.Trim();
        if (ContainsAtAnyTier(source, trimmed)) return true;

        // Tier 5: the quote came out of quoted history, so the source carries
        // "> " down the left margin and the quote does not. Strip the markers
        // from the source and run the ladder again. Stripping is anchored to the
        // start of a line and leaves the line break in place, so the only text it
        // brings together is text that was already on adjacent lines — the same
        // adjacency tier 2's whitespace collapse has always allowed for
        // hard-wrapped unquoted prose.
        var unquoted = StripReplyMarkers(source);
        return unquoted.Length != source.Length && ContainsAtAnyTier(unquoted, trimmed);
    }

    /// <summary>
    /// The grounding ladder for one candidate source rendering: literal, then
    /// whitespace-insensitive, then typography-normalized, then emphasis-stripped.
    /// </summary>
    private static bool ContainsAtAnyTier(string source, string trimmedQuote)
    {
        // Tier 1/2: the historical check — literal, then whitespace-insensitive.
        if (source.Contains(trimmedQuote, StringComparison.OrdinalIgnoreCase)) return true;

        var collapsedSource = CollapseWhitespace(source);
        var collapsedQuote = CollapseWhitespace(trimmedQuote);
        if (collapsedSource.Contains(collapsedQuote, StringComparison.OrdinalIgnoreCase)) return true;

        // Tier 3: smart punctuation on one side, ASCII on the other.
        var normalizedSource = CollapseWhitespace(NormalizeTypography(collapsedSource));
        var normalizedQuote = CollapseWhitespace(NormalizeTypography(collapsedQuote));
        if (normalizedSource.Contains(normalizedQuote, StringComparison.OrdinalIgnoreCase)) return true;

        // Tier 4: the model dropped markdown emphasis the source (or the quote)
        // carries — "*Check-In:*" quoted as "Check-In:".
        var strippedQuote = CollapseWhitespace(StripEmphasis(normalizedQuote));
        if (strippedQuote.Length == 0) return false;
        return CollapseWhitespace(StripEmphasis(normalizedSource))
            .Contains(strippedQuote, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Map Unicode typography onto its ASCII equivalent: the curly quote and
    /// apostrophe families to <c>'</c> and <c>"</c>, the dash family to <c>-</c>,
    /// a single-character ellipsis to three dots, every exotic space to a plain
    /// space, and zero-width / invisible formatting characters to nothing.
    /// </summary>
    public static string NormalizeTypography(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                // Single quotes / apostrophes: left, right, low-9, high-reversed-9,
                // modifier letter apostrophe, prime, acute accent.
                case '‘':
                case '’':
                case '‚':
                case '‛':
                case 'ʼ':
                case '′':
                case '´':
                    builder.Append('\'');
                    break;

                // Double quotes: left, right, low-9, high-reversed-9, double prime,
                // guillemets.
                case '“':
                case '”':
                case '„':
                case '‟':
                case '″':
                case '«':
                case '»':
                    builder.Append('"');
                    break;

                // Dashes: hyphen, non-breaking hyphen, figure/en/em dash,
                // horizontal bar, minus sign.
                case '‐':
                case '‑':
                case '‒':
                case '–':
                case '—':
                case '―':
                case '−':
                    builder.Append('-');
                    break;

                case '…':
                    builder.Append("...");
                    break;

                // Invisible: soft hyphen, zero-width space/non-joiner/joiner,
                // word joiner, BOM. Dropped rather than spaced — they render as
                // nothing, so a model copying the text emits nothing.
                case '\u00AD': // soft hyphen
                case '\u200B': // zero-width space
                case '\u200C': // zero-width non-joiner
                case '\u200D': // zero-width joiner
                case '\u2060': // word joiner
                case '\uFEFF': // zero-width no-break space / BOM
                    break;

                default:
                    switch (CharUnicodeInfo.GetUnicodeCategory(ch))
                    {
                        // NBSP, narrow NBSP, en/em/thin/figure spaces, ideographic space.
                        case UnicodeCategory.SpaceSeparator:
                            builder.Append(' ');
                            break;
                        // Bidi marks and other invisible format controls.
                        case UnicodeCategory.Format:
                            break;
                        default:
                            builder.Append(ch);
                            break;
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Trim and collapse every run of whitespace to a single space.
    /// </summary>
    public static string CollapseWhitespace(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Remove the reply markers quoted email history carries: the run of
    /// <c>&gt;</c> characters (and the indent before them) at the start of each
    /// line, equivalent to the regex <c>^[ \t]*(?:&gt;[ \t]?)+</c> in multiline
    /// mode. Line breaks are preserved and <c>&gt;</c> elsewhere in a line is
    /// left alone, so this cannot join text that was not already on adjacent
    /// lines. Returns the input unchanged when there is nothing to strip.
    /// </summary>
    public static string StripReplyMarkers(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!value.Contains('>')) return value;

        var builder = new StringBuilder(value.Length);
        var index = 0;
        var atLineStart = true;

        while (index < value.Length)
        {
            if (atLineStart)
            {
                atLineStart = false;

                var scan = index;
                while (scan < value.Length && value[scan] is ' ' or '\t') scan++;

                var markers = 0;
                while (scan < value.Length && value[scan] == '>')
                {
                    scan++;
                    markers++;
                    if (scan < value.Length && value[scan] is ' ' or '\t') scan++;
                }

                // The indent only goes with the markers: a line that merely
                // starts with whitespace is left exactly as it was.
                if (markers > 0) index = scan;
                continue;
            }

            var ch = value[index++];
            builder.Append(ch);
            if (ch is '\n' or '\r') atLineStart = true;
        }

        return builder.Length == value.Length ? value : builder.ToString();
    }

    /// <summary>
    /// Remove markdown emphasis markers (<c>*</c>, <c>_</c>, <c>~</c>). Used only
    /// as the last grounding tier and applied to both sides, so it can recover a
    /// quote the model un-emphasised without loosening what counts as verbatim.
    /// </summary>
    public static string StripEmphasis(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.AsSpan().IndexOfAny('*', '_', '~') < 0) return value;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is not ('*' or '_' or '~')) builder.Append(ch);
        }

        return builder.ToString();
    }
}
