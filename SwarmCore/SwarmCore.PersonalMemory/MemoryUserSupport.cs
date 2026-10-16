using System.Text.RegularExpressions;

namespace SwarmCore.PersonalMemory;

/// <summary>
/// Deterministic "is this statement supported by these words?" check. Used to prove that an
/// extracted or LLM-consolidated memory comes from the person's own message (or from the
/// claims it cites) rather than from the model's imagination or from quoted third-party text.
///
/// <para>Rules: every content word of the statement (after dropping function words and
/// first/third-person subject words such as "I", "my", "user") must appear in ONE sentence of
/// the source (two adjacent sentences are also tried); numbers must match exactly; the
/// sentence's negation must equal the statement's negation; quoted lines ("&gt; ...") and
/// reported speech ("the article says ...") are not the person's own words.</para>
/// </summary>
public static partial class MemoryUserSupport
{
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "i", "im", "ive", "id", "ill", "me", "my", "mine", "myself", "user", "users", "the", "a", "an",
        "is", "am", "are", "was", "were", "be", "been", "being", "to", "of", "and", "or", "in", "on",
        "at", "for", "with", "that", "this", "it", "its", "they", "them", "their", "theirs", "he", "she",
        "him", "his", "her", "hers", "has", "have", "had", "do", "does", "did", "so", "as", "by", "from",
        "about", "s", "currently", "really", "very", "also", "just", "please", "remember", "note",
        "that's", "thats", "we", "our", "us", "person", "subject", "will", "would", "going", "gonna",
        "now", "who", "which",
        // Connectives a model adds when it joins the person's clauses ("..., the conference moved online"
        // becomes "... because the conference moved online"). They assert nothing on their own.
        "because", "but", "though", "although", "however", "therefore", "thus", "since",
    };

    private static readonly HashSet<string> NegationWords = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent", "cant",
        "cannot", "wont", "wouldnt", "shouldnt", "havent", "hasnt", "hadnt", "neither", "nor", "without",
        "dislike", "dislikes", "hate", "hates", "avoid", "avoids",
    };

    /// <summary>Words a consolidation may add when rewriting a time-bound fact into the past tense.</summary>
    public static readonly IReadOnlySet<string> TemporalRewriteWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "went", "was", "were", "had", "did", "visited", "traveled", "travelled", "attended", "completed",
        "finished", "ended", "previously", "formerly", "former", "past", "earlier", "last", "ago",
        "returned", "back", "took", "trip", "used", "once", "already", "done", "moved", "left",
        "january", "february", "march", "april", "may", "june", "july", "august", "september",
        "october", "november", "december", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep",
        "sept", "oct", "nov", "dec", "spring", "summer", "autumn", "fall", "winter", "year", "month",
    };

    /// <summary>True when <paramref name="statement"/> is supported by the person's own words in <paramref name="source"/>.</summary>
    public static bool IsSupported(string? source, string? statement)
        => IsSupported(source, statement, extraAllowedWords: null);

    /// <summary>
    /// As <see cref="IsSupported(string?, string?)"/> but tokens in <paramref name="extraAllowedWords"/>
    /// may appear in the statement without source support (used for temporal rewrites).
    /// </summary>
    public static bool IsSupported(string? source, string? statement, IReadOnlySet<string>? extraAllowedWords)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(statement)) return false;

        var statementTokens = Tokenize(statement);
        var content = statementTokens.Where(t => !FunctionWords.Contains(t) && !NegationWords.Contains(t)).Select(Stem)
            .Where(t => extraAllowedWords is null || !extraAllowedWords.Contains(t))
            .Distinct(StringComparer.Ordinal).ToList();
        var statementNegated = statementTokens.Any(NegationWords.Contains);

        // A statement with no checkable content words cannot be proven.
        var allContent = statementTokens.Where(t => !FunctionWords.Contains(t) && !NegationWords.Contains(t)).ToList();
        if (allContent.Count == 0) return false;
        // A statement made only of allowed words ("Trip was last year.", "Already done.") keeps
        // none of the fact's subject; at least one of its own words must come from the source.
        if (content.Count == 0) return false;

        var statementHedged = statementTokens.Any(HedgeWords.Contains);
        var sentences = OwnSentences(source).ToList();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (Covers(sentences[i], content, statementNegated, statementHedged)) return true;
            if (i + 1 < sentences.Count && Covers(sentences[i] + " " + sentences[i + 1], content, statementNegated, statementHedged)) return true;
        }
        return false;
    }

    /// <summary>
    /// Framing words a model uses when it restates an answer-style directive as a preference
    /// ("keep answers short, bullet points" becomes "User prefers short, bullet-point answers").
    /// Stems, as compared after <see cref="Stem"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> StylePreferenceFramingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "prefer", "preferred", "preference", "like", "want", "answer", "response", "reply", "format",
        "formatted", "formatting", "style", "written", "use", "kept", "keep", "give", "given",
    };

    /// <summary>Words that make a sentence about how answers should look (stems).</summary>
    private static readonly HashSet<string> StyleCueWords = new(StringComparer.Ordinal)
    {
        "answer", "response", "reply", "bullet", "concise", "brief", "short", "shorter", "long", "longer",
        "detailed", "detail", "format", "formatted", "tone", "emoji", "emojis", "formal", "casual", "summary",
        "language", "english", "spanish", "french", "german", "sentence", "paragraph", "explanation", "jargon",
        "plain", "simple", "list", "table", "markdown", "verbose", "terse", "wordy", "headings", "heading",
        "step", "example", "code", "metric", "imperial", "units", "unit", "style",
    };

    /// <summary>
    /// Support check for a <see cref="MemoryCategory.ResponsePreference"/> statement. Like
    /// <see cref="IsSupported(string?, string?)"/>, and additionally accepts a restatement whose only
    /// unsupported words are preference framing ("prefers", "answers", "formatted") when the
    /// supporting sentence is itself about how answers should look (it contains a style cue such as
    /// "short", "bullet", "answers" or "tone"). An unrelated sentence ("I'm allergic to coffee") never
    /// supports a framed preference ("User likes coffee").
    /// </summary>
    public static bool IsSupportedStylePreference(string? source, string? statement)
    {
        if (IsSupported(source, statement)) return true;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(statement)) return false;

        var statementTokens = Tokenize(statement);
        var content = statementTokens.Where(t => !FunctionWords.Contains(t) && !NegationWords.Contains(t)).Select(Stem)
            .Where(t => !StylePreferenceFramingWords.Contains(t))
            .Distinct(StringComparer.Ordinal).ToList();
        if (content.Count == 0) return false;
        var statementNegated = statementTokens.Any(NegationWords.Contains);
        var statementHedged = statementTokens.Any(HedgeWords.Contains);
        var sentences = OwnSentences(source).ToList();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (IsStyleSentence(sentences[i]) && Covers(sentences[i], content, statementNegated, statementHedged)) return true;
        }
        return false;
    }

    private static bool IsStyleSentence(string sentence)
        => Tokenize(sentence).Select(Stem).Any(t => StyleCueWords.Contains(t) || StyleCueWords.Contains(t + "s"));

    private static bool Covers(string sentence, IReadOnlyList<string> content, bool statementNegated, bool statementHedged)
    {
        var tokens = Tokenize(sentence);
        var stems = tokens.Select(Stem).ToHashSet(StringComparer.Ordinal);
        if (!content.All(stems.Contains)) return false;
        var sentenceNegated = tokens.Any(NegationWords.Contains);
        if (sentenceNegated != statementNegated) return false;
        // "If I moved to Denver..." / "Maybe I'll ..." does not support a plain assertion.
        return statementHedged || !tokens.Any(HedgeWords.Contains);
    }

    private static readonly HashSet<string> HedgeWords = new(StringComparer.Ordinal)
    {
        "if", "wish", "maybe", "might", "perhaps", "possibly", "considering", "consider", "hypothetically",
        "suppose", "supposing", "imagine", "whether", "unless", "pretend", "someday",
    };

    /// <summary>Sentences that are the author's own words: quoted lines and reported speech removed.</summary>
    private static IEnumerable<string> OwnSentences(string source)
    {
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('>')) continue;
            if (QuotedHeaderRegex().IsMatch(line)) yield break; // "On <date>, X wrote:" starts a quoted thread
            line = QuotedSpanRegex().Replace(line, " ");
            foreach (var sentence in SentenceSplitRegex().Split(line))
            {
                var s = sentence.Trim();
                // Questions and reported speech are not the person asserting something about themselves.
                if (s.Length == 0 || s.EndsWith('?') || ReportedSpeechRegex().IsMatch(s)) continue;
                yield return s;
            }
        }
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in TokenRegex().Matches(text.ToLowerInvariant()))
        {
            var token = m.Value.Replace("'", string.Empty).Replace("’", string.Empty);
            if (token.Length > 0) tokens.Add(token);
        }
        return tokens;
    }

    internal static string Stem(string token)
    {
        if (token.Length <= 3 || token.All(char.IsDigit)) return token;
        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4) return token[..^3] + "y";
        if (token.EndsWith("sses", StringComparison.Ordinal)) return token[..^2];
        if (token.EndsWith("ss", StringComparison.Ordinal)) return token;
        if (token.EndsWith('s')) return token[..^1];
        return token;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?<=[.!?;])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"""[^""]{0,400}""|“[^”]{0,400}”", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedSpanRegex();

    [GeneratedRegex(@"^on\s.{3,120}\swrote:\s*$|^-{2,}\s*(?:original|forwarded)\s+message", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedHeaderRegex();

    [GeneratedRegex(@"\b(?:says|said|writes|wrote|according\s+to|claims?|claimed|reports?|reported|quote|quoted|the\s+(?:article|email|message|post|document|page|website)\s+(?:says|states|mentions))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReportedSpeechRegex();
}
