using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Gemini tool-use translator. Speaks Google's <c>generateContent</c> /
/// <c>streamGenerateContent</c> endpoints on
/// <c>https://generativelanguage.googleapis.com/v1beta</c>.
///
/// <para>Schema translation (OpenAI/Anthropic-style → Gemini):</para>
/// <list type="bullet">
///   <item>Assistant <c>tool_use</c> → Gemini <c>functionCall</c>.</item>
///   <item>Tool <c>tool_result</c> → Gemini <c>functionResponse</c>.</item>
///   <item>System messages → top-level <c>systemInstruction</c>.</item>
///   <item>Gemini functionCalls have no id; we synthesise ids <c>"gem-{n}"</c>
///     keyed by call order within a turn. When a <see cref="ToolResultPart"/>
///     comes back, we strip the synthesised id and route by function name
///     (Gemini's API doesn't accept an id on the response side).</item>
/// </list>
///
/// <para>Thinking behaviour:</para>
/// <list type="bullet">
///   <item>Gemini 2.5 models have thinking ON by default, which materially
///     hurts latency. We default <c>thinkingConfig.thinkingBudget = 0</c>
///     for <see cref="LlmInteractivity.UserWaiting"/> and reasoning-heavy
///     Background calls, matching the pattern used for OpenAI gpt-5 /
///     o-series. Callers can override via
///     <c>LlmRouterOptions.ExecutionSettings["thinking_budget"]</c>.</item>
/// </list>
/// </summary>
public sealed class GeminiToolUseProvider : IToolUseProvider
{
    private readonly ILlmApiKeyProvider _apiKeyProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiToolUseProvider> _logger;
    private readonly GeminiProviderOptions _options;

    public string ProviderName => "Gemini";

    public GeminiToolUseProvider(
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<GeminiProviderOptions> options,
        ILogger<GeminiToolUseProvider> logger)
    {
        _apiKeyProvider = apiKeyProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Override for tests that want to point at a fake endpoint.</summary>
    internal string? EndpointOverride { get; init; }

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.Endpoint)
        ? "https://generativelanguage.googleapis.com/v1beta"
        : _options.Endpoint!.TrimEnd('/');

    public async Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        var model = request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "GeminiToolUseProvider requires LlmRouterOptions.OverrideModel to be set.");

        var apiKey = await _apiKeyProvider.GetApiKeyAsync("Gemini");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("No API key configured for Gemini.");
        }

        var payload = BuildRequestPayload(request, model);
        var url = EndpointOverride
                  ?? $"{BaseUrl}/models/{Uri.EscapeDataString(model)}:generateContent";

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        using var response = await ToolUseHttpRetry.SendAsync(http,
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseContentRead, _logger, "Gemini", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Gemini tool-use call failed with {Status}: {Body}", (int)response.StatusCode, Truncate(body, 500));
            throw new HttpRequestException(
                $"Gemini tool-use call failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(body, 500)}",
                inner: null,
                response.StatusCode);
        }

        return ParseResponse(body, model);
    }

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(
        ChatToolUseRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var model = request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "GeminiToolUseProvider requires LlmRouterOptions.OverrideModel to be set.");

        var apiKey = await _apiKeyProvider.GetApiKeyAsync("Gemini");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("No API key configured for Gemini.");
        }

        var payload = BuildRequestPayload(request, model);
        var url = EndpointOverride
                  ?? $"{BaseUrl}/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        using var response = await ToolUseHttpRetry.SendAsync(http,
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseHeadersRead, _logger, "Gemini", ct);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Gemini streaming call failed with {Status}: {Body}", (int)response.StatusCode, Truncate(errBody, 500));
            throw new HttpRequestException(
                $"Gemini streaming call failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(errBody, 500)}",
                inner: null,
                response.StatusCode);
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);

        // Each SSE data line is a complete GenerateContentResponse with INCREMENTAL
        // parts (Gemini doesn't send cumulative text — each event is a delta).
        // We just need to flush parts through as they arrive.
        var textBuilder = new StringBuilder();
        var finalToolCalls = new List<ToolUsePart>();
        string? finishReason = null;
        ChatUsage? usage = null;
        var toolCallCounter = 0;
        var toolCallNonce = NewToolCallNonce();

        await foreach (var sse in SseParser.ParseAsync(body, ct))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(sse.Data); }
            catch (JsonException) { continue; }
            if (node is not JsonObject evt) continue;

            // Each event: { candidates: [{ content: { parts: [...] }, finishReason: ... }], usageMetadata: {...} }
            if (evt["candidates"] is JsonArray candidates && candidates.Count > 0
                && candidates[0] is JsonObject cand)
            {
                if (cand["finishReason"] is JsonValue fr)
                {
                    finishReason = fr.GetValue<string?>();
                }

                if (cand["content"] is JsonObject cc
                    && cc["parts"] is JsonArray parts)
                {
                    foreach (var partNode in parts)
                    {
                        if (partNode is not JsonObject partObj) continue;

                        if (partObj["text"] is JsonValue textVal)
                        {
                            var text = textVal.GetValue<string?>();
                            if (!string.IsNullOrEmpty(text))
                            {
                                textBuilder.Append(text);
                                yield return new TextDeltaEvent(text!);
                            }
                        }
                        else if (partObj["functionCall"] is JsonObject fc)
                        {
                            var name = (string?)fc["name"];
                            if (string.IsNullOrEmpty(name)) continue;
                            var args = fc["args"] as JsonObject ?? new JsonObject();
                            var id = BuildToolCallId(toolCallNonce, Interlocked.Increment(ref toolCallCounter));
                            var part = new ToolUsePart(id, name!, (JsonObject)args.DeepClone());
                            finalToolCalls.Add(part);

                            // Gemini emits the whole function call at once — no
                            // input-fragment stream — so we fire started/finished
                            // back-to-back and skip delta events.
                            yield return new ToolUseStartedEvent(id, name!);
                            yield return new ToolUseFinishedEvent(part);
                        }
                    }
                }
            }

            if (evt["usageMetadata"] is JsonObject um)
            {
                var input = (int?)um["promptTokenCount"] ?? usage?.InputTokens ?? 0;
                var output = (int?)um["candidatesTokenCount"] ?? usage?.OutputTokens ?? 0;
                usage = new ChatUsage(input, output);
            }
        }

        var stop = MapFinishReason(finishReason, finalToolCalls.Count > 0);
        yield return new MessageStoppedEvent(new ChatToolUseResponse
        {
            Text = textBuilder.Length == 0 ? null : textBuilder.ToString(),
            ToolCalls = finalToolCalls,
            StopReason = stop,
            FailureCategory = RefusalCategory(finishReason),
            ErrorMessage = RefusalMessage(finishReason, textBuilder.ToString()),
            Provider = ProviderName,
            Model = model,
            Usage = usage,
        });
    }

    // ────────────────────────────────────────────────────────────────────
    // Request building
    // ────────────────────────────────────────────────────────────────────

    private JsonObject BuildRequestPayload(ChatToolUseRequest request, string model)
    {
        var contents = new JsonArray();
        var systemText = new StringBuilder();

        // Top-level system instruction is Gemini's equivalent of Anthropic's
        // `system` field. Combine SystemPrompt + any ChatRole.System messages.
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            systemText.Append(request.SystemPrompt);
        }

        // Pass 1: collapse consecutive Tool-role messages into a single user
        // message with multiple functionResponse parts (Gemini doesn't have a
        // "tool" role — function responses go back as user-role parts).
        var i = 0;
        while (i < request.Messages.Count)
        {
            var current = request.Messages[i];

            if (current.Role == ChatRole.System)
            {
                foreach (var part in current.Parts.OfType<TextPart>())
                {
                    if (systemText.Length > 0) systemText.Append('\n');
                    systemText.Append(part.Text);
                }
                i++;
                continue;
            }

            if (current.Role == ChatRole.Tool)
            {
                var partsArr = new JsonArray();
                while (i < request.Messages.Count && request.Messages[i].Role == ChatRole.Tool)
                {
                    foreach (var result in request.Messages[i].Parts.OfType<ToolResultPart>())
                    {
                        // Gemini's functionResponse requires the function name (not an id).
                        // We recover it by walking backwards from THIS result's position to
                        // the nearest preceding assistant message that carries the id, so a
                        // transcript whose earlier turns reused an id (legacy "gem-1"
                        // counters reset per reply) still maps each result to its own call.
                        // If none is found (malformed conversation), fall back to the id.
                        var fnName = ResolveFunctionName(request.Messages, result.ToolUseId, i) ?? result.ToolUseId;
                        partsArr.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = fnName,
                                ["response"] = BuildFunctionResponsePayload(result),
                            }
                        });
                    }
                    i++;
                }
                if (partsArr.Count > 0)
                {
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = partsArr });
                }
                continue;
            }

            contents.Add(new JsonObject
            {
                ["role"] = current.Role == ChatRole.Assistant ? "model" : "user",
                ["parts"] = BuildPartsArray(current),
            });
            i++;
        }

        var payload = new JsonObject
        {
            ["contents"] = contents,
        };

        if (systemText.Length > 0)
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemText.ToString() })
            };
        }

        // Tools → Gemini's functionDeclarations. InputSchema is already JSON Schema;
        // Gemini accepts the Schema subset (type, properties, required, items, enum).
        if (request.Tools.Count > 0)
        {
            var decls = new JsonArray();
            foreach (var tool in request.Tools)
            {
                decls.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    // Catalog tools generated from CLR types already use the
                    // shared provider-safe subset. MCP and other external
                    // catalogs can supply full JSON Schema, however, and the
                    // Gemini endpoint rejects otherwise harmless keywords such
                    // as $schema and additionalProperties before inference.
                    // Normalize at the physical provider boundary so one
                    // incompatible external tool cannot make the entire
                    // assistant request fail with HTTP 400.
                    ["parameters"] = NormalizeParametersSchema(tool.InputSchema),
                });
            }
            payload["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = decls });
            if (request.DisableToolCalls)
            {
                payload["toolConfig"] = new JsonObject
                {
                    ["functionCallingConfig"] = new JsonObject { ["mode"] = "NONE" }
                };
            }
            else if (request.RequireToolCall)
            {
                payload["toolConfig"] = new JsonObject
                {
                    ["functionCallingConfig"] = new JsonObject { ["mode"] = "ANY" }
                };
            }
        }

        // Generation config + thinking control.
        var genConfig = new JsonObject();
        if (request.Options?.MaxTokens is int maxTokens)
        {
            genConfig["maxOutputTokens"] = maxTokens;
        }
        if (request.Options?.Temperature is float temp)
        {
            genConfig["temperature"] = temp;
        }

        // Thinking budget — 2.5 family defaults to ON which kills latency.
        // Respect explicit caller override, otherwise disable for UserWaiting
        // and default to 0 for Background too (matches OpenAI reasoning_effort
        // posture). Non-2.5 models silently ignore the field.
        var thinkingBudget = ResolveThinkingBudget(request.Options, model);
        if (thinkingBudget.HasValue)
        {
            genConfig["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = thinkingBudget.Value
            };
        }

        if (genConfig.Count > 0)
        {
            payload["generationConfig"] = genConfig;
        }

        return payload;
    }

    /// <summary>
    /// Reduces arbitrary catalog JSON Schema to the conservative subset
    /// accepted by Gemini function declarations. Tool catalogs are
    /// provider-neutral and may include schemas supplied by MCP servers, so
    /// this compatibility translation belongs here rather than in the shared
    /// catalog or schema generator.
    /// </summary>
    internal static JsonObject NormalizeParametersSchema(JsonObject schema)
    {
        var normalized = NormalizeSchemaObject(schema);
        if (HasType(normalized, "object"))
        {
            normalized["properties"] ??= new JsonObject();
            return normalized;
        }

        // Gemini function parameters are object-shaped. Preserve primitive or
        // array schemas under one explicit input field rather than sending an
        // invalid top-level declaration.
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["input"] = normalized },
            ["required"] = new JsonArray { "input" },
        };
    }

    private static JsonObject NormalizeSchemaObject(JsonObject schema)
    {
        var normalized = new JsonObject();

        if (TryNormalizeType(schema["type"], out var normalizedType))
        {
            normalized["type"] = normalizedType;
        }
        else if (schema["properties"] is JsonObject)
        {
            normalized["type"] = "object";
        }
        else if (schema["items"] is JsonObject)
        {
            normalized["type"] = "array";
        }

        if (schema["description"] is JsonValue description
            && description.TryGetValue<string>(out var descriptionText)
            && !string.IsNullOrWhiteSpace(descriptionText))
        {
            normalized["description"] = descriptionText;
        }

        if (schema["enum"] is JsonArray enumValues)
        {
            var accepted = new JsonArray();
            foreach (var value in enumValues)
            {
                if (value is JsonValue)
                {
                    accepted.Add(value.DeepClone());
                }
            }
            if (accepted.Count > 0)
            {
                normalized["enum"] = accepted;
            }
        }

        // date-time is the only format emitted by our canonical schema
        // generator and accepted consistently by the Gemini Schema subset.
        if (schema["format"] is JsonValue format
            && format.TryGetValue<string>(out var formatText)
            && string.Equals(formatText, "date-time", StringComparison.OrdinalIgnoreCase))
        {
            normalized["format"] = "date-time";
        }

        if (schema["properties"] is JsonObject properties)
        {
            var normalizedProperties = new JsonObject();
            foreach (var (name, propertySchema) in properties)
            {
                normalizedProperties[name] = propertySchema is JsonObject propertyObject
                    ? NormalizeSchemaObject(propertyObject)
                    : new JsonObject { ["type"] = "object" };
            }
            normalized["properties"] = normalizedProperties;
        }

        if (schema["required"] is JsonArray required)
        {
            var normalizedRequired = new JsonArray();
            foreach (var value in required)
            {
                if (value is JsonValue requiredValue
                    && requiredValue.TryGetValue<string>(out var requiredName)
                    && !string.IsNullOrWhiteSpace(requiredName))
                {
                    normalizedRequired.Add(requiredName);
                }
            }
            if (normalizedRequired.Count > 0)
            {
                normalized["required"] = normalizedRequired;
            }
        }

        if (schema["items"] is JsonObject items)
        {
            normalized["items"] = NormalizeSchemaObject(items);
        }

        // An unsupported or reference-only schema still needs a usable,
        // permissive shape after provider-specific keywords are removed.
        normalized["type"] ??= "object";
        if (HasType(normalized, "object"))
        {
            normalized["properties"] ??= new JsonObject();
        }

        return normalized;
    }

    private static bool TryNormalizeType(JsonNode? typeNode, out string type)
    {
        if (typeNode is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var rawType)
            && IsSupportedType(rawType))
        {
            type = rawType.ToLowerInvariant();
            return true;
        }

        // JSON Schema nullable unions commonly arrive from external MCP
        // catalogs as ["string", "null"]. Gemini expects one concrete type.
        if (typeNode is JsonArray union)
        {
            foreach (var candidate in union)
            {
                if (candidate is JsonValue candidateValue
                    && candidateValue.TryGetValue<string>(out var rawCandidate)
                    && !string.Equals(rawCandidate, "null", StringComparison.OrdinalIgnoreCase)
                    && IsSupportedType(rawCandidate))
                {
                    type = rawCandidate.ToLowerInvariant();
                    return true;
                }
            }
        }

        type = string.Empty;
        return false;
    }

    private static bool IsSupportedType(string? type) => type is not null
        && (type.Equals("object", StringComparison.OrdinalIgnoreCase)
            || type.Equals("array", StringComparison.OrdinalIgnoreCase)
            || type.Equals("string", StringComparison.OrdinalIgnoreCase)
            || type.Equals("number", StringComparison.OrdinalIgnoreCase)
            || type.Equals("integer", StringComparison.OrdinalIgnoreCase)
            || type.Equals("boolean", StringComparison.OrdinalIgnoreCase));

    private static bool HasType(JsonObject schema, string expected) =>
        schema["type"] is JsonValue type
        && type.TryGetValue<string>(out var actual)
        && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static JsonArray BuildPartsArray(ChatMessage m)
    {
        var arr = new JsonArray();
        foreach (var part in m.Parts)
        {
            switch (part)
            {
                case TextPart text when !string.IsNullOrEmpty(text.Text):
                    arr.Add(new JsonObject { ["text"] = text.Text });
                    break;

                case ToolUsePart toolUse:
                    // Round-trip back to Gemini as a functionCall. The id is
                    // synthesised on the outbound side and NOT sent — Gemini's
                    // functionCall shape has no id field.
                    arr.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = toolUse.Name,
                            ["args"] = (JsonNode)toolUse.Input.DeepClone(),
                        }
                    });
                    break;

                case ImagePart image when !string.IsNullOrEmpty(image.Base64Data):
                    // Gemini's inline image part — mimeType + raw base64 data.
                    // No data: URL prefix here, unlike OpenAI.
                    arr.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = image.MediaType,
                            ["data"] = image.Base64Data,
                        }
                    });
                    break;

                // ToolResultPart isn't valid inside an assistant message in the
                // canonical shape; tool results become their own user-role
                // message at the BuildRequestPayload level.
            }
        }
        return arr;
    }

    private static JsonObject BuildFunctionResponsePayload(ToolResultPart result)
    {
        // Gemini's functionResponse.response is a free-form object. If the
        // caller gave us JSON, pass it through; otherwise wrap as { content }.
        try
        {
            var parsed = JsonNode.Parse(result.Content);
            if (parsed is JsonObject obj)
            {
                return obj;
            }
        }
        catch (JsonException)
        {
            // Fall through to the wrapped shape.
        }

        return new JsonObject
        {
            ["content"] = result.Content,
            ["isError"] = result.IsError,
        };
    }

    private static string? ResolveFunctionName(IReadOnlyList<ChatMessage> messages, string toolUseId, int resultIndex)
    {
        // Walk backwards from the result's own position: the nearest preceding
        // assistant message that emitted this id is the call being answered.
        for (var j = Math.Min(resultIndex, messages.Count) - 1; j >= 0; j--)
        {
            var m = messages[j];
            if (m.Role != ChatRole.Assistant) continue;
            foreach (var part in m.Parts.OfType<ToolUsePart>())
            {
                if (part.Id == toolUseId) return part.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// Gemini functionCalls carry no id, so one is synthesised per call. The id
    /// embeds a per-reply nonce so calls from different replies never collide —
    /// a per-reply counter alone ("gem-1", "gem-2"…) made every turn's first
    /// call share an id with every other turn's first call.
    /// </summary>
    private static string NewToolCallNonce() => Guid.NewGuid().ToString("N")[..12];

    private static string BuildToolCallId(string nonce, int ordinal) => $"gem-{nonce}-{ordinal}";

    // ────────────────────────────────────────────────────────────────────
    // Response parsing
    // ────────────────────────────────────────────────────────────────────

    private ChatToolUseResponse ParseResponse(string body, string model)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? text = null;
        var toolCalls = new List<ToolUsePart>();
        string? finishReason = null;
        ChatUsage? usage = null;
        var toolCallCounter = 0;
        var toolCallNonce = NewToolCallNonce();

        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0)
        {
            var cand = candidates[0];
            if (cand.TryGetProperty("finishReason", out var fr))
            {
                finishReason = fr.GetString();
            }

            if (cand.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var tEl))
                    {
                        sb.Append(tEl.GetString());
                    }
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var name = fc.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;

                        var argsNode = fc.TryGetProperty("args", out var argsEl)
                            ? JsonNode.Parse(argsEl.GetRawText()) as JsonObject
                            : null;
                        var id = BuildToolCallId(toolCallNonce, ++toolCallCounter);
                        toolCalls.Add(new ToolUsePart(id, name!, argsNode ?? new JsonObject()));
                    }
                }
                if (sb.Length > 0) text = sb.ToString();
            }
        }

        if (root.TryGetProperty("usageMetadata", out var um))
        {
            var input = um.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            var output = um.TryGetProperty("candidatesTokenCount", out var ot) ? ot.GetInt32() : 0;
            usage = new ChatUsage(input, output);
        }

        return new ChatToolUseResponse
        {
            Text = text,
            ToolCalls = toolCalls,
            StopReason = MapFinishReason(finishReason, toolCalls.Count > 0),
            FailureCategory = RefusalCategory(finishReason),
            ErrorMessage = RefusalMessage(finishReason, text),
            Provider = ProviderName,
            Model = model,
            Usage = usage,
        };
    }

    /// <summary>Safety and prohibited-content finishes are typed content-policy failures, not ordinary errors.</summary>
    private static LlmFailureCategory? RefusalCategory(string? finishReason)
        => finishReason is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "RECITATION" or "SPII" ? LlmFailureCategory.ContentPolicy : null;

    private static string? RefusalMessage(string? finishReason, string? text)
        => RefusalCategory(finishReason) is null
            ? null
            : $"Gemini declined this request (content policy finish reason: {finishReason})." + (string.IsNullOrWhiteSpace(text) ? "" : " " + Truncate(text.Trim(), 300));

    private static ChatStopReason MapFinishReason(string? finishReason, bool hasToolCalls)
    {
        // Gemini emits: STOP, MAX_TOKENS, SAFETY, RECITATION, LANGUAGE, OTHER,
        // BLOCKLIST, PROHIBITED_CONTENT, SPII, MALFORMED_FUNCTION_CALL.
        // There's no dedicated "tool_use" finish — you detect it by presence of
        // functionCall parts in the content.
        return finishReason switch
        {
            "MAX_TOKENS" => ChatStopReason.MaxTokens,
            "STOP" => hasToolCalls ? ChatStopReason.ToolUse : ChatStopReason.EndTurn,
            "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "RECITATION" or "SPII" => ChatStopReason.Error,
            null or "" => hasToolCalls ? ChatStopReason.ToolUse : ChatStopReason.EndTurn,
            _ => hasToolCalls ? ChatStopReason.ToolUse : ChatStopReason.Other,
        };
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static int? ResolveThinkingBudget(LlmRouterOptions? options, string model)
    {
        // Only 2.5-generation models honour thinkingConfig.
        var is25 = model.Contains("2.5", StringComparison.OrdinalIgnoreCase);
        if (!is25) return null;

        // Explicit caller override wins.
        if (options?.ExecutionSettings != null
            && options.ExecutionSettings.TryGetValue("thinking_budget", out var raw))
        {
            if (raw is int i) return i;
            if (int.TryParse(raw?.ToString(), out var parsed)) return parsed;
        }

        // Default posture: disable thinking for UserWaiting (latency-sensitive)
        // and Background (we control cost budgets, not the model). Callers can
        // flip back on via ExecutionSettings above when they actually want it.
        return 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
