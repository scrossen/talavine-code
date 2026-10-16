using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ToolUse;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Native tool use. A tool turn is one <c>/api/chat</c> request on this
/// provider's node, sent under the same window policy as its text and vision
/// requests: truncation is forbidden, a window this provider granted the model
/// is carried, and a turn the runtime reports does not fit is re-sent once,
/// unchanged, at a window that holds it. Ollama's OpenAI-compatible
/// <c>/v1</c> endpoint ignores num_ctx and truncate, so a tool turn sent there
/// reloaded a grown runner at the default window and had whatever did not fit
/// silently cut.
/// </summary>
public partial class OllamaLlmProvider
{
    private OllamaNativeToolUseProvider? _toolUseProvider;

    // Whether each node's runtime serves an over-window request whole (it
    // rejects a request that forbids truncation instead of cutting it). Read
    // from the runtime's version; an overflow report proves it until the node
    // gets a different server.
    private readonly ConcurrentDictionary<string, (bool ServesWhole, DateTime ExpiresAtUtc)> _nodeServesWhole =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RuntimeVersionLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan UnknownRuntimeVersionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tool use bound to this instance: its node, its window grants, and its
    /// LocalOnly locality proof.
    /// </summary>
    public IToolUseProvider ToolUseProvider => _toolUseProvider ??= new OllamaNativeToolUseProvider(this, _logger);

    /// <summary>
    /// Sends one tool turn and returns the runtime's accepted response with its
    /// headers read; the caller reads the body, a stream of chunks when the
    /// turn streams. A turn no allowed window holds fails as a typed
    /// <see cref="LlmFailureCategory.ContextOverflow"/> without truncated
    /// inference. Any other refusal is an <see cref="HttpRequestException"/>
    /// carrying the runtime's status.
    /// </summary>
    internal async Task<OllamaNativeToolChat> SendNativeToolChatAsync(
        OllamaNativeToolChatRequest turn,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var physicalBudget = LlmReservedExecutionSettings.GetPhysicalRequestBudget(turn.Settings);
        if (physicalBudget?.Exhausted == true)
            throw new InvalidOperationException("Physical inference request limit exhausted; no tool turn retry was submitted.");
        var node = SelectToolUseNode();
        if (turn.Privacy == LlmPrivacyClass.LocalOnly)
        {
            await EnsureLocalOnlyToolUseAsync(node, turn.Model, ct).ConfigureAwait(false);
        }

        if (turn.ExpectedArtifactDigest is { } expectedDigest)
        {
            var installed = await GetInstalledArtifactDigestAsync(node, turn.Model, ct, forceRefresh: true)
                .ConfigureAwait(false);
            if (!installed.Success
                || !string.Equals(
                    EmbeddingModelIdentity.NormalizeDigest(expectedDigest),
                    EmbeddingModelIdentity.NormalizeDigest(installed.Digest),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Ollama text artifact mismatch for '{turn.Model}': expected {expectedDigest}, observed {installed.Digest ?? "missing"}. Inference was blocked.");
            }
        }

        var contextWindowResized = false;
        while (true)
        {
            var (chat, payload) = BuildNativeToolChat(turn);
            var client = _httpClientFactory.CreateClient();
            // The caller's token (the engine's turn deadline) is the only
            // deadline: loading a cold model and evaluating a long tool turn
            // routinely outlast any fixed HTTP timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
            HttpResponseMessage? response = null;
            try
            {
                if (physicalBudget is not null && !physicalBudget.TryConsume())
                    throw new InvalidOperationException("Physical inference request limit exhausted; no tool turn retry was submitted.");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{node.Endpoint}/api/chat")
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var accepted = new OllamaNativeToolChat(client, response);
                    client = null;
                    response = null;
                    return accepted;
                }

                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (physicalBudget?.Exhausted == true)
                    throw new HttpRequestException(BuildApiError(response.StatusCode, content), null, response.StatusCode);
                var overflow = await ResolveContextOverflowAsync(
                        node, chat, turn.SizingText, turn.ImageCount, response.StatusCode, content,
                        contextWindowResized, ct)
                    .ConfigureAwait(false);
                if (overflow.Retry)
                {
                    contextWindowResized = true;
                    continue;
                }

                if (overflow.Error is { } contextError)
                {
                    throw new LlmProviderHttpException(
                        contextError, response.StatusCode, LlmFailureCategory.ContextOverflow);
                }

                var call = turn.Stream ? "streaming" : "tool-use";
                var body = TrimApiErrorContent(content, 500);
                _logger.LogWarning(
                    "{Provider} {Call} call failed with {Status}: {Body}",
                    ProviderName, call, (int)response.StatusCode, body);
                throw new HttpRequestException(
                    $"{ProviderName} {call} call failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
                    inner: null,
                    response.StatusCode);
            }
            finally
            {
                response?.Dispose();
                client?.Dispose();
            }
        }
    }

    /// <summary>
    /// The window the node's runtime serves the model at now: a loaded runner's
    /// <c>/api/ps</c> context_length, else the Modelfile num_ctx. Null when the
    /// runtime does not say.
    /// </summary>
    internal Task<int?> ReadRuntimeContextWindowAsync(string model, CancellationToken ct)
        => Uri.TryCreate(SelectToolUseNode().Endpoint + "/", UriKind.Absolute, out var apiRoot)
            ? OllamaRuntimeContextWindow.ReadAsync(_httpClientFactory, apiRoot, model, _logger, ct)
            : Task.FromResult<int?>(null);

    /// <summary>
    /// The largest window this provider serves the model at on the node: the
    /// window it already granted, or the most <see cref="OllamaContextWindowSizing.Plan"/>
    /// would grow it to. Null while growth is blocked after a grown window
    /// failed to load, when the node's runtime cuts an over-window prompt
    /// instead of reporting it (older than 0.12.6 and never seen to reject
    /// one), and when neither window is known, since the model's attention
    /// shape or installed size is then unreported. A known window is recorded
    /// for Model Scout. The probes get the budget of any request metadata and
    /// fail soft.
    /// </summary>
    internal async Task<int?> GetMaximumContextWindowAsync(string model, CancellationToken ct)
    {
        if (IsContextWindowGrowthBlocked(model))
        {
            return null;
        }

        var node = SelectToolUseNode();
        if (!await NodeServesWholeAsync(node, ct).ConfigureAwait(false))
        {
            return null;
        }

        int? granted = TryGetContextWindowGrant(model, out var window) ? window : null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestMetadataTimeout);
        int? planned = null;
        try
        {
            var metadata = await TryGetModelMetadataAsync(node, model, budget.Token, RequestMetadataTimeout)
                .ConfigureAwait(false);
            if (OllamaContextWindowSizing.ReadProfile(metadata?.ModelInfo) is { } profile)
            {
                var artifactBytes = await GetInstalledArtifactBytesAsync(node, model, budget.Token)
                    .ConfigureAwait(false);
                planned = OllamaContextWindowSizing.MaximumWindow(profile, artifactBytes);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Ollama on {Node} did not describe {Model} in time to size its largest window", node.Name, model);
        }

        var largest = granted is int kept && (planned is not int most || kept > most) ? kept : planned;
        if (largest is int maximum)
        {
            _contextWindows?.RecordMaximumWindow(ProviderName, model, maximum);
        }

        return largest;
    }

    /// <summary>
    /// True when the node's runtime serves an over-window request whole: its
    /// version accepts truncate (0.12.6 and later), or it has already rejected
    /// a request that did not fit. An older runtime ignores truncate and cuts
    /// the prompt, so its window cannot be relied on to grow.
    /// </summary>
    private async Task<bool> NodeServesWholeAsync(OllamaNodeState node, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_nodeServesWhole.TryGetValue(node.Endpoint, out var known) && known.ExpiresAtUtc > now)
        {
            return known.ServesWhole;
        }

        var version = await TryGetRuntimeVersionAsync(node, ct).ConfigureAwait(false);
        var servesWhole = OllamaContextWindowSizing.RuntimeServesWhole(version);
        var reading = (servesWhole, now + (version is null ? UnknownRuntimeVersionLifetime : RuntimeVersionLifetime));
        // An overflow report recorded meanwhile is proof and outranks the version.
        var recorded = _nodeServesWhole.AddOrUpdate(
            node.Endpoint,
            reading,
            (_, existing) => existing.ServesWhole && existing.ExpiresAtUtc == DateTime.MaxValue ? existing : reading);
        return recorded.ServesWhole;
    }

    /// <summary>
    /// The node's runtime reported that a request did not fit instead of
    /// cutting it, so it serves over-window requests whole whatever its version
    /// says, until the node gets a different server.
    /// </summary>
    private void RecordRuntimeServesWhole(OllamaNodeState node)
        => _nodeServesWhole[node.Endpoint] = (true, DateTime.MaxValue);

    private async Task<string?> TryGetRuntimeVersionAsync(OllamaNodeState node, CancellationToken ct)
    {
        try
        {
            using var client = CreateHttpClient(RequestMetadataTimeout);
            using var response = await client.GetAsync($"{node.Endpoint}/api/version", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return body.RootElement.ValueKind == JsonValueKind.Object
                   && body.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Ollama on {Node} did not report its version", node.Name);
            return null;
        }
    }

    /// <summary>
    /// The node a tool turn goes to: the one a text request would be offered
    /// first. The turn does not take that node's request slot. Tool turns never
    /// held it, and a long turn's prompt evaluation would time out text
    /// requests queued behind it; this machine's runtime lease and the router's
    /// LAN load tracker already pace both kinds of work.
    /// </summary>
    private OllamaNodeState SelectToolUseNode()
    {
        var ordered = GetOrderedNodesForRouting(includeCoolingDownNodes: false);
        return ordered.Count > 0 ? ordered[0] : GetOrderedNodesForRouting(includeCoolingDownNodes: true)[0];
    }

    /// <summary>
    /// A LocalOnly tool turn must reach a local endpoint and a model that
    /// executes there. Both are proved before any prompt-bearing request, and a
    /// proof the daemon does not answer blocks the turn.
    /// </summary>
    private async Task EnsureLocalOnlyToolUseAsync(OllamaNodeState node, string model, CancellationToken ct)
    {
        var endpoint = new LlmProviderSettings
        {
            ProviderName = ProviderName,
            Endpoint = node.Endpoint,
            IsLocalProvider = true,
        };
        if (!LlmProviderLocalityPolicy.IsTrustedLocalRoute(ProviderName, model, endpoint))
        {
            throw new InvalidOperationException(
                $"Route {ProviderName}/{model} cannot satisfy LocalOnly privacy at this tool-use endpoint.");
        }

        if (await GetLocalOnlyRejectionAsync(node, model, ct).ConfigureAwait(false) is { } rejection)
        {
            throw new InvalidOperationException(rejection);
        }
    }

    /// <summary>
    /// One attempt's wire body. Its options come from the same mapping as a
    /// native text request, and its window from this provider's grant, so the
    /// attempt after a resize carries the grown window.
    /// </summary>
    private (OllamaChatRequest Chat, JsonObject Payload) BuildNativeToolChat(OllamaNativeToolChatRequest turn)
    {
        var chat = new OllamaChatRequest { Model = turn.Model };
        ApplyExecutionSettings(chat, turn.Settings);
        chat.Stream = turn.Stream;
        ApplyContextWindow(chat);

        var payload = JsonSerializer.SerializeToNode(chat)!.AsObject();
        payload["messages"] = turn.Messages.DeepClone();
        if (turn.Tools is { Count: > 0 } tools)
        {
            payload["tools"] = tools.DeepClone();
        }

        if (turn.Think is { } think)
        {
            payload["think"] = think.DeepClone();
        }

        return (chat, payload);
    }
}

/// <summary>One tool turn for <see cref="OllamaLlmProvider.SendNativeToolChatAsync"/>.</summary>
/// <param name="Messages">Native chat messages, the system prompt included.</param>
/// <param name="Tools">Native tool definitions; null or empty for none.</param>
/// <param name="Think">The think value to send (false, or a reasoning level); null leaves the model's default.</param>
/// <param name="Settings">Execution-contract wire settings, applied as for a native text request.</param>
/// <param name="SizingText">What the model reads, for sizing a window when the runtime reports no token count.</param>
/// <param name="ImageCount">Images in the turn, charged as a vision request's are when sizing without a count.</param>
internal sealed record OllamaNativeToolChatRequest(
    string Model,
    JsonArray Messages,
    JsonArray? Tools,
    bool Stream,
    JsonNode? Think,
    Dictionary<string, object> Settings,
    LlmPrivacyClass Privacy,
    string? ExpectedArtifactDigest,
    string SizingText,
    int ImageCount);

/// <summary>
/// A tool turn the runtime accepted: the response with its headers read and
/// the client that owns it. Disposing releases both.
/// </summary>
internal sealed class OllamaNativeToolChat(HttpClient client, HttpResponseMessage response) : IDisposable
{
    public HttpResponseMessage Response { get; } = response;

    public void Dispose()
    {
        Response.Dispose();
        client.Dispose();
    }
}
