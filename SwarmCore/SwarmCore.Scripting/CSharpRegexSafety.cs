using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SwarmCore.Scripting;

/// <summary>
/// Lightweight textual defense-in-depth over a Roslyn-tokenized code stream. Comments and literal
/// contents are excluded, and token spacing is canonicalized, so policy rules inspect executable C#
/// rather than blocking documentation/error messages or being bypassed with comments around dots.
/// </summary>
public static class CSharpRegexSafety
{
    /// <summary>Return patterns that match executable tokens in <paramref name="code"/>.</summary>
    public static IReadOnlyList<string> MatchingPatterns(string code, IEnumerable<string> patterns)
    {
        var executableText = BuildExecutableTokenText(code);
        var hits = new List<string>();
        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(executableText, pattern, RegexOptions.IgnoreCase))
                hits.Add(pattern);
        }
        return hits;
    }

    /// <summary>True if any pattern matches.</summary>
    public static bool MatchesAny(string code, IEnumerable<string> patterns)
    {
        var executableText = BuildExecutableTokenText(code);
        return patterns.Any(p => Regex.IsMatch(executableText, p, RegexOptions.IgnoreCase));
    }

    private static string BuildExecutableTokenText(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var builder = new StringBuilder(code.Length);
        SyntaxToken previous = default;
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            var rendered = RenderToken(token);
            if (rendered.Length == 0)
            {
                continue;
            }

            if (previous.RawKind != 0 && IsWordLike(previous) && IsWordLike(token))
            {
                builder.Append(' ');
            }

            builder.Append(rendered);
            previous = token;
        }

        return builder.ToString();
    }

    private static string RenderToken(SyntaxToken token)
        => token.Kind() switch
        {
            SyntaxKind.StringLiteralToken or
            SyntaxKind.Utf8StringLiteralToken or
            SyntaxKind.SingleLineRawStringLiteralToken or
            SyntaxKind.Utf8SingleLineRawStringLiteralToken or
            SyntaxKind.MultiLineRawStringLiteralToken or
            SyntaxKind.Utf8MultiLineRawStringLiteralToken or
            SyntaxKind.CharacterLiteralToken or
            SyntaxKind.InterpolatedStringTextToken => "\"\"",
            _ => token.Text,
        };

    private static bool IsWordLike(SyntaxToken token)
    {
        var kind = token.Kind();
        return kind is SyntaxKind.IdentifierToken
            or SyntaxKind.NumericLiteralToken
            || SyntaxFacts.IsKeywordKind(kind)
            || SyntaxFacts.IsContextualKeyword(kind);
    }

    /// <summary>Turn a regex pattern into a human-readable API name for messages.</summary>
    public static string Humanize(string pattern)
    {
        // Case-sensitive type-name rules are wrapped in an inline (?-i:...) group.
        if (pattern.StartsWith("(?-i:", StringComparison.Ordinal) && pattern.EndsWith(')'))
            pattern = pattern[5..^1];
        return pattern.Replace(@"\b", "").Replace(@"\s", " ").Replace(@"\(", "(").Replace(@"\.", ".");
    }
}
