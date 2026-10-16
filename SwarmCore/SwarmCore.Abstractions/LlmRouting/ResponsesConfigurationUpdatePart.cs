namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>A bound, ordered Responses input item. Effective effort is tracked by the
/// host; the response's request-level reasoning setting does not report this update.</summary>
public sealed record ResponsesConfigurationUpdatePart(string Effort, ProviderContextBinding Binding) : ChatContentPart;

/// <summary>Reviewed protocol compatibility, not route admission or account access.</summary>
public static class ResponsesConfigurationUpdateCapability
{
    public static ProviderContextCapability Evaluate(string provider, string model, string effort,
        string transport = "responses-http", bool multiAgent = false, bool pro = false,
        bool background = false, bool automaticCompaction = false, bool automaticTruncation = false)
    {
        if (!provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) || model != "gpt-6-astra"
            || transport is not ("responses-http" or "responses-websocket"))
            return new(false, "response-boundary", "No reviewed configuration-update contract for this exact route and transport.");
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            return new(false, "response-boundary", "Unsupported Astra reasoning effort.");
        if (multiAgent || pro || automaticCompaction || automaticTruncation)
            return new(false, "response-boundary", "Configuration updates require standard single-agent mode without automatic compaction or truncation.");
        if (background)
            return new(false, "response-boundary", "Background configuration updates are not covered by this reviewed adapter.");
        return new(true, "responses-configuration-update", "Apply before the next user input; retain the original request-level effort and ordered history.");
    }
}
