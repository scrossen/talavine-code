using Microsoft.CodeAnalysis;

namespace SwarmCore.Scripting;

/// <summary>
/// What an agent-authored C# program is allowed to do, decided before it compiles.
/// <see cref="ReadOnly"/> code computes, reads files, and prints; <see cref="ReadWrite"/>
/// code also writes files or talks to the network; <see cref="Blocked"/> code reaches for
/// process control, code loading, private reflection, native interop, or raw sockets and
/// never runs anywhere.
/// </summary>
public enum CSharpSafetyTier
{
    ReadOnly = 0,
    ReadWrite = 1,
    Blocked = 2,
}

public sealed record CSharpSafetyVerdict(CSharpSafetyTier Tier, IReadOnlyList<string> Reasons)
{
    public bool IsBlocked => Tier == CSharpSafetyTier.Blocked;

    /// <summary>True when code at this tier may run under a host that permits <paramref name="allowed"/>.</summary>
    public bool FitsWithin(CSharpSafetyTier allowed) => !IsBlocked && Tier <= allowed;
}

/// <summary>
/// The generic three-tier classifier every Swarm app can share (Talavine adds its own
/// repository-specific rules on top of the same lists). It is a policy gate, not a
/// sandbox: the tier says whether the host should run the code at all, and with which
/// peer's authority; the operating system account still bounds what the code can touch.
/// </summary>
public static class CSharpSafetyTiers
{
    public const string ReadOnlyName = "read-only";
    public const string ReadWriteName = "read-write";

    private static readonly string[] ApprovalPatterns =
    [
        .. CSharpDangerousApis.WriteAndNetworkPatterns,
        .. CSharpDangerousApis.ReflectionInvocationPatterns,
    ];

    private static readonly string[] ApprovalSymbolPrefixes =
    [
        .. CSharpDangerousApis.WriteAndNetworkSymbolPrefixes,
        .. CSharpDangerousApis.ReflectionInvocationSymbolPrefixes,
        .. CSharpDangerousApis.NetworkNamespaceSymbolPrefixes,
    ];

    // The read-only tier is what a merely paired peer gets, so it has to be provable:
    // networking is denied by namespace (value types excepted), and run-time binding,
    // which hides every member it reaches, needs the read-write tier.
    private static readonly SemanticSafetyOptions SemanticOptions = new()
    {
        DynamicBindingRequiresApproval = true,
        ExemptSymbolPrefixes = CSharpDangerousApis.NetworkValueSymbolPrefixes,
    };

    public static string Name(CSharpSafetyTier tier) => tier switch
    {
        CSharpSafetyTier.ReadOnly => ReadOnlyName,
        CSharpSafetyTier.ReadWrite => ReadWriteName,
        _ => "blocked",
    };

    public static bool TryParse(string? name, out CSharpSafetyTier tier)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case ReadOnlyName:
            case "readonly":
            case "read":
                tier = CSharpSafetyTier.ReadOnly; return true;
            case ReadWriteName:
            case "readwrite":
            case "write":
            case "full":
                tier = CSharpSafetyTier.ReadWrite; return true;
            default:
                tier = CSharpSafetyTier.ReadOnly; return false;
        }
    }

    /// <summary>
    /// Classify one or more source files as a unit, compiled exactly as
    /// <see cref="CSharpProgramRunner"/> would compile them; the strictest finding wins.
    /// </summary>
    public static CSharpSafetyVerdict Classify(IEnumerable<string> sources)
        => Classify(CSharpProgramRunner.CreateCompilation(new CSharpProgramRequest
        {
            Sources = sources.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
        }));

    public static CSharpSafetyVerdict Classify(string source) => Classify([source]);

    /// <summary>
    /// Classify the program the host will actually emit. The semantic pass reads the symbols
    /// this compilation binds to (top-level statements and full <c>Main</c> programs alike,
    /// with the real reference set), and the textual pass is the backstop over every tree.
    /// </summary>
    public static CSharpSafetyVerdict Classify(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var blocked = new List<string>();
        var approval = new List<string>();
        var sources = compilation.SyntaxTrees.Select(tree => tree.ToString()).ToList();

        foreach (var source in sources)
        {
            foreach (var pattern in CSharpRegexSafety.MatchingPatterns(source, CSharpDangerousApis.BlockedPatterns))
                blocked.Add("blocked API: " + CSharpRegexSafety.Humanize(pattern));
        }

        // Semantic pass catches aliases, using-directive spellings, and pivots the regexes miss.
        var symbols = CSharpSemanticSafety.Analyze(compilation, CSharpDangerousApis.BlockedSymbolPrefixes, ApprovalSymbolPrefixes, SemanticOptions);
        blocked.AddRange(symbols.BlockedSymbols.Select(s => "blocked symbol: " + s));
        if (blocked.Count > 0)
            return new CSharpSafetyVerdict(CSharpSafetyTier.Blocked, blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var source in sources)
        {
            foreach (var pattern in CSharpRegexSafety.MatchingPatterns(source, ApprovalPatterns))
                approval.Add("writes or networks: " + CSharpRegexSafety.Humanize(pattern));
        }
        approval.AddRange(symbols.ApprovalSymbols.Select(s => "writes or networks: " + s));

        if (approval.Count > 0)
            return new CSharpSafetyVerdict(CSharpSafetyTier.ReadWrite, approval.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return new CSharpSafetyVerdict(CSharpSafetyTier.ReadOnly, Array.Empty<string>());
    }
}
