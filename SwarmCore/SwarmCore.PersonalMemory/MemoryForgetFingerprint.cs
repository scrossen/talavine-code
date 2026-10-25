using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.PersonalMemory;

/// <summary>
/// What a forget rule keeps instead of the forgotten words: a hash of the whole normalized statement plus short
/// hashes of its distinctive terms. That is enough to refuse relearning the same statement (any key) or a statement
/// that contains every forgotten term (same key, or any key when the rule has none), without storing readable text.
/// Single common words can still be guessed against a word list; the goal is that the forgotten wording is not
/// sitting in the database, not cryptographic secrecy.
/// </summary>
public static class MemoryForgetFingerprint
{
    private const string Prefix = "fp1:";

    public static bool IsFingerprint(string? pattern) => pattern?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Version prefix of <see cref="Statement"/>; a stored value without it predates the current normalization.</summary>
    public const string StatementPrefix = "st1:";

    /// <summary>
    /// Index key for "the same statement": a hash of the normalized text. Stores may persist it beside a
    /// fact or claim so a forget can find every copy of a statement without reading all of them.
    /// </summary>
    public static string Statement(string? text) => StatementPrefix + Full(text ?? string.Empty);

    public static string Create(string text)
    {
        var terms = MemoryRelevanceRanker.DistinctiveTerms(text).Select(Short).Distinct().Order(StringComparer.Ordinal);
        return Prefix + Full(text) + "|" + string.Join(",", terms);
    }

    /// <summary>The same statement (normalized), regardless of key.</summary>
    public static bool SameStatement(string fingerprint, string text)
        => Parse(fingerprint) is { } parsed && parsed.Full == Full(text);

    /// <summary>The same statement, or one that contains every distinctive term of the forgotten one.</summary>
    public static bool Covers(string fingerprint, string text)
    {
        if (Parse(fingerprint) is not { } parsed) return false;
        if (parsed.Full == Full(text)) return true;
        if (parsed.Terms.Count == 0) return false;
        var candidate = MemoryRelevanceRanker.DistinctiveTerms(text).Select(Short).ToHashSet(StringComparer.Ordinal);
        return parsed.Terms.All(candidate.Contains);
    }

    private static (string Full, IReadOnlyList<string> Terms)? Parse(string fingerprint)
    {
        if (!IsFingerprint(fingerprint)) return null;
        var body = fingerprint[Prefix.Length..];
        var bar = body.IndexOf('|');
        if (bar < 0) return null;
        var terms = body[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        return (body[..bar], terms);
    }

    private static string Full(string text) => Hash(MemoryTextScreen.NormalizeForComparison(text), 32);

    private static string Short(string term) => Hash(term, 12);

    private static string Hash(string value, int hexChars)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..hexChars];
}
