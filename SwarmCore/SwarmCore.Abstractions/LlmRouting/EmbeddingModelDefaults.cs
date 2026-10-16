namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// App-wide embedding model policy.
/// Embedding vector spaces are model-specific, so Talavine pins exactly one
/// local embedding model. This is the intentional hard-coded model exception:
/// changing it makes existing semantic search indexes incompatible.
/// </summary>
public static class EmbeddingModelDefaults
{
    public const string Provider = "Ollama";
    public const string Model = "qwen3-embedding:0.6b";
    public const string ArtifactDigest = "ac6da0dfba84a81fdbfbaf330198c33cd77c4cdfc53e8bc50eb581914a15621d";
    public const string Family = "qwen3-embedding";
    public const int Dimension = 1024;
    // Ollama otherwise allocates the model's full 32K KV cache. On a 4 GiB
    // Vulkan device that cache alone is about 3.5 GiB, so the small model
    // cannot start. Provider input is already capped at 3,000 characters.
    public const int OllamaContextWindowTokens = 2048;

    // Explicit migration identity for development profiles created before the
    // 0.6B default. Never reinterpret these vectors as the new latent space.
    public const string LegacyModel8B = "qwen3-embedding:8b";
    public const string LegacyArtifactDigest = "64b933495768fbd3b87c20583d379728a07471e0c66733a9df87cd1901b3c44b";
    public const int LegacyDimension8B = 4096;

    public static bool IsPinnedModel(string? model)
        => string.Equals(model?.Trim(), Model, StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedMigrationModel(string? model)
        => IsPinnedModel(model)
           || string.Equals(model?.Trim(), LegacyModel8B, StringComparison.OrdinalIgnoreCase);

    public static bool IsPinnedFamily(string? model)
        => !string.IsNullOrWhiteSpace(model)
           && (model.StartsWith(Family + ":", StringComparison.OrdinalIgnoreCase)
               || model.Equals(Family, StringComparison.OrdinalIgnoreCase));
}
