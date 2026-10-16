namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Static, I/O-free lane classification for models that are dedicated to a
/// non-text workload. This is a final safety boundary: provider/model strings
/// can arrive through saved profiles, sync, explicit overrides, or discovery,
/// so advisory candidate filtering alone is insufficient.
/// </summary>
public static class ModelWorkloadFamilies
{
    /// <summary>
    /// Returns true for dedicated embedding models that must never be executed
    /// as a chat or tool-use model.
    /// </summary>
    public static bool IsEmbeddingOnly(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var lower = model.Trim().ToLowerInvariant();
        return lower.Contains("embed", StringComparison.Ordinal)
               || lower.Contains("nomic", StringComparison.Ordinal)
               || lower.Contains("minilm", StringComparison.Ordinal)
               || lower.Contains("bge-", StringComparison.Ordinal)
               || lower.StartsWith("bge", StringComparison.Ordinal)
               || lower.Contains("e5-", StringComparison.Ordinal)
               || lower.StartsWith("e5", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true for local multimodal families that are a dedicated vision
    /// lane rather than an ordinary text/tool-use assistant. General frontier
    /// models that also support images (Claude, GPT, Gemini, Gemma 3) are not
    /// classified as vision-only.
    /// </summary>
    public static bool IsVisionOnly(string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var lower = model.Trim().ToLowerInvariant();
        if (lower.Contains("llava", StringComparison.Ordinal)
            || lower.Contains("bakllava", StringComparison.Ordinal)
            || lower.Contains("moondream", StringComparison.Ordinal)
            || lower.Contains("minicpm-v", StringComparison.Ordinal)
            || lower.Contains("llama3.2-vision", StringComparison.Ordinal)
            || lower.Contains("qwen2-vl", StringComparison.Ordinal)
            || lower.Contains("qwen2.5-vl", StringComparison.Ordinal))
        {
            return true;
        }

        // Unknown local models that explicitly identify as a vision artifact
        // remain in the vision lane. Do not apply this broad rule to cloud
        // models such as gpt-4-vision-preview that are also capable chat models.
        return IsLocalProvider(provider)
               && lower.Contains("vision", StringComparison.Ordinal);
    }

    private static bool IsLocalProvider(string? provider)
        => OllamaProviderFamily.IsOllamaFamily(provider)
           || provider?.Trim().ToLowerInvariant() is
               "lmstudio" or "lm studio" or "remotepeer" or "remote peer";
}
