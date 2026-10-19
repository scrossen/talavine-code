using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Text-protocol tool-use adapter for subscription-backed CLIs. The normal
/// SwarmCore agent engine still executes tools; the CLI only chooses the next
/// native tool call or final answer.
/// </summary>
public abstract class SubscriptionCliToolUseProviderBase : IToolUseProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        // CLIs proxy arbitrary models; cosmetic slips (trailing commas,
        // comments) must not read as "this model can't do tool use".
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly ILlmProvider _provider;
    private readonly ILogger _logger;

    protected SubscriptionCliToolUseProviderBase(ILlmProvider provider, ILogger logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public abstract string ProviderName { get; }

    public async Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var model = request.Options?.OverrideModel ?? DefaultModel;
        var images = request.Messages.SelectMany(m => m.Parts).OfType<ImagePart>().ToArray();
        if (images.Length > 0 && !_provider.SupportsVision(model))
            throw new NotSupportedException("The selected subscription CLI route does not support image attachments.");
        var prompt = BuildPrompt(request);
        var executionSettings = request.Options?.ExecutionSettings is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(
                request.Options.ExecutionSettings,
                StringComparer.OrdinalIgnoreCase);
        if (images.Length > 0) executionSettings[CliImageAttachments.ExecutionSetting] = new CliImageAttachments(images);
        if (ct.CanBeCanceled)
        {
            executionSettings[LlmReservedExecutionSettings.RequestCancellationToken] = ct;
        }

        var response = await _provider.GenerateResponseAsync(
            prompt,
            model,
            executionSettings);

        if (!response.WasSuccessful)
        {
            throw new InvalidOperationException(response.ErrorMessage ?? $"{ProviderName} tool-use CLI call failed.");
        }

        CaptureRawExchange(prompt, response.Content);

        try
        {
            return WithMeasuredUsage(WithReasoningEffort(ParseResponse(response.Content, model, request.Tools), model, executionSettings), response);
        }
        catch (InvalidOperationException firstFailure) when (IsRecoverableReply(firstFailure))
        {
            // One corrective reprompt against the SAME model before the router
            // escalates to cross-provider fallback. An envelope slip is usually
            // cosmetic (prose preamble, broken quoting) — asking the model to
            // re-emit fixes most of them, and without this a single slip reads
            // as "this model can't do tool use". Dead air (exit 0, nothing on
            // stdout) is transient often enough to deserve the same one retry.
            var wasEmpty = IsEmptyCompletion(firstFailure);
            _logger.LogWarning(
                firstFailure,
                "{Provider} tool-use reply was {Problem}; sending one corrective reprompt.",
                ProviderName,
                wasEmpty ? "empty" : "malformed");

            var corrective = wasEmpty
                ? BuildEmptyReplyPrompt(prompt)
                : BuildCorrectivePrompt(prompt, response.Content);
            var retry = await _provider.GenerateResponseAsync(
                corrective,
                model,
                executionSettings);
            if (!retry.WasSuccessful)
            {
                throw;
            }

            CaptureRawExchange(corrective, retry.Content, wasCorrective: true);
            return WithMeasuredUsage(WithReasoningEffort(ParseResponse(retry.Content, model, request.Tools), model, executionSettings), response, retry);
        }
    }

    private static ChatToolUseResponse WithMeasuredUsage(ChatToolUseResponse parsed, params LlmResponse[] executions)
    {
        if (executions.Any(r => !r.UsageMeasurementComplete)) return parsed;
        return parsed with
        {
            Usage = new(executions.Aggregate(0, (sum, r) => checked(sum + r.InputTokens)),
                executions.Aggregate(0, (sum, r) => checked(sum + r.OutputTokens)))
            { CachedInputTokens = executions.Aggregate(0, (sum, r) => checked(sum + r.CacheReadInputTokens)) }
        };
    }

    private ChatToolUseResponse WithReasoningEffort(ChatToolUseResponse response, string model,
        IReadOnlyDictionary<string, object> executionSettings)
        => _provider is CodexCliLlmProvider
            ? response with { RequestedReasoningEffort = CodexCliLlmProvider.ResolveReasoningEffort(model, executionSettings) }
            : response;

    private static int _captureSequence;

    /// <summary>
    /// Opt-in diagnostic capture of the exact prompt/reply exchange, for
    /// investigating protocol drift (tool calls emitted as prose) that is
    /// invisible once parsing has cleaned the reply up. Off unless
    /// SWARMCORE_TOOLUSE_CAPTURE_DIR names a directory; failures are ignored —
    /// diagnostics must never fail a turn. Raw replies can contain user data,
    /// so this must only ever be enabled against harness/test profiles.
    /// </summary>
    private void CaptureRawExchange(string prompt, string? reply, bool wasCorrective = false)
    {
        var dir = Environment.GetEnvironmentVariable("SWARMCORE_TOOLUSE_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            var n = Interlocked.Increment(ref _captureSequence);
            var stamp = DateTime.UtcNow.ToString("HHmmss");
            var suffix = wasCorrective ? "-corrective" : string.Empty;
            File.WriteAllText(Path.Combine(dir, $"{stamp}-{n:D3}{suffix}-prompt.txt"), prompt);
            File.WriteAllText(Path.Combine(dir, $"{stamp}-{n:D3}{suffix}-reply.txt"), reply ?? string.Empty);
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static string BuildCorrectivePrompt(string originalPrompt, string invalidOutput)
    {
        var excerpt = invalidOutput.Length <= 2000 ? invalidOutput : invalidOutput[..2000] + "…";
        return originalPrompt
               + "\n\nYour previous reply was not a valid JSON envelope and could not be parsed. "
               + "It began:\n" + excerpt
               + "\n\nRe-emit your answer now as ONLY the JSON object in the exact shape specified above — "
               + "no markdown fences, no commentary, no text before or after the JSON.";
    }

    private static string BuildEmptyReplyPrompt(string originalPrompt)
        => originalPrompt
           + "\n\nYour previous reply was empty. An empty reply is never valid. "
           + "Answer now with ONLY the JSON object in the exact shape specified above — "
           + "either a non-empty \"toolCalls\" array or a non-empty \"text\" answer.";

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(
        ChatToolUseRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await ChatAsync(request, ct);
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            yield return new TextDeltaEvent(response.Text);
        }

        foreach (var call in response.ToolCalls)
        {
            yield return new ToolUseStartedEvent(call.Id, call.Name);
            yield return new ToolUseFinishedEvent(call);
        }

        yield return new MessageStoppedEvent(response);
    }

    protected abstract string DefaultModel { get; }

    private string BuildPrompt(ChatToolUseRequest request)
    {
        var payload = new JsonObject
        {
            ["systemPrompt"] = request.SystemPrompt,
            ["messages"] = new JsonArray(request.Messages.Select(ConvertMessage).ToArray<JsonNode?>()),
            ["tools"] = new JsonArray(request.Tools.Select(ConvertTool).ToArray<JsonNode?>()),
            ["requireToolCall"] = request.RequireToolCall
        };

        // The turn payload comes first and the protocol rules last: a model that
        // ends on the payload tends to answer it conversationally, which is how
        // prose tool calls and bare narration got emitted instead of envelopes.
        return $$"""
You are serving one SwarmCore native function-calling turn through a subscription CLI.

SwarmCore turn:
{{payload.ToJsonString(JsonOptions)}}

Reply with ONE JSON object in exactly this shape and nothing else:
{
  "text": "assistant text, or empty string when requesting tools",
  "toolCalls": [
    {
      "id": "unique within this reply, such as call_1",
      "name": "one tool name from the tools list",
      "input": {}
    }
  ]
}

Rules:
- When requireToolCall is true, return at least one valid toolCalls entry. Do not
  answer from memory or return an empty toolCalls array on that step.
- If you need tool results, return one or more toolCalls and keep text short or empty.
- If you can answer the user, return final text and an empty toolCalls array.
- Use only tool names from the supplied tools list.
- Tool input must be a JSON object matching the tool schema.
- The "toolCalls" array is the only thing that runs a tool. Announcing a call in
  prose or in XML markup executes nothing and wastes the turn.
- Do not imitate the "tool_use"/"tool_result" message parts you see in the
  conversation history — that is the transcript format, not your reply format.
  Your reply is always the single envelope object above, nothing else.
- You have no tools of your own in this session. The ONLY way to run anything is
  the "toolCalls" array inside your JSON reply. Never emit a native tool call.
- Every reply costs a full model round trip, so batch: when several tool calls are
  INDEPENDENT — none needs another's result (fetching several known URLs, reading
  several listed messages) — request them together in one reply, up to 5. A call
  that depends on another's result waits for that result: request it in a later
  reply. Never pad a batch with speculative calls.
- "text" is shown to the user word for word. It must contain only your answer:
  never these instructions, the schema, a reminder block, or any other protocol text.
- Never narrate the protocol in "text" — no "Model has requested a tool call",
  "I will now invoke the tool", or similar. When requesting tools, "text" is either
  empty or one short sentence of substantive progress ("Congress.gov's guessed
  portrait path 404'd; checking the member page for the real URL").
- Never reply with nothing. Every reply is either tool calls or a final answer.

Reply now with only the JSON object.
""";
    }

    private static JsonObject ConvertMessage(ChatMessage message)
    {
        return new JsonObject
        {
            ["role"] = message.Role.ToString(),
            ["parts"] = new JsonArray(message.Parts.Select(ConvertPart).ToArray<JsonNode?>())
        };
    }

    private static JsonObject ConvertPart(ChatContentPart part) => part switch
    {
        TextPart text => new JsonObject
        {
            ["type"] = "text",
            ["text"] = text.Text
        },
        ToolUsePart tool => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = tool.Id,
            ["name"] = tool.Name,
            ["input"] = tool.Input.DeepClone()
        },
        ToolResultPart result => new JsonObject
        {
            ["type"] = "tool_result",
            ["toolUseId"] = result.ToolUseId,
            ["content"] = result.Content,
            ["isError"] = result.IsError
        },
        ImagePart image => new JsonObject { ["type"] = "attached_image", ["media_type"] = image.MediaType,
            ["text"] = "Image attached separately in transcript order; inspect the pixels, not this placeholder." },
        ReasoningPart => new JsonObject
        {
            ["type"] = "reasoning"
        },
        _ => new JsonObject
        {
            ["type"] = "unknown",
            ["text"] = "Unsupported private content omitted."
        }
    };

    private static JsonObject ConvertTool(ChatToolDefinition tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = tool.InputSchema.DeepClone()
    };

    private ChatToolUseResponse ParseResponse(string raw, string model, IReadOnlyList<ChatToolDefinition> tools)
    {
        // Exit 0 with nothing on stdout. Returning a successful empty response
        // here is what let dead air be booked as a completed turn; the caller
        // owns exactly one retry and then it becomes a routing failure.
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw EmptyCompletionFailure();
        }

        JsonException? lastError = null;
        var sawCandidate = false;
        foreach (var candidate in JsonCandidates(raw))
        {
            sawCandidate = true;
            try
            {
                var envelope = JsonSerializer.Deserialize<ToolUseEnvelope>(candidate, JsonOptions);
                // Any JSON object deserializes into the envelope DTO with all
                // properties null — an unrelated blob in the reply (a fenced
                // example, a tool's arguments) must not be mistaken for the
                // envelope. Require at least one envelope field to be present.
                if (envelope is null || (envelope.Text is null && envelope.ToolCalls is null))
                {
                    continue;
                }

                return BuildEnvelopeResponse(envelope, model, tools);
            }
            catch (JsonException ex)
            {
                lastError = ex;
            }
        }

        // No JSON envelope. Before declaring the turn lost, recover the two
        // shapes a CLI-hosted model reaches for instead: a prose call
        // announcement and the Anthropic XML calling convention.
        var salvaged = SubscriptionCliReplySalvage.Salvage(raw, tools);
        if (salvaged.Calls.Count > 0)
        {
            _logger.LogWarning(
                "{Provider} expressed {Count} tool call(s) as text instead of the JSON envelope; parsed them into real calls.",
                ProviderName,
                salvaged.Calls.Count);
            return BuildResponse(salvaged.ResidualText, salvaged.Calls, model);
        }

        // Call syntax that could not be trusted: a truncated <invoke> block, or
        // a name the request never declared. Either way it is not an answer —
        // reprompt rather than render markup to the user.
        if (salvaged.SawIncompleteCallSyntax || salvaged.SawUndeclaredToolName)
        {
            throw MarkMalformedEnvelope(new InvalidOperationException(
                $"{ProviderName} returned an unusable tool call expressed as text instead of a tool-use envelope. Please retry or switch models."));
        }

        // A reply that is nothing but a bare call id (`call_11`) or a lone
        // `<request for tool call>` placeholder is the residue of a mangled
        // envelope — never an answer, never renderable. The same goes for a
        // reply that echoes the request payload skeleton back at us.
        if (SubscriptionCliReplySalvage.IsToolSyntaxDebris(salvaged.ResidualText)
            || SubscriptionCliReplySalvage.ContainsRequestSkeletonEcho(salvaged.ResidualText)
            || SubscriptionCliReplySalvage.IsBareToolNameReply(salvaged.ResidualText, tools))
        {
            throw MarkMalformedEnvelope(new InvalidOperationException(
                $"{ProviderName} returned tool-call debris instead of an answer or a tool-use envelope. Please retry or switch models."));
        }

        // An envelope cut off INSIDE its "text" string: the model finished its
        // answer but not its JSON. The decoded string content IS the answer —
        // rendering the raw `{"text":"…` JSON, or reprompting a model that
        // already answered, both waste it.
        if (SubscriptionCliReplySalvage.RecoverTruncatedEnvelopeText(raw) is { } recoveredText)
        {
            _logger.LogWarning(
                "{Provider} returned an envelope truncated inside its text field; recovered the answer text ({Length} chars).",
                ProviderName,
                recoveredText.Length);
            return BuildTextResponse(recoveredText, model);
        }

        if (LooksLikeToolUseEnvelope(raw))
        {
            throw lastError is null
                ? MarkMalformedEnvelope(new InvalidOperationException(
                    $"{ProviderName} returned a malformed tool-use envelope instead of a final answer. Please retry or switch models."))
                : MarkMalformedEnvelope(new InvalidOperationException(
                    $"{ProviderName} returned malformed tool-use JSON. Please retry or switch models.", lastError));
        }

        if (sawCandidate && lastError is not null)
        {
            _logger.LogWarning(lastError, "{Provider} tool-use JSON was malformed; treating raw output as final text.", ProviderName);
        }
        else
        {
            _logger.LogDebug("{Provider} tool-use response was not JSON; treating it as final text.", ProviderName);
        }

        return BuildTextResponse(salvaged.ResidualText, model);
    }

    private ChatToolUseResponse BuildEnvelopeResponse(
        ToolUseEnvelope envelope,
        string model,
        IReadOnlyList<ChatToolDefinition> tools)
    {
        var toolCalls = new List<ToolUsePart>();
        var index = 1;
        foreach (var call in envelope.ToolCalls ?? [])
        {
            if (string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(call.Id) ? $"cli_call_{index}" : call.Id!;
            var input = call.Input is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject();
            toolCalls.Add(new ToolUsePart(id, call.Name!, input));
            index++;
        }

        // A well-formed envelope can still carry a call trapped in its own text
        // field, and can still echo scaffolding into user-visible text.
        var salvaged = SubscriptionCliReplySalvage.Salvage(envelope.Text, tools);
        if (toolCalls.Count == 0 && salvaged.Calls.Count > 0)
        {
            _logger.LogWarning(
                "{Provider} put {Count} tool call(s) in the envelope text instead of toolCalls; parsed them into real calls.",
                ProviderName,
                salvaged.Calls.Count);
            toolCalls.AddRange(salvaged.Calls);
        }

        return BuildResponse(salvaged.ResidualText, toolCalls, model);
    }

    private ChatToolUseResponse BuildResponse(string? text, IReadOnlyList<ToolUsePart> toolCalls, string model)
    {
        var cleanText = SubscriptionCliReplySalvage.StripScaffolding(text);
        if (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(cleanText))
        {
            // Nothing survived: either the model said nothing, or everything it
            // said was protocol scaffolding. Both are dead air, not an answer.
            throw EmptyCompletionFailure();
        }

        // Text-protocol IDs are model-authored labels, not durable operation IDs:
        // fresh CLI replies (including salvaged/missing IDs) often restart at call_1.
        // Give each accepted reply a host namespace exactly once, before streaming or
        // transcript persistence. Re-delivery of this response keeps these IDs, so the
        // scheduler still rejects changed content and never replays completed effects.
        // Do not derive identity from arguments or retained history: identical new
        // requests are legitimate, and context compaction can remove prior call IDs.
        if (toolCalls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count() != toolCalls.Count)
            throw MarkMalformedEnvelope(new InvalidOperationException(
                $"{ProviderName} returned duplicate tool call IDs within one reply. Each call must have a unique ID."));
        var replyId = Guid.NewGuid().ToString("N");
        var scopedCalls = toolCalls.Select((call, index) => call with { Id = $"cli_{replyId}_{index + 1}" }).ToArray();

        return new ChatToolUseResponse
        {
            Text = cleanText,
            ToolCalls = scopedCalls,
            StopReason = toolCalls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn,
            Usage = null,
            Provider = ProviderName,
            Model = model
        };
    }

    /// <summary>
    /// Keeps the tool-envelope-specific object extraction order, but delegates
    /// every structural transformation to the shared JSON repair ladder.
    /// Cheapest candidate that deserializes wins.
    /// </summary>
    private static IEnumerable<string> JsonCandidates(string raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var balanced = ExtractBalancedJson(raw);
        var sliced = ExtractJson(raw);
        foreach (var seed in new[] { balanced, sliced, raw })
        {
            if (string.IsNullOrWhiteSpace(seed)) continue;
            // A truncated tool argument must be reprompted, not guessed. The
            // shared repair still owns all cosmetic fixes; only its final EOF
            // completion is disabled for this executable payload envelope.
            foreach (var candidate in JsonRepair.RepairCandidates(
                         seed,
                         includeTruncationCompletion: false))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Walks from the first '{' to its string-aware balanced closer, so prose
    /// after the envelope ("Hope that helps!") or a second JSON blob doesn't
    /// poison the slice the way a naive LastIndexOf('}') does.
    /// </summary>
    private static string? ExtractBalancedJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = StripFence(value.Trim());
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    private ChatToolUseResponse BuildTextResponse(string text, string model)
        => BuildResponse(text, [], model);

    private static string? ExtractJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = StripFence(value.Trim());
        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        return first >= 0 && last > first ? trimmed[first..(last + 1)] : null;
    }

    private static string StripFence(string trimmed)
    {
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence].Trim()
            : trimmed;
    }

    private static bool LooksLikeToolUseEnvelope(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("toolCalls", StringComparison.OrdinalIgnoreCase)
           && value.Contains("text", StringComparison.OrdinalIgnoreCase);

    // The output looked like a tool-use envelope but no repair candidate
    // deserialized. Tagged via Exception.Data (not a subclass) so callers that
    // assert on the historical InvalidOperationException type keep working,
    // while ChatAsync can still recognize the case to issue exactly one
    // corrective reprompt.
    private const string MalformedEnvelopeMarker = "swarmcore.malformed_tool_use_envelope";

    // Exit 0 with no usable content. Tagged the same way so the router's
    // generic exception path (cooldown check → cross-provider fallback) sees a
    // real failure instead of a successful turn that happened to say nothing.
    private const string EmptyCompletionMarker = "swarmcore.empty_tool_use_completion";

    private static InvalidOperationException MarkMalformedEnvelope(InvalidOperationException ex)
    {
        ex.Data[MalformedEnvelopeMarker] = true;
        return ex;
    }

    private InvalidOperationException EmptyCompletionFailure()
    {
        var ex = new InvalidOperationException(
            $"{ProviderName} returned an empty completion (no text and no tool calls). Please retry or switch models.");
        ex.Data[EmptyCompletionMarker] = true;
        return ex;
    }

    private static bool IsMalformedEnvelope(Exception ex)
        => ex.Data.Contains(MalformedEnvelopeMarker);

    private static bool IsEmptyCompletion(Exception ex)
        => ex.Data.Contains(EmptyCompletionMarker);

    private static bool IsRecoverableReply(Exception ex)
        => IsMalformedEnvelope(ex) || IsEmptyCompletion(ex);

    private sealed class ToolUseEnvelope
    {
        public string? Text { get; set; }
        public List<ToolCallDto>? ToolCalls { get; set; }
    }

    private sealed class ToolCallDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public JsonNode? Input { get; set; }
    }
}

public sealed class ClaudeCodeToolUseProvider : SubscriptionCliToolUseProviderBase
{
    public ClaudeCodeToolUseProvider(
        ClaudeCodeLlmProvider provider,
        ILogger<ClaudeCodeToolUseProvider> logger)
        : base(provider, logger)
    {
    }

    public override string ProviderName => SubscriptionCliProviders.ClaudeCode;
    protected override string DefaultModel => SubscriptionCliModels.ClaudeCodeDefault;
}

public sealed class CodexCliToolUseProvider : SubscriptionCliToolUseProviderBase
{
    public CodexCliToolUseProvider(
        CodexCliLlmProvider provider,
        ILogger<CodexCliToolUseProvider> logger)
        : base(provider, logger)
    {
    }

    public override string ProviderName => SubscriptionCliProviders.CodexCli;
    protected override string DefaultModel => SubscriptionCliModels.CodexCliDefault;
}
