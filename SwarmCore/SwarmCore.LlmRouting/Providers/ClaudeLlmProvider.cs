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
/// Anthropic Claude LLM provider implementation for SwarmCore.
/// </summary>
public class ClaudeLlmProvider : LlmProviderBase, ILlmModelCatalogProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("Claude").DirectApi!;
    private readonly ClaudeProviderOptions _options;
    private const string AnthropicApiVersion = "2023-06-01";
    private List<string>? _cachedModels;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _catalogEfforts = new Dictionary<string, IReadOnlyList<string>>();

    public override string ProviderName => "Claude";
    public override bool SupportsStreaming => true;
    public override bool SupportsEmbeddings => false;
    public override string? DefaultEmbeddingModel => null;

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override string DefaultModel => _options.DefaultModel ?? Pack.DefaultModel;

    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    public ClaudeLlmProvider(
        ILogger<ClaudeLlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<ClaudeProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _options = options.Value;
    }

    public override Task<List<string>> GetAvailableModelsAsync() => DiscoverModelsAsync(false, CancellationToken.None);

    public async Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        => new(await DiscoverModelsAsync(true, cancellationToken).ConfigureAwait(false), "Provider API catalog") { ReasoningEfforts = _catalogEfforts };

    private async Task<List<string>> DiscoverModelsAsync(bool strict, CancellationToken ct)
    {
        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

            var models = new List<string>();
            var efforts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                using var response = await client.GetAsync(BuildApiUrl("models?limit=100" + (cursor is null ? "" : "&after_id=" + Uri.EscapeDataString(cursor))), ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                var page = JsonSerializer.Deserialize<ClaudeModelsResponse>(json, SwarmJsonOptions.ExternalApi);
                if (page?.Data is null) throw new InvalidDataException("The provider returned an invalid model catalog.");
                using (var document = JsonDocument.Parse(json))
                    foreach (var pair in ParseReasoningEfforts(document.RootElement)) efforts[pair.Key] = pair.Value;
                models.AddRange(page.Data.Select(m => m.Id).OfType<string>().Where(id => !string.IsNullOrWhiteSpace(id)));
                cursor = page.HasMore ? page.LastId : null;
                if (page.HasMore && (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor)))
                    throw new InvalidDataException("The provider returned an invalid model catalog cursor.");
            } while (cursor is not null);
            _catalogEfforts = efforts;
            _cachedModels = models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            Logger.LogInformation("Claude: fetched {Count} models from API", _cachedModels.Count);
            return _cachedModels;
        }
        catch (Exception ex)
        {
            if (strict) throw new InvalidOperationException("Could not load the provider model catalog. Check the connection and refresh, or enter a custom model ID.", ex);
            Logger.LogDebug(ex, "Failed to fetch Claude model list from API; using defaults");
        }

        if (strict) throw new InvalidOperationException("The provider model catalog is unavailable. Check the connection and refresh, or enter a custom model ID.");
        return _cachedModels ?? DefaultModels.ToList();
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseReasoningEfforts(JsonElement root)
    {
        var choices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in root.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } name
                || !model.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object
                || !capabilities.TryGetProperty("effort", out var effort) || effort.ValueKind != JsonValueKind.Object) continue;
            choices[name] = new[] { "low", "medium", "high", "xhigh", "max" }.Where(level =>
                effort.TryGetProperty(level, out var entry) && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True).ToArray();
        }
        return choices;
    }

    public override bool SupportsModel(string model)
    {
        if (_cachedModels is not null)
            return _cachedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
        return base.SupportsModel(model);
    }

    public override async Task<LlmResponse> GenerateResponseAsync(string prompt, string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var apiKey = await GetApiKeyAsync();
            var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;

            using var client = HttpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

            // Prompt caching is GA on the current Messages API (no header
            // needed for recent models), but some older entries — including
            // a couple of Haiku variants — still require the historical
            // beta header to have their usage block surface
            // cache_read_input_tokens / cache_creation_input_tokens. Setting
            // it defensively is a no-op on models where it isn't required.
            // Only emits the header when the prompt actually contains the
            // cache boundary marker so generic chats aren't affected.
            if (prompt.Contains(PromptCaching.CacheBoundaryMarker, StringComparison.Ordinal))
            {
                client.DefaultRequestHeaders.Add("anthropic-beta", "prompt-caching-2024-07-31");
            }

            var request = new ClaudeCompletionRequest
            {
                Model = effectiveModel,
                // Output ceiling, not a target — Anthropic bills only generated
                // tokens. 4096 silently truncated long answers/summaries at
                // stop_reason=max_tokens; 8192 gives normal generations room.
                MaxTokens = 8192,
                Messages = BuildUserMessages(prompt)
            };

            ApplyExecutionSettings(request, executionSettings);

            var json = JsonSerializer.Serialize(request);

            // One-time diagnostic: log the request shape (without the prompt
            // content itself) so we can verify cache_control is being emitted
            // on the first content block. Strip the "text" payloads so the
            // log stays short and doesn't leak prompt content.
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                var shapeDoc = JsonDocument.Parse(json);
                var shape = ExtractClaudeRequestShape(shapeDoc.RootElement);
                Logger.LogDebug("Claude request shape: {Shape}", shape);
            }

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(
                BuildApiUrl("messages"),
                content,
                requestCancellation);
            var responseContent = await response.Content.ReadAsStringAsync(requestCancellation);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var completionResponse = JsonSerializer.Deserialize<ClaudeCompletionResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                var responseText = ExtractTextContent(completionResponse);
                var diagnostics = BuildDiagnostics(completionResponse);

                var usage = completionResponse?.Usage;
                var input = usage?.InputTokens ?? EstimateTokens(prompt);
                var cacheRead = usage?.CacheReadInputTokens ?? 0;
                var cacheCreate = usage?.CacheCreationInputTokens ?? 0;

                // Anthropic's usage block reports input_tokens as "fresh"
                // input and cache_read/cache_creation as separate buckets.
                // Fold cache-related tokens into InputTokens so the total
                // reflects what the user actually sent, while keeping the
                // discount-eligible portion visible to the cost calculator.
                var totalInput = input + cacheRead + cacheCreate;

                Logger.LogInformation(
                    "Claude token usage for {Model}: {Fresh} fresh input, {CacheRead} cache-read, {CacheCreate} cache-created, {Output} output",
                    effectiveModel, input, cacheRead, cacheCreate, usage?.OutputTokens ?? 0);

                return new LlmResponse
                {
                    Content = responseText,
                    Provider = ProviderName,
                    Model = NormalizeReturnedModel(completionResponse?.Model),
                    InputTokens = totalInput,
                    CacheReadInputTokens = cacheRead,
                    CacheCreationInputTokens = cacheCreate,
                    OutputTokens = usage?.OutputTokens ?? EstimateTokens(responseText),
                    TotalTokens = totalInput + (usage?.OutputTokens ?? 0),
                    UsageMeasurementComplete = usage is
                        { InputTokens: not null, OutputTokens: not null },
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    WasSuccessful = true,
                    Diagnostics = diagnostics
                };
            }

            Logger.LogError("Claude API error: {StatusCode} - {Content}", response.StatusCode, responseContent);
            // Preserve the provider's structured error text for the shared failure
            // classifier. Anthropic reports exhausted credits as HTTP 400, so the
            // status alone looks like a generic provider error and leaves a dead
            // route enabled for the next scheduled health sweep.
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
            Logger.LogError(ex, "Error generating response with Claude");
            return CreateErrorResponse(model, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

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
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);
            if (prompt.Contains(PromptCaching.CacheBoundaryMarker, StringComparison.Ordinal))
            {
                client.DefaultRequestHeaders.Add("anthropic-beta", "prompt-caching-2024-07-31");
            }
            if (requestCancellation.CanBeCanceled)
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            }

            var request = new ClaudeCompletionRequest
            {
                Model = effectiveModel,
                MaxTokens = 8192,
                Messages = BuildUserMessages(prompt),
                Stream = true
            };
            ApplyExecutionSettings(request, executionSettings);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("messages"))
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
                Logger.LogError("Claude streaming API error: {StatusCode} - {Content}", response.StatusCode, errorBody);
                return CreateErrorResponse(effectiveModel, $"API error: {response.StatusCode} - {errorBody}",
                    (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
            }

            // Anthropic streams named events: message_start (model + input
            // usage), content_block_delta (text_delta chunks), message_delta
            // (stop_reason + output usage), message_stop, ping, and error.
            await using var body = await response.Content.ReadAsStreamAsync(requestCancellation);
            var content = new StringBuilder();
            string? responseModel = null;
            string? stopReason = null;
            int? inputTokens = null;
            int? outputTokens = null;
            var cacheRead = 0;
            var cacheCreate = 0;
            var contentBlocks = 0;
            var textBlocks = 0;

            await foreach (var sse in ToolUse.SseParser.ParseAsync(body, requestCancellation))
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(sse.Data); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                    switch (type)
                    {
                        case "message_start":
                            if (root.TryGetProperty("message", out var message))
                            {
                                if (message.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                                {
                                    responseModel = modelEl.GetString();
                                }
                                if (message.TryGetProperty("usage", out var startUsage))
                                {
                                    var parsed = JsonSerializer.Deserialize<ClaudeUsage>(startUsage.GetRawText(), SwarmJsonOptions.ExternalApi);
                                    inputTokens = parsed?.InputTokens;
                                    outputTokens = parsed?.OutputTokens;
                                    cacheRead = parsed?.CacheReadInputTokens ?? 0;
                                    cacheCreate = parsed?.CacheCreationInputTokens ?? 0;
                                }
                            }
                            break;

                        case "content_block_start":
                            contentBlocks++;
                            if (root.TryGetProperty("content_block", out var block)
                                && block.TryGetProperty("type", out var blockType)
                                && string.Equals(blockType.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                            {
                                textBlocks++;
                            }
                            break;

                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var delta)
                                && delta.TryGetProperty("type", out var deltaType)
                                && string.Equals(deltaType.GetString(), "text_delta", StringComparison.OrdinalIgnoreCase)
                                && delta.TryGetProperty("text", out var textEl)
                                && textEl.ValueKind == JsonValueKind.String)
                            {
                                var text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    content.Append(text);
                                    await onChunk(text).ConfigureAwait(false);
                                }
                            }
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("delta", out var messageDelta)
                                && messageDelta.TryGetProperty("stop_reason", out var stopEl)
                                && stopEl.ValueKind == JsonValueKind.String)
                            {
                                stopReason = stopEl.GetString();
                            }
                            if (root.TryGetProperty("usage", out var deltaUsage)
                                && deltaUsage.TryGetProperty("output_tokens", out var outEl)
                                && outEl.ValueKind == JsonValueKind.Number)
                            {
                                outputTokens = outEl.GetInt32();
                            }
                            break;

                        case "error":
                            stopwatch.Stop();
                            var errorText = root.TryGetProperty("error", out var errorEl)
                                ? errorEl.GetRawText()
                                : sse.Data;
                            Logger.LogError("Claude streaming error event: {Error}", errorText);
                            return CreateErrorResponse(effectiveModel, $"API error: {errorText}",
                                (int)stopwatch.ElapsedMilliseconds);
                    }
                }
            }

            stopwatch.Stop();
            var responseText = content.ToString();
            var freshInput = inputTokens ?? EstimateTokens(prompt);
            var totalInput = freshInput + cacheRead + cacheCreate;
            var diagnostics = new List<string>();
            if (!string.IsNullOrWhiteSpace(stopReason))
            {
                diagnostics.Add($"stop_reason={stopReason}");
                if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add("finish_reason_max_tokens");
                }
            }
            if (contentBlocks > 1)
            {
                diagnostics.Add($"content_blocks={contentBlocks}");
                diagnostics.Add($"text_blocks={textBlocks}");
            }
            if (contentBlocks - textBlocks > 0)
            {
                diagnostics.Add($"non_text_content_blocks={contentBlocks - textBlocks}");
            }

            return new LlmResponse
            {
                Content = responseText,
                Provider = ProviderName,
                Model = NormalizeReturnedModel(responseModel),
                InputTokens = totalInput,
                CacheReadInputTokens = cacheRead,
                CacheCreationInputTokens = cacheCreate,
                OutputTokens = outputTokens ?? EstimateTokens(responseText),
                TotalTokens = totalInput + (outputTokens ?? 0),
                UsageMeasurementComplete = inputTokens is not null && outputTokens is not null,
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
            Logger.LogError(ex, "Error generating streaming response with Claude");
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public override async Task<LlmResponse> GenerateVisionResponseAsync(string prompt, string imageBase64, string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var apiKey = await GetApiKeyAsync();
            var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;

            using var client = HttpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

            // Vision request with image
            var mediaType = DetectMediaType(imageBase64);
            var request = new
            {
                model = effectiveModel,
                max_tokens = 8192,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "image",
                                source = new { type = "base64", media_type = mediaType, data = imageBase64 }
                            },
                            new { type = "text", text = prompt }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(BuildApiUrl("messages"), content, requestCancellation);
            var responseContent = await response.Content.ReadAsStringAsync(requestCancellation);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var completionResponse = JsonSerializer.Deserialize<ClaudeCompletionResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                var responseText = ExtractTextContent(completionResponse);

                return new LlmResponse
                {
                    Content = responseText,
                    Provider = ProviderName,
                    Model = NormalizeReturnedModel(completionResponse?.Model),
                    InputTokens = completionResponse?.Usage?.InputTokens ?? 0,
                    OutputTokens = completionResponse?.Usage?.OutputTokens ?? 0,
                    TotalTokens = (completionResponse?.Usage?.InputTokens ?? 0) +
                                  (completionResponse?.Usage?.OutputTokens ?? 0),
                    UsageMeasurementComplete = completionResponse?.Usage is
                        { InputTokens: not null, OutputTokens: not null },
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    WasSuccessful = true,
                    Diagnostics = BuildDiagnostics(completionResponse)
                };
            }

            Logger.LogError("Claude vision API error: {StatusCode} - {Content}", response.StatusCode, responseContent);
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
            Logger.LogError(ex, "Error generating vision response with Claude");
            return CreateErrorResponse(model, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public override async Task<(bool IsHealthy, string Message)> TestConnectivityAsync()
    {
        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

            var response = await client.GetAsync(BuildApiUrl("models?limit=1"));

            if (response.IsSuccessStatusCode)
                return (true, "Claude API is reachable");

            return (false, $"Claude API returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Error connecting to Claude: {ex.Message}");
        }
    }

    private static void ApplyExecutionSettings(ClaudeCompletionRequest request,
        Dictionary<string, object>? executionSettings)
    {
        if (executionSettings == null) return;
        var effort = ClaudeEffortSettings.Resolve(request.Model, null, executionSettings);
        if (effort is not null) request.OutputConfig = new() { ["effort"] = effort };

        if (executionSettings.TryGetValue("temperature", out var temp)
            && SupportsTemperature(request.Model))
        {
            request.Temperature = Convert.ToDouble(temp);
        }

        if (executionSettings.TryGetValue("max_tokens", out var maxTokens))
            request.MaxTokens = Convert.ToInt32(maxTokens);
    }

    private static bool SupportsTemperature(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return true;
        }

        var normalized = model.Trim().ToLowerInvariant();
        return !normalized.StartsWith("claude-opus-4-7", StringComparison.Ordinal);
    }

    private static string ExtractTextContent(ClaudeCompletionResponse? response)
    {
        if (response?.Content is not { Count: > 0 } content)
        {
            return "";
        }

        return string.Concat(content
            .Where(block => string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(block => block.Text));
    }

    private static List<string> BuildDiagnostics(ClaudeCompletionResponse? response)
    {
        var diagnostics = new List<string>();
        if (response is null)
        {
            return diagnostics;
        }

        if (!string.IsNullOrWhiteSpace(response.StopReason))
        {
            diagnostics.Add($"stop_reason={response.StopReason}");
            if (string.Equals(response.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add("finish_reason_max_tokens");
            }
        }

        if (response.Content is { Count: > 0 } content)
        {
            var textBlocks = content.Count(block => string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase));
            if (content.Count > 1)
            {
                diagnostics.Add($"content_blocks={content.Count}");
                diagnostics.Add($"text_blocks={textBlocks}");
            }

            var nonTextBlocks = content.Count - textBlocks;
            if (nonTextBlocks > 0)
            {
                diagnostics.Add($"non_text_content_blocks={nonTextBlocks}");
            }
        }

        return diagnostics;
    }

    private static string NormalizeReturnedModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();

    private static string DetectMediaType(string base64Data)
    {
        if (base64Data.StartsWith("/9j/")) return "image/jpeg";
        if (base64Data.StartsWith("iVBORw")) return "image/png";
        if (base64Data.StartsWith("R0lGOD")) return "image/gif";
        if (base64Data.StartsWith("UklGR")) return "image/webp";
        return "image/jpeg";
    }

    /// <summary>
    /// Build the `messages` array for a Claude request. When the prompt
    /// contains <see cref="PromptCaching.CacheBoundaryMarker"/> we emit a
    /// two-block content array with <c>cache_control: ephemeral</c> on the
    /// prefix — Anthropic will cache that block on the first call and
    /// charge the cheap cache-read rate on subsequent calls with the same
    /// prefix. When no marker is present we fall back to the old string
    /// Content shape to keep requests minimal.
    /// </summary>
    /// <summary>
    /// Summarize a serialized Claude request body for diagnostic logging:
    /// shows structural keys + message/content-block counts + text lengths
    /// + whether cache_control is present, but strips the actual prompt
    /// text so logs stay under a reasonable size and don't leak content.
    /// </summary>
    private static string ExtractClaudeRequestShape(JsonElement root)
    {
        var sb = new StringBuilder();
        sb.Append("{model=").Append(root.TryGetProperty("model", out var m) ? m.GetString() : "?");
        sb.Append(", messages=[");
        if (root.TryGetProperty("messages", out var msgs))
        {
            var i = 0;
            foreach (var msg in msgs.EnumerateArray())
            {
                if (i++ > 0) sb.Append(", ");
                sb.Append("{role=").Append(msg.TryGetProperty("role", out var r) ? r.GetString() : "?");
                if (msg.TryGetProperty("content", out var c))
                {
                    if (c.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(", content=<string, len=").Append(c.GetString()?.Length ?? 0).Append(">");
                    }
                    else if (c.ValueKind == JsonValueKind.Array)
                    {
                        sb.Append(", content=[");
                        var j = 0;
                        foreach (var block in c.EnumerateArray())
                        {
                            if (j++ > 0) sb.Append(", ");
                            sb.Append("{type=").Append(block.TryGetProperty("type", out var t) ? t.GetString() : "?");
                            if (block.TryGetProperty("text", out var tx))
                                sb.Append(", text-len=").Append(tx.GetString()?.Length ?? 0);
                            if (block.TryGetProperty("cache_control", out var cc))
                                sb.Append(", cache_control=").Append(cc.GetRawText());
                            sb.Append("}");
                        }
                        sb.Append("]");
                    }
                }
                sb.Append("}");
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static List<ClaudeMessage> BuildUserMessages(string prompt)
    {
        var (prefix, suffix) = PromptCaching.Split(prompt);
        if (string.IsNullOrEmpty(prefix))
        {
            return new List<ClaudeMessage>
            {
                new() { Role = "user", Content = prompt }
            };
        }

        var blocks = new List<ClaudeContentBlock>
        {
            new()
            {
                Type = "text",
                Text = prefix,
                CacheControl = new ClaudeCacheControl { Type = "ephemeral" }
            },
            new()
            {
                Type = "text",
                Text = suffix
            }
        };

        return new List<ClaudeMessage>
        {
            new() { Role = "user", Content = blocks }
        };
    }

    private string BuildApiUrl(string relative)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.Endpoint)
            ? Pack.BaseUrl
            : _options.Endpoint!.Trim();
        return $"{baseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
    }
}

#region Claude API Models

internal class ClaudeCompletionRequest
{
    [JsonPropertyName("output_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? OutputConfig { get; set; }
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8192;

    [JsonPropertyName("messages")]
    public List<ClaudeMessage> Messages { get; set; } = new();

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }
}

internal class ClaudeMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>
    /// Content. For simple string prompts this is a raw string. For
    /// prompt-cached requests we emit a list of <see cref="ClaudeContentBlock"/>
    /// instead (a mix of cached and uncached text blocks). The serializer
    /// writes the non-null one; deserialization is never exercised for the
    /// request model.
    /// </summary>
    [JsonPropertyName("content")]
    public object? Content { get; set; }
}

internal class ClaudeContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClaudeCacheControl? CacheControl { get; set; }
}

internal class ClaudeCacheControl
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ephemeral";
}

internal class ClaudeCompletionResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("content")]
    public List<ClaudeContent>? Content { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    public ClaudeUsage? Usage { get; set; }
}

internal class ClaudeContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal class ClaudeUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }

    /// <summary>
    /// Tokens served from the ephemeral prompt cache (90% discount on
    /// Anthropic's list rate). Non-zero on the second-and-subsequent
    /// calls with the same cached prefix — which is exactly what the
    /// MessagePipeline prompt prefix is engineered to produce.
    /// </summary>
    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; set; }

    /// <summary>
    /// Tokens written to the ephemeral cache on this call (25% markup
    /// over the list input rate). Only populated on the first call that
    /// seeds a new prefix into the cache; subsequent calls within the
    /// 5-minute TTL get <see cref="CacheReadInputTokens"/> instead.
    /// </summary>
    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; set; }
}

internal class ClaudeModelsResponse
{
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("last_id")] public string? LastId { get; set; }
    [JsonPropertyName("data")]
    public List<ClaudeModelEntry>? Data { get; set; }
}

internal class ClaudeModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

#endregion
