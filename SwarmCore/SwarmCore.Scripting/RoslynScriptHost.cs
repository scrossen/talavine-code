using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SwarmCore.Scripting;

public sealed record RoslynScriptResult(bool Ok, object? Value, string? Error);

/// <summary>
/// A reusable Roslyn compile/run host for agent-authored C# scripts. Configured
/// once with the assembly references and imports a host wants to expose, then
/// reused to compile-check and run scripts (with a hard timeout) against a
/// globals object. This is the shared mechanism; safety policy is applied by the
/// caller (see <see cref="CSharpDangerousApis"/>).
///
/// Deliberately NOT built on the <c>CSharpScript</c> API: Roslyn scripting resolves
/// the corlib and globals references from assembly file paths, which don't exist on
/// Android/iOS (assemblies are embedded in the app bundle, <see cref="Assembly.Location"/>
/// is empty) or in single-file publishes. Instead the script is compiled directly with
/// <see cref="CSharpCompilation.CreateScriptCompilation"/> against references built by
/// <see cref="RuntimeMetadataReferences"/> (raw in-memory metadata when there's no file),
/// emitted to memory, and invoked through the synthesized script entry point — the same
/// pipeline the scripting API uses underneath, minus the file-path assumptions.
/// </summary>
public sealed class RoslynScriptHost
{
    /// <summary>
    /// Successful compiles cached by source text — Roslyn's compile cost is
    /// hundreds of ms, and hosts re-run the same authored script on every
    /// panel refresh / tool call. Bounded crudely: an authoring loop that
    /// churns sources clears the cache instead of growing without limit.
    /// (Assemblies already emitted and loaded stay loaded — same trade-off
    /// the scripting API's interactive loader makes.)
    /// </summary>
    private const int MaxCachedScripts = 128;

    private static readonly CSharpParseOptions ScriptParseOptions =
        CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Latest);

    /// <summary>Emitted script assemblies need unique names; collisions confuse loader + stack traces.</summary>
    private static int s_assemblyCounter;

    private sealed class CompiledScript
    {
        public required CSharpCompilation Compilation { get; init; }
        public Func<object?[], Task<object?>>? Runner; // emit+load deferred to first run
    }

    private readonly ConcurrentDictionary<string, CompiledScript> _compiled = new(StringComparer.Ordinal);

    private readonly Type _globalsType;

    /// <summary>
    /// Reference/option materialization is deferred so constructing a host can never throw —
    /// hosts live in static initializers (e.g. CSharpServiceAdapter), where a platform quirk
    /// in reference resolution must surface as a per-script compile error, not a
    /// TypeInitializationException that takes the adapter type down for the process lifetime.
    /// </summary>
    private readonly Lazy<(ImmutableArray<MetadataReference> References, CSharpCompilationOptions Options)> _setup;

    public RoslynScriptHost(Type globalsType, IEnumerable<Assembly> references, IEnumerable<string> imports)
    {
        _globalsType = globalsType;
        var assemblies = references.Append(globalsType.Assembly).Distinct().ToArray();
        var usings = imports.ToArray();
        _setup = new(() => (
            assemblies.Select(a => (MetadataReference)RuntimeMetadataReferences.Create(a)).Distinct().ToImmutableArray(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: usings,
                optimizationLevel: OptimizationLevel.Release,
                metadataReferenceResolver: LoadedAssemblyMissingResolver.Instance)));
    }

    /// <summary>Compile-check only. Returns (ok, errorText).</summary>
    public (bool Ok, string? Errors) Compile(string code)
    {
        var (script, errors) = GetOrCompile(code);
        return script is not null ? (true, null) : (false, errors);
    }

    /// <summary>Compile and run the script against <paramref name="globals"/>, bounded by <paramref name="timeout"/>.</summary>
    public async Task<RoslynScriptResult> RunAsync(string code, object globals, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var (script, errors) = GetOrCompile(code);
            if (script is null)
                return new RoslynScriptResult(false, null, "Compilation error:\n" + errors);

            var runner = script.Runner;
            if (runner is null)
            {
                lock (script)
                {
                    runner = script.Runner ??= CreateRunner(script.Compilation);
                }
            }

            // Race the run against the wall clock. The token cancels the script
            // at await points, but a pure CPU loop never observes it — the race
            // lets us return a timeout anyway (the orphaned task leaks until
            // process exit, which we accept over hanging the caller).
            var runTask = runner(new object?[] { globals, null }); // [0] globals, [1] submission instance slot
            var winner = await Task.WhenAny(runTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (winner != runTask)
                return new RoslynScriptResult(false, null, "Timed out.");

            var value = await runTask.ConfigureAwait(false);
            return new RoslynScriptResult(true, value, null);
        }
        catch (OperationCanceledException)
        {
            return new RoslynScriptResult(false, null, "Timed out.");
        }
        catch (Exception ex)
        {
            return new RoslynScriptResult(false, null, "Runtime error: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    private (CompiledScript? Script, string? Errors) GetOrCompile(string code)
    {
        if (_compiled.TryGetValue(code, out var cached))
            return (cached, null);

        try
        {
            var (references, options) = _setup.Value;
            var tree = CSharpSyntaxTree.ParseText(code, ScriptParseOptions);
            var compilation = CSharpCompilation.CreateScriptCompilation(
                "SwarmScript" + Interlocked.Increment(ref s_assemblyCounter),
                tree, references, options,
                returnType: typeof(object), globalsType: _globalsType);
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();
            if (errors.Count > 0)
                return (null, string.Join("\n", errors));

            if (_compiled.Count >= MaxCachedScripts) _compiled.Clear();
            return (_compiled.GetOrAdd(code, new CompiledScript { Compilation = compilation }), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Emit the compilation to memory, load it, and bind the synthesized script entry point
    /// (the factory Roslyn generates for script compilations: <c>Task&lt;object&gt; f(object[] submissionStates)</c>,
    /// where slot 0 is the globals instance). Loading emitted IL requires JIT or interpreter —
    /// fine on Android and desktop; iOS needs the Mono interpreter enabled.
    /// </summary>
    private static Func<object?[], Task<object?>> CreateRunner(CSharpCompilation compilation)
    {
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success)
            throw new InvalidOperationException(string.Join("; ",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())));
        pe.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(pe);

        var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
            ?? throw new InvalidOperationException("Script compilation produced no entry point.");
        var typeName = string.IsNullOrEmpty(entryPoint.ContainingNamespace.MetadataName)
            ? entryPoint.ContainingType.MetadataName
            : entryPoint.ContainingNamespace.MetadataName + "." + entryPoint.ContainingType.MetadataName;
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(entryPoint.MetadataName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Script entry point '{entryPoint.MetadataName}' not found on '{typeName}'.");
        return (Func<object?[], Task<object?>>)method.CreateDelegate(typeof(Func<object?[], Task<object?>>));
    }
}
