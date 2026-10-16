namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Database- and provider-implementation-neutral capability seam used by
/// control planes that must validate whether a provider has a native tool-use
/// translator without depending on the concrete LLM routing assembly.
/// </summary>
public interface IToolUseRouteSupport
{
    bool Supports(string providerName);
}
