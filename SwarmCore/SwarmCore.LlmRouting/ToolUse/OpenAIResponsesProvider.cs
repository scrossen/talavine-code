using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Providers;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>Responses HTTP/SSE and turn-owned WebSocket adapter. The existing engine owns all tool execution.</summary>
internal sealed class OpenAIResponsesProvider(
    string endpoint,
    Func<CancellationToken, Task<AuthenticationHeaderValue?>> authFactory,
    IHttpClientFactory clients,
    Func<Uri, AuthenticationHeaderValue?, CancellationToken, Task<WebSocket>>? connectSocket = null,
    Func<CancellationToken, Task<LlmCredential>>? credentialFactory = null) : IToolUseProvider
{
    public string ProviderName => "OpenAI";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record Request(string Model, JsonArray Input, bool Stream,
        IReadOnlyList<Function> Tools, string? ToolChoice, int? MaxOutputTokens,
        JsonObject? Reasoning, JsonNode? Text, string? PreviousResponseId = null, bool NativeAsync = false,
        string? Instructions = null)
    {
        public bool Store => NativeAsync;
        [JsonIgnore] public bool NativeAsync { get; init; } = NativeAsync;
        public string[] Include => ["reasoning.encrypted_content"];
    }
    internal sealed record Function(string? Name, string? Description, JsonNode? Parameters, bool? Async = null)
    {
        public string Type { get; init; } = "function";
        // Preserve optional fields in the existing host tool schemas.
        public bool? Strict => Type == "function" ? false : null;
    }
    internal sealed record Result(string? Id, string? Model, string? Status, JsonArray? Output,
        Usage? Usage, JsonObject? Error, JsonObject? IncompleteDetails);
    internal sealed record Usage(int? InputTokens, int? OutputTokens, int? TotalTokens,
        JsonObject? InputTokensDetails, JsonObject? OutputTokensDetails);
    private sealed record Event(string Type, int? OutputIndex, string? ItemId, JsonObject? Item,
        string? Delta, string? Arguments, Result? Response);

    public async Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bound = await BindAsync(request, ct).ConfigureAwait(false);
        request = bound.Request;
        if (request.IsContextCompaction) return await CompactAsync(request, bound.Auth, ct).ConfigureAwait(false);
        var payload = Build(request, false);
        using var http = clients.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var message = await MessageAsync(payload, bound.Auth, ct).ConfigureAwait(false);
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<Result>(body, Json)
            ?? throw new InvalidDataException("Responses returned a null result.");
        return Translate(result, request);
    }

    public async IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(ChatToolUseRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.IsContextCompaction) throw new NotSupportedException("Compaction uses the explicit nonstreaming endpoint.");
        var bound = await BindAsync(request, ct).ConfigureAwait(false);
        request = bound.Request;
        var pending = new Dictionary<int, (string Id, string CallId, string Name, bool IsAsync, StringBuilder Arguments)>();
        var names = NameMap(request);
        var dispatchedAsync = new HashSet<string>(StringComparer.Ordinal);
        ChatUsage? precedingUsage = null;
        var precedingUsageMissing = false;
        void AccountParent(Result parent)
        {
            var usage = Translate(parent, request).Usage;
            precedingUsageMissing |= usage is null;
            precedingUsage = AddUsage(precedingUsage, usage);
        }
        await foreach (var frame in ReadFramesAsync(request, bound.Auth, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (frame.Delivery is { } delivery) { yield return delivery; continue; }
            var wire = frame.Wire ?? throw new InvalidDataException("Missing Responses event.");
            if ((string?)wire["type"] == "response.created")
            {
                if (dispatchedAsync.Count != 0) throw new InvalidDataException("A successor arrived before the native async parent was reconciled.");
                pending.Clear();
            }
            var ev = wire.Deserialize<Event>(Json)
                ?? throw new InvalidDataException("Responses emitted a null event.");
            switch (ev.Type)
            {
                case "response.output_item.done" when ((string?)ev.Item?["type"]) == "function_call"
                    && ((bool?)ev.Item?["async"]) == true:
                {
                    if (request.ToolExecutionProtocol != ToolExecutionProtocol.ResponsesNativeAsyncSingleAgent)
                        throw new InvalidDataException("Unrequested native async call.");
                    var item = ev.Item!;
                    if (ev.OutputIndex is not { } index || !pending.TryGetValue(index, out var fragments)
                        || fragments.Id != Required(item, "id") || fragments.CallId != Required(item, "call_id")
                        || fragments.Name != Required(item, "name")
                        || fragments.Arguments.ToString() != Required(item, "arguments")
                        || (string?)item["status"] is "in_progress" or "incomplete")
                        throw new InvalidDataException("Native async completion does not match its assembled call.");
                    var name = names[Required(item, "name")];
                    if (!request.Tools.Any(t => t.Name == name && t.AllowsNativeAsync)
                        || !dispatchedAsync.Add(fragments.CallId))
                        throw new InvalidDataException("Unapproved or duplicate native async call.");
                    var arguments = JsonNode.Parse(Required(item, "arguments")) as JsonObject
                        ?? throw new InvalidDataException("Native async arguments must be an object.");
                    yield return new NativeAsyncToolReadyEvent(new(fragments.CallId, name, arguments) { IsNativeAsync = true });
                    break;
                }
                case "response.output_text.delta":
                    if (ev.Delta is { Length: > 0 }) yield return new TextDeltaEvent(ev.Delta);
                    break;
                case "response.output_item.added" when ((string?)ev.Item?["type"]) == "function_call":
                {
                    var item = ev.Item!;
                    var index = ev.OutputIndex ?? throw new InvalidDataException("Missing output index.");
                    var name = Required(item, "name");
                    var callId = Required(item, "call_id");
                    if (!pending.TryAdd(index, (Required(item, "id"), callId, name, (bool?)item["async"] == true, new StringBuilder())))
                        throw new InvalidDataException("Duplicate streamed tool index.");
                    if (!names.TryGetValue(name, out var original)) throw new InvalidDataException("Unknown response tool name.");
                    yield return new ToolUseStartedEvent(callId, original);
                    break;
                }
                case "response.function_call_arguments.delta":
                case "response.function_call_arguments.done":
                {
                    if (ev.OutputIndex is not { } index || !pending.TryGetValue(index, out var call)
                        || ev.ItemId != call.Id)
                        throw new InvalidDataException("Uncorrelated streamed tool arguments.");
                    if (ev.Type.EndsWith(".delta", StringComparison.Ordinal))
                    {
                        call.Arguments.Append(ev.Delta ?? throw new InvalidDataException("Missing argument delta."));
                        yield return new ToolUseInputDeltaEvent(call.CallId, ev.Delta);
                    }
                    else if (ev.Arguments != call.Arguments.ToString())
                        throw new InvalidDataException("Streamed tool argument completion disagrees with its fragments.");
                    break;
                }
                case "response.completed":
                case "response.incomplete":
                case "response.failed":
                {
                    var terminal = ev.Response ?? throw new InvalidDataException("Missing terminal response.");
                    var expectedStatus = ev.Type["response.".Length..];
                    if (terminal.Status != expectedStatus) throw new InvalidDataException("Inconsistent terminal response status.");
                    var connection = request.SteeringSession?.Transport as ResponsesSteeringConnection;
                    if (connection is not null && terminal.Status == "incomplete"
                        && (string?)terminal.IncompleteDetails?["reason"] == "steered")
                    {
                        if (dispatchedAsync.Count > 0)
                            throw new InvalidDataException("Steering interrupted a native async parent before completed terminal proof; quarantine its pending operations before recovery.");
                        AccountParent(terminal);
                        break; // The automatic successor is on this same socket; never create a second response.
                    }
                    var translated = Translate(terminal, request);
                    if (dispatchedAsync.Count > 0 && translated.StopReason != ChatStopReason.ToolUse)
                        throw new InvalidDataException("Native async parent lost its completed tool-call proof.");
                    if (connection is not null && translated.StopReason == ChatStopReason.EndTurn
                        && (connection.HasPendingSteering || connection.ResponseId != terminal.Id))
                    {
                        AccountParent(terminal);
                        break; // A normally completed parent may still have an accepted steering successor.
                    }
                    if (dispatchedAsync.Except(translated.ToolCalls.Select(c => c.Id)).Any())
                        throw new InvalidDataException("A native async call has no matching successful terminal envelope; quarantine its outcome.");
                    if (translated.ToolCalls.Any(c => dispatchedAsync.Contains(c.Id) && !c.IsNativeAsync))
                        throw new InvalidDataException("Terminal response changed a dispatched native async call's protocol.");
                    // No executable tool event escapes until the whole response completed successfully.
                    if (translated.StopReason == ChatStopReason.ToolUse)
                    {
                        foreach (var pair in pending)
                        {
                            if (terminal.Output is null || pair.Key >= terminal.Output.Count
                                || terminal.Output[pair.Key] is not JsonObject item
                                || (string?)item["call_id"] != pair.Value.CallId
                                || (string?)item["name"] != pair.Value.Name
                                || (pair.Value.IsAsync && (bool?)item["async"] != true)
                                || (string?)item["arguments"] != pair.Value.Arguments.ToString())
                                throw new InvalidDataException("Terminal tool call disagrees with streamed arguments.");
                        }
                        foreach (var call in translated.ToolCalls) yield return new ToolUseFinishedEvent(call);
                    }
                    translated = translated with { Usage = precedingUsageMissing || (precedingUsage is not null && translated.Usage is null) ? null : AddUsage(precedingUsage, translated.Usage) };
                    yield return new MessageStoppedEvent(translated);
                    yield break;
                }
                case "error":
                    var error = wire["error"] as JsonObject ?? wire;
                    // Expose bounded machine identifiers, never an arbitrary provider message
                    // which may quote user input, tool arguments or credentials.
                    static string Identifier(JsonNode? value)
                    {
                        var text = value?.ToString() ?? "unknown";
                        return text.Length <= 100 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '[' or ']') ? text : "unknown";
                    }
                    throw new InvalidDataException($"Responses streaming error: code={Identifier(error["code"])}; type={Identifier(error["type"])}; param={Identifier(error["param"])}.");
            }
        }
        throw new InvalidDataException("Responses stream ended without a terminal response; no tools may execute.");
    }

    private static ChatUsage? AddUsage(ChatUsage? first, ChatUsage? second)
        => first is null ? second : second is null ? first : new(first.InputTokens + second.InputTokens, first.OutputTokens + second.OutputTokens)
        { CachedInputTokens = first.CachedInputTokens + second.CachedInputTokens, ReasoningTokens = first.ReasoningTokens + second.ReasoningTokens };

    private async IAsyncEnumerable<ResponsesSteeringConnection.Frame> ReadFramesAsync(ChatToolUseRequest request, AuthenticationHeaderValue? auth,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (request.SteeringSession is { } steering)
        {
            var payload = Build(request, false);
            if (request.Options?.OverrideModel != "gpt-6-astra") throw new NotSupportedException("Native steering requires reviewed Astra support.");
            if (request.Options.ExecutionSettings?.Keys.Any(k => k is "conversation" or "context_management" or "truncation") == true)
                throw new NotSupportedException("Native steering does not support conversation binding or automatic compaction/truncation.");
            if (steering.Transport is null)
            {
                var endpointUri = new Uri(endpoint);
                var uri = new UriBuilder(endpointUri) { Scheme = endpointUri.Scheme == "https" ? "wss" : "ws", Port = endpointUri.IsDefaultPort ? -1 : endpointUri.Port }.Uri;
                WebSocket socket;
                if (connectSocket is not null) socket = await connectSocket(uri, auth, ct).ConfigureAwait(false);
                else
                {
                    var client = new ClientWebSocket();
                    if (auth is not null) client.Options.SetRequestHeader("Authorization", auth.ToString());
                    try { await client.ConnectAsync(uri, ct).ConfigureAwait(false); } catch { client.Dispose(); throw; }
                    socket = client;
                }
                steering.Transport = new ResponsesSteeringConnection(socket, steering.Source, payload.Model, steering.LifetimeToken, request.ContextBinding, auth);
            }
            var connection = steering.Transport as ResponsesSteeringConnection
                ?? throw new InvalidOperationException("Cannot reuse steering across provider transports.");
            connection.ValidateBinding(request.ContextBinding, auth);
            await connection.StartResponseAsync(JsonSerializer.SerializeToNode(payload, Json)!.AsObject(), ct).ConfigureAwait(false);
            await foreach (var frame in connection.ReadFramesAsync(ct).ConfigureAwait(false)) yield return frame;
            yield break;
        }
        using var http = clients.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var message = await MessageAsync(Build(request, true), auth, ct).ConfigureAwait(false);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var sse in SseParser.ParseAsync(body, ct).ConfigureAwait(false))
        {
            if (sse.Data == "[DONE]") break;
            yield return new(JsonNode.Parse(sse.Data) as JsonObject ?? throw new InvalidDataException("Malformed Responses event."));
        }
    }

    private Task<HttpRequestMessage> MessageAsync(Request payload, AuthenticationHeaderValue? auth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = auth;
        return Task.FromResult(message);
    }

    private async Task<(ChatToolUseRequest Request, AuthenticationHeaderValue? Auth)> BindAsync(ChatToolUseRequest request, CancellationToken ct)
    {
        var credential = credentialFactory is null ? null : await credentialFactory(ct).ConfigureAwait(false);
        var auth = credential is null ? await authFactory(ct).ConfigureAwait(false)
            : string.IsNullOrEmpty(credential.Key) ? null : new AuthenticationHeaderValue("Bearer", credential.Key);
        var template = request.ContextBinding ?? new ProviderContextBinding("OpenAI", request.Options?.OverrideModel ?? "",
            Guid.Empty, null, "unbound-direct-request", "direct-request", DateTimeOffset.UtcNow.AddHours(1));
        return (request with { ContextBinding = template with { Provider = "OpenAI", Model = request.Options?.OverrideModel ?? "",
            Endpoint = new Uri(endpoint).AbsoluteUri, CredentialReference = credential?.Reference } }, auth);
    }

    private async Task<ChatToolUseResponse> CompactAsync(ChatToolUseRequest request, AuthenticationHeaderValue? auth, CancellationToken ct)
    {
        var capability = ProviderContextCapabilities.StandaloneCompaction("OpenAI", request.Options!.OverrideModel!,
            request.SteeringSession is not null, request.Messages.SelectMany(m => m.Parts).OfType<ToolUsePart>()
                .Any(call => !request.Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Any(r => r.ToolUseId == call.Id)),
            request.Messages.SelectMany(m => m.Parts).Any(p => p is ResponsesConfigurationUpdatePart)
                || request.Messages.SelectMany(m => m.Parts).OfType<ResponsesCompactionPart>()
                    .Any(p => p.Items.Any(i => (string?)i?["type"] == "configuration_update"))
                || request.Messages.SelectMany(m => m.Parts).OfType<ResponsesOutputPart>()
                    .Any(p => p.Items.Any(i => (string?)i?["type"] == "configuration_update")));
        if (!capability.Supported) throw new NotSupportedException(capability.Reason);
        if (string.IsNullOrWhiteSpace(request.ContextBinding?.CredentialReference))
            throw new NotSupportedException("Native compaction needs a verifiable credential reference for canonical state replay.");
        var built = Build(request, false);
        var payload = new JsonObject { ["model"] = built.Model, ["input"] = built.Input.DeepClone(), ["instructions"] = request.SystemPrompt };
        using var http = clients.CreateClient(); http.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/compact")
        { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        message.Headers.Authorization = auth;
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidDataException("Missing canonical compaction response.");
        if ((string?)body["object"] != "response.compaction" || body["output"] is not JsonArray { Count: > 0 } output
            || !output.Any(i => (string?)i?["type"] == "compaction" && (string?)i?["encrypted_content"] is { Length: > 0 }))
            throw new InvalidDataException("Compaction omitted its canonical opaque window.");
        var usage = body["usage"]?.Deserialize<Usage>(Json);
        return new ChatToolUseResponse { Provider = "OpenAI", Model = built.Model, ResponseId = (string?)body["id"], StopReason = ChatStopReason.EndTurn,
            ToolCalls = [], OrderedParts = [new ResponsesCompactionPart((JsonArray)output.DeepClone(), request.ContextBinding!)],
            Usage = usage?.InputTokens is { } input && usage.OutputTokens is { } tokens ? new(input, tokens) : null };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
                    code = (string?)error["code"];
            }
            catch (JsonException) { }
            var category = code switch
            {
                "insufficient_quota" => LlmFailureCategory.QuotaExhausted,
                "model_not_found" => LlmFailureCategory.ModelNotFound,
                "content_policy_violation" => LlmFailureCategory.ContentPolicy,
                "context_length_exceeded" => LlmFailureCategory.ContextOverflow,
                _ => (int)response.StatusCode switch {
                    401 or 403 => LlmFailureCategory.AuthInvalid, 404 => LlmFailureCategory.ModelNotFound,
                    429 => LlmFailureCategory.RateLimit, 400 or 422 => LlmFailureCategory.InvalidRequest,
                    _ => LlmFailureCategory.ProviderError }
            };
            throw new LlmProviderHttpException($"OpenAI Responses returned HTTP {(int)response.StatusCode} ({category}); verify API account access, endpoint and model permissions.",
                response.StatusCode, category);
        }
    }

    internal static Request Build(ChatToolUseRequest request, bool stream)
    {
        var options = request.Options ?? throw new ArgumentException("Resolved route options are required.");
        var model = options.OverrideModel ?? throw new ArgumentException("An exact model is required.");
        if (!OpenAIResponsesContract.IsSelected("OpenAI", model)) throw new NotSupportedException("No reviewed Responses contract for this model.");
        if (options.PrivacyClass == LlmPrivacyClass.LocalOnly || options.RequestControl?.PrivacyClass == LlmPrivacyClass.LocalOnly)
            throw new InvalidOperationException("LocalOnly privacy forbids OpenAI Responses.");
        var names = NameMap(request);
        var nativeAsync = request.ToolExecutionProtocol == ToolExecutionProtocol.ResponsesNativeAsyncSingleAgent;
        var computer = request.Computer;
        if (computer is not null && (model != "gpt-6-astra" || nativeAsync || request.SteeringSession is not null))
            throw new NotSupportedException("Native computer use requires the reviewed Astra HTTP/SSE conventional single-agent contract.");
        if (computer is not null && !request.Tools.Any(t => t.Name == computer.ToolName))
            throw new InvalidOperationException("Native computer tool is not admitted by the current host.");
        var nativeState = nativeAsync || request.SteeringSession is not null || computer is not null;
        if (request.PreviousResponseId is not null && !nativeState)
            throw new ArgumentException("Response continuation requires the native async contract.");
        var input = new JsonArray();
        if (!nativeState && !string.IsNullOrEmpty(request.SystemPrompt)) input.Add(Message("system", "input_text", request.SystemPrompt));
        foreach (var message in request.Messages)
        {
            if (message.Parts.OfType<ResponsesOutputPart>().SingleOrDefault() is { } output)
            {
                if (message.Parts.OfType<ResponsesConfigurationUpdatePart>().Any())
                    throw new InvalidDataException("A configuration update cannot share a canonical output message.");
                if (request.ContextBinding is not { } outputBinding || !output.Binding.IsCompatibleWith(outputBinding, DateTimeOffset.UtcNow))
                    throw new ProviderContextReconstructionRequiredException("Retained response output binding changed.");
                foreach (var item in output.Items) input.Add(item?.DeepClone());
                continue;
            }
        foreach (var part in message.Parts)
        {
            var role = message.Role.ToString().ToLowerInvariant();
            switch (part)
            {
                case ResponsesConfigurationUpdatePart update:
                    if (request.ContextBinding is not { } updateBinding || !update.Binding.IsCompatibleWith(updateBinding, DateTimeOffset.UtcNow))
                        throw new ProviderContextReconstructionRequiredException("Reasoning configuration binding changed.");
                    input.Add(new JsonObject { ["type"] = "configuration_update", ["reasoning"] = new JsonObject { ["effort"] = update.Effort } });
                    break;
                case TextPart text:
                    input.Add(Message(role, message.Role == ChatRole.Assistant ? "output_text" : "input_text", text.Text));
                    break;
                case ResponsesReasoningPart reasoning when message.Role == ChatRole.Assistant:
                    if ((string?)reasoning.Item["type"] != "reasoning") throw new InvalidDataException("Invalid opaque reasoning item.");
                    if (request.ContextBinding is { } current && reasoning.Binding?.IsCompatibleWith(current, DateTimeOffset.UtcNow) == true)
                        input.Add(reasoning.Item.DeepClone());
                    break;
                case ResponsesCompactionPart compact when message.Role == ChatRole.Assistant:
                    if (request.ContextBinding is not { } binding || !compact.Binding.IsCompatibleWith(binding, DateTimeOffset.UtcNow))
                        throw new ProviderContextReconstructionRequiredException("Opaque compacted state requires deliberate reconstruction for this route or authority.");
                    foreach (var item in compact.Items) input.Add(item?.DeepClone());
                    break;
                case ToolUsePart call when call.NativeComputerCall is not null:
                    if (computer is null) throw new ProviderContextReconstructionRequiredException("Computer state requires its original authorized surface.");
                    input.Add(call.NativeComputerCall.DeepClone());
                    break;
                case ToolUsePart call:
                    input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id,
                        ["name"] = OpenAIStyleToolUseProvider.SanitizeToolName(call.Name), ["arguments"] = call.Input.ToJsonString(),
                        ["async"] = call.IsNativeAsync ? true : null });
                    break;
                case ToolResultPart result when result.NativeComputerScreenshot is { } screenshot:
                    if (computer is null || screenshot.SessionId != computer.Observation.SessionId || screenshot.SurfaceId != computer.Observation.SurfaceId
                        || string.IsNullOrWhiteSpace(screenshot.PngBase64) || result.IsError)
                        throw new ProviderContextReconstructionRequiredException("Native computer screenshot surface is unavailable or changed.");
                    input.Add(new JsonObject { ["type"] = "computer_call_output", ["call_id"] = result.ToolUseId,
                        ["output"] = new JsonObject { ["type"] = "computer_screenshot", ["image_url"] = "data:image/png;base64," + screenshot.PngBase64, ["detail"] = "original" } });
                    break;
                case ToolResultPart result:
                    input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.ToolUseId, ["output"] = result.Content });
                    break;
                case ImagePart image:
                    input.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray(new JsonObject {
                        ["type"] = "input_image", ["image_url"] = image.Base64Data.StartsWith("http", StringComparison.Ordinal)
                            ? image.Base64Data : $"data:{image.MediaType};base64,{image.Base64Data}" }) });
                    break;
                case ReasoningPart:
                    break; // Another provider's private reasoning is never replayed as Responses state.
                default:
                    throw new NotSupportedException("Unsupported Responses input content.");
            }
        }
        }
        var settings = options.ExecutionSettings;
        ResponsesConfigurationUpdateValidation.Validate(input, model,
            request.SteeringSession is null ? "responses-http" : "responses-websocket", settings);
        var parameters = options.GenerationParameters;
        var effort = ResolveRequestedEffort(options);
        int? max = options.SuppressOutputTokenLimit ? null : parameters?.MaxOutputTokens ?? options.MaxTokens;
        if (max is null && !options.SuppressOutputTokenLimit && settings is not null)
            foreach (var key in new[] { "max_output_tokens", "max_completion_tokens", "max_tokens" })
                if (settings.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out var value)) { max = value; break; }
        JsonNode? textFormat = null;
        if (settings?.TryGetValue("text", out var textSetting) == true)
            textFormat = JsonSerializer.SerializeToNode(textSetting);
        if (parameters is { StructuredOutput: not LlmStructuredOutputMode.None })
            textFormat = JsonSerializer.SerializeToNode(OpenAIResponsesContract.Translate(parameters, new()).WireSettings["text"]);
        else if (textFormat is null && (options.ForceJson || settings?.ContainsKey("response_format") == true))
            textFormat = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } };
        if (request.RequireToolCall && request.Tools.Count == 0) throw new ArgumentException("Required tool choice needs tools.");
        return new(model, input, stream, request.Tools.Select(t => computer is not null && t.Name == computer.ToolName ? new Function(null, null, null) { Type = "computer" } : new Function(
            OpenAIStyleToolUseProvider.SanitizeToolName(t.Name), t.Description,
            OpenAIStyleToolUseProvider.NormalizeParametersSchema(t.InputSchema), nativeAsync && t.AllowsNativeAsync ? true : null)).ToArray(),
            request.DisableToolCalls ? "none" : request.RequireToolCall ? "required" : null, max,
            effort is null ? null : new JsonObject { ["effort"] = effort }, textFormat, request.PreviousResponseId, nativeState,
            nativeState ? request.SystemPrompt : null);
    }

    private static JsonObject Message(string role, string type, string text) => new()
    {
        ["role"] = role, ["content"] = new JsonArray(new JsonObject { ["type"] = type, ["text"] = text })
    };

    private static Dictionary<string, string> NameMap(ChatToolUseRequest request)
    {
        var names = OpenAIStyleToolUseProvider.BuildNameMap(request);
        foreach (var tool in request.Tools)
            if (names[OpenAIStyleToolUseProvider.SanitizeToolName(tool.Name)] != tool.Name)
                throw new ArgumentException("Tool names collide after OpenAI normalization.");
        return names;
    }

    internal static ChatToolUseResponse Translate(Result result, ChatToolUseRequest request)
    {
        var model = request.Options!.OverrideModel!;
        if (string.IsNullOrEmpty(result.Id) || result.Output is null || string.IsNullOrEmpty(result.Model))
            throw new InvalidDataException("Malformed Responses result: missing identity or output.");
        var usage = result.Usage is { InputTokens: >= 0, OutputTokens: >= 0 } u
            ? new ChatUsage(u.InputTokens.Value, u.OutputTokens.Value) {
                CachedInputTokens = (int?)u.InputTokensDetails?["cached_tokens"] ?? 0,
                ReasoningTokens = (int?)u.OutputTokensDetails?["reasoning_tokens"] ?? 0 } : null;
        var parts = new List<ChatContentPart>();
        var calls = new List<ToolUsePart>();
        var names = NameMap(request);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var refused = false;
        var incompleteItem = false;
        foreach (var node in result.Output)
        {
            var item = node as JsonObject ?? throw new InvalidDataException("Malformed Responses output item.");
            incompleteItem |= (string?)item["status"] is "in_progress" or "incomplete";
            switch (Required(item, "type"))
            {
                case "reasoning":
                    parts.Add(new ResponsesReasoningPart((JsonObject)item.DeepClone()) { Binding = request.ContextBinding });
                    break;
                case "message":
                    foreach (var content in item["content"] as JsonArray ?? throw new InvalidDataException("Missing message content."))
                    {
                        if (content is not JsonObject block) throw new InvalidDataException("Malformed message content.");
                        switch (Required(block, "type"))
                        {
                            case "output_text": parts.Add(new TextPart(Required(block, "text", allowEmpty: true))); break;
                            case "refusal": refused = true; parts.Add(new TextPart(Required(block, "refusal", allowEmpty: true))); break;
                            default: throw new InvalidDataException("Unsupported Responses message content.");
                        }
                    }
                    break;
                case "computer_call":
                    if (result.Status != "completed" || incompleteItem) break;
                    if (request.Computer is not { } computer) throw new InvalidDataException("Unrequested computer action.");
                    if (item["pending_safety_checks"] is JsonArray checks && checks.Count > 0)
                        throw new InvalidDataException("Unexpected legacy computer safety-check envelope is unsupported by this GA adapter; no action dispatched.");
                    var computerId = Required(item, "call_id");
                    if (!ids.Add(computerId)) throw new InvalidDataException("Duplicate Responses call_id.");
                    if (item["actions"] is not JsonArray actions || actions.Count == 0 || actions.Count > 32)
                        throw new InvalidDataException("Malformed or oversized native computer action batch.");
                    var mappedActions = (JsonArray)actions.DeepClone();
                    foreach (var nodeAction in mappedActions)
                    {
                        if (nodeAction is not JsonObject action || (string?)action["type"] is not ("click" or "double_click" or "drag" or "move" or "scroll" or "keypress" or "type" or "wait" or "screenshot"))
                            throw new InvalidDataException("Unsupported native computer action.");
                        foreach (var field in new[] { ("scroll_x", "scrollX"), ("scroll_y", "scrollY") })
                            if (action.Remove(field.Item1, out var value)) action[field.Item2] = value;
                    }
                    var computerCall = new ToolUsePart(computerId, computer.ToolName, new JsonObject {
                        ["sessionId"] = computer.Observation.SessionId, ["surfaceId"] = computer.Observation.SurfaceId,
                        ["observationId"] = computer.Observation.ObservationId, ["actions"] = mappedActions })
                        { NativeComputerCall = (JsonObject)item.DeepClone() };
                    calls.Add(computerCall); parts.Add(computerCall);
                    break;
                case "function_call":
                    // Incomplete/error envelopes can contain partial JSON; never parse it into executable calls.
                    if (result.Status != "completed" || incompleteItem) break;
                    var id = Required(item, "call_id");
                    if (!ids.Add(id)) throw new InvalidDataException("Duplicate Responses call_id.");
                    if (!names.TryGetValue(Required(item, "name"), out var name)) throw new InvalidDataException("Unknown response tool name.");
                    var args = JsonNode.Parse(Required(item, "arguments")) as JsonObject
                        ?? throw new InvalidDataException("Tool arguments must be a JSON object.");
                    var isAsync = (bool?)item["async"] == true;
                    if (isAsync && (request.ToolExecutionProtocol != ToolExecutionProtocol.ResponsesNativeAsyncSingleAgent
                        || !request.Tools.Any(t => t.Name == name && t.AllowsNativeAsync)))
                        throw new InvalidDataException("Provider returned an unrequested native async call.");
                    var call = new ToolUsePart(id, name, args) { IsNativeAsync = isAsync };
                    calls.Add(call); parts.Add(call);
                    break;
                default: throw new InvalidDataException("Unsupported Responses output item; hosted tools are not enabled.");
            }
        }
        if (calls.Count(c => c.NativeComputerCall is not null) > 1)
            throw new InvalidDataException("Multiple native computer calls cannot share one observation; no action dispatched.");
        var failed = result.Status != "completed" || refused || incompleteItem || result.Error is not null;
        if (failed) { calls.Clear(); parts.RemoveAll(p => p is ToolUsePart); }
        var stop = result.Status == "incomplete" && (string?)result.IncompleteDetails?["reason"] == "max_output_tokens"
            ? ChatStopReason.MaxTokens : failed ? ChatStopReason.Error : calls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn;
        return new() { Provider = "OpenAI", Model = result.Model, ResponseId = result.Id, UsedNativeComputer = request.Computer is not null,
            RequestedReasoningEffort = ResolveRequestedEffort(request.Options),
            ToolExecutionProtocol = request.ToolExecutionProtocol, Text = string.Concat(parts.OfType<TextPart>().Select(p => p.Text)),
            NativeOutput = (JsonArray)result.Output.DeepClone(), ContextBinding = request.ContextBinding, OrderedParts = parts, ToolCalls = calls, Usage = usage, StopReason = stop,
            ErrorMessage = failed ? refused ? "OpenAI refused this request." : "OpenAI Responses did not complete successfully." : null,
            FailureCategory = failed ? refused ? LlmFailureCategory.ContentPolicy : LlmFailureCategory.ProviderError : null };
    }

    private static string? ResolveRequestedEffort(LlmRouterOptions options)
    {
        var effort = options.GenerationParameters is { Reasoning: not LlmReasoningMode.ProviderDefault } parameters
            ? parameters.Reasoning.ToString().ToLowerInvariant()
            : options.ExecutionSettings?.GetValueOrDefault("reasoning_effort")?.ToString();
        if (effort is not null && effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Astra reasoning effort must be low, medium, high, xhigh, or max.");
        return effort;
    }

    private static string Required(JsonObject item, string key, bool allowEmpty = false) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) && (allowEmpty || !string.IsNullOrWhiteSpace(text))
            ? text : throw new InvalidDataException($"Missing Responses {key}.");
}
