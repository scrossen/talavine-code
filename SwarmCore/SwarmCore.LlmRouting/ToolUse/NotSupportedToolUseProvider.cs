using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Fallback tool-use provider for providers that don't yet have a native
/// tool-use translator implemented. Throws on any call with a clear message;
/// the router surfaces this to the caller so the user can switch providers.
/// Currently unused — every registered provider has a real translator —
/// but kept available for any future provider that lands without tool-use
/// support.
/// </summary>
public sealed class NotSupportedToolUseProvider : IToolUseProvider
{
    public NotSupportedToolUseProvider(string providerName)
    {
        ProviderName = providerName;
    }

    public string ProviderName { get; }

    public Task<ChatToolUseResponse> ChatAsync(ChatToolUseRequest request, CancellationToken ct)
        => throw new NotSupportedException(
            $"Provider '{ProviderName}' does not yet support native tool-use in SwarmCore. " +
            "Choose a provider with tool-use support (OpenAI, Claude, or Ollama) on the agent profile.");

    public IAsyncEnumerable<ToolUseStreamEvent> StreamAsync(ChatToolUseRequest request, CancellationToken ct)
        => throw new NotSupportedException(
            $"Provider '{ProviderName}' does not yet support native tool-use in SwarmCore. " +
            "Choose a provider with tool-use support (OpenAI, Claude, or Ollama) on the agent profile.");
}
