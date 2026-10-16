using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Native tool-use translator for one registered Ollama-family provider (this
/// machine's daemon or an <c>Ollama@node</c>). It maps a tool turn to the
/// <c>/api/chat</c> wire format and sends it through that
/// <see cref="OllamaLlmProvider"/>, so the turn is served whole under the same
/// window policy as the provider's text and vision requests and shares its
/// per-model window grants.
/// <para>
/// The request is the one Ollama's OpenAI-compatible layer built for the same
/// turn, so tool rendering, tool-call parsing and sampling are unchanged: tool
/// names are sanitized and mapped back the same way, thinking is off unless
/// requested, and temperature and top_p default to 1.0 as that layer set them.
/// Ollama has no tool_choice; a required tool call is enforced by the router's
/// reply check, as it was when the compatible layer ignored the field.
/// </para>
/// </summary>
public sealed class OllamaNativeToolUseProvider : IToolUseProvider, IToolUseGrowableContextWindowSource
{
    /// <summary>
    /// Sampling Ollama's OpenAI-compatible layer applied to a turn that set
    /// none. Kept so tool-use evidence gathered through it still describes
    /// these turns.
    /// </summary>
    private const double CompatibleLayerTemperature = 1.0;
    private const double CompatibleLayerTopP = 1.0;

    // Legacy aliases of typed generation parameters. As in the OpenAI-style
    // translator, the semantic layer owns them and they are never competing
    // wire fields. keep_alive is not one of them: the local runtime's
    // keep-alive policy reaches tool turns as it reaches text requests.
    private static readonly string[] SemanticAliases =
    [
        "max_tokens", "max_completion_tokens", "num_predict", "temperature", "top_p", "seed",
        "response_format", "format", "stream", "ollama_stream", "think", "ollama_think",
        "enable_think", "reasoning_effort",
    ];

    private readonly OllamaLlmProvider _runtime;
    private readonly ILogger _logger;

    internal OllamaNativeToolUseProvider(OllamaLlmProvider runtime, ILogger logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public string ProviderName => _runtime.ProviderName;

    /// <inheritdoc />
    public Task<int?> GetContextWindowTokensAsync(string model, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(model)
            ? Task.FromResult<int?>(null)
            : _runtime.ReadRuntimeContextWindowAsync(model.Trim(), ct);

    /// <inheritdoc />
    public Task<int?> GetMaximumContextWindowTokensAsync(string model, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(model)
            ? Task.FromResult<int?>(null)
            : _runtime.GetMaximumContextWindowAsync(model.Trim(), ct);

    public async Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        var model = RequireModel(request);
        var nameMap = OpenAIStyleToolUseProvider.BuildNameMap(request);
        var turn = BuildTurn(request, model, stream: false);
        string body;
        using (var chat = await _runtime.SendNativeToolChatAsync(turn, ct).ConfigureAwait(false))
        {
            body = await chat.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        var reply = JsonNode.Parse(body) as JsonObject
                    ?? throw new InvalidOperationException($"{ProviderName} returned a tool-use reply that is not a JSON object.");
        ThrowIfRuntimeError(reply, streaming: false);
        var message = reply["message"] as JsonObject;
        return BuildResponse(
            model,
            turn,
            (string?)message?["content"],
            (string?)message?["thinking"],
            ReadToolCalls(message, nameMap),
            reply);
    }

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(
        ChatToolUseRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var model = RequireModel(request);
        var nameMap = OpenAIStyleToolUseProvider.BuildNameMap(request);
        var turn = BuildTurn(request, model, stream: true);
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<ToolUsePart>();
        JsonObject? final = null;

        // The connection is released before the final event, so a consumer
        // acting on the stopped message holds nothing open.
        using (var chat = await _runtime.SendNativeToolChatAsync(turn, ct).ConfigureAwait(false))
        {
            await using var body = await chat.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(body);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || ParseChunk(line) is not { } chunk) continue;
                ThrowIfRuntimeError(chunk, streaming: true);

                if (chunk["message"] is JsonObject message)
                {
                    if ((string?)message["thinking"] is { Length: > 0 } thought)
                    {
                        reasoning.Append(thought);
                        yield return new ReasoningDeltaEvent(thought);
                    }

                    if ((string?)message["content"] is { Length: > 0 } delta)
                    {
                        text.Append(delta);
                        yield return new TextDeltaEvent(delta);
                    }

                    // Ollama parses a tool call from the model's output and
                    // streams it whole, never as argument fragments.
                    foreach (var call in ReadToolCalls(message, nameMap))
                    {
                        toolCalls.Add(call);
                        yield return new ToolUseStartedEvent(call.Id, call.Name);
                        yield return new ToolUseInputDeltaEvent(call.Id, call.Input.ToJsonString());
                    }
                }

                if (chunk["done"] is JsonValue done && done.TryGetValue<bool>(out var finished) && finished)
                {
                    final = chunk;
                    break;
                }
            }
        }

        // A stream cut before the runtime's final chunk is an incomplete reply,
        // not a turn that ended.
        if (final is null)
        {
            throw new HttpRequestException(
                $"{ProviderName} streaming call ended before the runtime reported the turn complete.");
        }

        foreach (var call in toolCalls)
        {
            yield return new ToolUseFinishedEvent(call);
        }

        yield return new MessageStoppedEvent(BuildResponse(
            model,
            turn,
            text.Length == 0 ? null : text.ToString(),
            reasoning.Length == 0 ? null : reasoning.ToString(),
            toolCalls,
            final));
    }

    private static string RequireModel(ChatToolUseRequest request)
        => request.Options?.OverrideModel ?? throw new InvalidOperationException(
            "OllamaNativeToolUseProvider requires LlmRouterOptions.OverrideModel to be set (the router resolves this).");

    private OllamaNativeToolChatRequest BuildTurn(ChatToolUseRequest request, string model, bool stream)
    {
        var options = request.Options;
        var semantic = options?.GenerationParameters ?? new LlmGenerationParameters();
        var reasoning = OllamaToolUseReasoning.Resolve(options, semantic.Reasoning);
        semantic = semantic with
        {
            MaxOutputTokens = semantic.MaxOutputTokens ?? options?.MaxTokens,
            Temperature = semantic.Temperature ?? options?.Temperature ?? CompatibleLayerTemperature,
            TopP = semantic.TopP ?? CompatibleLayerTopP,
            Stream = stream,
            // Resolved above into the think value the turn sends.
            Reasoning = LlmReasoningMode.ProviderDefault,
        };

        Dictionary<string, object>? experimental = null;
        if (options?.ExecutionSettings is { } settings)
        {
            experimental = new Dictionary<string, object>(settings, StringComparer.OrdinalIgnoreCase);
            foreach (var alias in SemanticAliases)
            {
                experimental.Remove(alias);
            }
        }

        var translated = _runtime.TranslateExecution(
            model,
            semantic,
            options?.RequestControl ?? new LlmRequestControl(),
            experimental);
        var wire = new Dictionary<string, object>(translated.WireSettings, StringComparer.OrdinalIgnoreCase);
        var privacy = options?.PrivacyClass is { } requested and not LlmPrivacyClass.Unspecified
            ? requested
            : LlmReservedExecutionSettings.GetRequestPrivacyClass(wire);

        return new OllamaNativeToolChatRequest(
            model,
            BuildMessages(request),
            BuildTools(request),
            stream,
            reasoning == OllamaToolUseReasoning.None ? JsonValue.Create(false) : JsonValue.Create(reasoning),
            wire,
            privacy,
            LlmReservedExecutionSettings.GetExpectedModelArtifactDigest(wire),
            BuildSizingText(request),
            request.Messages.Sum(message => message.Parts.OfType<ImagePart>().Count()));
    }

    private static JsonArray BuildMessages(ChatToolUseRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }

        // Ollama names a tool result by its tool; a result part carries only
        // the id of the call it answers.
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var use in request.Messages.SelectMany(message => message.Parts).OfType<ToolUsePart>())
        {
            toolNames[use.Id] = OpenAIStyleToolUseProvider.SanitizeToolName(use.Name);
        }

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = Text(message) });
                    break;

                case ChatRole.User:
                    var user = new JsonObject { ["role"] = "user", ["content"] = Text(message) };
                    var images = message.Parts.OfType<ImagePart>()
                        .Where(image => !string.IsNullOrEmpty(image.Base64Data))
                        .Select(image => (JsonNode?)JsonValue.Create(StripDataUriPrefix(image.Base64Data)))
                        .ToArray();
                    if (images.Length > 0)
                    {
                        user["images"] = new JsonArray(images);
                    }

                    messages.Add(user);
                    break;

                case ChatRole.Assistant:
                    // Reasoning parts stay out of the replay, as they did on the
                    // compatible endpoint.
                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = Text(message) };
                    var calls = message.Parts.OfType<ToolUsePart>()
                        .Select(call => (JsonNode?)new JsonObject
                        {
                            ["id"] = call.Id,
                            ["function"] = new JsonObject
                            {
                                ["name"] = OpenAIStyleToolUseProvider.SanitizeToolName(call.Name),
                                ["arguments"] = call.Input.DeepClone(),
                            },
                        })
                        .ToArray();
                    if (calls.Length > 0)
                    {
                        assistant["tool_calls"] = new JsonArray(calls);
                    }

                    messages.Add(assistant);
                    break;

                case ChatRole.Tool:
                    var results = message.Parts.OfType<ToolResultPart>().ToList();
                    if (results.Count == 0)
                    {
                        throw new InvalidOperationException("Tool-role message must contain a ToolResultPart.");
                    }

                    // One wire message per result, so every call in the
                    // preceding assistant turn is answered by its own message.
                    foreach (var result in results)
                    {
                        var tool = new JsonObject
                        {
                            ["role"] = "tool",
                            ["content"] = result.Content,
                            ["tool_call_id"] = result.ToolUseId,
                        };
                        if (toolNames.TryGetValue(result.ToolUseId, out var name))
                        {
                            tool["tool_name"] = name;
                        }

                        messages.Add(tool);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unknown chat role {message.Role}.");
            }
        }

        return messages;
    }

    private static JsonArray? BuildTools(ChatToolUseRequest request)
        => request.Tools.Count == 0
            ? null
            : new JsonArray(request.Tools
                .Select(tool => (JsonNode?)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = OpenAIStyleToolUseProvider.SanitizeToolName(tool.Name),
                        ["description"] = tool.Description,
                        ["parameters"] = OpenAIStyleToolUseProvider.NormalizeParametersSchema(tool.InputSchema),
                    },
                })
                .ToArray());

    /// <summary>
    /// What the model reads, for sizing a window when the runtime reports no
    /// token count. Images are charged separately by count.
    /// </summary>
    private static string BuildSizingText(ChatToolUseRequest request)
    {
        var text = new StringBuilder(request.SystemPrompt);
        foreach (var part in request.Messages.SelectMany(message => message.Parts))
        {
            text.Append('\n').Append(part switch
            {
                TextPart plain => plain.Text,
                ToolUsePart use => use.Name + use.Input.ToJsonString(),
                ToolResultPart result => result.Content,
                _ => string.Empty,
            });
        }

        foreach (var tool in request.Tools)
        {
            text.Append('\n').Append(tool.Name).Append(tool.Description).Append(tool.InputSchema.ToJsonString());
        }

        return text.ToString();
    }

    private static string Text(ChatMessage message)
        => string.Concat(message.Parts.OfType<TextPart>().Select(part => part.Text));

    private static string StripDataUriPrefix(string base64)
    {
        var comma = base64.IndexOf(',');
        return base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
            ? base64[(comma + 1)..]
            : base64;
    }

    private JsonObject? ParseChunk(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            _logger.LogDebug("{Provider}: could not parse a streamed chunk '{Raw}'.", ProviderName, Truncate(line, 200));
            return null;
        }
    }

    /// <summary>
    /// An error the runtime reports inside an accepted response: a streamed
    /// chunk after output began, or a body the runtime sent with success.
    /// </summary>
    private void ThrowIfRuntimeError(JsonObject reply, bool streaming)
    {
        if (reply["error"] is not { } error)
        {
            return;
        }

        var message = error is JsonValue value && value.TryGetValue<string>(out var text) ? text : error.ToJsonString();
        throw new HttpRequestException(
            $"{ProviderName} {(streaming ? "streaming" : "tool-use")} call failed: {Truncate(message, 500)}");
    }

    private List<ToolUsePart> ReadToolCalls(JsonObject? message, IReadOnlyDictionary<string, string> nameMap)
    {
        var parts = new List<ToolUsePart>();
        if (message?["tool_calls"] is not JsonArray calls)
        {
            return parts;
        }

        foreach (var node in calls)
        {
            if (node is not JsonObject call)
            {
                continue;
            }

            var function = call["function"] as JsonObject;
            var sanitized = function?["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var wireName)
                ? wireName
                : null;
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                throw new InvalidOperationException("tool_calls entry missing function.name.");
            }

            var name = nameMap.TryGetValue(sanitized, out var original) ? original : sanitized;
            // Runtimes before tool-call ids name calls only by position.
            var id = call["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var callId)
                                                     && !string.IsNullOrWhiteSpace(callId)
                ? callId
                : "call_" + Guid.NewGuid().ToString("N");
            parts.Add(new ToolUsePart(id, name, ReadArguments(function?["arguments"], name)));
        }

        return parts;
    }

    private JsonObject ReadArguments(JsonNode? arguments, string toolName)
    {
        switch (arguments)
        {
            case JsonObject values:
                return (JsonObject)values.DeepClone();
            case JsonValue value when value.TryGetValue<string>(out var json):
                try
                {
                    return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
                }
                catch (JsonException)
                {
                    _logger.LogWarning(
                        "{Provider} tool call '{Name}' had unparseable arguments; falling back to empty object. Raw: {Raw}",
                        ProviderName, toolName, Truncate(json, 300));
                    return new JsonObject();
                }
            default:
                return new JsonObject();
        }
    }

    private ChatToolUseResponse BuildResponse(
        string model,
        OllamaNativeToolChatRequest turn,
        string? text,
        string? reasoning,
        IReadOnlyList<ToolUsePart> toolCalls,
        JsonObject final)
    {
        var doneReason = final["done_reason"] is JsonValue reason && reason.TryGetValue<string>(out var why) ? why : null;
        var stopReason = toolCalls.Count > 0
            ? ChatStopReason.ToolUse
            : doneReason == "length" ? ChatStopReason.MaxTokens : ChatStopReason.EndTurn;

        var promptTokens = ReadCount(final, "prompt_eval_count");
        var outputTokens = ReadCount(final, "eval_count");
        var usage = promptTokens is null && outputTokens is null
            // The runtime counted nothing: estimate from what the model read
            // (~4 chars/token) so usage never records a free turn.
            ? new ChatUsage(
                Math.Max(1, turn.SizingText.Length / 4),
                ((text?.Length ?? 0) + (reasoning?.Length ?? 0)
                 + toolCalls.Sum(call => call.Name.Length + call.Input.ToJsonString().Length)) / 4)
            : new ChatUsage(promptTokens ?? 0, outputTokens ?? 0)
            {
                CachedInputTokens = ReadCount(final, "prompt_eval_cached_count") ?? 0,
            };

        return new ChatToolUseResponse
        {
            Text = string.IsNullOrEmpty(text) ? null : text,
            ReasoningContent = string.IsNullOrEmpty(reasoning) ? null : reasoning,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Provider = ProviderName,
            Model = model,
            Usage = usage,
        };
    }

    private static int? ReadCount(JsonObject reply, string name)
        => reply[name] is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0 ? count : null;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
