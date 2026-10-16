namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Static, I/O-free knowledge of which model families support vision, per provider.
/// This is the deep fallback for hosts that need a synchronous
/// <see cref="ILlmCapabilitiesService.GetVisionCapableModels"/>/<see cref="ILlmCapabilitiesService.GetVisionCapableProviders"/>
/// answer without querying a provider's live model list (which is async and, for a
/// local runtime, can be slow). Prefer asking the concrete <see cref="ILlmProvider.SupportsVision"/>
/// for a definitive per-model answer when a provider instance is available — this table
/// is for callers that only have provider/model strings.
/// </summary>
public static class VisionModelFamilies
{
    private static readonly Dictionary<string, HashSet<string>> ByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAI"] = new(StringComparer.OrdinalIgnoreCase) { "gpt-4o", "gpt-4o-mini", "gpt-4-vision-preview", "gpt-4-turbo", "gpt-5", "o1", "o3", "o3-mini" },
        // "claude-sonnet-4" stays first: hosts that need a concrete default (e.g. picking
        // the first entry to persist as a vision override) get a real model id, while the
        // bare "claude-sonnet"/"claude-opus" families keep Contains() matching broad.
        ["Claude"] = new(StringComparer.OrdinalIgnoreCase) { "claude-sonnet-4", "claude-sonnet", "claude-opus", "claude-3.5-sonnet", "claude-3-opus", "claude-3-sonnet", "claude-3-haiku" },
        ["Gemini"] = new(StringComparer.OrdinalIgnoreCase) { "gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash", "gemini-pro-vision", "gemini-flash", "gemini-pro" },
        // Ollama vision-capable families. Matching is Contains()-based, so tagged
        // variants like "llama3.2-vision:latest" resolve correctly. Keep in sync with
        // OllamaLlmProvider.SupportsVision's recognized patterns.
        ["Ollama"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "llava", "llava-llama3", "llava-phi3", "bakllava", "moondream",
            "llama3.2-vision", "llama4-scout", "llama4-maverick",
            "minicpm-v", "qwen2-vl", "qwen2.5-vl", "qwen3.5", "qwen3.8", "gemma3",
        },
    };

    private static string Normalize(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" or "gpt" => "OpenAI",
        "claude" or "anthropic" => "Claude",
        "gemini" or "google" => "Gemini",
        "ollama" => "Ollama",
        _ when OllamaProviderFamily.IsLanNode(provider) => "Ollama",
        _ => provider,
    };

    public static bool SupportsVision(string provider, string model)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return false;
        if (!ByProvider.TryGetValue(Normalize(provider), out var models)) return false;
        return models.Any(family => model.Contains(family, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> GetVisionCapableModels(string provider) =>
        ByProvider.TryGetValue(Normalize(provider), out var models) ? models : Enumerable.Empty<string>();

    public static IEnumerable<string> GetVisionCapableProviders() => ByProvider.Keys;
}
