namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Provider-specific translator that speaks native tool-use (Anthropic tool_use,
/// OpenAI tool_calls, Ollama native chat) against a single underlying API.
/// <para>
/// Implementations live in SwarmCore.LlmRouting and are resolved by the
/// <see cref="ILlmRouter"/> based on the chosen provider for a given request.
/// </para>
/// </summary>
public interface IToolUseProvider
{
    /// <summary>
    /// Name of the provider this implementation handles (e.g. "openai",
    /// "anthropic", "ollama"). Used for DI keyed registration.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Execute a non-streaming tool-use turn. Returns the assistant's text
    /// plus any tool calls the model wants executed.
    /// </summary>
    Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct);

    /// <summary>
    /// Execute a streaming tool-use turn. Emits text deltas, tool-use events,
    /// and finally a <see cref="MessageStoppedEvent"/> with the aggregate.
    /// </summary>
    IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(ChatToolUseRequest request, CancellationToken ct);
}

/// <summary>
/// Optional <see cref="IToolUseProvider"/> capability for runtimes whose context
/// window is a server setting rather than a fixed model fact. Ollama, for
/// example, serves a model with the num_ctx its runner was started with, and
/// unless a request forbids truncation it silently drops the oldest messages of
/// a longer prompt, so the only reliable window is the one the runtime reports.
/// </summary>
public interface IToolUseContextWindowSource
{
    /// <summary>
    /// The context window, in tokens, the serving runtime applies to the next
    /// request for <paramref name="model"/>; null when the runtime cannot prove
    /// it. Fails soft: an unreachable runtime or unrecognized report is null.
    /// </summary>
    Task<int?> GetContextWindowTokensAsync(string model, CancellationToken ct = default);
}

/// <summary>
/// Optional <see cref="IToolUseContextWindowSource"/> capability for a runtime
/// that sets the window on each request and grows it when a request does not
/// fit, as a native Ollama chat does. The current window then does not bound
/// what the route serves whole: a request up to the largest window is served
/// at a grown window, and only a request beyond it fails as a context overflow.
/// </summary>
public interface IToolUseGrowableContextWindowSource : IToolUseContextWindowSource
{
    /// <summary>
    /// The largest window, in tokens, the runtime grows to for a request to
    /// <paramref name="model"/>; null when it cannot grow the window now (the
    /// memory a larger window needs is unknown, or a grown window recently
    /// failed to load). Fails soft like
    /// <see cref="IToolUseContextWindowSource.GetContextWindowTokensAsync"/>.
    /// </summary>
    Task<int?> GetMaximumContextWindowTokensAsync(string model, CancellationToken ct = default);
}
