namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Which search mode the caller should use right now. Search callsites consult the
/// policy before deciding whether to ask the LLM for a query embedding.
/// </summary>
public enum EmbeddingSearchMode
{
    /// <summary>Semantic search via the active embedding pin. Full path: embed query, vector compare.</summary>
    Semantic = 1,

    /// <summary>
    /// Lexical only (FTS / keyword). The pin model is unavailable, OR the target collection
    /// has zero rows compatible with the active pin. Returning semantic-zero results would
    /// silently miss everything; lexical at least surfaces something.
    /// </summary>
    Lexical = 2,
}

/// <summary>
/// Per-decision snapshot from <see cref="ISearchFallbackPolicy"/>. Carries enough
/// context for the UI to explain what mode is active and why ("embedding model
/// offline", "no compatible vectors in this folder yet", etc).
/// </summary>
public sealed record SearchFallbackDecision(
    EmbeddingSearchMode Mode,
    EmbeddingModelIdentity ActivePin,
    string? Reason);

/// <summary>
/// Decides whether semantic search is currently viable, or whether the caller should
/// fall back to lexical (FTS / keyword) search. The policy is consulted on every
/// search — it's cheap and avoids the case where we spend an embedding round-trip
/// only to find no compatible vectors exist in the target collection.
/// </summary>
public interface ISearchFallbackPolicy
{
    /// <summary>
    /// Decide the mode for a specific collection. The collection is needed because
    /// per-collection coverage matters (a fresh account with no embeddings yet should
    /// fall back to lexical even if the pin model is healthy).
    /// </summary>
    Task<SearchFallbackDecision> DecideAsync(string collection, CancellationToken ct = default);

    /// <summary>
    /// Decide the mode globally without a specific collection (e.g. cross-account search
    /// or non-collection-scoped surfaces). Only considers pin availability, not coverage.
    /// </summary>
    Task<SearchFallbackDecision> DecideAsync(CancellationToken ct = default);
}
