using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Reads the context window an Ollama daemon applies to a model's next request.
/// The window is a runtime setting, not a model fact: the runner is started
/// with the num_ctx from the model's Modelfile or the daemon default
/// (OLLAMA_CONTEXT_LENGTH), while /api/show's model_info
/// "&lt;architecture&gt;.context_length" is only the trained maximum and is
/// routinely many times larger. Unless a request forbids truncation, a prompt
/// longer than the window is not rejected — Ollama drops its oldest messages —
/// so a host sending through the OpenAI-compatible endpoint has to learn the
/// real window to compact before sending. Native tool turns forbid truncation
/// and grow the window instead (see <c>OllamaLlmProvider.SendNativeToolChatAsync</c>).
/// </summary>
internal static partial class OllamaRuntimeContextWindow
{
    // Native metadata endpoints answer from memory in milliseconds; a slow or
    // unreachable daemon must not hold a user-waiting turn for long.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private const string OpenAiCompatibleCompletionsPath = "/v1/chat/completions";

    /// <summary>
    /// The native API root serving an OpenAI-compatible completions URL, keeping
    /// any reverse-proxy path prefix in front of <c>/v1/chat/completions</c>.
    /// </summary>
    public static Uri? ApiRootFor(string chatCompletionsUrl)
    {
        if (!Uri.TryCreate(chatCompletionsUrl, UriKind.Absolute, out var completions))
        {
            return null;
        }

        var path = completions.AbsolutePath.TrimEnd('/');
        var prefix = path.EndsWith(OpenAiCompatibleCompletionsPath, StringComparison.OrdinalIgnoreCase)
            ? path[..^OpenAiCompatibleCompletionsPath.Length]
            : string.Empty;
        return new UriBuilder(completions) { Path = prefix + "/", Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    /// <summary>
    /// The loaded runner's <c>/api/ps</c> context_length is authoritative. A model
    /// that is not loaded is next loaded with its Modelfile num_ctx (never above
    /// the trained maximum) when it declares one; otherwise the daemon default
    /// applies, which the API does not expose, so the window stays unknown until
    /// the first request loads the model.
    /// </summary>
    public static async Task<int?> ReadAsync(
        IHttpClientFactory httpClientFactory,
        Uri apiRoot,
        string model,
        ILogger logger,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var http = httpClientFactory.CreateClient();
            using (var loaded = await http.GetAsync(new Uri(apiRoot, "api/ps"), timeout.Token).ConfigureAwait(false))
            {
                if (loaded.IsSuccessStatusCode
                    && ParseLoadedContextLength(
                        await loaded.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false),
                        model) is int running)
                {
                    return running;
                }
            }

            using var request = new StringContent(
                JsonSerializer.Serialize(new { model }),
                Encoding.UTF8,
                "application/json");
            using var metadata = await http.PostAsync(new Uri(apiRoot, "api/show"), request, timeout.Token)
                .ConfigureAwait(false);
            return metadata.IsSuccessStatusCode
                ? ParseConfiguredContextLength(
                    await metadata.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Ollama at {Endpoint} did not report a context window for {Model}.", apiRoot, model);
            return null;
        }
    }

    /// <summary>
    /// The context_length of the loaded runner for <paramref name="model"/> in an
    /// <c>/api/ps</c> body, or null when that model is not loaded. An omitted tag
    /// means <c>:latest</c>, as Ollama resolves it.
    /// </summary>
    internal static int? ParseLoadedContextLength(string psJson, string model)
    {
        using var document = JsonDocument.Parse(psJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var wanted = LlmRouter.NormalizeOllamaModelTag(model);
        int? window = null;
        foreach (var runner in models.EnumerateArray())
        {
            if (runner.ValueKind != JsonValueKind.Object
                || !Names(runner).Any(name => string.Equals(
                    LlmRouter.NormalizeOllamaModelTag(name), wanted, StringComparison.OrdinalIgnoreCase))
                || !runner.TryGetProperty("context_length", out var length)
                || length.ValueKind != JsonValueKind.Number
                || !length.TryGetInt32(out var tokens)
                || tokens <= 0)
            {
                continue;
            }

            window = window is int seen ? Math.Min(seen, tokens) : tokens;
        }

        return window;
    }

    /// <summary>
    /// The num_ctx a model's Modelfile sets in an <c>/api/show</c> body, capped at
    /// the trained maximum the runner never exceeds; null when the Modelfile
    /// leaves the window to the daemon default.
    /// </summary>
    internal static int? ParseConfiguredContextLength(string showJson)
    {
        using var document = JsonDocument.Parse(showJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.String
            || NumCtxParameter().Match(parameters.GetString() ?? string.Empty) is not { Success: true } declared
            || !int.TryParse(declared.Groups["tokens"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var configured)
            || configured <= 0)
        {
            return null;
        }

        if (root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in info.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var trained)
                    && trained > 0)
                {
                    configured = Math.Min(configured, trained);
                }
            }
        }

        return configured;
    }

    /// <summary>
    /// Every loaded runner's context_length in an <c>/api/ps</c> body, keyed by
    /// model name. Empty when nothing is loaded or the body is not recognized.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> ParseLoadedContextLengths(string psJson)
    {
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(psJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return windows;
        }

        foreach (var runner in models.EnumerateArray())
        {
            if (runner.ValueKind != JsonValueKind.Object
                || !runner.TryGetProperty("context_length", out var length)
                || length.ValueKind != JsonValueKind.Number
                || !length.TryGetInt32(out var tokens)
                || tokens <= 0)
            {
                continue;
            }

            foreach (var name in Names(runner))
            {
                var key = LlmRouter.NormalizeOllamaModelTag(name);
                windows[key] = windows.TryGetValue(key, out var seen) ? Math.Min(seen, tokens) : tokens;
            }
        }

        return windows;
    }

    private static IEnumerable<string> Names(JsonElement runner)
    {
        foreach (var property in new[] { "name", "model" })
        {
            if (runner.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } name)
            {
                yield return name;
            }
        }
    }

    [GeneratedRegex(@"^\s*num_ctx\s+(?<tokens>\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NumCtxParameter();
}
