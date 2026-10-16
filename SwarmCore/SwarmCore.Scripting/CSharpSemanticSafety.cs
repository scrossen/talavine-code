using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SwarmCore.Scripting;

/// <summary>
/// The result of a Roslyn semantic-symbol safety pass: the human-readable symbol
/// names that matched a blocked prefix and an approval-required prefix, respectively.
/// Both are empty when the snippet is clean (or could not be analyzed).
/// </summary>
public sealed record SemanticSafetyResult(IReadOnlyList<string> BlockedSymbols, IReadOnlyList<string> ApprovalSymbols)
{
    /// <summary>An empty result (no matches) — returned when analysis is skipped or fails.</summary>
    public static readonly SemanticSafetyResult Empty =
        new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>Host choices for the semantic pass.</summary>
public sealed record SemanticSafetyOptions
{
    public static readonly SemanticSafetyOptions Default = new();

    /// <summary>
    /// Report any use of the <c>dynamic</c> type as blocked. Member access through a dynamic
    /// receiver binds at run time, so the symbol pass cannot see it; hosts whose fence depends
    /// on this pass refuse it and point authors at typed access instead.
    /// </summary>
    public bool BlockDynamicBinding { get; init; }

    /// <summary>
    /// Report any use of the <c>dynamic</c> type as approval-required instead (when
    /// <see cref="BlockDynamicBinding"/> is off). A tier that must prove code is read-only
    /// cannot, once member access is deferred to run time.
    /// </summary>
    public bool DynamicBindingRequiresApproval { get; init; }

    /// <summary>
    /// Symbol prefixes carved out of a broader blocked/approval prefix. A name is reported only
    /// when its longest matching list prefix is more specific than its longest matching
    /// exemption, so a deny-by-namespace rule can leave named value types usable.
    /// </summary>
    public IReadOnlyList<string> ExemptSymbolPrefixes { get; init; } = [];
}

/// <summary>
/// Shared Roslyn-based <em>semantic</em> safety analyzer for agent-/LLM-authored C#.
/// Where the regex pass (<see cref="CSharpRegexSafety"/>) matches text, this resolves
/// actual symbols, so it catches reflection pivots the regex misses — e.g.
/// <c>x.GetType().Assembly.GetTypes()</c> resolves to <c>System.Reflection.Assembly.GetTypes</c>
/// even though no literal "Assembly" token appears in the source. It is the single
/// implementation every Swarm app uses; apps supply their own prefix lists (composed
/// from <see cref="CSharpDangerousApis"/>) so a newly discovered bypass is fixed once.
/// </summary>
public static class CSharpSemanticSafety
{
    private static readonly SymbolDisplayFormat FullyQualifiedSymbolFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>
    /// Compile <paramref name="code"/> (wrapped so top-level statements parse), get the semantic model
    /// with <c>ignoreAccessibility: true</c>, walk every node, expand each resolved symbol to its display
    /// name(s) (including <c>ContainingType.Method</c> / <c>ContainingType.Property</c>), and match those
    /// names against <paramref name="blockedSymbolPrefixes"/> and <paramref name="approvalSymbolPrefixes"/>
    /// with a case-insensitive <c>StartsWith</c>. Parse/semantic failures are swallowed and return
    /// <see cref="SemanticSafetyResult.Empty"/> — the genuine compiler path catches real errors, and a
    /// snippet that won't even bind is not a safety signal on its own.
    /// </summary>
    public static SemanticSafetyResult Analyze(
        string code,
        IReadOnlyList<string> blockedSymbolPrefixes,
        IReadOnlyList<string> approvalSymbolPrefixes)
        => Analyze(code, blockedSymbolPrefixes, approvalSymbolPrefixes, options: null);

    /// <summary>
    /// <see cref="Analyze(string, IReadOnlyList{string}, IReadOnlyList{string})"/> with host
    /// <paramref name="options"/> (for example refusing <c>dynamic</c>).
    /// </summary>
    public static SemanticSafetyResult Analyze(
        string code,
        IReadOnlyList<string> blockedSymbolPrefixes,
        IReadOnlyList<string> approvalSymbolPrefixes,
        SemanticSafetyOptions? options)
    {
        if (string.IsNullOrWhiteSpace(code)) return SemanticSafetyResult.Empty;

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                BuildSafetyCompilationUnit(code),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
            var compilation = CSharpCompilation.Create(
                "SwarmCore.SafetyAnalysis",
                [syntaxTree],
                GetSafetyReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(false)
                    .WithNullableContextOptions(NullableContextOptions.Enable));
            return Analyze(compilation, blockedSymbolPrefixes, approvalSymbolPrefixes, options);
        }
        catch
        {
            // Parse/semantic failures are handled by the actual compiler path; never a safety signal here.
            return SemanticSafetyResult.Empty;
        }
    }

    /// <summary>
    /// Analyze a compilation the host already built — typically the real script
    /// compilation, with the host's references and typed globals — so every symbol
    /// binds exactly as it will at run time. The generic wrapper overloads declare the
    /// globals as <c>dynamic</c>, which cannot see host members; a host that owns its
    /// script type should prefer this overload. Every syntax tree in the compilation is
    /// walked. Semantic failures return <see cref="SemanticSafetyResult.Empty"/>.
    /// </summary>
    public static SemanticSafetyResult Analyze(
        Compilation compilation,
        IReadOnlyList<string> blockedSymbolPrefixes,
        IReadOnlyList<string> approvalSymbolPrefixes,
        SemanticSafetyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        blockedSymbolPrefixes ??= Array.Empty<string>();
        approvalSymbolPrefixes ??= Array.Empty<string>();
        options ??= SemanticSafetyOptions.Default;

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var approval = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
                AnalyzeTree(model, syntaxTree.GetRoot(), blockedSymbolPrefixes, approvalSymbolPrefixes, options, blocked, approval);
            }
        }
        catch
        {
            // Parse/semantic failures are handled by the actual compiler path; never a safety signal here.
            return SemanticSafetyResult.Empty;
        }

        return new SemanticSafetyResult(blocked.ToList(), approval.ToList());
    }

    private static void AnalyzeTree(
        SemanticModel model,
        SyntaxNode root,
        IReadOnlyList<string> blockedSymbolPrefixes,
        IReadOnlyList<string> approvalSymbolPrefixes,
        SemanticSafetyOptions options,
        HashSet<string> blocked,
        HashSet<string> approval)
    {
        foreach (var node in root.DescendantNodes())
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is null && node is ObjectCreationExpressionSyntax creation)
                symbol = model.GetSymbolInfo(creation.Type).Symbol;
            if (symbol is null)
                continue;

            foreach (var name in ExpandSymbolNames(symbol))
            {
                if (Matches(name, blockedSymbolPrefixes, options.ExemptSymbolPrefixes))
                    blocked.Add(name);
                // Importing a namespace does nothing by itself; approval tiers judge the
                // types and members actually used from it.
                if (symbol is not INamespaceSymbol
                    && Matches(name, approvalSymbolPrefixes, options.ExemptSymbolPrefixes))
                    approval.Add(name);
            }
        }

        // An extern method is native code however its import attribute is spelled
        // (aliased, fully qualified, or a local function in top-level statements).
        foreach (var declaration in root.DescendantNodes())
        {
            var modifiers = declaration switch
            {
                BaseMethodDeclarationSyntax method => method.Modifiers,
                LocalFunctionStatementSyntax local => local.Modifiers,
                BasePropertyDeclarationSyntax property => property.Modifiers,
                _ => default,
            };
            if (modifiers.Any(SyntaxKind.ExternKeyword))
            {
                blocked.Add("extern (native interop)");
                break;
            }
        }

        // `dynamic` defers every member binding to run time, so nothing reached
        // through a dynamic receiver can be matched above. A host that relies on
        // this pass can refuse it; typed access plus the host's own by-name call
        // facade (if any) covers the same need without blinding the analyzer.
        if (options.BlockDynamicBinding || options.DynamicBindingRequiresApproval)
        {
            foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
            {
                if (typeSyntax is IdentifierNameSyntax { Identifier.ValueText: "dynamic" }
                    && model.GetTypeInfo(typeSyntax).Type is IDynamicTypeSymbol)
                {
                    (options.BlockDynamicBinding ? blocked : approval).Add("dynamic (run-time binding)");
                    break;
                }
            }
        }

        // BindingFlags can be written as a named enum member, a compound expression, or a
        // numeric cast. Symbol-prefix matching covers the named spelling; constant evaluation
        // closes the numeric/aliased forms without banning useful public reflection.
        foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            var convertedType = model.GetTypeInfo(expression).ConvertedType;
            if (!string.Equals(
                    convertedType?.ToDisplayString(FullyQualifiedSymbolFormat),
                    "System.Reflection.BindingFlags",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var constant = model.GetConstantValue(expression);
            if (!constant.HasValue || !TryConvertToInt64(constant.Value, out var flags))
            {
                continue;
            }

            var privateBits = (long)(BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if ((flags & privateBits) != 0)
            {
                blocked.Add("System.Reflection.BindingFlags.NonPublic");
            }
        }

        // Activator's ordinary overloads create public types and belong to the mediated
        // invocation tier. Its explicit nonPublic=true overload crosses the private-state
        // boundary even though it does not use BindingFlags.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || method.Name != "CreateInstance"
                || method.ContainingType?.ToDisplayString(FullyQualifiedSymbolFormat) != "System.Activator")
            {
                continue;
            }

            for (var i = 0; i < invocation.ArgumentList.Arguments.Count && i < method.Parameters.Length; i++)
            {
                var argument = invocation.ArgumentList.Arguments[i];
                var parameter = argument.NameColon is { } nameColon
                    ? method.Parameters.FirstOrDefault(candidate => candidate.Name.Equals(
                        nameColon.Name.Identifier.ValueText,
                        StringComparison.OrdinalIgnoreCase))
                    : method.Parameters[i];
                if (parameter is null
                    || !parameter.Name.Equals("nonPublic", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var constant = model.GetConstantValue(argument.Expression);
                if (constant is { HasValue: true, Value: true })
                {
                    blocked.Add("System.Activator.CreateInstance(nonPublic: true)");
                }
            }
        }
    }

    private static bool Matches(string name, IReadOnlyList<string> prefixes, IReadOnlyList<string> exemptions)
    {
        var rule = LongestMatchingPrefix(name, prefixes);
        return rule > 0 && rule > LongestMatchingPrefix(name, exemptions);
    }

    private static int LongestMatchingPrefix(string name, IReadOnlyList<string> prefixes)
    {
        var longest = 0;
        foreach (var prefix in prefixes)
        {
            if (prefix.Length > longest && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                longest = prefix.Length;
        }
        return longest;
    }

    private static bool TryConvertToInt64(object? value, out long result)
    {
        try
        {
            result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }

    /// <summary>
    /// Every name a symbol should be matched under: its fully-qualified display name, plus
    /// <c>ContainingType.Member</c> forms for methods and properties (so a member prefix matches even
    /// when the fully-qualified form differs), plus the named-type form for type references. Methods
    /// are also named with their parameter types (<c>System.Xml.Linq.XDocument.Save(string)</c>) so a
    /// rule can single out one overload, and members of constructed generics are also named by
    /// their definition (<c>System.Linq.Expressions.Expression&lt;TDelegate&gt;.Compile</c>).
    /// </summary>
    private static IEnumerable<string> ExpandSymbolNames(ISymbol symbol)
    {
        yield return symbol.ToDisplayString(FullyQualifiedSymbolFormat);

        if (symbol is IMethodSymbol method)
        {
            foreach (var form in new[] { method, method.OriginalDefinition }.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
            {
                var containing = form.ContainingType?.ToDisplayString(FullyQualifiedSymbolFormat);
                if (string.IsNullOrWhiteSpace(containing))
                    continue;
                yield return $"{containing}.{form.Name}";
                yield return $"{containing}.{form.Name}({string.Join(",", form.Parameters.Select(p => p.Type.ToDisplayString(FullyQualifiedSymbolFormat)))})";
            }
        }

        if (symbol is IPropertySymbol property)
        {
            var containing = property.ContainingType?.ToDisplayString(FullyQualifiedSymbolFormat);
            if (!string.IsNullOrWhiteSpace(containing))
                yield return $"{containing}.{property.Name}";
        }

        if (symbol is INamedTypeSymbol namedType)
            yield return namedType.ToDisplayString(FullyQualifiedSymbolFormat);
    }

    /// <summary>
    /// Wrap a script body (top-level statements) in an async method so it parses and binds, hoisting any
    /// <c>using</c> directives to the file scope. The common globals an authored tool sees (Input/Output/
    /// Web and friends) are declared as <c>dynamic</c> parameters so member access against them binds
    /// without needing the host's globals type referenced here.
    /// </summary>
    private static string BuildSafetyCompilationUnit(string code)
    {
        // Let the parser find the leading using *directives*; using *statements*
        // (`using var x = ...;`, `using (var w = ...) { }`) stay in the body with the
        // locals they declare, so the analyzed program is the one that runs.
        var script = CSharpSyntaxTree.ParseText(
            code, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithKind(SourceCodeKind.Script));
        var directives = script.GetCompilationUnitRoot().Usings;
        var split = directives.Count == 0 ? 0 : directives.Last().FullSpan.End;
        var usings = new[] { code[..split] };
        var body = new[] { code[split..] };

        return """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using System.Net;
        using System.Net.Http;
        using System.Net.Sockets;
        using System.Reflection;
        using System.Text;
        using System.Text.RegularExpressions;
        using System.Threading;
        using System.Threading.Tasks;

        """ + string.Join(Environment.NewLine, usings) + """

        public static class __SafetyWrapper
        {
            public static async Task<object?> Run(dynamic Input, dynamic Output, dynamic Web, CancellationToken ct)
            {
        """ + Indent(string.Join(Environment.NewLine, body), 8) + """

                return null;
            }
        }
        """;
    }

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Split('\n').Select(line => prefix + line));
    }

    /// <summary>
    /// The common BCL reference set needed to bind the analyzed snippet. Mirrors the set the regex/symbol
    /// rules describe (object/Console/Enumerable/Task/File/HttpClient/Socket/StringBuilder/Regex plus the
    /// runtime facade assemblies). Missing platform assemblies are tolerated — binding is best-effort.
    /// </summary>
    private static IReadOnlyList<MetadataReference> GetSafetyReferences()
    {
        var assemblies = new HashSet<Assembly>
        {
            typeof(object).Assembly,
            typeof(Console).Assembly,
            typeof(Enumerable).Assembly,
            typeof(System.Threading.Tasks.Task).Assembly,
            typeof(File).Assembly,
            typeof(System.Net.Http.HttpClient).Assembly,
            typeof(System.Net.Sockets.Socket).Assembly,
            typeof(StringBuilder).Assembly,
            typeof(Regex).Assembly,
            typeof(System.Diagnostics.Process).Assembly,   // so Process.Start binds (alias/pivot detection)
        };

        foreach (var assemblyName in new[]
                 {
                     "System.Runtime",
                     "System.Collections",
                     "System.Linq",
                     "System.Net.Primitives",
                     "System.Private.CoreLib",
                     "Microsoft.CSharp",
                     "netstandard",
                 })
        {
            try
            {
                assemblies.Add(Assembly.Load(assemblyName));
            }
            catch
            {
                // Runtime assemblies vary by platform.
            }
        }

        // RuntimeMetadataReferences falls back to raw in-memory metadata where Assembly.Location
        // is empty (Android/iOS embedded assemblies, single-file publishes) — filtering on
        // Location here would silently drop every reference on mobile and leave this safety
        // pass binding against nothing, i.e. failing open.
        return assemblies
            .Select(RuntimeMetadataReferences.TryCreate)
            .OfType<MetadataReference>()
            .Distinct()
            .ToList();
    }
}
