using System.Text.Json.Nodes;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Role of a message in an agent tool-use conversation.
/// </summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// Reason a provider stopped generating. Normalized across providers.
/// </summary>
public enum ChatStopReason
{
    /// <summary>Model chose to end its turn without more tool calls.</summary>
    EndTurn,

    /// <summary>Model requested one or more tool calls. Caller must execute and reply.</summary>
    ToolUse,

    /// <summary>Hit the configured max tokens.</summary>
    MaxTokens,

    /// <summary>Hit a configured stop sequence.</summary>
    StopSequence,

    /// <summary>Provider returned an error.</summary>
    Error,

    /// <summary>Anything else.</summary>
    Other
}

/// <summary>
/// A single content block inside a <see cref="ChatMessage"/>.
/// Modeled on Anthropic's content-part schema because it round-trips cleanly
/// from OpenAI tool_calls / tool_results.
/// </summary>
public abstract record ChatContentPart;

/// <summary>
/// Plain text content. Can appear in user, assistant, or system messages.
/// </summary>
public sealed record TextPart(string Text) : ChatContentPart;

/// <summary>
/// An assistant-emitted tool invocation request. Caller is expected to execute
/// the tool and reply with a matching <see cref="ToolResultPart"/>.
/// </summary>
/// <param name="Id">Provider-assigned id used to correlate the result.</param>
/// <param name="Name">Tool name (matches <see cref="ChatToolDefinition.Name"/>).</param>
/// <param name="Input">Parsed input as a JsonObject (provider-agnostic).</param>
public sealed record ToolUsePart(string Id, string Name, JsonObject Input) : ChatContentPart
{
    public JsonObject? NativeComputerCall { get; init; }
    public bool IsNativeAsync { get; init; }
}

/// <summary>
/// The tool execution result, supplied back to the model on the next turn.
/// </summary>
/// <param name="ToolUseId">Must match the originating <see cref="ToolUsePart.Id"/>.</param>
/// <param name="Content">Serialized tool output. Usually JSON or plain text.</param>
/// <param name="IsError">True when the tool threw or returned an error.</param>
public sealed record ToolResultPart(string ToolUseId, string Content, bool IsError = false) : ChatContentPart
{
    public SwarmCore.Abstractions.Computer.ComputerObservation? NativeComputerScreenshot { get; init; }
}

/// <summary>
/// Assistant chain-of-thought content emitted by reasoning-capable providers
/// (e.g. DeepSeek's <c>reasoning_content</c>). DeepSeek requires the prior
/// turn's reasoning content to be echoed back on subsequent requests within
/// the same conversation, so the engine appends this part to assistant
/// transcript messages and the matching translator emits it on the wire.
/// Providers that don't know about reasoning content silently drop the part.
/// </summary>
public sealed record ReasoningPart(string Text) : ChatContentPart;

/// <summary>Opaque Responses reasoning item, including encrypted continuation state.
/// Never display, log, or send to another provider.</summary>
public sealed record ResponsesReasoningPart(JsonObject Item) : ChatContentPart
{
    public ProviderContextBinding? Binding { get; init; }
    public override string ToString() => "ResponsesReasoningPart(opaque)";
}

/// <summary>
/// A user-attached image, encoded as base64 plus a media type
/// (e.g. <c>image/png</c>, <c>image/jpeg</c>). Carried inside a user-role
/// <see cref="ChatMessage"/> alongside any <see cref="TextPart"/>s so the
/// translator can splice it into the provider's vision wire format:
/// <list type="bullet">
///   <item>Anthropic — <c>{type:"image", source:{type:"base64", media_type, data}}</c>.</item>
///   <item>OpenAI — <c>{type:"image_url", image_url:{url:"data:&lt;media_type&gt;;base64,&lt;data&gt;"}}</c>.</item>
///   <item>Gemini — <c>{inlineData:{mimeType, data}}</c>.</item>
/// </list>
/// Providers that don't support vision drop the part silently — the
/// model selection layer is responsible for picking a vision-capable
/// model when an <see cref="ImagePart"/> is present, not the translator.
/// </summary>
/// <param name="Base64Data">Image bytes encoded as base64, with no
/// <c>data:</c> URL prefix. The translator constructs the prefix itself
/// where the wire format requires one.</param>
/// <param name="MediaType">IANA media type. <c>image/png</c>,
/// <c>image/jpeg</c>, <c>image/webp</c>, and <c>image/gif</c> are the
/// universally-supported set across the three frontier providers.</param>
public sealed record ImagePart(string Base64Data, string MediaType) : ChatContentPart
{
    public override string ToString() => $"ImagePart({MediaType}, pixels redacted)";
}

/// <summary>
/// A single message in an agent tool-use conversation. Contains one or more
/// ordered <see cref="ChatContentPart"/>s.
/// </summary>
public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ChatContentPart> Parts)
{
    /// <summary>Convenience constructor for a pure-text message.</summary>
    public static ChatMessage Text(ChatRole role, string text) =>
        new(role, [new TextPart(text)]);
}

/// <summary>
/// A tool exposed to the model. Contains the name, semantic description,
/// and a JSON Schema describing the input shape.
/// </summary>
public sealed record ChatToolDefinition(string Name, string Description, JsonObject InputSchema)
{
    public bool AllowsNativeAsync { get; init; }
}

public enum ToolExecutionProtocol { Conventional, ResponsesNativeAsyncSingleAgent }

/// <summary>
/// A full request to a provider's tool-use endpoint.
/// </summary>
public sealed record ChatToolUseRequest
{
    public ComputerToolBinding? Computer { get; init; }
    public bool DisableToolCalls { get; init; }
    public ProviderContextBinding? ContextBinding { get; init; }
    public bool IsContextCompaction { get; init; }
    public ChatSteeringSession? SteeringSession { get; init; }
    public ToolExecutionProtocol ToolExecutionProtocol { get; init; }
    public string? PreviousResponseId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required IReadOnlyList<ChatToolDefinition> Tools { get; init; }
    public string? SystemPrompt { get; init; }
    public LlmRouterOptions? Options { get; init; }

    /// <summary>
    /// Require this physical provider call to return at least one tool call.
    /// Callers must set this only when <see cref="Tools"/> is non-empty.
    /// </summary>
    public bool RequireToolCall { get; init; }
}

/// <summary>
/// Token-usage summary returned on a tool-use response. Populated on a
/// best-effort basis per provider.
/// </summary>
public sealed record ChatUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Input tokens the provider served from its prompt cache (Anthropic cache reads, OpenAI/DeepSeek cached prompt tokens).</summary>
    public int CachedInputTokens { get; init; }
    /// <summary>Input tokens the provider wrote into its prompt cache on this call (Anthropic only); zero where unreported.</summary>
    public int CacheCreationInputTokens { get; init; }
    public int ReasoningTokens { get; init; }
}

/// <summary>
/// Non-streaming response from a tool-use call.
/// </summary>
public sealed record ChatToolUseResponse
{
    public bool UsedNativeComputer { get; init; }
    /// <summary>Resolved original request-level effort, when known. Configuration updates
    /// can change effective effort without changing this value.</summary>
    public string? RequestedReasoningEffort { get; init; }
    public JsonArray? NativeOutput { get; init; }
    public ProviderContextBinding? ContextBinding { get; init; }
    public string? ResponseId { get; init; }
    public ToolExecutionProtocol ToolExecutionProtocol { get; init; }
    /// <summary>Ordered assistant transcript, when required by the provider protocol.</summary>
    public IReadOnlyList<ChatContentPart>? OrderedParts { get; init; }
    public string? ErrorMessage { get; init; }
    public LlmFailureCategory? FailureCategory { get; init; }
    /// <summary>Assistant text content, if any. Null/empty when only tool calls were emitted.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Assistant chain-of-thought emitted by reasoning-capable providers
    /// (DeepSeek's <c>reasoning_content</c>). The engine round-trips this on
    /// the next turn for providers that require it.
    /// </summary>
    public string? ReasoningContent { get; init; }

    /// <summary>Tool calls the model wants the caller to execute. Empty when done.</summary>
    public required IReadOnlyList<ToolUsePart> ToolCalls { get; init; }

    /// <summary>Normalized stop reason.</summary>
    public ChatStopReason StopReason { get; init; }

    /// <summary>Provider that served the request.</summary>
    public required string Provider { get; init; }

    /// <summary>Model that served the request.</summary>
    public required string Model { get; init; }

    /// <summary>Token usage if reported.</summary>
    public ChatUsage? Usage { get; init; }
}

/// <summary>
/// A single event emitted during streaming tool-use. Enables the UI to render
/// partial text and tool calls as they happen.
/// </summary>
public abstract record ToolUseStreamEvent;

/// <summary>A complete, correlated native async call; partial arguments never reach this event.</summary>
public sealed record NativeAsyncToolReadyEvent(ToolUsePart Call) : ToolUseStreamEvent;

/// <summary>Incremental text chunk appended to the assistant message.</summary>
public sealed record TextDeltaEvent(string Delta) : ToolUseStreamEvent;

/// <summary>
/// Incremental chain-of-thought chunk from a thinking-capable model
/// (DeepSeek thinking mode, Claude extended thinking, etc.). Surfaced as a
/// distinct event so the UI can render it as a separate "Thinking" stream
/// instead of mixing it into the visible answer. The full reasoning text
/// is also available on <see cref="ChatToolUseResponse.ReasoningContent"/>
/// after the message stops.
/// </summary>
public sealed record ReasoningDeltaEvent(string Delta) : ToolUseStreamEvent;

/// <summary>Signals the start of a tool_use block. Input JSON may still stream in via <see cref="ToolUseInputDeltaEvent"/>.</summary>
public sealed record ToolUseStartedEvent(string Id, string Name) : ToolUseStreamEvent;

/// <summary>Partial JSON fragment for a tool's input arguments.</summary>
public sealed record ToolUseInputDeltaEvent(string Id, string JsonFragment) : ToolUseStreamEvent;

/// <summary>Signals that a tool_use block is complete; input is fully known.</summary>
public sealed record ToolUseFinishedEvent(ToolUsePart ToolCall) : ToolUseStreamEvent;

/// <summary>Emitted once at the end with the final aggregate response.</summary>
public sealed record MessageStoppedEvent(ChatToolUseResponse Response) : ToolUseStreamEvent;
