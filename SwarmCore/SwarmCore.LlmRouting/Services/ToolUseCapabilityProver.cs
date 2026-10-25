using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// <see cref="IToolUseCapabilityProver"/> implemented as a single, tiny,
/// real tool-use turn against the route in question.
/// <para>
/// Design notes:
/// - Lazy. Probing only happens when a route is about to be used and its
///   capability is unknown, never as a startup sweep across every model.
/// - Cached per provider/model, so a route costs at most one probe per process.
/// - Single-flight: concurrent askers for the same route await one probe.
/// - Inconclusive results are not cached as capability verdicts, so a provider
///   being briefly down does not permanently blacklist a model that works.
/// - A provider that answers "model not found" is a special inconclusive
///   result: it is remembered for <see cref="MissingModelRetryInterval"/> so
///   routing does not re-send the same doomed probe on every decision, and it
///   is surfaced through <see cref="GetLastProbeFailureCategory"/> so the
///   router can stop treating the route as "evidence pending".
/// </para>
/// </summary>
public sealed class ToolUseCapabilityProver : IToolUseCapabilityProver
{
    // Deliberately short. The probe asks for one trivial call; a model that
    // needs longer than this to emit it is not a viable agent route anyway,
    // and the caller is a user waiting on a turn.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long a "model not installed" answer is trusted before the route is
    /// probed again. Long enough that a retry storm cannot hammer the provider,
    /// short enough that an <c>ollama pull</c> is noticed without a restart.
    /// </summary>
    internal static readonly TimeSpan MissingModelRetryInterval = TimeSpan.FromMinutes(5);

    private readonly IToolUseProviderRegistryAccessor _providers;
    private readonly ILogger<ToolUseCapabilityProver> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, bool> _verdicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool?>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProbeFailure> _failures = new(StringComparer.OrdinalIgnoreCase);

    public ToolUseCapabilityProver(
        IToolUseProviderRegistryAccessor providers,
        ILogger<ToolUseCapabilityProver> logger)
        : this(providers, logger, static () => DateTime.UtcNow)
    {
    }

    internal ToolUseCapabilityProver(
        IToolUseProviderRegistryAccessor providers,
        ILogger<ToolUseCapabilityProver> logger,
        Func<DateTime> utcNow)
    {
        _providers = providers;
        _logger = logger;
        _utcNow = utcNow;
    }

    private static string Key(string provider, string model) => $"{provider}|{model}";

    public bool? GetCachedVerdict(string provider, string model)
        => _verdicts.TryGetValue(Key(provider, model), out var v) ? v : null;

    public LlmFailureCategory? GetLastProbeFailureCategory(string provider, string model)
    {
        var key = Key(provider, model);
        if (!_failures.TryGetValue(key, out var failure))
        {
            return null;
        }

        if (failure.ExpiresAtUtc <= _utcNow())
        {
            _failures.TryRemove(new KeyValuePair<string, ProbeFailure>(key, failure));
            return null;
        }

        return failure.Category;
    }

    public void RecordSuccessfulToolCall(string provider, string model)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var key = Key(provider, model);
        _verdicts[key] = true;
        _failures.TryRemove(key, out _);
    }

    public Task<bool?> ProveToolUseAsync(string provider, string model, CancellationToken ct = default)
    {
        var key = Key(provider, model);
        if (_verdicts.TryGetValue(key, out var cached))
        {
            return Task.FromResult<bool?>(cached);
        }

        // A fresh "model not installed" answer is authoritative for a while.
        // Re-sending the probe would only produce the same 404 and, under a
        // retry loop, a log storm.
        if (GetLastProbeFailureCategory(provider, model) is not null)
        {
            return Task.FromResult<bool?>(null);
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<bool?>(ct);
        }

        // Single-flight: a turn that fans out across candidate routes should not
        // fire the same probe several times concurrently. GetOrAdd may run its
        // factory on several threads, so the factory only builds a Lazy; the
        // probe starts once, when the stored Lazy is first read. The probe is
        // owned by no single caller: it runs under its own timeout and each
        // caller waits with its own token, so one caller's cancellation neither
        // aborts the shared probe nor reaches the others. The in-flight entry is
        // released by a continuation once the probe completes, so a probe that
        // fails synchronously still leaves no stale entry behind.
        Lazy<Task<bool?>>? created = null;
        var entry = _inFlight.GetOrAdd(key, _ => created = new Lazy<Task<bool?>>(
            () => ProbeAsync(provider, model),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var probe = entry.Value;
        if (ReferenceEquals(entry, created))
        {
            _ = probe.ContinueWith(
                _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<bool?>>>(key, entry)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return probe.WaitAsync(ct);
    }

    private async Task<bool?> ProbeAsync(string provider, string model)
    {
        var key = Key(provider, model);
        try
        {
            var toolUse = _providers.Resolve(provider);
            if (toolUse is null)
            {
                return null; // no translator — a different gate's problem, not a capability verdict
            }

            using var timeout = new CancellationTokenSource(ProbeTimeout);

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["city"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("city")
            };

            var request = new ChatToolUseRequest
            {
                SystemPrompt =
                    "You are being checked for tool-calling support. Call the provided tool. "
                    + "Do not answer in prose.",
                Messages =
                [
                    ChatMessage.Text(ChatRole.User,
                        "Use the echo_city tool with city set to \"Austin\". Call the tool; do not reply with text.")
                ],
                Tools =
                [
                    new ChatToolDefinition(
                        "echo_city",
                        "Echo back the city name you are given. Call this tool.",
                        schema)
                ],
                Options = new LlmRouterOptions
                {
                    OverrideProvider = provider,
                    OverrideModel = model,
                    RequiresToolUse = true,
                }
            };

            var response = await toolUse.ChatAsync(request, timeout.Token).ConfigureAwait(false);
            var calledTool = response.ToolCalls.Count > 0;

            _verdicts[key] = calledTool;
            _failures.TryRemove(key, out _);
            if (calledTool)
            {
                _logger.LogInformation(
                    "Tool-use probe: {Provider}/{Model} emitted a tool call — capability proven, route admitted.",
                    provider, model);
            }
            else
            {
                _logger.LogWarning(
                    "Tool-use probe: {Provider}/{Model} answered without calling the tool — recorded as not tool-capable. Replied: {Excerpt}",
                    provider, model, Excerpt(response.Text));
            }

            return calledTool;
        }
        catch (OperationCanceledException)
        {
            // Timed out rather than refused. Inconclusive, so do NOT cache.
            _logger.LogWarning(
                "Tool-use probe for {Provider}/{Model} timed out after {Seconds}s; leaving capability unknown.",
                provider, model, ProbeTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (LlmErrorPolicy.Classify(ex) is LlmFailureCategory.ModelNotFound)
        {
            // The provider answered, and its answer is that the model does not
            // exist there. That is not a capability verdict (the model might
            // call tools perfectly well once pulled) but it is also not a
            // question worth asking again every 250ms. Remember it for a
            // bounded interval and let the router treat the route as
            // unavailable rather than "evidence pending".
            _failures[key] = new ProbeFailure(
                LlmFailureCategory.ModelNotFound,
                _utcNow() + MissingModelRetryInterval);
            _logger.LogWarning(
                "Tool-use probe for {Provider}/{Model}: the provider reports the model is not installed ({Error}). The route is treated as unavailable for {Minutes} minutes; pull the model or let Model Scout pick another.",
                provider, model, Excerpt(ex.Message), MissingModelRetryInterval.TotalMinutes);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Tool-use probe for {Provider}/{Model} could not complete ({Error}); leaving capability unknown.",
                provider, model, ex.Message);
            return null;
        }
    }

    private static string Excerpt(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? "(no text)"
            : text.Length <= 120 ? text : text[..120] + "…";

    private sealed record ProbeFailure(LlmFailureCategory Category, DateTime ExpiresAtUtc);
}

/// <summary>
/// Narrow seam over the tool-use provider registry so the prover does not take
/// a dependency on the routing assembly's concrete registry type.
/// </summary>
public interface IToolUseProviderRegistryAccessor
{
    IToolUseProvider? Resolve(string providerName);
}
