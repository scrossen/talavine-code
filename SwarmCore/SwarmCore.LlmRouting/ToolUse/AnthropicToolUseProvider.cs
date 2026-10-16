using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Anthropic tool-use translator. Speaks the <c>/v1/messages</c> endpoint
/// with native <c>tool_use</c> / <c>tool_result</c> content blocks.
/// </summary>
public sealed class AnthropicToolUseProvider : IToolUseProvider
{
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicApiVersion = "2023-06-01";

    private readonly ILlmApiKeyProvider _apiKeyProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnthropicToolUseProvider> _logger;

    public string ProviderName => "Claude";

    public AnthropicToolUseProvider(
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<AnthropicToolUseProvider> logger)
    {
        _apiKeyProvider = apiKeyProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>For testing — allows overriding the endpoint URL (e.g. via a fake HttpMessageHandler).</summary>
    internal string EndpointOverride { get; init; } = MessagesUrl;

    public async Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        var model = request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "AnthropicToolUseProvider requires LlmRouterOptions.OverrideModel to be set.");

        var apiKey = await _apiKeyProvider.GetApiKeyAsync("Claude");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("No API key configured for Claude.");
        }

        var nameMap = BuildNameMap(request);
        var payload = BuildRequestPayload(request, model);

        using var http = _httpClientFactory.CreateClient();
        // Disable HttpClient's default 100s timeout — the caller's CT (engine
        // HardTimeout, default 10 min) is authoritative. Large Claude Opus
        // tool-use responses can exceed 100s on their own.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

        using var response = await ToolUseHttpRetry.SendAsync(http,
            () => new HttpRequestMessage(HttpMethod.Post, EndpointOverride) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseContentRead, _logger, "Anthropic", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic tool-use call failed with {Status}: {Body}", (int)response.StatusCode, Truncate(body, 500));
            throw new HttpRequestException(
                $"Anthropic tool-use call failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(body, 500)}",
                inner: null,
                response.StatusCode);
        }

        return ParseResponse(body, model, nameMap);
    }

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(ChatToolUseRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "AnthropicToolUseProvider requires LlmRouterOptions.OverrideModel to be set.");

        var apiKey = await _apiKeyProvider.GetApiKeyAsync("Claude");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("No API key configured for Claude.");
        }

        var nameMap = BuildNameMap(request);
        var payload = BuildRequestPayload(request, model);
        payload["stream"] = true;

        using var http = _httpClientFactory.CreateClient();
        // Streaming keeps the connection open for the entire generation. The
        // caller's CT (engine HardTimeout) is authoritative — disable the
        // HttpClient 100s default so long generations aren't cut off mid-way.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

        using var response = await ToolUseHttpRetry.SendAsync(http,
            () => new HttpRequestMessage(HttpMethod.Post, EndpointOverride) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseHeadersRead, _logger, "Anthropic", ct);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Anthropic streaming call failed with {Status}: {Body}", (int)response.StatusCode, Truncate(errBody, 500));
            throw new HttpRequestException(
                $"Anthropic streaming call failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(errBody, 500)}",
                inner: null,
                response.StatusCode);
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);

        // Anthropic's stream emits named events per content block:
        //   message_start, content_block_start, content_block_delta,
        //   content_block_stop, message_delta, message_stop, ping.
        // Tool-use blocks carry their id+name on content_block_start and
        // their input JSON as input_json_delta fragments.
        var blocks = new Dictionary<int, AnthropicBlockAccumulator>();
        var textBuilder = new StringBuilder();
        var finalToolCalls = new List<ToolUsePart>();
        string? stopReason = null;
        ChatUsage? usage = null;

        await foreach (var sse in SseParser.ParseAsync(body, ct))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(sse.Data); }
            catch (JsonException) { continue; }
            if (node is not JsonObject evt) continue;
            var type = (string?)evt["type"];

            switch (type)
            {
                case "message_start":
                    // Usage input tokens arrive here.
                    if (evt["message"] is JsonObject ms && ms["usage"] is JsonObject startUsage)
                    {
                        var input = (int?)startUsage["input_tokens"] ?? 0;
                        var output = (int?)startUsage["output_tokens"] ?? 0;
                        // Anthropic's input_tokens excludes cached and cache-written tokens; InputTokens is the
                        // total the request carried, with the cache split kept alongside for accounting.
                        var cached = (int?)startUsage["cache_read_input_tokens"] ?? 0;
                        var created = (int?)startUsage["cache_creation_input_tokens"] ?? 0;
                        usage = new ChatUsage(input + cached + created, output) { CachedInputTokens = cached, CacheCreationInputTokens = created };
                    }
                    break;

                case "content_block_start":
                {
                    var index = (int?)evt["index"] ?? 0;
                    if (evt["content_block"] is not JsonObject cb) break;
                    var blockType = (string?)cb["type"];
                    var acc = new AnthropicBlockAccumulator { Type = blockType ?? "text" };
                    if (blockType == "tool_use")
                    {
                        acc.Id = (string?)cb["id"];
                        acc.Name = ResolveOriginalName((string?)cb["name"], nameMap);
                        if (!string.IsNullOrEmpty(acc.Id) && !string.IsNullOrEmpty(acc.Name))
                        {
                            yield return new ToolUseStartedEvent(acc.Id, acc.Name);
                        }
                    }
                    blocks[index] = acc;
                    break;
                }

                case "content_block_delta":
                {
                    var index = (int?)evt["index"] ?? 0;
                    if (evt["delta"] is not JsonObject delta) break;
                    var deltaType = (string?)delta["type"];
                    if (deltaType == "text_delta")
                    {
                        var text = (string?)delta["text"];
                        if (!string.IsNullOrEmpty(text))
                        {
                            textBuilder.Append(text);
                            yield return new TextDeltaEvent(text);
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var frag = (string?)delta["partial_json"];
                        if (!string.IsNullOrEmpty(frag) && blocks.TryGetValue(index, out var acc))
                        {
                            acc.Args.Append(frag);
                            if (!string.IsNullOrEmpty(acc.Id))
                            {
                                yield return new ToolUseInputDeltaEvent(acc.Id, frag);
                            }
                        }
                    }
                    break;
                }

                case "content_block_stop":
                {
                    var index = (int?)evt["index"] ?? 0;
                    if (!blocks.TryGetValue(index, out var acc)) break;
                    if (acc.Type == "tool_use" && !string.IsNullOrEmpty(acc.Id) && !string.IsNullOrEmpty(acc.Name))
                    {
                        JsonObject input;
                        try { input = JsonNode.Parse(acc.Args.Length == 0 ? "{}" : acc.Args.ToString()) as JsonObject ?? new JsonObject(); }
                        catch (JsonException)
                        {
                            _logger.LogWarning("Anthropic: tool '{Name}' streamed malformed argument JSON; defaulting to empty object.", acc.Name);
                            input = new JsonObject();
                        }
                        var part = new ToolUsePart(acc.Id, acc.Name, input);
                        finalToolCalls.Add(part);
                        yield return new ToolUseFinishedEvent(part);
                    }
                    break;
                }

                case "message_delta":
                {
                    if (evt["delta"] is JsonObject msgDelta)
                    {
                        var sr = (string?)msgDelta["stop_reason"];
                        if (!string.IsNullOrEmpty(sr)) stopReason = sr;
                    }
                    if (evt["usage"] is JsonObject deltaUsage)
                    {
                        var outputTokens = (int?)deltaUsage["output_tokens"] ?? 0;
                        // Keep the cache counters reported at message_start; the delta only carries output.
                        usage = (usage ?? new ChatUsage(0, 0)) with { OutputTokens = outputTokens };
                    }
                    break;
                }

                case "message_stop":
                case "ping":
                default:
                    break;
            }
        }

        var stop = stopReason switch
        {
            "end_turn" => ChatStopReason.EndTurn,
            "tool_use" => ChatStopReason.ToolUse,
            "max_tokens" => ChatStopReason.MaxTokens,
            "stop_sequence" => ChatStopReason.StopSequence,
            _ => finalToolCalls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn
        };

        yield return new MessageStoppedEvent(new ChatToolUseResponse
        {
            Text = textBuilder.Length == 0 ? null : textBuilder.ToString(),
            ToolCalls = finalToolCalls,
            StopReason = stop,
            FailureCategory = RefusalCategory(stopReason),
            ErrorMessage = RefusalMessage(stopReason, textBuilder.ToString()),
            Provider = "Claude",
            Model = model,
            Usage = usage
        });
    }

    private sealed class AnthropicBlockAccumulator
    {
        public string Type { get; set; } = "text";
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Args { get; } = new();
    }

    private static JsonObject BuildRequestPayload(ChatToolUseRequest request, string model)
    {
        var messages = new JsonArray();

        // Anthropic requires alternating user/assistant. Coalesce tool results
        // from adjacent Tool-role messages into a single user message whose
        // content is an array of tool_result blocks (Anthropic's convention).
        var pending = new List<ChatMessage>();
        foreach (var m in request.Messages)
        {
            pending.Add(m);
        }

        var i = 0;
        while (i < pending.Count)
        {
            var current = pending[i];
            if (current.Role == ChatRole.Tool)
            {
                // Gather consecutive tool results into one user message.
                var contentArr = new JsonArray();
                while (i < pending.Count && pending[i].Role == ChatRole.Tool)
                {
                    foreach (var result in pending[i].Parts.OfType<ToolResultPart>())
                    {
                        var block = new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.ToolUseId,
                            ["content"] = result.Content
                        };
                        if (result.IsError) block["is_error"] = true;
                        contentArr.Add(block);
                    }
                    i++;
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = contentArr
                });
                continue;
            }

            if (current.Role == ChatRole.System)
            {
                // System messages are carried on the top-level `system` field,
                // not inside `messages`. We handle this after the loop.
                i++;
                continue;
            }

            var contentArray = BuildContentArray(current);
            if (contentArray.Count == 0)
            {
                // Anthropic rejects {"role":"assistant","content":[]} (and the
                // user-role equivalent) with HTTP 400. A message that carried
                // only whitespace text or unsupported parts has nothing to say
                // on the wire; dropping it keeps the turn sequence valid.
                i++;
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = current.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = contentArray
            });
            i++;
        }

        // Combine system prompt from SystemPrompt field plus any System-role messages in the transcript.
        var systemText = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            systemText.Append(request.SystemPrompt);
        }
        foreach (var m in request.Messages.Where(x => x.Role == ChatRole.System))
        {
            foreach (var part in m.Parts.OfType<TextPart>())
            {
                if (systemText.Length > 0) systemText.Append('\n');
                systemText.Append(part.Text);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            // Anthropic requires max_tokens, so this fallback always applies on
            // tool-use turns unless the caller sets one. 4096 truncated long
            // final messages / many-tool-block turns; 8192 is a safer ceiling
            // (output-only billing means a higher ceiling costs nothing extra).
            ["max_tokens"] = request.Options?.MaxTokens ?? 8192,
            ["messages"] = messages
        };

        // Prompt caching for the conversation itself: a breakpoint on the final content block
        // makes this request's whole message history cacheable, and the next iteration (which
        // extends it) is served from that prefix. Without it only the system block and the tool
        // catalog were ever cached and every iteration re-read the full history at full price.
        if (messages.Count > 0 && messages[^1] is JsonObject lastMessage)
        {
            if (lastMessage["content"] is JsonArray blocks && blocks.Count > 0 && blocks[^1] is JsonObject lastBlock)
                lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            else if (lastMessage["content"] is JsonValue text && text.TryGetValue<string>(out var plain))
                lastMessage["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = plain, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
        }

        if (systemText.Length > 0)
        {
            // Prompt caching: the system prompt (coding instructions plus repository files) is identical
            // across the iterations of a turn, so mark it as a cache breakpoint.
            payload["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = systemText.ToString(),
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            });
        }

        if (request.Options?.Temperature is float temp)
        {
            payload["temperature"] = temp;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = SanitizeToolName(tool.Name),
                    ["description"] = tool.Description,
                    ["input_schema"] = (JsonNode)tool.InputSchema.DeepClone()
                });
            }
            // The tool catalog is stable for the whole turn: cache it too (breakpoint on the last tool).
            if (tools[^1] is JsonObject lastTool) lastTool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            payload["tools"] = tools;
            if (request.DisableToolCalls)
            {
                // Tools stay in the payload (their cached block is reused); the model just may not call them.
                payload["tool_choice"] = new JsonObject { ["type"] = "none" };
            }
            else if (request.RequireToolCall)
            {
                payload["tool_choice"] = new JsonObject { ["type"] = "any" };
            }
        }

        return payload;
    }

    private static JsonArray BuildContentArray(ChatMessage m)
    {
        var arr = new JsonArray();
        foreach (var part in m.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    if (!string.IsNullOrEmpty(text.Text))
                    {
                        arr.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    }
                    break;
                case ToolUsePart toolUse:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolUse.Id,
                        ["name"] = SanitizeToolName(toolUse.Name),
                        ["input"] = (JsonNode)toolUse.Input.DeepClone()
                    });
                    break;
                case ToolResultPart result:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.ToolUseId,
                        ["content"] = result.Content,
                        ["is_error"] = result.IsError
                    });
                    break;
                case ImagePart image:
                    // Anthropic's vision content block — base64 source with
                    // an explicit media_type. Empty bytes would be a no-op
                    // that Anthropic rejects, so skip empties defensively.
                    if (!string.IsNullOrEmpty(image.Base64Data))
                    {
                        arr.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = image.MediaType,
                                ["data"] = image.Base64Data
                            }
                        });
                    }
                    break;
            }
        }
        return arr;
    }

    private ChatToolUseResponse ParseResponse(string body, string model, IReadOnlyDictionary<string, string> nameMap)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? text = null;
        var toolCalls = new List<ToolUsePart>();

        if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in contentArr.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                switch (type)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var tEl))
                        {
                            sb.Append(tEl.GetString());
                        }
                        break;
                    case "tool_use":
                        var id = block.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                        var sanitizedName = block.GetProperty("name").GetString()
                                            ?? throw new InvalidOperationException("tool_use block missing name.");
                        var name = ResolveOriginalName(sanitizedName, nameMap)!;
                        var inputNode = block.TryGetProperty("input", out var inp) && inp.ValueKind != JsonValueKind.Null
                            ? JsonNode.Parse(inp.GetRawText()) as JsonObject
                            : new JsonObject();
                        toolCalls.Add(new ToolUsePart(id, name, inputNode ?? new JsonObject()));
                        break;
                    // Ignore other block types (thinking, etc.) for now.
                }
            }
            if (sb.Length > 0) text = sb.ToString();
        }

        var stopReasonStr = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        var stopReason = stopReasonStr switch
        {
            "end_turn" => ChatStopReason.EndTurn,
            "tool_use" => ChatStopReason.ToolUse,
            "max_tokens" => ChatStopReason.MaxTokens,
            "stop_sequence" => ChatStopReason.StopSequence,
            _ => toolCalls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn
        };

        ChatUsage? usage = null;
        if (root.TryGetProperty("usage", out var usageEl))
        {
            var input = usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            var output = usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            var cached = usageEl.TryGetProperty("cache_read_input_tokens", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt32() : 0;
            var created = usageEl.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt32() : 0;
            usage = new ChatUsage(input + cached + created, output) { CachedInputTokens = cached, CacheCreationInputTokens = created };
        }

        return new ChatToolUseResponse
        {
            Text = text,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            FailureCategory = RefusalCategory(stopReasonStr),
            ErrorMessage = RefusalMessage(stopReasonStr, text),
            Provider = "Claude",
            Model = model,
            Usage = usage
        };
    }

    /// <summary>Anthropic reports a model refusal as stop_reason "refusal"; surface it as a typed content-policy failure.</summary>
    private static LlmFailureCategory? RefusalCategory(string? stopReason)
        => string.Equals(stopReason, "refusal", StringComparison.Ordinal) ? LlmFailureCategory.ContentPolicy : null;

    private static string? RefusalMessage(string? stopReason, string? text)
        => RefusalCategory(stopReason) is null
            ? null
            : "Claude declined this request (content policy stop_reason: refusal)." + (string.IsNullOrWhiteSpace(text) ? "" : " " + Truncate(text.Trim(), 300));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Sanitize a tool name to Anthropic's <c>^[a-zA-Z0-9_-]{1,128}$</c> constraint.
    /// The catalog uses dotted IDs (e.g. <c>fs.read_file</c>, <c>data.list-databases</c>);
    /// dots and other punctuation are rewritten to underscores. <see cref="BuildNameMap"/>
    /// captures the inverse so streamed/parsed responses can be translated back.
    /// </summary>
    private static string SanitizeToolName(string name)
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
    /// Build the sanitized→original tool-name map for one request. Covers
    /// both tool definitions and any historical tool_use blocks in the
    /// transcript (the model needs to see its own prior calls).
    /// </summary>
    private static Dictionary<string, string> BuildNameMap(ChatToolUseRequest request)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in request.Tools)
        {
            map.TryAdd(SanitizeToolName(t.Name), t.Name);
        }
        foreach (var m in request.Messages)
        {
            foreach (var part in m.Parts)
            {
                if (part is ToolUsePart use)
                {
                    map.TryAdd(SanitizeToolName(use.Name), use.Name);
                }
            }
        }
        return map;
    }

    private static string? ResolveOriginalName(string? sanitized, IReadOnlyDictionary<string, string> map)
    {
        if (string.IsNullOrEmpty(sanitized)) return sanitized;
        return map.TryGetValue(sanitized, out var original) ? original : sanitized;
    }
}
