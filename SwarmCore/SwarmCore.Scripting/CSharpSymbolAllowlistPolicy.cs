using System.Net;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SwarmCore.Scripting;

/// <summary>
/// The explicit allow-list a hosted, read-only script tier compiles against. Everything not listed is
/// refused by <see cref="CSharpSymbolAllowlist"/>: there is no "dangerous API" list to keep current,
/// so a new runtime API is unreachable until a host deliberately allows it.
///
/// Types are listed in three ways. A <em>member type</em> may appear anywhere and all of its accessible
/// members may be used (minus <see cref="DenyMember"/> entries). An <em>opaque type</em> may flow through
/// code (as the type of an expression, local or lambda conversion) but none of its members may be called
/// unless listed with <see cref="AllowMember"/>. A <em>deferred query type</em> (IQueryable and friends) is
/// opaque and may only live in single-assignment top-level variables, so a script cannot grow an
/// unbounded expression tree in a loop or through recursion. Host types (a tenant-scoped data facade,
/// the globals object, the entities it exposes) are added the same way.
/// </summary>
public sealed class CSharpSymbolAllowlistPolicy
{
    private readonly Dictionary<string, string> _memberTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _opaqueTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deferredQueryTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _memberNamespaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedMembers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deniedMembers = new(StringComparer.Ordinal);

    /// <summary>
    /// Scripts nested deeper than this are refused before binding. Deeply nested expressions are not
    /// written by people or models; they are how runtime visitors (expression-tree translators, the
    /// compiler) are driven into stack exhaustion.
    /// </summary>
    public int MaxSyntaxDepth { get; init; } = 160;

    /// <summary>
    /// The safe baseline for read-only analysis scripts: primitives, strings, dates, math, tuples,
    /// delegates for lambdas, in-memory collections, LINQ, <see cref="StringBuilder"/>, invariant
    /// formatting, HTML/URL encoding, and awaiting tasks. No I/O, environment, network, reflection,
    /// threads, process, interop, code loading or global state.
    /// </summary>
    public static CSharpSymbolAllowlistPolicy ReadOnlyBaseline()
    {
        var policy = new CSharpSymbolAllowlistPolicy();

        policy.AllowMembersOf(
            typeof(object), typeof(string), typeof(char), typeof(bool),
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal),
            typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
            typeof(Guid), typeof(Math), typeof(MathF), typeof(Nullable<>), typeof(Nullable), typeof(Enum),
            typeof(Convert), typeof(StringComparison), typeof(StringComparer), typeof(StringSplitOptions),
            typeof(MidpointRounding), typeof(DayOfWeek), typeof(DateTimeKind), typeof(Index), typeof(Range),
            typeof(Array), typeof(Random), typeof(IComparable), typeof(IComparable<>), typeof(IEquatable<>),
            typeof(IFormattable), typeof(IFormatProvider), typeof(IDisposable),
            typeof(Exception), typeof(ArgumentException), typeof(ArgumentNullException),
            typeof(ArgumentOutOfRangeException), typeof(InvalidOperationException), typeof(NotSupportedException),
            typeof(FormatException), typeof(OverflowException), typeof(DivideByZeroException),
            typeof(NullReferenceException), typeof(IndexOutOfRangeException),
            typeof(StringBuilder),
            typeof(System.Globalization.NumberStyles), typeof(System.Globalization.DateTimeStyles),
            typeof(System.Globalization.CompareOptions),
            typeof(Enumerable), typeof(Queryable), typeof(IGrouping<,>), typeof(ILookup<,>), typeof(Lookup<,>));

        policy.AllowMembersOf(GenericFamily(typeof(Tuple), "System.Tuple`", 8));
        policy.AllowMembersOf(GenericFamily(typeof(ValueTuple), "System.ValueTuple`", 8));
        var delegates = GenericFamily(typeof(Func<>), "System.Func`", 17)
            .Concat(GenericFamily(typeof(Action), "System.Action`", 16))
            .Concat([typeof(Predicate<>), typeof(Comparison<>), typeof(Converter<,>)])
            .ToArray();
        policy.AllowMembersOf(delegates);
        foreach (var type in delegates)
        {
            // Asynchronous delegate invocation is the one delegate-declared surface beyond Invoke.
            policy.DenyMember(type, "BeginInvoke", "EndInvoke");
        }

        // In-memory collections only: List, Dictionary, HashSet, queues, comparers and their enumerators.
        policy.AllowMembersInNamespace("System.Collections.Generic", typeof(List<>), typeof(SortedSet<>));

        // Reflection, process-wide state and server detail reachable from otherwise ordinary types.
        policy.DenyMember(typeof(object), nameof(GetType));
        policy.DenyMember(typeof(Exception), nameof(GetType), nameof(Exception.StackTrace), nameof(Exception.TargetSite), nameof(Exception.Data));
        policy.DenyMember(typeof(Queryable), nameof(Queryable.AsQueryable));

        policy.AllowTypes(
            typeof(System.Globalization.CultureInfo),
            typeof(System.Linq.Expressions.Expression<>),
            typeof(Task), typeof(Task<>), typeof(ValueTask), typeof(ValueTask<>),
            typeof(CancellationToken),
            typeof(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler),
            typeof(StringBuilder.AppendInterpolatedStringHandler),
            typeof(IOrderedEnumerable<>));
        policy.AllowMember(typeof(System.Globalization.CultureInfo),
            nameof(System.Globalization.CultureInfo.InvariantCulture),
            nameof(System.Globalization.CultureInfo.GetCultureInfo),
            nameof(System.Globalization.CultureInfo.Name));
        policy.AllowMember(typeof(Task), nameof(Task.FromResult), nameof(Task.CompletedTask), nameof(Task.WhenAll));
        policy.AllowMember(typeof(CancellationToken),
            nameof(CancellationToken.IsCancellationRequested),
            nameof(CancellationToken.ThrowIfCancellationRequested),
            nameof(CancellationToken.CanBeCanceled));
        policy.AllowMember(typeof(WebUtility), nameof(WebUtility.HtmlEncode), nameof(WebUtility.UrlEncode));

        policy.TreatAsDeferredQuery(typeof(IQueryable<>), typeof(IQueryable), typeof(IOrderedQueryable<>), typeof(IOrderedQueryable));
        return policy;
    }

    /// <summary>Allow every accessible member of these types (generic definitions for generic types).</summary>
    public CSharpSymbolAllowlistPolicy AllowMembersOf(params Type[] types)
    {
        foreach (var type in types) _memberTypes[Key(type)] = AssemblyName(type);
        return this;
    }

    /// <summary>
    /// Allow every type a namespace holds in the assemblies of <paramref name="assemblyMarkers"/> as a member
    /// type (for pure in-memory collection namespaces). Other assemblies that reuse the namespace gain nothing.
    /// </summary>
    public CSharpSymbolAllowlistPolicy AllowMembersInNamespace(string @namespace, params Type[] assemblyMarkers)
    {
        if (!_memberNamespaces.TryGetValue(@namespace, out var assemblies))
        {
            _memberNamespaces[@namespace] = assemblies = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var marker in assemblyMarkers) assemblies.Add(AssemblyName(marker));
        return this;
    }

    /// <summary>Allow these types to flow through code without allowing their members.</summary>
    public CSharpSymbolAllowlistPolicy AllowTypes(params Type[] types)
    {
        foreach (var type in types) _opaqueTypes[Key(type)] = AssemblyName(type);
        return this;
    }

    /// <summary>
    /// Allow specific members (all overloads) of a type that is otherwise opaque or unlisted. The type itself
    /// becomes opaque: a static member such as <c>EF.Functions</c> cannot be reached without naming its type,
    /// and naming it grants nothing beyond the members listed here.
    /// </summary>
    public CSharpSymbolAllowlistPolicy AllowMember(Type type, params string[] members)
    {
        _opaqueTypes[Key(type)] = AssemblyName(type);
        foreach (var member in members) _allowedMembers.Add(MemberKey(Key(type), member));
        return this;
    }

    /// <summary>Refuse specific members (all overloads) even on a member type.</summary>
    public CSharpSymbolAllowlistPolicy DenyMember(Type type, params string[] members)
    {
        foreach (var member in members) _deniedMembers.Add(MemberKey(Key(type), member));
        return this;
    }

    /// <summary>
    /// Deferred query types are opaque, and their values may only be held by top-level variables that are
    /// assigned once. They cannot be method/lambda parameters, returned from script methods, stored inside
    /// other values, or reassigned.
    /// </summary>
    public CSharpSymbolAllowlistPolicy TreatAsDeferredQuery(params Type[] types)
    {
        foreach (var type in types)
        {
            _deferredQueryTypes[Key(type)] = AssemblyName(type);
            _opaqueTypes[Key(type)] = AssemblyName(type);
        }
        return this;
    }

    internal bool IsMemberType(INamedTypeSymbol type)
        => Matches(_memberTypes, type)
           || (type.OriginalDefinition.ContainingNamespace is { IsGlobalNamespace: false } ns
               && _memberNamespaces.TryGetValue(ns.ToDisplayString(), out var assemblies)
               && type.OriginalDefinition.ContainingAssembly is { } assembly
               && assemblies.Contains(assembly.Identity.Name));

    internal bool IsOpaqueType(INamedTypeSymbol type) => Matches(_opaqueTypes, type);

    internal bool IsDeferredQueryType(INamedTypeSymbol type) => Matches(_deferredQueryTypes, type);

    internal bool IsAllowedMember(INamedTypeSymbol containingType, string member)
        => _allowedMembers.Contains(MemberKey(Key(containingType), member));

    internal bool IsDeniedMember(INamedTypeSymbol containingType, string member)
        => _deniedMembers.Contains(MemberKey(Key(containingType), member));

    private static bool Matches(Dictionary<string, string> set, INamedTypeSymbol type)
        => set.TryGetValue(Key(type), out var assembly)
           && string.Equals(type.OriginalDefinition.ContainingAssembly?.Identity.Name, assembly, StringComparison.Ordinal);

    private static IEnumerable<Type> GenericFamilyTypes(Type nonGeneric, string prefix, int maxArity)
    {
        yield return nonGeneric;
        for (var arity = 1; arity <= maxArity; arity++)
        {
            if (nonGeneric.Assembly.GetType(prefix + arity) is { } type) yield return type;
        }
    }

    private static Type[] GenericFamily(Type nonGeneric, string prefix, int maxArity)
        => GenericFamilyTypes(nonGeneric, prefix, maxArity).Distinct().ToArray();

    private static string MemberKey(string typeKey, string member) => typeKey + "::" + member;

    private static string AssemblyName(Type type) => type.Assembly.GetName().Name ?? string.Empty;

    /// <summary>The CLR metadata name of a type definition: <c>System.Collections.Generic.List`1</c>, <c>Outer+Inner</c>.</summary>
    internal static string Key(Type type)
    {
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.FullName ?? definition.Name;
    }

    /// <summary>The same metadata name for a Roslyn type symbol.</summary>
    internal static string Key(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        var name = definition.MetadataName;
        for (var outer = definition.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "+" + name;
        }

        return definition.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + name
            : name;
    }
}
