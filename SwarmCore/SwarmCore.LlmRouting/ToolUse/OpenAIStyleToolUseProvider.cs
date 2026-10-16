using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// OpenAI-style tool-use translator. Speaks the OpenAI <c>/chat/completions</c>
/// wire format for both OpenAI and any OpenAI-compatible endpoint (LM Studio,
/// Groq, Together, etc.). Instances differ only in endpoint URL, provider
/// name, and auth header builder.
/// <para>
/// Used by the unified tool-use engine. Registered once per OpenAI-compat
/// provider via <see cref="ToolUseServiceCollectionExtensions"/>. A registered
/// Ollama-family provider serves tool use natively instead
/// (<see cref="OllamaNativeToolUseProvider"/>), because Ollama's
/// OpenAI-compatible endpoint ignores <c>num_ctx</c> and <c>truncate</c>. An
/// Ollama endpoint reaches this translator only when a host constructs one
/// explicitly; it keeps the LocalOnly locality proof and the reasoning default
/// of the native path.
/// </para>
/// </summary>
public sealed class OpenAIStyleToolUseProvider : IToolUseProvider, IToolUseContextWindowSource
{
    private readonly string _providerName;
    private readonly string _chatCompletionsUrl;
    private readonly Func<CancellationToken, Task<AuthenticationHeaderValue?>> _authFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<LlmCredential>>? _credentialFactory;
    private readonly bool _streamRequiredToolCalls;

    public string ProviderName => _providerName;

    private OpenAIResponsesProvider CreateResponsesProvider() => new(
        _chatCompletionsUrl[..^"/chat/completions".Length] + "/responses",
        _authFactory, _httpClientFactory, credentialFactory: _credentialFactory);

    /// <summary>
    /// Construct a translator. Prefer <see cref="OpenAIStyleToolUseProvider.ForOpenAI"/>
    /// or the Ollama factory in <see cref="ToolUseServiceCollectionExtensions"/>
    /// rather than calling this directly.
    /// </summary>
    /// <param name="providerName">Display name returned by <see cref="ProviderName"/>.</param>
    /// <param name="chatCompletionsUrl">Fully qualified URL of the <c>/chat/completions</c> endpoint.</param>
    /// <param name="authFactory">Builds the Authorization header per request. Return null to skip auth (Ollama).</param>
    /// <param name="httpClientFactory">Shared factory.</param>
    /// <param name="logger">Logger used for wire-level warnings.</param>
    public OpenAIStyleToolUseProvider(
        string providerName,
        string chatCompletionsUrl,
        Func<CancellationToken, Task<AuthenticationHeaderValue?>> authFactory,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Func<CancellationToken, Task<LlmCredential>>? credentialFactory = null,
        bool streamRequiredToolCalls = true)
    {
        _providerName = providerName;
        _chatCompletionsUrl = chatCompletionsUrl;
        _authFactory = authFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _credentialFactory = credentialFactory;
        _streamRequiredToolCalls = streamRequiredToolCalls;
    }

    /// <summary>
    /// Factory for the OpenAI translator. Pulls the API key lazily.
    /// </summary>
    public static OpenAIStyleToolUseProvider ForOpenAI(
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        ILogger logger) =>
        ForOpenAICompatible(
            providerName: "OpenAI",
            chatCompletionsUrl: "https://api.openai.com/v1/chat/completions",
            apiKeyProvider: apiKeyProvider,
            httpClientFactory: httpClientFactory,
            logger: logger);

    /// <summary>
    /// Factory for any OpenAI-compatible chat-completions endpoint (Grok, DeepSeek,
    /// Hugging Face Inference Router). The caller supplies the provider's canonical
    /// name + base URL; auth uses Bearer with the key returned from
    /// <see cref="ILlmApiKeyProvider"/>.
    /// </summary>
    public static OpenAIStyleToolUseProvider ForOpenAICompatible(
        string providerName,
        string chatCompletionsUrl,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        bool streamRequiredToolCalls = true) =>
        new(
            providerName: providerName,
            chatCompletionsUrl: chatCompletionsUrl,
            authFactory: async _ =>
            {
                var key = await apiKeyProvider.GetApiKeyAsync(providerName);
                if (string.IsNullOrEmpty(key))
                {
                    throw new InvalidOperationException($"No API key configured for {providerName}.");
                }
                return new AuthenticationHeaderValue("Bearer", key);
            },
            httpClientFactory,
            logger,
            _ => apiKeyProvider.GetCredentialAsync(providerName),
            streamRequiredToolCalls);

    /// <summary>
    /// Only the Ollama family serves a runtime-owned window through a native
    /// API next to its OpenAI-compatible endpoint; other endpoints report none
    /// and make no request.
    /// </summary>
    public async Task<int?> GetContextWindowTokensAsync(string model, CancellationToken ct = default)
    {
        if (!OllamaProviderFamily.IsOllamaFamily(_providerName)
            || string.IsNullOrWhiteSpace(model)
            || OllamaRuntimeContextWindow.ApiRootFor(_chatCompletionsUrl) is not { } apiRoot)
        {
            return null;
        }

        return await OllamaRuntimeContextWindow
            .ReadAsync(_httpClientFactory, apiRoot, model.Trim(), _logger, ct)
            .ConfigureAwait(false);
    }

    public async Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        var model = request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "OpenAIStyleToolUseProvider requires LlmRouterOptions.OverrideModel to be set (the router resolves this).");
        await EnsureLocalOnlyBoundaryAsync(request, model, ct).ConfigureAwait(false);

        if (OpenAIResponsesContract.IsSelected(_providerName, model))
            return await CreateResponsesProvider().ChatAsync(request, ct).ConfigureAwait(false);

        var nameMap = BuildNameMap(request);
        using var http = _httpClientFactory.CreateClient();
        // Disable the HttpClient's default 100s timeout — the caller's
        // CancellationToken (tied to the engine's HardTimeout on
        // AgentProfileSnapshot, typically 10 min) is the single source of
        // truth for cancellation. Local Ollama models on a cold GPU routinely
        // take longer than 100s just to return first-token, which was
        // causing spurious TaskCanceledExceptions before first response.
        http.Timeout = Timeout.InfiniteTimeSpan;
        var auth = await _authFactory(ct);
        if (auth is not null) http.DefaultRequestHeaders.Authorization = auth;

        var (response, payloadJson) = await SendAsync(http, request, model, stream: false, nameMap, ct).ConfigureAwait(false);
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseResponse(body, model, nameMap, request.Tools, payloadJson);
        }
    }

    /// <summary>
    /// Below this output budget a tool turn cannot reliably finish (a reasoning
    /// model can spend it all before replying), so a balance that affords less
    /// surfaces as the provider's billing failure instead of a retry.
    /// </summary>
    internal const int MinimumAffordableOutputTokens = 4096;

    private static readonly System.Text.RegularExpressions.Regex AffordableOutputTokensPattern = new(
        @"can only afford (?<tokens>\d{1,9})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Sends one chat-completions request and returns the successful response.
    /// A prepaid-credit endpoint such as OpenRouter authorizes each request
    /// against the balance using its output budget. A request that sets none
    /// is charged for the model's whole completion limit (131,072 tokens for
    /// some current models), so a low balance refuses every call with HTTP 402
    /// long before an ordinary turn would exhaust it. When the 402 names the
    /// budget the balance still covers and the caller chose no budget of its
    /// own, the request is sent once more with that budget.
    /// </summary>
    private async Task<(HttpResponseMessage Response, string PayloadJson)> SendAsync(
        HttpClient http,
        ChatToolUseRequest request,
        string model,
        bool stream,
        Dictionary<string, string> nameMap,
        CancellationToken ct)
    {
        int? affordableOutputTokens = null;
        while (true)
        {
            var payloadJson = BuildRequestPayload(request, model, stream, nameMap, affordableOutputTokens).ToJsonString();
            var response = await ToolUseHttpRetry.SendAsync(http,
                () => new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUrl) { Content = new StringContent(payloadJson, Encoding.UTF8, "application/json") },
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                _logger, _providerName, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (response, payloadJson);

            string body;
            var status = response.StatusCode;
            var reason = response.ReasonPhrase;
            using (response)
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            if (affordableOutputTokens is null
                && status == System.Net.HttpStatusCode.PaymentRequired
                && !HasCallerOutputBudget(request)
                && TryReadAffordableOutputTokens(body) is { } affordable
                && affordable >= MinimumAffordableOutputTokens)
            {
                _logger.LogWarning(
                    "{Provider} balance covers only {Affordable} output tokens for {Model}; retrying once with that output budget.",
                    _providerName, affordable, model);
                affordableOutputTokens = affordable;
                continue;
            }

            if (stream)
                _logger.LogWarning("{Provider} streaming call failed with {Status}: {Body}",
                    _providerName, (int)status, Truncate(body, 500));
            else
                _logger.LogWarning("{Provider} tool-use call failed with {Status}: {Body}",
                    _providerName, (int)status, Truncate(body, 500));
            throw new HttpRequestException(
                $"{_providerName} {(stream ? "streaming call" : "tool-use call")} failed: {(int)status} {reason}. Body: {Truncate(body, 500)}",
                inner: null,
                status);
        }
    }

    private static bool HasCallerOutputBudget(ChatToolUseRequest request)
        => request.Options?.GenerationParameters?.MaxOutputTokens is not null
           || request.Options?.MaxTokens is not null
           || request.Options?.SuppressOutputTokenLimit == true;

    internal static int? TryReadAffordableOutputTokens(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            var match = AffordableOutputTokensPattern.Match(body);
            return match.Success && int.TryParse(match.Groups["tokens"].Value, out var tokens) && tokens > 0
                ? tokens
                : null;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(ChatToolUseRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "OpenAIStyleToolUseProvider requires LlmRouterOptions.OverrideModel to be set.");
        await EnsureLocalOnlyBoundaryAsync(request, model, ct).ConfigureAwait(false);

        // Some compatible endpoints honor required tool_choice only for a
        // non-streaming completion. Keep the engine event contract while using
        // that transport for providers where this has been observed.
        if (request.RequireToolCall && request.Tools.Count > 0 && !_streamRequiredToolCalls)
        {
            var completed = await ChatAsync(request, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(completed.Text)) yield return new TextDeltaEvent(completed.Text);
            if (!string.IsNullOrEmpty(completed.ReasoningContent))
                yield return new ReasoningDeltaEvent(completed.ReasoningContent);
            foreach (var call in completed.ToolCalls)
            {
                yield return new ToolUseStartedEvent(call.Id, call.Name);
                yield return new ToolUseFinishedEvent(call);
            }
            yield return new MessageStoppedEvent(completed);
            yield break;
        }

        if (OpenAIResponsesContract.IsSelected(_providerName, model))
        {
            await foreach (var item in CreateResponsesProvider().StreamAsync(request, ct).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        var nameMap = BuildNameMap(request);
        using var http = _httpClientFactory.CreateClient();
        // Streaming: the connection stays open for the entire generation, and
        // local Ollama models can take minutes before first token when the
        // model is cold. Let the caller's CancellationToken (engine hard
        // timeout) govern; disable HttpClient.Timeout so it doesn't fire
        // before then.
        http.Timeout = Timeout.InfiniteTimeSpan;
        var auth = await _authFactory(ct);
        if (auth is not null) http.DefaultRequestHeaders.Authorization = auth;

        var (response, payloadJson) = await SendAsync(http, request, model, stream: true, nameMap, ct).ConfigureAwait(false);
        using var ownedResponse = response;

        await using var body = await response.Content.ReadAsStreamAsync(ct);

        // Accumulators for tool-call fragments. OpenAI streams tool calls via
        // `tool_calls` deltas keyed by index; id/name arrive on the first
        // fragment, argument JSON arrives in chunks on subsequent fragments.
        var toolCallsByIndex = new Dictionary<int, ToolCallAccumulator>();
        var textBuilder = new StringBuilder();
        // DeepSeek thinking-mode streams chain-of-thought as
        // `delta.reasoning_content` chunks alongside the regular `delta.content`.
        // We accumulate it so the engine can echo it back on the next turn,
        // which DeepSeek requires within a single conversation.
        var reasoningBuilder = new StringBuilder();
        var refusalBuilder = new StringBuilder();
        string? finishReason = null;
        ChatUsage? usage = null;
        var emittedStart = new HashSet<int>();

        await foreach (var sse in SseParser.ParseAsync(body, ct))
        {
            // OpenAI signals the end with a literal "[DONE]" data line.
            if (sse.Data == "[DONE]") break;

            JsonNode? node;
            try { node = JsonNode.Parse(sse.Data); }
            catch (JsonException)
            {
                _logger.LogDebug("{Provider}: could not parse SSE data '{Raw}'.", _providerName, Truncate(sse.Data, 200));
                continue;
            }

            if (node is not JsonObject root) continue;

            // With stream_options.include_usage the final usage chunk arrives
            // with an EMPTY choices array, so usage must be read before the
            // choices guard below — otherwise it is skipped and cost tracking
            // records zero input tokens for the call.
            if (root["usage"] is JsonObject usageEl)
            {
                var inputTokens = (int?)usageEl["prompt_tokens"] ?? 0;
                var outputTokens = (int?)usageEl["completion_tokens"] ?? 0;
                usage = new ChatUsage(inputTokens, outputTokens) { CachedInputTokens = CachedPromptTokens(usageEl) };
            }

            if (root["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var choice = choices[0]!.AsObject();

            if (choice["delta"] is JsonObject delta)
            {
                // Text delta.
                var content = (string?)delta["content"];
                if (!string.IsNullOrEmpty(content))
                {
                    textBuilder.Append(content);
                    yield return new TextDeltaEvent(content);
                }

                // Reasoning delta (DeepSeek thinking mode). We accumulate it
                // for the final response (so the engine can append a
                // ReasoningPart to the assistant transcript and echo it back
                // next turn — DeepSeek requires that) AND emit a distinct
                // stream event so the UI can render it as a live "Thinking"
                // affordance rather than letting it disappear into the
                // text stream. Don't fold reasoning into TextDeltaEvent —
                // mixing reasoning with the answer is exactly the UX bug
                // we are fixing.
                var refusalChunk = (string?)delta["refusal"];
                if (!string.IsNullOrEmpty(refusalChunk)) refusalBuilder.Append(refusalChunk);

                var reasoningChunk = (string?)delta["reasoning_content"];
                if (!string.IsNullOrEmpty(reasoningChunk))
                {
                    reasoningBuilder.Append(reasoningChunk);
                    yield return new ReasoningDeltaEvent(reasoningChunk);
                }

                // Tool call deltas.
                if (delta["tool_calls"] is JsonArray tcDeltas)
                {
                    foreach (var tcNode in tcDeltas)
                    {
                        if (tcNode is not JsonObject tc) continue;
                        var index = (int?)tc["index"] ?? 0;
                        if (!toolCallsByIndex.TryGetValue(index, out var acc))
                        {
                            acc = new ToolCallAccumulator();
                            toolCallsByIndex[index] = acc;
                        }

                        var id = (string?)tc["id"];
                        if (!string.IsNullOrEmpty(id)) acc.Id = id;

                        string? argFrag = null;
                        if (tc["function"] is JsonObject fn)
                        {
                            var name = (string?)fn["name"];
                            if (!string.IsNullOrEmpty(name)) acc.Name = name;
                            argFrag = (string?)fn["arguments"];
                            if (!string.IsNullOrEmpty(argFrag)) acc.Args.Append(argFrag);
                        }

                        // Emit a start event as soon as we have id+name.
                        if (!emittedStart.Contains(index) && !string.IsNullOrEmpty(acc.Id) && !string.IsNullOrEmpty(acc.Name))
                        {
                            emittedStart.Add(index);
                            var unsanitized = nameMap.TryGetValue(acc.Name, out var original) ? original : acc.Name;
                            yield return new ToolUseStartedEvent(acc.Id, unsanitized);
                        }

                        // Forward arg fragments for UIs that want to render them live.
                        if (!string.IsNullOrEmpty(argFrag) && !string.IsNullOrEmpty(acc.Id))
                        {
                            yield return new ToolUseInputDeltaEvent(acc.Id, argFrag);
                        }
                    }
                }
            }

            var fr = (string?)choice["finish_reason"];
            if (!string.IsNullOrEmpty(fr)) finishReason = fr;
        }

        // Finalize tool calls: parse accumulated argument JSON.
        var finalToolCalls = new List<ToolUsePart>();
        foreach (var kv in toolCallsByIndex.OrderBy(k => k.Key))
        {
            var acc = kv.Value;
            var id = acc.Id ?? Guid.NewGuid().ToString("N");
            var sanitized = acc.Name ?? "";
            var name = nameMap.TryGetValue(sanitized, out var original) ? original : sanitized;
            JsonObject input;
            try { input = JsonNode.Parse(acc.Args.Length == 0 ? "{}" : acc.Args.ToString()) as JsonObject ?? new JsonObject(); }
            catch (JsonException)
            {
                _logger.LogWarning("{Provider}: tool '{Name}' streamed malformed argument JSON; defaulting to empty object.", _providerName, name);
                input = new JsonObject();
            }
            var part = new ToolUsePart(id, name, input);
            finalToolCalls.Add(part);
            yield return new ToolUseFinishedEvent(part);
        }

        var stopReason = finishReason switch
        {
            "tool_calls" => ChatStopReason.ToolUse,
            "stop" => ChatStopReason.EndTurn,
            "length" => ChatStopReason.MaxTokens,
            _ => finalToolCalls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn
        };

        // Endpoints that ignore stream_options never send a usage chunk. Cost
        // tracking must not record zero input tokens for a paid call, so fall
        // back to a size-based estimate of the exact request we serialized.
        usage ??= EstimateUsage(payloadJson, textBuilder, reasoningBuilder, finalToolCalls);

        yield return new MessageStoppedEvent(new ChatToolUseResponse
        {
            Text = textBuilder.Length == 0 ? null : textBuilder.ToString(),
            ReasoningContent = reasoningBuilder.Length == 0 ? null : reasoningBuilder.ToString(),
            ToolCalls = finalToolCalls,
            FailureCategory = RefusalCategory(finishReason, refusalBuilder.ToString()),
            ErrorMessage = RefusalMessage(finishReason, refusalBuilder.ToString(), textBuilder.ToString()),
            StopReason = stopReason,
            Provider = _providerName,
            Model = model,
            Usage = usage
        });
    }

    /// <summary>
    /// Rough (~4 chars/token) usage estimate for endpoints that report none.
    /// The serialized request is the exact bytes the provider billed for.
    /// </summary>
    private static ChatUsage EstimateUsage(
        string payloadJson,
        StringBuilder text,
        StringBuilder reasoning,
        IReadOnlyList<ToolUsePart> toolCalls)
    {
        var outputChars = text.Length + reasoning.Length
                          + toolCalls.Sum(call => call.Name.Length + call.Input.ToJsonString().Length);
        return new ChatUsage(
            Math.Max(1, payloadJson.Length / 4),
            outputChars / 4);
    }

    /// <summary>
    /// Prompt tokens the provider served from its cache: OpenAI and OpenRouter report
    /// <c>prompt_tokens_details.cached_tokens</c>, DeepSeek reports <c>prompt_cache_hit_tokens</c>.
    /// </summary>
    internal static int CachedPromptTokens(JsonObject usage)
    {
        if (usage["prompt_tokens_details"] is JsonObject details && (int?)details["cached_tokens"] is { } cached) return cached;
        return (int?)usage["prompt_cache_hit_tokens"] ?? 0;
    }

    internal static int CachedPromptTokens(JsonElement usage)
    {
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var cached) && cached.ValueKind == JsonValueKind.Number)
            return cached.GetInt32();
        return usage.TryGetProperty("prompt_cache_hit_tokens", out var hit) && hit.ValueKind == JsonValueKind.Number ? hit.GetInt32() : 0;
    }

    /// <summary>
    /// Whether to request <c>stream_options.include_usage</c>. OpenAI, OpenRouter,
    /// DeepSeek, and xAI honour it; other OpenAI-compatible servers (Ollama, LM
    /// Studio, routed inference) may reject unknown fields, so those get the
    /// estimate path instead.
    /// </summary>
    private bool ShouldRequestStreamUsage()
    {
        if (_providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || _providerName.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)
            || _providerName.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
            || _providerName.Equals("Grok", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(_chatCompletionsUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
               || host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureLocalOnlyBoundaryAsync(
        ChatToolUseRequest request,
        string model,
        CancellationToken ct)
    {
        var privacy = request.Options?.PrivacyClass ?? LlmPrivacyClass.Unspecified;
        if (privacy == LlmPrivacyClass.Unspecified)
        {
            privacy = LlmReservedExecutionSettings.GetRequestPrivacyClass(
                request.Options?.ExecutionSettings);
        }
        if (privacy != LlmPrivacyClass.LocalOnly)
        {
            return;
        }

        var endpointSettings = new LlmProviderSettings
        {
            ProviderName = _providerName,
            Endpoint = _chatCompletionsUrl,
            IsLocalProvider = true,
        };
        if (!LlmProviderLocalityPolicy.IsTrustedLocalRoute(
                _providerName,
                model,
                endpointSettings))
        {
            throw new InvalidOperationException(
                $"Route {_providerName}/{model} cannot satisfy LocalOnly privacy at this tool-use endpoint.");
        }

        if (!OllamaProviderFamily.IsOllamaFamily(_providerName))
        {
            return;
        }

        // Ollama can expose a hosted model through localhost, including under a
        // copied alias with no cloud suffix. /api/show is queried before the
        // prompt-bearing request; any non-empty remote_host is remote and a
        // missing/failed metadata response is unknown, so both fail closed.
        var completionsUri = new Uri(_chatCompletionsUrl, UriKind.Absolute);
        var showUri = new UriBuilder(completionsUri)
        {
            Path = "/api/show",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var http = _httpClientFactory.CreateClient();
            using var content = new StringContent(
                JsonSerializer.Serialize(new { model }),
                Encoding.UTF8,
                "application/json");
            using var response = await http.PostAsync(showUri, content, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Ollama could not prove that model '{model}' executes on this device; LocalOnly tool use was blocked.");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var metadata = JsonDocument.Parse(body);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Ollama returned invalid locality metadata for model '{model}'; LocalOnly tool use was blocked.");
            }

            string? remoteHost = null;
            if (metadata.RootElement.TryGetProperty("remote_host", out var remoteHostElement))
            {
                if (remoteHostElement.ValueKind == JsonValueKind.String)
                {
                    remoteHost = remoteHostElement.GetString();
                }
                else if (remoteHostElement.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidOperationException(
                        $"Ollama returned invalid locality metadata for model '{model}'; LocalOnly tool use was blocked.");
                }
            }
            if (OllamaModelLocalityPolicy.IsCloudBacked(model, remoteHost))
            {
                throw new InvalidOperationException(
                    $"Ollama model '{model}' is cloud-backed and cannot satisfy LocalOnly privacy.");
            }

            var hasRecognizedMetadata =
                metadata.RootElement.TryGetProperty("capabilities", out var capabilities)
                    && capabilities.ValueKind == JsonValueKind.Array
                || metadata.RootElement.TryGetProperty("details", out var details)
                    && details.ValueKind == JsonValueKind.Object
                || metadata.RootElement.TryGetProperty("model_info", out var modelInfo)
                    && modelInfo.ValueKind == JsonValueKind.Object
                || metadata.RootElement.TryGetProperty("template", out var template)
                    && template.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(template.GetString());
            if (!hasRecognizedMetadata)
            {
                throw new InvalidOperationException(
                    $"Ollama returned incomplete locality metadata for model '{model}'; LocalOnly tool use was blocked.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Ollama locality metadata timed out for model '{model}'; LocalOnly tool use was blocked.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Ollama locality metadata failed for model '{model}'; LocalOnly tool use was blocked.",
                ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Ollama returned invalid locality metadata for model '{model}'; LocalOnly tool use was blocked.",
                ex);
        }
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Args { get; } = new();
    }

    /// <summary>
    /// Sanitize a tool name to OpenAI's <c>^[a-zA-Z0-9_-]+$</c> constraint.
    /// Anthropic enforces the same shape, with its own sanitizer in
    /// <see cref="AnthropicToolUseProvider"/>. We translate on the way out
    /// and use <see cref="BuildNameMap"/> to translate back when parsing
    /// tool_calls from the response.
    /// </summary>
    internal static string SanitizeToolName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '_' || c == '-';
            sb.Append(ok ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Coerce a tool's input schema into the shape OpenAI requires — a JSON
    /// Schema object with <c>"type": "object"</c> at the top level. Some
    /// catalog entries (e.g. markdown skills that take a single string arg,
    /// or tools auto-generated from a primitive <c>string</c> input type)
    /// emit <c>{"type": "string"}</c>, which OpenAI rejects with
    /// <c>invalid_function_parameters</c>. Wrapping it in a single-property
    /// object preserves the shape the model gets to fill in, while passing
    /// OpenAI's validator.
    /// <para>
    /// Anthropic is lenient here, so the Anthropic translator can keep the
    /// original schema untouched.
    /// </para>
    /// </summary>
    internal static JsonNode NormalizeParametersSchema(JsonObject schema)
    {
        var cloned = (JsonObject)schema.DeepClone();
        var typeNode = cloned["type"];

        if (typeNode is JsonValue value && value.TryGetValue<string>(out var typeStr))
        {
            if (string.Equals(typeStr, "object", StringComparison.OrdinalIgnoreCase))
            {
                // Already an object schema — OpenAI requires a "properties" key
                // even if empty. Add one if missing.
                if (cloned["properties"] is null)
                {
                    cloned["properties"] = new JsonObject();
                }
                return cloned;
            }

            // Non-object top-level schema (string/integer/array/etc.). Wrap
            // it under an "input" property so the model still sees the real
            // expected shape, and OpenAI's validator is satisfied.
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["input"] = cloned },
                ["required"] = new JsonArray { "input" }
            };
        }

        // Missing or non-string "type". Treat as an opaque object schema —
        // we preserve whatever else was there (properties/required) but
        // force type=object and ensure properties exists.
        cloned["type"] = "object";
        if (cloned["properties"] is null)
        {
            cloned["properties"] = new JsonObject();
        }
        return cloned;
    }

    /// <summary>
    /// Build the sanitized-name → original-name map for one request. Covers
    /// both tool definitions and any historical tool_use blocks in the
    /// assistant messages (the model needs to see its own prior tool calls).
    /// </summary>
    internal static Dictionary<string, string> BuildNameMap(ChatToolUseRequest request)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in request.Tools)
        {
            var sanitized = SanitizeToolName(t.Name);
            map.TryAdd(sanitized, t.Name);
        }
        foreach (var m in request.Messages)
        {
            foreach (var part in m.Parts)
            {
                if (part is ToolUsePart use)
                {
                    var sanitized = SanitizeToolName(use.Name);
                    map.TryAdd(sanitized, use.Name);
                }
            }
        }
        return map;
    }

    private JsonObject BuildRequestPayload(
        ChatToolUseRequest request,
        string model,
        bool stream,
        Dictionary<string, string> nameMap,
        int? affordableOutputTokens = null)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        foreach (var m in request.Messages)
        {
            foreach (var converted in ConvertMessage(m))
            {
                messages.Add(converted);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages
        };

        var flavor = _providerName.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)
            ? OpenAICompatibleContractFlavor.OpenRouter
            : _providerName.Equals("LMStudio", StringComparison.OrdinalIgnoreCase)
                ? OpenAICompatibleContractFlavor.LMStudio
                : _providerName.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
                    ? OpenAICompatibleContractFlavor.DeepSeek
                    : OpenAICompatibleContractFlavor.OpenAI;
        var contract = new OpenAICompatibleExecutionContract(flavor);
        var semantic = request.Options?.GenerationParameters ?? new LlmGenerationParameters();
        semantic = semantic with
        {
            MaxOutputTokens = semantic.MaxOutputTokens ?? request.Options?.MaxTokens ?? affordableOutputTokens,
            Temperature = semantic.Temperature ?? request.Options?.Temperature,
            Stream = stream,
        };
        var ollamaReasoning = ResolveOllamaToolUseReasoning(request.Options, semantic.Reasoning);
        var experimental = request.Options?.ExecutionSettings is null
            ? null
            : new Dictionary<string, object>(request.Options.ExecutionSettings, StringComparer.OrdinalIgnoreCase);
        if (experimental is not null)
        {
            // The agent gives subscription CLI routes process controls through
            // ExecutionSettings. They are host metadata, never chat-completions
            // fields; strict OpenAI-compatible APIs reject unknown keys.
            foreach (var key in experimental.Keys.Where(key =>
                         key.StartsWith("subscription_cli_", StringComparison.OrdinalIgnoreCase)).ToArray())
                experimental.Remove(key);
            // Tool-use and ordinary text share the contract mapper. Legacy common
            // aliases are inputs to the semantic layer, never competing wire fields.
            foreach (var alias in new[]
                     {
                         "max_tokens", "max_completion_tokens", "num_predict", "temperature", "top_p", "seed",
                         "response_format", "format", "stream", "ollama_stream", "think", "ollama_think",
                         "enable_think", "reasoning_effort", "keep_alive",
                     })
                experimental.Remove(alias);
        }
        var translated = contract.Translate(
            model,
            semantic,
            request.Options?.RequestControl ?? new LlmRequestControl(),
            experimental);
        foreach (var setting in translated.WireSettings.Where(pair =>
                     !pair.Key.StartsWith("__swarmcore_", StringComparison.Ordinal)))
        {
            payload[setting.Key] = JsonSerializer.SerializeToNode(setting.Value);
        }

        if (stream && ShouldRequestStreamUsage())
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (ollamaReasoning is { } reasoning)
        {
            // Ollama's OpenAI-compatible endpoint enables thinking by default for
            // Qwen reasoning models. In a tool loop that can spend the entire
            // output budget in the private reasoning channel and return no final
            // content after a successful tool call. Keep tool execution concise
            // and receipt-led by default; callers can opt back into a reasoning
            // level through typed GenerationParameters or the legacy aliases.
            payload["reasoning_effort"] = reasoning;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = SanitizeToolName(tool.Name),
                        ["description"] = tool.Description,
                        ["parameters"] = NormalizeParametersSchema(tool.InputSchema)
                    }
                });
            }
            payload["tools"] = tools;
            payload["tool_choice"] = request.DisableToolCalls ? "none" : request.RequireToolCall ? "required" : "auto";
        }

        return payload;
    }

    private string? ResolveOllamaToolUseReasoning(
        LlmRouterOptions? options,
        LlmReasoningMode typedReasoning)
        => OllamaProviderFamily.IsOllamaFamily(_providerName)
            ? OllamaToolUseReasoning.Resolve(options, typedReasoning)
            : null;

    private IEnumerable<JsonObject> ConvertMessage(ChatMessage m)
    {
        // Decompose parts into the OpenAI wire format.
        // Assistant messages with tool_use become role=assistant with tool_calls.
        // Tool results become role=tool with tool_call_id — one wire message
        // per ToolResultPart, since OpenAI requires every tool_call id in the
        // preceding assistant turn to be answered by its own tool message.
        if (m.Role == ChatRole.Tool)
        {
            var results = m.Parts.OfType<ToolResultPart>().ToList();
            if (results.Count == 0)
            {
                throw new InvalidOperationException("Tool-role message must contain a ToolResultPart.");
            }
            return results.Select(result => new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = result.ToolUseId,
                ["content"] = result.Content
            });
        }

        return [ConvertSingleMessage(m)];
    }

    private JsonObject ConvertSingleMessage(ChatMessage m)
    {
        switch (m.Role)
        {
            case ChatRole.User:
            case ChatRole.System:
            {
                // System messages are always text-only — OpenAI's system role
                // doesn't accept image content blocks, and an attached image
                // belongs to the user that pasted it, not the system prompt.
                if (m.Role == ChatRole.System)
                {
                    return new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text))
                    };
                }

                // User messages: stay on the simple plain-string content path
                // when there's nothing but text — that's the historical wire
                // format, lower overhead, and what every text-only model
                // expects. The moment an ImagePart appears, switch to the
                // OpenAI vision content-array format so the image lands.
                var images = m.Parts.OfType<ImagePart>().ToList();
                if (images.Count == 0)
                {
                    return new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text))
                    };
                }

                var contentArr = new JsonArray();
                foreach (var part in m.Parts)
                {
                    if (part is TextPart t && !string.IsNullOrEmpty(t.Text))
                    {
                        contentArr.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = t.Text
                        });
                    }
                    else if (part is ImagePart img && !string.IsNullOrEmpty(img.Base64Data))
                    {
                        contentArr.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:{img.MediaType};base64,{img.Base64Data}"
                            }
                        });
                    }
                }
                return new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = contentArr
                };
            }

            case ChatRole.Assistant:
                var toolCalls = m.Parts.OfType<ToolUsePart>().ToList();
                var assistantText = string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text));
                var obj = new JsonObject
                {
                    ["role"] = "assistant"
                };
                // Content may be null when only tool_calls are present.
                obj["content"] = string.IsNullOrEmpty(assistantText) ? null : assistantText;
                if (toolCalls.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var tc in toolCalls)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = SanitizeToolName(tc.Name),
                                ["arguments"] = tc.Input.ToJsonString()
                            }
                        });
                    }
                    obj["tool_calls"] = arr;
                }
                // DeepSeek's thinking-mode requires the prior turn's
                // `reasoning_content` to be echoed back, otherwise the API
                // rejects subsequent requests with HTTP 400 "The
                // `reasoning_content` in the thinking mode must be passed back
                // to the API." OpenAI proper doesn't accept this field, so we
                // only emit it when the configured provider is DeepSeek.
                if (string.Equals(_providerName, "DeepSeek", StringComparison.OrdinalIgnoreCase))
                {
                    var reasoning = string.Concat(m.Parts.OfType<ReasoningPart>().Select(p => p.Text));
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        obj["reasoning_content"] = reasoning;
                    }
                }
                return obj;

            default:
                throw new InvalidOperationException($"Unknown chat role {m.Role}.");
        }
    }

    private ChatToolUseResponse ParseResponse(string body, string model, Dictionary<string, string> nameMap,
        IReadOnlyList<ChatToolDefinition> offeredTools, string payloadJson)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"{_providerName} response had no choices.");
        }
        var choice = choices[0];
        var message = choice.GetProperty("message");

        string? text = null;
        if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            text = contentEl.GetString();
        }

        string? reasoningContent = null;
        if (message.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
        {
            reasoningContent = reasoningEl.GetString();
        }

        string? refusal = null;
        if (message.TryGetProperty("refusal", out var refusalEl) && refusalEl.ValueKind == JsonValueKind.String)
        {
            refusal = refusalEl.GetString();
        }

        var toolCalls = new List<ToolUsePart>();
        if (message.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcArr.EnumerateArray())
            {
                var id = tc.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                var fn = tc.GetProperty("function");
                var sanitized = fn.GetProperty("name").GetString()
                           ?? throw new InvalidOperationException("tool_calls entry missing function.name.");
                var name = nameMap.TryGetValue(sanitized, out var original) ? original : sanitized;
                var argsStr = fn.GetProperty("arguments").GetString() ?? "{}";
                JsonObject input;
                try
                {
                    input = JsonNode.Parse(argsStr) as JsonObject ?? new JsonObject();
                }
                catch (JsonException)
                {
                    _logger.LogWarning("{Provider} tool call '{Name}' had unparseable arguments; falling back to empty object. Raw: {Raw}",
                        _providerName, name, Truncate(argsStr, 300));
                    input = new JsonObject();
                }
                toolCalls.Add(new ToolUsePart(id, name, input));
            }
        }

        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        // A few OpenAI-compatible endpoints signal tool_calls but serialize
        // the calls as the entire message content instead of message.tool_calls.
        // Accept only an exact JSON call array whose names were offered in this
        // request; never infer executable calls from ordinary assistant prose.
        if (finish == "tool_calls" && toolCalls.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            toolCalls.AddRange(ParseContentToolCalls(text, nameMap, offeredTools));
            if (toolCalls.Count > 0) text = null;
        }
        var stopReason = finish switch
        {
            "tool_calls" => ChatStopReason.ToolUse,
            "stop" => ChatStopReason.EndTurn,
            "length" => ChatStopReason.MaxTokens,
            "content_filter" => ChatStopReason.Other,
            _ => toolCalls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn
        };

        ChatUsage? usage = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var input = usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0;
            var output = usageEl.TryGetProperty("completion_tokens", out var ct2) && ct2.ValueKind == JsonValueKind.Number ? ct2.GetInt32() : 0;
            usage = new ChatUsage(input, output) { CachedInputTokens = CachedPromptTokens(usageEl) };
        }
        usage ??= new ChatUsage(
            Math.Max(1, payloadJson.Length / 4),
            ((text?.Length ?? 0) + (reasoningContent?.Length ?? 0)) / 4);

        return new ChatToolUseResponse
        {
            Text = text,
            ReasoningContent = reasoningContent,
            ToolCalls = toolCalls,
            FailureCategory = RefusalCategory(finish, refusal),
            ErrorMessage = RefusalMessage(finish, refusal, text),
            StopReason = stopReason,
            Provider = _providerName,
            Model = model,
            Usage = usage
        };
    }

    private static IReadOnlyList<ToolUsePart> ParseContentToolCalls(string content,
        Dictionary<string, string> nameMap, IReadOnlyList<ChatToolDefinition> offeredTools)
    {
        try
        {
            if (JsonNode.Parse(content) is not JsonArray calls || calls.Count == 0 || calls.Count > offeredTools.Count)
                return [];
            var offered = offeredTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var parsed = new List<ToolUsePart>(calls.Count);
            foreach (var item in calls)
            {
                if (item is not JsonObject call || call["name"] is not JsonValue nameValue
                    || !nameValue.TryGetValue<string>(out var wireName)
                    || string.IsNullOrWhiteSpace(wireName)) return [];
                var name = nameMap.TryGetValue(wireName, out var original) ? original : wireName;
                if (!offered.Contains(name)) return [];
                JsonObject? args = call["arguments"] switch
                {
                    JsonObject objectArgs => (JsonObject)objectArgs.DeepClone(),
                    JsonValue valueArgs when valueArgs.TryGetValue<string>(out var jsonArgs)
                        => JsonNode.Parse(jsonArgs) as JsonObject,
                    _ => null
                };
                if (args is null) return [];
                parsed.Add(new ToolUsePart(Guid.NewGuid().ToString("N"), name, args));
            }
            return parsed;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A content_filter finish or a non-empty refusal field is a typed content-policy failure, not a normal answer.</summary>
    private static LlmFailureCategory? RefusalCategory(string? finishReason, string? refusal)
        => string.Equals(finishReason, "content_filter", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(refusal)
            ? LlmFailureCategory.ContentPolicy
            : null;

    private string? RefusalMessage(string? finishReason, string? refusal, string? text)
    {
        if (RefusalCategory(finishReason, refusal) is null) return null;
        var detail = !string.IsNullOrWhiteSpace(refusal) ? refusal.Trim() : text?.Trim();
        var kind = string.Equals(finishReason, "content_filter", StringComparison.Ordinal) ? "content_filter" : "refusal";
        return $"{_providerName} declined this request (content policy: {kind})." + (string.IsNullOrWhiteSpace(detail) ? "" : " " + Truncate(detail, 300));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
