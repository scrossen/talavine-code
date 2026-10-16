namespace SwarmCore.Services.Abstractions;

/// <summary>
/// Turns text into an embedding vector for semantic service selection. The host
/// supplies an implementation (e.g. over an LLM router's embedding model). Return
/// null when embeddings aren't available so the selector falls back to keywords.
/// </summary>
public interface IServiceEmbedder
{
    /// <summary>
    /// Stable identity of the vector space used by <see cref="EmbedAsync"/>.
    /// Selectors may cache document vectors only when this is non-null, and
    /// must segment that cache when the identity changes.
    /// </summary>
    string? CacheIdentity => null;

    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}
