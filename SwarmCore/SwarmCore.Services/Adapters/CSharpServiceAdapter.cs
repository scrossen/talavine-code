using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using SwarmCore.Scripting;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Scripting;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Invokes a C# service: the script in <c>Config["source"]</c> is run through the
/// shared Roslyn host against <see cref="ServiceCodeGlobals"/> (Input, Web,
/// Files, Progress, and Cancellation), with a transparent host-capability policy and
/// a renewable no-progress lease instead of a total wall-clock timeout. This
/// is where an agent's authored tools live as catalog services.
/// </summary>
public sealed class CSharpServiceAdapter : IServiceKindAdapter
{
    public const string PolicyId = "service-csharp-v2";
    public static readonly TimeSpan DefaultNoProgressTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] Blocked =
        CSharpDangerousApis.BlockedPatterns
            .Concat(CSharpDangerousApis.WriteAndNetworkPatterns)
            .Concat(CSharpDangerousApis.ReflectionInvocationPatterns)
            .ToArray();

    private static readonly RoslynScriptHost Host = new(
        typeof(ServiceCodeGlobals),
        references: new[]
        {
            typeof(object).Assembly, typeof(Console).Assembly, typeof(Enumerable).Assembly,
            typeof(System.Threading.Tasks.Task).Assembly, typeof(System.Text.StringBuilder).Assembly,
            typeof(System.Text.RegularExpressions.Regex).Assembly, typeof(List<>).Assembly,
            typeof(System.Globalization.CultureInfo).Assembly, typeof(JsonSerializer).Assembly,
            // Dynamic input/member calls need this at emit time even when validation
            // succeeds. Do not depend on another host feature loading the binder first.
            typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
            typeof(ServiceCodeGlobals).Assembly,
        },
        imports: new[]
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks",
            "System.Threading", "System.Text", "System.Text.Json", "System.Text.RegularExpressions", "System.Globalization",
        });

    private readonly IServiceHttpClient _http;
    private readonly ICredentialStore? _credentials;
    private readonly IServiceFileWorkspace? _files;

    public CSharpServiceAdapter(
        IServiceHttpClient http,
        ICredentialStore? credentials = null,
        IServiceFileWorkspace? files = null)
    {
        _http = http;
        _credentials = credentials;
        _files = files;
    }

    public ServiceKind Kind => ServiceKind.CSharp;

    // The verdict is a pure function of the source text, and every call of an unchanged service
    // would otherwise pay a full semantic pass plus a compile. Bounded like the script host's cache.
    private const int MaxCachedValidations = 128;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Ok, string? Error)> Validations = new(StringComparer.Ordinal);

    /// <summary>Compile-check a script (used when adding a C# service so errors come back immediately).</summary>
    public static (bool Ok, string? Error) Validate(string source)
    {
        source ??= "";
        if (Validations.TryGetValue(source, out var cached)) return cached;
        var verdict = ValidateUncached(source);
        if (Validations.Count >= MaxCachedValidations) Validations.Clear();
        Validations[source] = verdict;
        return verdict;
    }

    private static (bool Ok, string? Error) ValidateUncached(string source)
    {
        var regexHits = CSharpRegexSafety.MatchingPatterns(source, Blocked);
        var semantic = CSharpSemanticSafety.Analyze(
            source,
            CSharpDangerousApis.BlockedSymbolPrefixes
                .Concat(CSharpDangerousApis.WriteAndNetworkSymbolPrefixes)
                .Concat(CSharpDangerousApis.ReflectionInvocationSymbolPrefixes)
                .ToArray(),
            []);
        var hit = regexHits.Select(CSharpRegexSafety.Humanize)
            .Concat(semantic.BlockedSymbols)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(hit))
        {
            return (false,
                $"Blocked by {PolicyId}: '{hit}' bypasses the task-scoped service receipt. "
                + "Use Input for call data, Web for credentialed/audited HTTP, Files for observable filesystem work, "
                + "Progress for long batches, and a named host capability for process/database work.");
        }

        var instrumented = CSharpLoopCancellation.Inject(source);
        return Host.Compile(instrumented);
    }

    /// <summary>Structural policy for autonomous pure-compute services.</summary>
    public static (bool Ok, string? Error) ValidatePureCompute(string source)
    {
        var validation = Validate(source);
        if (!validation.Ok) return validation;
        var forbidden = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTokens()
            .Where(token => token.RawKind == (int)SyntaxKind.IdentifierToken)
            .Select(token => token.ValueText)
            .FirstOrDefault(identifier => identifier.Equals("Web", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("Files", StringComparison.OrdinalIgnoreCase)
                || identifier.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        return forbidden is null
            ? (true, null)
            : (false, $"Pure-compute C# cannot access host channel '{forbidden}'.");
    }

    public async Task<ServiceCallResult> InvokeAsync(ServiceDefinition s, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("service.csharp", s.Name, ct);
        var source = s.Config.GetValueOrDefault("source");
        if (string.IsNullOrWhiteSpace(source)) return ServiceCallResult.Fail("Service has no source.");
        var validation = Validate(source!);
        if (!validation.Ok)
            return ServiceCallResult.Fail(validation.Error ?? $"Blocked by {PolicyId}.");

        var noProgressTimeout = ResolveNoProgressTimeout(s.Config);
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new ServiceExecutionProgress();

        var globals = new ServiceCodeGlobals
        {
            Input = new Dictionary<string, object?>(args),
            Web = new ScriptWeb(
                _http,
                executionCts.Token,
                _credentials,
                s.CredentialRef,
                activity: progress.Observe),
            Files = new ScriptFiles(_files, executionCts.Token, progress.Observe),
            Progress = progress,
            Cancellation = executionCts.Token,
        };
        var instrumented = CSharpLoopCancellation.Inject(source!);
        var runTask = Host.RunAsync(
            instrumented,
            globals,
            Timeout.InfiniteTimeSpan,
            executionCts.Token);
        // Compilation/emit occurs synchronously before Roslyn's runner reaches
        // the injected initial yield. It is setup work, not a stalled service.
        progress.Observe("script started");

        RoslynScriptResult result;
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(noProgressTimeout.TotalMilliseconds / 4, 25, 1_000));
        while (true)
        {
            var completed = await Task.WhenAny(runTask, Task.Delay(poll, ct)).ConfigureAwait(false);
            if (completed == runTask)
            {
                result = await runTask.ConfigureAwait(false);
                break;
            }

            if (DateTime.UtcNow - progress.LastProgressUtc < noProgressTimeout)
            {
                continue;
            }

            executionCts.Cancel();
            await runTask.ConfigureAwait(false);
            return ServiceCallResult.Fail(
                $"Stopped after {noProgressTimeout.TotalSeconds:0.###} seconds without observable progress. "
                + $"Last progress: {progress.Describe()}. This is not a total runtime limit: for long loops "
                + "call Progress.Report(completed, total, message) after each item or checkpoint, then revise/test the service.");
        }

        if (!result.Ok) return ServiceCallResult.Fail(result.Error ?? "failed");

        try { return ServiceCallResult.Success(JsonSerializer.Serialize(result.Value)); }
        catch { return ServiceCallResult.Success(result.Value?.ToString()); }
    }

    private static TimeSpan ResolveNoProgressTimeout(IReadOnlyDictionary<string, string> config)
    {
        if (!config.TryGetValue("noProgressTimeoutSeconds", out var raw)
            || !double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds))
        {
            return DefaultNoProgressTimeout;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.05, 3_600));
    }
}
