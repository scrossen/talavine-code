using System.Globalization;
using System.Net;

namespace SwarmCore.Abstractions.Networking;

/// <summary>
/// Registrable-domain ("site") computation over the Mozilla Public Suffix List,
/// including its private section (github.io, herokuapp.com, s3.amazonaws.com).
/// Use it wherever a host decides which saved credential, cookie scope or
/// browser authorization applies: a fixed list of two-label suffixes collapses
/// login.mybank.com.br into com.br and lets every site under that suffix match.
/// The list is an embedded snapshot (see the header of public_suffix_list.txt).
/// </summary>
public static class PublicSuffixList
{
    private static readonly Lazy<Rules> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true };

    /// <summary>
    /// The registrable domain (public suffix plus one label) of <paramref name="host"/>,
    /// in lower-case ASCII (punycode) form. Null for an empty or malformed host,
    /// an IP address, or a host that is itself a public suffix ("com", "com.br").
    /// </summary>
    public static string? RegistrableDomainOf(string? host)
    {
        var labels = SplitHost(host);
        if (labels is null) return null;
        var suffixLabels = PublicSuffixLabelCount(labels);
        return labels.Length > suffixLabels ? string.Join('.', labels[^(suffixLabels + 1)..]) : null;
    }

    /// <summary>
    /// True when <paramref name="host"/> is a public suffix (a TLD such as "com",
    /// or a listed suffix such as "co.uk" or "github.io") and therefore cannot
    /// identify one site. False for IP addresses and ordinary site hosts.
    /// </summary>
    public static bool IsPublicSuffix(string? host)
    {
        var labels = SplitHost(host);
        return labels is not null && PublicSuffixLabelCount(labels) >= labels.Length;
    }

    /// <summary>
    /// True when both hosts have the same non-null registrable domain.
    /// </summary>
    public static bool IsSameSite(string? host, string? otherHost)
    {
        var site = RegistrableDomainOf(host);
        return site is not null && string.Equals(site, RegistrableDomainOf(otherHost), StringComparison.Ordinal);
    }

    private static string[]? SplitHost(string? host)
    {
        var trimmed = host?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(trimmed) || IPAddress.TryParse(trimmed.Trim('[', ']'), out _))
            return null;
        string ascii;
        try
        {
            ascii = Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (Uri.CheckHostName(ascii) != UriHostNameType.Dns) return null;
        var labels = ascii.Split('.');
        return labels.Any(label => label.Length == 0) ? null : labels;
    }

    private static int PublicSuffixLabelCount(string[] labels)
    {
        var rules = Loaded.Value;
        // An exception rule prevails over every other rule; its public suffix is
        // the rule minus its leftmost label.
        for (var count = labels.Length; count >= 1; count--)
        {
            if (rules.Exceptions.Contains(string.Join('.', labels[^count..])))
                return count - 1;
        }

        // Otherwise the longest matching normal or wildcard rule wins; with no
        // match the implicit "*" rule makes the top-level label the suffix.
        var longest = 1;
        for (var count = 1; count <= labels.Length; count++)
        {
            var candidate = string.Join('.', labels[^count..]);
            if (rules.Exact.Contains(candidate))
                longest = Math.Max(longest, count);
            if (count >= 2 && rules.Wildcards.Contains(string.Join('.', labels[^(count - 1)..])))
                longest = Math.Max(longest, count);
        }
        return longest;
    }

    private static Rules Load()
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        using var stream = typeof(PublicSuffixList).Assembly.GetManifestResourceStream(
                "SwarmCore.Abstractions.Networking.public_suffix_list.txt")
            ?? throw new InvalidOperationException("The embedded public suffix list is missing.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var rule = line.Trim();
            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            if (rule.StartsWith('!'))
                Add(exceptions, rule[1..]);
            else if (rule.StartsWith("*.", StringComparison.Ordinal))
                Add(wildcards, rule[2..]);
            else
                Add(exact, rule);
        }
        return new Rules(exact, wildcards, exceptions);

        static void Add(HashSet<string> set, string rule)
        {
            try
            {
                set.Add(Idn.GetAscii(rule).ToLowerInvariant());
            }
            catch (ArgumentException)
            {
                // A rule that cannot be expressed as an ASCII host can never match one.
            }
        }
    }

    private sealed record Rules(HashSet<string> Exact, HashSet<string> Wildcards, HashSet<string> Exceptions);
}
