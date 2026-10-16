using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SwarmCore.Scripting;

/// <summary>
/// One request to compile and run an agent-authored C# program: the sources (top-level
/// statements or a <c>Main</c>), plain arguments, and the safety tier the host permits.
/// The runner also generates a <c>Swarm</c> helper class from the context fields so the
/// program can find its artifact folder and announce files without touching environment
/// variables (which the safety policy blocks).
/// </summary>
public sealed record CSharpProgramRequest
{
    public int ProtocolVersion { get; init; } = CSharpProgramRunner.ProtocolVersion;
    public string AssemblyName { get; init; } = "SwarmCSharp";
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary><see cref="CSharpSafetyTiers.ReadOnlyName"/> or <see cref="CSharpSafetyTiers.ReadWriteName"/>.</summary>
    public string AllowedTier { get; init; } = CSharpSafetyTiers.ReadOnlyName;
    /// <summary>
    /// Hard deadline in seconds, or 0 (the default) for none. With no deadline the host that
    /// launched this process decides when to stop it, from whether the program is still
    /// producing output or using CPU, rather than from an arbitrary clock.
    /// </summary>
    public int TimeoutSeconds { get; init; }

    public string? WorkId { get; init; }
    public string? ArtifactDirectory { get; init; }
    public string? PeerDisplayName { get; init; }
    public string? ScriptName { get; init; }
}

/// <summary>
/// Compiles a C# program with Roslyn against the running runtime's assemblies and runs it
/// in a collectible load context, streaming its console output to the given writers. Meant
/// to run inside a helper process (SwarmSpan.Mcp <c>--run-csharp</c>, or an equivalent) so
/// a misbehaving program can be killed without touching the app that asked for it.
/// Exit codes: 0 ok, 2 compile error, 3 runtime exception, 4 blocked by policy,
/// 5 needs a higher tier than allowed, 64 protocol error, 124 timed out; a program's own
/// non-zero <c>Main</c> return is passed through.
/// </summary>
public static class CSharpProgramRunner
{
    public const int ProtocolVersion = 1;
    public const int ExitCompileFailed = 2;
    public const int ExitRuntimeFailed = 3;
    public const int ExitBlocked = 4;
    public const int ExitTierExceeded = 5;
    public const int ExitProtocol = 64;
    public const int ExitTimedOut = 124;
    public const int MaxRequestCharacters = 2_000_000;

    public const string ArtifactLinePrefix = "SWARMSPAN_ARTIFACT=";
    public const string ProgressLinePrefix = "SWARMSPAN_PROGRESS=";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(CSharpProgramRequest request, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        if (request.ProtocolVersion != ProtocolVersion)
        {
            await stderr.WriteLineAsync($"[csharp] unsupported protocol version {request.ProtocolVersion}.").ConfigureAwait(false);
            return ExitProtocol;
        }
        var sources = request.Sources.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (sources.Count == 0)
        {
            await stderr.WriteLineAsync("[csharp] no source was provided.").ConfigureAwait(false);
            return ExitProtocol;
        }
        if (!CSharpSafetyTiers.TryParse(request.AllowedTier, out var allowed))
            allowed = CSharpSafetyTier.ReadOnly;

        // Classify the compilation that is about to be emitted, not a re-parse of the text,
        // so a full Main program binds exactly as it will run.
        var compilation = CreateCompilation(request with { Sources = sources });
        var verdict = CSharpSafetyTiers.Classify(compilation);
        if (verdict.IsBlocked)
        {
            await stderr.WriteLineAsync("[csharp] blocked by safety policy: " + string.Join("; ", verdict.Reasons)).ConfigureAwait(false);
            await stderr.WriteLineAsync("[csharp] process control, code loading, private reflection, native interop, registry, environment access, and raw sockets never run through this lane. Use a repo job for those.").ConfigureAwait(false);
            return ExitBlocked;
        }
        if (!verdict.FitsWithin(allowed))
        {
            await stderr.WriteLineAsync($"[csharp] this program needs the {CSharpSafetyTiers.Name(verdict.Tier)} tier but this device allows {CSharpSafetyTiers.Name(allowed)} for the requesting peer: {string.Join("; ", verdict.Reasons)}").ConfigureAwait(false);
            await stderr.WriteLineAsync("[csharp] the receiving device grants read-write C# to peers marked 'Always allow' (trusted) in its SwarmSpan pairing list.").ConfigureAwait(false);
            return ExitTierExceeded;
        }
        await stderr.WriteLineAsync($"[csharp] policy: {CSharpSafetyTiers.Name(verdict.Tier)} program, {CSharpSafetyTiers.Name(allowed)} allowed").ConfigureAwait(false);

        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success)
        {
            await stderr.WriteLineAsync("[csharp] compilation failed:").ConfigureAwait(false);
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                await stderr.WriteLineAsync("  " + d).ConfigureAwait(false);
            return ExitCompileFailed;
        }

        pe.Position = 0;
        var context = new AssemblyLoadContext($"SwarmCSharp.{Guid.NewGuid():N}", isCollectible: true);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var assembly = context.LoadFromStream(pe);
            var entry = assembly.EntryPoint ?? throw new MissingMethodException("The program has no entry point (add top-level statements or a Main method).");
            var args = request.Arguments.ToArray();
            var parameters = entry.GetParameters();
            var invokeArgs = parameters.Length == 0 ? null : new object?[] { args };

            var run = Task.Run(async () =>
            {
                var result = entry.Invoke(null, invokeArgs);
                return result switch
                {
                    Task<int> t => await t.ConfigureAwait(false),
                    Task t => await Complete(t).ConfigureAwait(false),
                    int code => code,
                    _ => 0,
                };
            }, ct);

            if (request.TimeoutSeconds > 0)
            {
                var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds);
                if (await Task.WhenAny(run, Task.Delay(timeout, ct)).ConfigureAwait(false) != run)
                {
                    await stderr.WriteLineAsync($"[csharp] timed out after {timeout.TotalSeconds:F0}s.").ConfigureAwait(false);
                    return ExitTimedOut;
                }
            }
            var exit = await run.ConfigureAwait(false);
            await stdout.FlushAsync(ct).ConfigureAwait(false);
            return exit;
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            await stderr.WriteLineAsync("[csharp] runtime error: " + actual).ConfigureAwait(false);
            return ExitRuntimeFailed;
        }
        finally
        {
            try { stdout.Flush(); stderr.Flush(); } catch { }
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            try { context.Unload(); } catch { }
        }

        static async Task<int> Complete(Task t) { await t.ConfigureAwait(false); return 0; }
    }

    /// <summary>
    /// The compilation <see cref="RunAsync"/> emits for <paramref name="request"/>: its sources
    /// plus the generated <c>Swarm</c> helper, against the runner's fixed framework reference set.
    /// <see cref="CSharpSafetyTiers"/> classifies this same compilation.
    /// </summary>
    public static CSharpCompilation CreateCompilation(CSharpProgramRequest request)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = request.Sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"source{i + 1}.cs"))
            .ToList();
        trees.Add(CSharpSyntaxTree.ParseText(BuildSwarmHelperSource(request), parseOptions, path: "Swarm.g.cs"));

        return CSharpCompilation.Create(
            SanitizeAssemblyName(request.AssemblyName),
            trees,
            ProgramReferences.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    /// <summary>
    /// The <c>Swarm</c> class every program can use: <c>Swarm.Args</c>, <c>Swarm.ArtifactDir</c>,
    /// <c>Swarm.Artifact(path)</c> to announce an output file, <c>Swarm.Progress(note)</c> to report
    /// progress to the caller's ledger, plus the work id and the requesting peer's name.
    /// </summary>
    public static string BuildSwarmHelperSource(CSharpProgramRequest request)
    {
        static string Lit(string? value) => SymbolDisplay.FormatLiteral(value ?? string.Empty, quote: true);
        var args = string.Join(", ", request.Arguments.Select(a => Lit(a)));
        // CompilationOptions.Usings only applies to script compilations; a regular program
        // gets its implicit namespaces as global using directives in this generated file.
        return $$"""
            // Generated by SwarmSpan for this run. Not part of the classified program.
            global using System;
            global using System.IO;
            global using System.Linq;
            global using System.Collections.Generic;
            global using System.Text;
            global using System.Threading;
            global using System.Threading.Tasks;

            public static class Swarm
            {
                public static readonly string WorkId = {{Lit(request.WorkId)}};
                public static readonly string ArtifactDir = {{Lit(request.ArtifactDirectory)}};
                public static readonly string PeerDisplayName = {{Lit(request.PeerDisplayName)}};
                public static readonly string ScriptName = {{Lit(request.ScriptName)}};
                public static readonly string[] Args = new string[] { {{args}} };

                /// <summary>Full path for a file inside this run's Agent Handoff folder.</summary>
                public static string ArtifactPath(string fileName)
                    => global::System.IO.Path.Combine(ArtifactDir, fileName);

                /// <summary>Announce an output file; SwarmSpan stages it and reports its swarm path to the caller.</summary>
                public static string Artifact(string path)
                {
                    var full = global::System.IO.Path.IsPathRooted(path) ? path : ArtifactPath(path);
                    global::System.Console.WriteLine({{Lit(ArtifactLinePrefix)}} + full);
                    return full;
                }

                /// <summary>Report progress to the device that assigned this work.</summary>
                public static void Progress(string note)
                    => global::System.Console.WriteLine({{Lit(ProgressLinePrefix)}} + note);
            }
            """;
    }

    private static readonly Lazy<IReadOnlyList<MetadataReference>> ProgramReferences = new(BuildReferences);

    /// <summary>
    /// The framework assemblies a program compiles against. This is a fixed list on purpose,
    /// not whatever the runner process happens to have loaded: the runner's own assemblies
    /// (this one, Roslyn, the host app) would hand a read-only program a nested runner, a
    /// compiler that writes files, and the host's own capabilities, none of which the safety
    /// lists describe.
    /// </summary>
    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var assemblies = new HashSet<Assembly>
        {
            typeof(object).Assembly,
            typeof(Console).Assembly,
            typeof(Enumerable).Assembly,
            typeof(List<>).Assembly,
            typeof(StringBuilder).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(System.Text.RegularExpressions.Regex).Assembly,
            typeof(Task).Assembly,
            typeof(File).Assembly,
            typeof(Uri).Assembly,
            typeof(System.Net.Http.HttpClient).Assembly,
            typeof(System.Xml.Linq.XDocument).Assembly,
            typeof(System.Xml.XmlReader).Assembly,
            typeof(System.Diagnostics.Stopwatch).Assembly,
            typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly,
            typeof(System.Collections.Immutable.ImmutableArray).Assembly,
            typeof(System.Diagnostics.FileVersionInfo).Assembly,
            typeof(System.Security.Cryptography.SHA256).Assembly,
            typeof(System.Net.Http.Json.HttpClientJsonExtensions).Assembly,
            typeof(System.Numerics.BigInteger).Assembly,
        };
        foreach (var name in new[]
                 {
                     "System.Runtime", "System.Collections", "System.Linq", "System.Console", "System.IO.FileSystem",
                     "System.Text.Json", "System.Text.RegularExpressions", "System.Threading.Tasks", "System.Net.Http",
                     "System.Net.Primitives", "System.Private.CoreLib", "System.Private.Uri", "System.Private.Xml",
                     "System.Private.Xml.Linq", "System.Xml.ReaderWriter", "System.Xml.XDocument",
                     "System.ComponentModel.Primitives", "System.Collections.Concurrent", "System.Collections.Immutable",
                     "System.Collections.NonGeneric", "System.Collections.Specialized", "System.Memory",
                     "System.Runtime.InteropServices", "System.Runtime.InteropServices.RuntimeInformation", "System.IO.FileSystem.DriveInfo",
                     "System.Text.Encoding.Extensions", "System.Threading", "System.Threading.Thread", "System.Threading.Channels",
                     "System.Diagnostics.Process", "System.Security.Cryptography", "System.Security.Cryptography.Algorithms",
                     "System.Linq.Expressions", "System.Linq.Parallel", "System.ObjectModel", "System.Globalization",
                     "System.IO.Compression", "System.IO.Compression.ZipFile", "System.Diagnostics.FileVersionInfo",
                     "System.Net.Http.Json", "System.Runtime.Extensions", "System.Runtime.Numerics", "System.Data.Common",
                     "System.Web.HttpUtility", "System.Text.Encodings.Web", "Microsoft.CSharp", "netstandard",
                 })
        {
            try { assemblies.Add(Assembly.Load(name)); }
            catch { /* facades vary by runtime */ }
        }

        return assemblies
            .Where(a => !a.IsDynamic)
            .Select(RuntimeMetadataReferences.TryCreate)
            .OfType<PortableExecutableReference>()
            .DistinctBy(r => r.Display)
            .ToArray();
    }

    private static string SanitizeAssemblyName(string? name)
    {
        var sanitized = new string((name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? $"SwarmCSharp.{Guid.NewGuid():N}" : sanitized;
    }
}
