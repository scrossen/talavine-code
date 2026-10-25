using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Serialization;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// OpenAI LLM provider implementation for SwarmCore.
/// Can be used with any application that provides ILlmApiKeyProvider.
/// Self-heals from model deprecation by blacklisting failed models and retrying with the next available.
/// </summary>
public class OpenAILlmProvider : LlmProviderBase, ILlmProviderExecutionContract, ILlmModelCatalogProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("OpenAI").DirectApi!;
    private readonly OpenAICompatibleExecutionContract _executionContract =
        new(OpenAICompatibleContractFlavor.OpenAI);
    private readonly OpenAIProviderOptions _options;
    private List<string>? _cachedModels;
    // Every id the provider catalog returned, before the listing prefix filter.
    private HashSet<string>? _catalogModels;

    // Models confirmed deprecated/unavailable at runtime — excluded from selection permanently until restart.
    // The provider is a singleton shared by concurrent requests.
    private readonly ConcurrentDictionary<string, byte> _deprecatedModels = new(StringComparer.OrdinalIgnoreCase);

    public override string ProviderName => "OpenAI";
    public virtual LlmProviderExecutionContractDescriptor ExecutionContract => _executionContract.Descriptor;
    public virtual LlmProviderExecutionContractDescriptor GetExecutionContract(string model) =>
        OpenAIResponsesContract.IsSelected(ProviderName, model)
            ? new(OpenAIResponsesContract.ContractId, new(true, false, false, false, true, true, true, false))
            : ExecutionContract;
    public virtual LlmProviderExecutionTranslation TranslateExecution(
        string model,
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? providerExperimentalSettings = null)
        => OpenAIResponsesContract.IsSelected(ProviderName, model)
            ? OpenAIResponsesContract.Translate(parameters, control)
            : _executionContract.Translate(model, parameters, control, providerExperimentalSettings);
    public override bool SupportsStreaming => true;
    public override bool SupportsEmbeddings => true;
    public override string? DefaultEmbeddingModel => _options.DefaultEmbeddingModel ?? Pack.DefaultEmbeddingModel;

    /// <summary>
    /// Base URL for the OpenAI-compatible API. Virtualised so OpenAI-compatible providers
    /// (Grok, DeepSeek, HuggingFace Inference Router, etc.) can override by pointing at
    /// their own host without duplicating the rest of the provider logic.
    /// Endpoints used: <c>{BaseUrl}/models</c>, <c>{BaseUrl}/chat/completions</c>, <c>{BaseUrl}/embeddings</c>.
    /// </summary>
    protected virtual string BaseUrl => string.IsNullOrWhiteSpace(_options.Endpoint)
        ? Pack.BaseUrl
        : _options.Endpoint!.Trim().TrimEnd('/');

    /// <summary>
    /// Model-id prefixes that the catalog-fetch endpoint should keep. OpenAI returns
    /// many non-chat entries (tts, whisper, dall-e); we filter to the families we know
    /// how to route. Subclasses point their own catalog filter at their own namespaces
    /// (e.g. Grok returns "grok-*", DeepSeek "deepseek-*").
    /// </summary>
    protected virtual IReadOnlyList<string> CatalogModelPrefixes => Pack.CatalogModelPrefixes;

    protected virtual IReadOnlyList<string> PreferredModels => Pack.PreferredModels;

    /// <summary>
    /// Whether this provider exposes a public /models list endpoint. Subclasses that
    /// don't (e.g. HuggingFace routed endpoints are per-deployment) should override to
    /// <c>false</c> so we fall straight through to <see cref="DefaultModels"/>.
    /// </summary>
    protected virtual bool HasListModelsEndpoint => true;

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override string DefaultModel => _options.DefaultModel ?? Pack.DefaultModel;

    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    // gpt-5-nano matches the "gpt-5" vision entry via substring but does not
    // accept image inputs (OpenAI API returns BadRequest). Exclude it explicitly.
    // o1-mini likewise — the base o1 supports vision, the mini variant does not.
    protected override IReadOnlyList<string> NonVisionModels => Pack.NonVisionModelPatterns;

    public OpenAILlmProvider(
        ILogger<OpenAILlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<OpenAIProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Inheritance constructor for OpenAI-compatible providers (Grok, DeepSeek,
    /// HuggingFace Inference Router). The base's <see cref="_options"/> is an empty
    /// stub — subclasses override <see cref="DefaultModel"/> /
    /// <see cref="DefaultEmbeddingModel"/> / <see cref="BaseUrl"/> directly instead.
    /// </summary>
    protected OpenAILlmProvider(
        ILogger logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _options = new OpenAIProviderOptions();
    }

    /// <summary>
    /// Applies provider-level headers to an OpenAI-compatible request client.
    /// Subclasses can add provider-specific metadata while reusing the chat,
    /// vision, model-list, and embedding request flow.
    /// </summary>
    protected virtual void ApplyDefaultRequestHeaders(HttpClient client, string apiKey)
    {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public override Task<List<string>> GetAvailableModelsAsync() => DiscoverModelsAsync(false, CancellationToken.None);

    public virtual async Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        => new(await DiscoverModelsAsync(true, cancellationToken).ConfigureAwait(false), "Provider API catalog");

    private async Task<List<string>> DiscoverModelsAsync(bool strict, CancellationToken ct)
    {
        if (!HasListModelsEndpoint)
        {
            if (strict) throw new InvalidOperationException("This provider does not expose a model catalog. Enter a custom model ID.");
            return DefaultModels.ToList();
        }

        string apiKey;
        try
        {
            apiKey = await GetApiKeyAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing key is not a connection problem; say what to fix.
            if (strict) throw new InvalidOperationException("No API key is configured for this provider. Add the API key, then refresh the model catalog.", ex);
            Logger.LogDebug(ex, "No usable {Provider} API key for the model list; using defaults", ProviderName);
            return _cachedModels ?? DefaultModels.ToList();
        }

        try
        {
            using var client = HttpClientFactory.CreateClient();
            ApplyDefaultRequestHeaders(client, apiKey);

            using var response = await client.GetAsync($"{BaseUrl}/models", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var modelsResponse = JsonSerializer.Deserialize<OpenAIModelsResponse>(json, SwarmJsonOptions.ExternalApi);
                var catalog = modelsResponse?.Data?
                    .Select(m => m.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .ToList();

                if (catalog is not null)
                {
                    // Only the listing is narrowed to routable families. Support
                    // checks keep the whole catalog (fine-tunes, aliases such as
                    // chatgpt-4o-latest) so a pin does not stop working once the
                    // catalog has been fetched.
                    var prefixes = CatalogModelPrefixes;
                    var models = catalog
                        .Where(id => prefixes.Count == 0
                                     || prefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _catalogModels = new HashSet<string>(catalog, StringComparer.OrdinalIgnoreCase);
                    _cachedModels = models;
                    Logger.LogInformation("{Provider}: fetched {Count} models from API", ProviderName, _cachedModels.Count);
                    return _cachedModels;
                }
            }
        }
        catch (Exception ex)
        {
            if (strict) throw new InvalidOperationException("Could not load the provider model catalog. Check the connection and refresh, or enter a custom model ID.", ex);
            Logger.LogDebug(ex, "Failed to fetch {Provider} model list from API; using defaults", ProviderName);
        }

        if (strict) throw new InvalidOperationException("The provider model catalog is unavailable. Check the connection and refresh, or enter a custom model ID.");
        return _cachedModels ?? DefaultModels.ToList();
    }

    public override bool SupportsModel(string model)
        => base.SupportsModel(model) || _catalogModels?.Contains(model) == true;

    public override async Task<LlmResponse> GenerateResponseAsync(string prompt, string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;

        try
        {
            var result = await CallChatCompletionAsync(prompt, effectiveModel, executionSettings, stopwatch);

            // Self-repair: if the model is deprecated/not found, retry with next available model.
            if (!OpenAIResponsesContract.IsSelected(ProviderName, effectiveModel)
                && !result.WasSuccessful && IsModelDeprecationError(result.ErrorMessage))
            {
                Logger.LogWarning(
                    "Model {Model} appears deprecated or unavailable. Blacklisting and retrying with next available model.",
                    effectiveModel);

                _deprecatedModels.TryAdd(effectiveModel, 0);

                if (LlmReservedExecutionSettings.RequiresExactModel(executionSettings))
                {
                    Logger.LogWarning(
                        "Exact-model execution forbids substituting deprecated/unavailable model {Model}.",
                        effectiveModel);
                    return result;
                }

                var fallback = await SelectBestAvailableModelAsync();
                if (fallback != null && !fallback.Equals(effectiveModel, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("Retrying with fallback model: {Fallback}", fallback);
                    stopwatch.Restart();
                    return await CallChatCompletionAsync(prompt, fallback, executionSettings, stopwatch);
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Error generating response with OpenAI");
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<LlmResponse> CallChatCompletionAsync(
        string prompt, string model, Dictionary<string, object>? executionSettings, Stopwatch stopwatch)
    {
        if (OpenAIResponsesContract.IsSelected(ProviderName, model))
            return await CallResponsesAsync(prompt, model, executionSettings, stopwatch).ConfigureAwait(false);
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        var apiKey = await GetApiKeyAsync();

        using var client = HttpClientFactory.CreateClient();
        ApplyDefaultRequestHeaders(client, apiKey);

        // OpenAI auto-caches any input prefix >= 1024 tokens that repeats
        // across requests. The shared boundary marker protects callers from
        // accidentally invalidating the cache (e.g. timestamps spliced
        // anywhere in the prompt) by giving them a place to explicitly
        // separate stable prefix from per-call suffix. We strip the marker
        // itself — OpenAI just needs the prefix bytes to be identical; it
        // doesn't read any directive.
        var strippedPrompt = PromptCaching.Strip(prompt);

        var request = new OpenAICompletionRequest
        {
            Model = model,
            Messages = new List<OpenAIRequestMessage>
            {
                new() { Role = "user", Content = strippedPrompt }
            }
        };

        ApplyExecutionSettings(request, executionSettings);

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(
            $"{BaseUrl}/chat/completions",
            content,
            requestCancellation);
        var responseContent = await response.Content.ReadAsStringAsync(requestCancellation);

        stopwatch.Stop();

        if (response.IsSuccessStatusCode)
        {
            var completionResponse = JsonSerializer.Deserialize<OpenAICompletionResponse>(responseContent, SwarmJsonOptions.ExternalApi);
            if (completionResponse?.Error is { } bodyError)
                return ProviderReportedFailure(model, bodyError, "completion", stopwatch);
            var choice = completionResponse?.Choices?.FirstOrDefault();
            var responseText = choice?.Message?.Content ?? "";

            // Reasoning models (DeepSeek v4 family, OpenAI o-series via this
            // path when extended thinking is on) can return content="" with
            // all the budget spent on hidden chain-of-thought. The wire-level
            // HTTP 200 makes this look like a success, but downstream
            // validators see empty content and either reject the response or
            // cache it as a broken model. Surface it as a real error so the
            // caller (and the validation cache) get an actionable message.
            if (string.IsNullOrEmpty(responseText)
                && !string.IsNullOrEmpty(choice?.Message?.ReasoningContent)
                && string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                var reasoningTokens = completionResponse?.Usage?.CompletionTokens ?? 0;
                Logger.LogWarning(
                    "{Provider} model {Model} spent all {ReasoningTokens} output tokens on hidden reasoning_content with no visible reply; increase max_tokens or disable thinking",
                    ProviderName, model, reasoningTokens);
                return CreateErrorResponse(
                    model,
                    $"Model emitted only hidden reasoning ({reasoningTokens} tokens) and produced no visible content. " +
                    "Increase max_tokens to give it room for an answer, or pick a non-reasoning model variant.",
                    (int)stopwatch.ElapsedMilliseconds);
            }

            return new LlmResponse
            {
                Content = responseText,
                Provider = ProviderName,
                Model = string.IsNullOrWhiteSpace(completionResponse?.Model) ? model : completionResponse.Model!,
                InputTokens = completionResponse?.Usage?.PromptTokens ?? EstimateTokens(prompt),
                OutputTokens = completionResponse?.Usage?.CompletionTokens ?? EstimateTokens(responseText),
                TotalTokens = completionResponse?.Usage?.TotalTokens ?? 0,
                UsageMeasurementComplete = completionResponse?.Usage is
                    { PromptTokens: not null, CompletionTokens: not null },
                ProviderReportedCostUsd = completionResponse?.Usage?.Cost,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                WasSuccessful = true
            };
        }

        Logger.LogError("OpenAI API error: {StatusCode} - {Content}", response.StatusCode, responseContent);
        return CreateErrorResponse(model, $"API error: {response.StatusCode} - {responseContent}",
            (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
    }

    private async Task<LlmResponse> CallResponsesAsync(string prompt, string model,
        Dictionary<string, object>? settings, Stopwatch stopwatch, Func<string, Task>? onChunk = null,
        string? image = null)
    {
        var ct = LlmReservedExecutionSettings.GetRequestCancellationToken(settings);
        ct.ThrowIfCancellationRequested();
        try
        {
            var adapter = new ToolUse.OpenAIResponsesProvider($"{BaseUrl}/responses", async token =>
            {
                token.ThrowIfCancellationRequested();
                return new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetApiKeyAsync().ConfigureAwait(false));
            }, HttpClientFactory);
            var parts = new List<ChatContentPart> { new TextPart(PromptCaching.Strip(prompt)) };
            if (image is not null) parts.Add(new ImagePart(image, "image/jpeg"));
            var request = new ChatToolUseRequest
            {
                Messages = [new(ChatRole.User, parts)], Tools = [],
                Options = new() { OverrideProvider = ProviderName, OverrideModel = model, ExecutionSettings = settings,
                    PrivacyClass = LlmReservedExecutionSettings.GetRequestPrivacyClass(settings) }
            };
            ChatToolUseResponse? result = null;
            if (onChunk is null) result = await adapter.ChatAsync(request, ct).ConfigureAwait(false);
            else
                await foreach (var ev in adapter.StreamAsync(request, ct).ConfigureAwait(false))
                {
                    if (ev is TextDeltaEvent text) await onChunk(text.Delta).ConfigureAwait(false);
                    if (ev is MessageStoppedEvent stopped) result = stopped.Response;
                }
            if (result is null) throw new InvalidDataException("Responses stream omitted its final result.");
            return new LlmResponse
            {
                Provider = ProviderName, Model = result.Model, Content = result.Text ?? "",
                InputTokens = result.Usage?.InputTokens ?? 0, OutputTokens = result.Usage?.OutputTokens ?? 0,
                TotalTokens = (result.Usage?.InputTokens ?? 0) + (result.Usage?.OutputTokens ?? 0),
                CacheReadInputTokens = result.Usage?.CachedInputTokens ?? 0,
                UsageMeasurementComplete = result.Usage is not null,
                WasSuccessful = result.StopReason == ChatStopReason.EndTurn && !string.IsNullOrWhiteSpace(result.Text),
                ErrorMessage = result.ErrorMessage, FailureCategory = result.FailureCategory,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            var error = CreateErrorResponse(model, ex.Message, (int)stopwatch.ElapsedMilliseconds, ex.StatusCode);
            error.FailureCategory = ex is LlmProviderHttpException typed ? typed.Category : Services.LlmErrorPolicy.Classify(ex);
            return error;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            return CreateErrorResponse(model, "Invalid Astra Responses request or reply: " + ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Whether this endpoint accepts <c>stream_options.include_usage</c> so the
    /// final streamed chunk carries prompt/completion token counts. OpenAI,
    /// OpenRouter, DeepSeek, and xAI honour it; endpoints that reject unknown
    /// request fields should override to <c>false</c> and accept estimated usage.
    /// </summary>
    protected virtual bool SupportsStreamUsageOption => true;

    public override async Task<LlmResponse> GenerateStreamingResponseAsync(string prompt, string model,
        Func<string, Task> onChunk, Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;

        try
        {
            // Inside the try so a transport timeout on the Responses path is
            // reported like the chat-completions path, as a failed response.
            if (OpenAIResponsesContract.IsSelected(ProviderName, effectiveModel))
                return await CallResponsesAsync(prompt, effectiveModel, executionSettings, stopwatch, onChunk).ConfigureAwait(false);

            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            ApplyDefaultRequestHeaders(client, apiKey);
            if (requestCancellation.CanBeCanceled)
            {
                // The caller's token governs a streamed generation; HttpClient's
                // 100 s default would cut long generations off mid-stream.
                client.Timeout = Timeout.InfiniteTimeSpan;
            }

            var request = new OpenAICompletionRequest
            {
                Model = effectiveModel,
                Messages = new List<OpenAIRequestMessage>
                {
                    new() { Role = "user", Content = PromptCaching.Strip(prompt) }
                },
                Stream = true,
                StreamOptions = SupportsStreamUsageOption
                    ? new Dictionary<string, object> { ["include_usage"] = true }
                    : null
            };
            ApplyExecutionSettings(request, executionSettings);
            request.Stream = true;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestCancellation);
                stopwatch.Stop();
                Logger.LogError("OpenAI streaming API error: {StatusCode} - {Content}", response.StatusCode, errorBody);
                return CreateErrorResponse(effectiveModel, $"API error: {response.StatusCode} - {errorBody}",
                    (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
            }

            await using var body = await response.Content.ReadAsStreamAsync(requestCancellation);
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            string? responseModel = null;
            string? finishReason = null;
            OpenAIUsage? usage = null;

            await foreach (var sse in ToolUse.SseParser.ParseAsync(body, requestCancellation))
            {
                if (sse.Data == "[DONE]") break;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(sse.Data); }
                catch (JsonException)
                {
                    Logger.LogDebug("{Provider}: could not parse streaming SSE data.", ProviderName);
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    // OpenRouter and other compatible servers report a failure
                    // after the 200 headers as an error record; the partial
                    // text is not a completed answer.
                    if (root.TryGetProperty("error", out var streamError) && streamError.ValueKind != JsonValueKind.Null)
                    {
                        return ProviderReportedFailure(effectiveModel,
                            System.Text.Json.Nodes.JsonNode.Parse(streamError.GetRawText()), "stream", stopwatch);
                    }

                    if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                    {
                        responseModel ??= modelEl.GetString();
                    }

                    if (root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array
                        && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                        {
                            if (delta.TryGetProperty("content", out var textEl)
                                && textEl.ValueKind == JsonValueKind.String)
                            {
                                var text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    content.Append(text);
                                    await onChunk(text).ConfigureAwait(false);
                                }
                            }

                            if (delta.TryGetProperty("reasoning_content", out var reasoningEl)
                                && reasoningEl.ValueKind == JsonValueKind.String)
                            {
                                reasoning.Append(reasoningEl.GetString());
                            }
                        }

                        if (choice.TryGetProperty("finish_reason", out var finishEl)
                            && finishEl.ValueKind == JsonValueKind.String)
                        {
                            finishReason = finishEl.GetString();
                        }
                    }

                    if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        usage = JsonSerializer.Deserialize<OpenAIUsage>(usageEl.GetRawText(), SwarmJsonOptions.ExternalApi);
                    }
                }
            }

            stopwatch.Stop();
            var responseText = content.ToString();
            var diagnostics = new List<string>();
            if (!string.IsNullOrWhiteSpace(finishReason))
            {
                diagnostics.Add($"finish_reason={finishReason}");
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add("finish_reason_max_tokens");
                }
            }
            if (reasoning.Length > 0)
            {
                diagnostics.Add(responseText.Length == 0 ? "reasoning_channel_without_content" : "reasoning_channel_present");
            }

            return new LlmResponse
            {
                Content = responseText,
                Provider = ProviderName,
                Model = string.IsNullOrWhiteSpace(responseModel) ? effectiveModel : responseModel!,
                InputTokens = usage?.PromptTokens ?? EstimateTokens(prompt),
                OutputTokens = usage?.CompletionTokens ?? EstimateTokens(responseText),
                TotalTokens = usage?.TotalTokens ?? 0,
                UsageMeasurementComplete = usage is { PromptTokens: not null, CompletionTokens: not null },
                ProviderReportedCostUsd = usage?.Cost,
                ReasoningContent = reasoning.Length == 0 ? null : reasoning.ToString(),
                Diagnostics = diagnostics,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                WasSuccessful = true
            };
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Error generating streaming response with {Provider}", ProviderName);
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Detects model deprecation or "model not found" errors from OpenAI error message text.
    /// OpenAI returns these as 400/404 with error codes like "model_not_found" or "deprecated" in the body.
    /// </summary>
    private static bool IsModelDeprecationError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        var lower = errorMessage.ToLowerInvariant();
        return lower.Contains("model_not_found") ||
               lower.Contains("deprecated") ||
               lower.Contains("does not exist") ||
               lower.Contains("invalid model") ||
               (lower.Contains("404") && lower.Contains("model"));
    }

    /// <summary>
    /// Selects the best available chat model, skipping any that have been blacklisted as deprecated.
    /// </summary>
    private async Task<string?> SelectBestAvailableModelAsync()
    {
        var available = await GetAvailableModelsAsync();

        foreach (var preferred in PreferredModels)
        {
            if (_deprecatedModels.ContainsKey(preferred)) continue;
            if (available.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                return preferred;
        }

        // Dynamic fallback: any non-deprecated GPT chat model.
        // Sort by: newest version first, then prefer smaller/cheaper variants within a version.
        return available
            .Where(id => CatalogModelPrefixes.Count > 0
                         && CatalogModelPrefixes.Any(prefix =>
                             id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Where(id => !_deprecatedModels.ContainsKey(id))
            .Where(id => !id.Contains("instruct", StringComparison.OrdinalIgnoreCase))
            .Where(id => !id.Contains("realtime", StringComparison.OrdinalIgnoreCase))
            .Where(id => !id.Contains("audio", StringComparison.OrdinalIgnoreCase))
            .Where(id => !id.Contains("embedding", StringComparison.OrdinalIgnoreCase))
            .Where(id => !id.Contains("tts", StringComparison.OrdinalIgnoreCase))
            .Where(id => !id.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            .Where(id => !id.Contains("dall", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(ExtractVersionScore)   // Newer versions first (gpt-5 > gpt-4)
            .ThenByDescending(id => id.Contains("nano", StringComparison.OrdinalIgnoreCase) ? 2 :
                                    id.Contains("mini", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(id => id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Extracts a numeric version score from a model ID so newer models sort first.
    /// E.g. "gpt-5.2-mini" → 52, "gpt-5-mini" → 50, "gpt-4.1-mini" → 41, "gpt-4o" → 40.
    /// </summary>
    private static int ExtractVersionScore(string modelId)
    {
        // Strip prefix and any trailing variant (e.g. "-mini", "-nano", date snapshots)
        var lower = modelId.ToLowerInvariant();
        // Remove "gpt-" prefix
        var rest = lower.StartsWith("gpt-") ? lower[4..] : lower;
        // Take the version portion before the first non-numeric/non-dot/non-hyphen char
        // e.g. "5.2-mini" → "5.2", "4o-mini" → "4" (o counts as non-numeric)
        var versionPart = new string(rest.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (string.IsNullOrEmpty(versionPart)) return 0;

        // Parse "5.2" → 52, "5" → 50, "4.1" → 41, "4" → 40
        var segments = versionPart.Split('.');
        var major = int.TryParse(segments[0], out var m) ? m : 0;
        var minor = segments.Length > 1 && int.TryParse(segments[1], out var n) ? n : 0;
        return major * 10 + minor;
    }

    public override async Task<LlmResponse> GenerateVisionResponseAsync(string prompt, string imageBase64, string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = model;

        try
        {
            var apiKey = await GetApiKeyAsync();
            if (string.IsNullOrEmpty(effectiveModel))
                effectiveModel = await SelectBestAvailableModelAsync() ?? DefaultModel;
            if (OpenAIResponsesContract.IsSelected(ProviderName, effectiveModel))
                return await CallResponsesAsync(prompt, effectiveModel, executionSettings, stopwatch, image: imageBase64).ConfigureAwait(false);

            using var client = HttpClientFactory.CreateClient();
            ApplyDefaultRequestHeaders(client, apiKey);

            // Vision request with image
            var imageUrl = imageBase64.StartsWith("http") ? imageBase64 : $"data:image/jpeg;base64,{imageBase64}";

            var request = new OpenAICompletionRequest
            {
                Model = effectiveModel,
                Messages =
                [
                    new()
                    {
                        Role = "user",
                        Content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = imageUrl } }
                        }
                    }
                ]
            };

            // The same output-budget, sampler, reasoning and structured-output
            // rules as text requests: GPT-5/o-series reject max_tokens.
            ApplyExecutionSettings(request, executionSettings);
            if (request.MaxTokens is null && request.MaxCompletionTokens is null
                && !RequiresMaxCompletionTokens(request.Model))
            {
                // Older chat vision models default to a very small completion
                // when no budget is sent. Reasoning models keep their own
                // ceiling: a fixed total cap there would include hidden
                // reasoning and spend outside the caller's reservation.
                request.MaxTokens = DefaultVisionMaxTokens;
            }

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{BaseUrl}/chat/completions", content, requestCancellation);
            var responseContent = await response.Content.ReadAsStringAsync(requestCancellation);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var completionResponse = JsonSerializer.Deserialize<OpenAICompletionResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                if (completionResponse?.Error is { } bodyError)
                    return ProviderReportedFailure(effectiveModel, bodyError, "vision completion", stopwatch);
                var responseText = completionResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? "";

                return new LlmResponse
                {
                    Content = responseText,
                    Provider = ProviderName,
                    Model = effectiveModel,
                    InputTokens = completionResponse?.Usage?.PromptTokens ?? 0,
                    OutputTokens = completionResponse?.Usage?.CompletionTokens ?? 0,
                    TotalTokens = completionResponse?.Usage?.TotalTokens ?? 0,
                    UsageMeasurementComplete = completionResponse?.Usage is
                        { PromptTokens: not null, CompletionTokens: not null },
                    ProviderReportedCostUsd = completionResponse?.Usage?.Cost,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    WasSuccessful = true
                };
            }

            Logger.LogError("OpenAI vision API error: {StatusCode} - {Content}", response.StatusCode, responseContent);
            return CreateErrorResponse(effectiveModel, $"API error: {response.StatusCode} - {responseContent}",
                (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Error generating vision response with {Provider}", ProviderName);
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    private const int DefaultVisionMaxTokens = 4096;

    /// <summary>
    /// A failure the provider reported inside an HTTP 200 body or stream, as a
    /// failed response with its typed category and bounded identifiers.
    /// </summary>
    private LlmResponse ProviderReportedFailure(
        string model,
        System.Text.Json.Nodes.JsonNode? error,
        string context,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var failure = ToolUse.ProviderErrorRecord.FromOpenAICompatible(ProviderName, error, context);
        Logger.LogWarning("{Failure}", failure.Message);
        var response = CreateErrorResponse(model, failure.Message, (int)stopwatch.ElapsedMilliseconds, failure.StatusCode);
        response.FailureCategory = failure.Category;
        return response;
    }

    public override async Task<(bool IsHealthy, string Message)> TestConnectivityAsync()
    {
        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            ApplyDefaultRequestHeaders(client, apiKey);

            var response = await client.GetAsync($"{BaseUrl}/models");

            if (response.IsSuccessStatusCode)
                return (true, $"{ProviderName} API is reachable");

            return (false, $"{ProviderName} API returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Error connecting to {ProviderName}: {ex.Message}");
        }
    }

    public override async Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = string.IsNullOrEmpty(model) ? DefaultEmbeddingModel ?? "text-embedding-3-small" : model;

        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            ApplyDefaultRequestHeaders(client, apiKey);

            var request = new { input = text, model = effectiveModel };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{BaseUrl}/embeddings", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var embeddingResponse = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                var vector = embeddingResponse?.Data?.FirstOrDefault()?.Embedding;

                return new LlmEmbeddingResponse
                {
                    Success = true,
                    Vector = vector,
                    Provider = ProviderName,
                    Model = effectiveModel,
                    TokenCount = embeddingResponse?.Usage?.TotalTokens ?? EstimateTokens(text),
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
                };
            }

            return new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = effectiveModel,
                ErrorMessage = $"API error: {response.StatusCode}",
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = effectiveModel,
                ErrorMessage = ex.Message,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
            };
        }
    }

    public override async Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(
        IList<string> texts,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        // OpenAI supports batch embeddings
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = string.IsNullOrEmpty(model) ? DefaultEmbeddingModel ?? "text-embedding-3-small" : model;

        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            ApplyDefaultRequestHeaders(client, apiKey);

            var request = new { input = texts, model = effectiveModel };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{BaseUrl}/embeddings", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var embeddingResponse = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseContent, SwarmJsonOptions.ExternalApi);

                return embeddingResponse?.Data?.Select((d, i) => new LlmEmbeddingResponse
                {
                    Success = true,
                    Vector = d.Embedding,
                    Provider = ProviderName,
                    Model = effectiveModel,
                    TokenCount = EstimateTokens(texts[i]),
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds / texts.Count
                }).ToList() ?? new List<LlmEmbeddingResponse>();
            }

            return texts.Select(_ => new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = effectiveModel,
                ErrorMessage = $"API error: {response.StatusCode}",
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
            }).ToList();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return texts.Select(_ => new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = effectiveModel,
                ErrorMessage = ex.Message,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
            }).ToList();
        }
    }

    private protected virtual void ApplyExecutionSettings(OpenAICompletionRequest request, Dictionary<string, object>? executionSettings)
        => ApplyOpenAICompatibleExecutionSettings(request, executionSettings);

    internal static void ApplyOpenAICompatibleExecutionSettings(
        OpenAICompletionRequest request,
        Dictionary<string, object>? executionSettings)
    {
        if (executionSettings == null) return;

        if (executionSettings.TryGetValue("temperature", out var temp))
        {
            var parsedTemperature = Convert.ToDouble(temp);
            // GPT-5/o-series currently only accept default temperature behavior.
            if (!RequiresMaxCompletionTokens(request.Model) || Math.Abs(parsedTemperature - 1d) < 0.000001d)
            {
                request.Temperature = parsedTemperature;
            }
        }

        var hasCompletionBudget = executionSettings.TryGetValue("max_completion_tokens", out var maxCompletionTokens);
        var hasLegacyBudget = executionSettings.TryGetValue("max_tokens", out var maxTokens);
        if (hasCompletionBudget || hasLegacyBudget)
        {
            var parsedMaxTokens = Convert.ToInt32(hasCompletionBudget ? maxCompletionTokens : maxTokens);
            if (RequiresMaxCompletionTokens(request.Model))
            {
                // This is a total output ceiling, including reasoning. Increasing
                // it here would spend tokens outside the caller's reservation.
                // Budgeted truncation recovery belongs to the request owner.
                request.MaxCompletionTokens = parsedMaxTokens;
            }
            else
            {
                request.MaxTokens = parsedMaxTokens;
            }
        }

        if (executionSettings.TryGetValue("top_p", out var topP))
        {
            // GPT-5/o-series reject custom sampling params; omit top_p there.
            if (!RequiresMaxCompletionTokens(request.Model))
            {
                request.TopP = Convert.ToDouble(topP);
            }
        }

        if (executionSettings.TryGetValue("seed", out var seed) && SupportsSeed(request.Model))
        {
            request.Seed = Convert.ToInt32(seed);
        }

        if (executionSettings.TryGetValue("response_format", out var responseFormat))
        {
            request.ResponseFormat = responseFormat;
        }

        // reasoning_effort is only valid for reasoning models (gpt-5 family, o-series). Applying
        // it to gpt-4o / gpt-4.1 returns a 400 "unrecognized parameter", so gate on the same
        // predicate we use for max_completion_tokens routing.
        if (RequiresMaxCompletionTokens(request.Model)
            && executionSettings.TryGetValue("reasoning_effort", out var effort))
        {
            var str = NormalizeReasoningEffort(request.Model, effort?.ToString());
            if (str is not null)
            {
                request.ReasoningEffort = str;
            }
        }
    }

    private static bool RequiresMaxCompletionTokens(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var normalized = NormalizeModelFamilyId(model);
        return normalized.StartsWith("gpt-5", StringComparison.Ordinal) ||
               normalized.StartsWith("o1", StringComparison.Ordinal) ||
               normalized.StartsWith("o3", StringComparison.Ordinal) ||
               normalized.StartsWith("o4", StringComparison.Ordinal);
    }

    private static string? NormalizeReasoningEffort(string model, string? effort)
    {
        var normalizedEffort = effort?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedEffort))
        {
            return null;
        }

        if (normalizedEffort == "minimal")
        {
            return UsesNoneReasoningEffort(model) ? "none" : "minimal";
        }

        if (normalizedEffort == "none")
        {
            return UsesNoneReasoningEffort(model) ? "none" : "minimal";
        }

        return normalizedEffort is "low" or "medium" or "high" or "xhigh"
            ? normalizedEffort
            : null;
    }

    private static bool UsesNoneReasoningEffort(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalized = NormalizeModelFamilyId(model);
        return normalized.StartsWith("gpt-5.4", StringComparison.Ordinal)
               || normalized.StartsWith("gpt-5.5", StringComparison.Ordinal);
    }

    private static bool SupportsSeed(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalized = NormalizeModelFamilyId(model);
        return normalized.StartsWith("gpt-4o", StringComparison.Ordinal)
               || normalized.StartsWith("gpt-4.1", StringComparison.Ordinal)
               || normalized.StartsWith("gpt-3.5", StringComparison.Ordinal);
    }

    private static string NormalizeModelFamilyId(string model)
    {
        var normalized = model.Trim().TrimStart('~').ToLowerInvariant();
        var slashIdx = normalized.LastIndexOf('/');
        if (slashIdx >= 0 && slashIdx < normalized.Length - 1)
        {
            normalized = normalized[(slashIdx + 1)..];
        }

        return normalized;
    }
}

#region OpenAI API Models

internal class OpenAICompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OpenAIRequestMessage> Messages { get; set; } = new();

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; set; }

    /// <summary>
    /// Reasoning-model control (gpt-5 family, o-series). Newer GPT-5.4+ models accept
    /// "none" / "low" / "medium" / "high" / "xhigh"; older reasoning models use "minimal"
    /// instead of "none".
    /// Default (when omitted) is "medium" — which produces 30–120s latency for non-trivial prompts
    /// because the model does hidden chain-of-thought. For Background classification/extraction
    /// work we request the lowest supported effort; the quality hit is negligible but latency drops
    /// an order of magnitude.
    /// Rejected by non-reasoning models, so we only attach this when the target model is reasoning-capable.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Reasoning { get; set; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Thinking { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ResponseFormat { get; set; }

    [JsonPropertyName("models")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Models { get; set; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Provider { get; set; }

    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }

    [JsonPropertyName("plugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Dictionary<string, object>>? Plugins { get; set; }

    // OpenRouter usage-accounting opt-in ({"include": true}) so the response
    // carries the billed cost; omitted entirely for OpenAI-compatible hosts.
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? UsageAccounting { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    // {"include_usage": true} asks the endpoint to append a final chunk that
    // carries prompt/completion token counts; omitted for non-streaming calls.
    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? StreamOptions { get; set; }
}

internal class OpenAIRequestMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>A string for text turns, or a content-part array (text and image_url) for vision.</summary>
    [JsonPropertyName("content")]
    public object Content { get; set; } = "";
}

internal class OpenAIMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    // DeepSeek reasoning-model thinking output. Empty on non-reasoning providers.
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

internal class OpenAICompletionResponse
{
    // OpenRouter and several gateways answer an upstream failure with HTTP 200
    // and an error object in place of choices.
    [JsonPropertyName("error")]
    public System.Text.Json.Nodes.JsonNode? Error { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenAIChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAIUsage? Usage { get; set; }
}

internal class OpenAIChoice
{
    [JsonPropertyName("message")]
    public OpenAIMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal class OpenAIUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    // OpenRouter reports the billed dollar cost here when the request opts
    // into usage accounting; OpenAI itself never sends this field.
    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }
}

internal class OpenAIEmbeddingResponse
{
    [JsonPropertyName("data")]
    public List<OpenAIEmbeddingData>? Data { get; set; }

    [JsonPropertyName("usage")]
    public OpenAIUsage? Usage { get; set; }
}

internal class OpenAIEmbeddingData
{
    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

internal class OpenAIModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenAIModelEntry>? Data { get; set; }
}

internal class OpenAIModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

#endregion
