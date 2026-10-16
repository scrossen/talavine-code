namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Identity of an embedding model the app is pinned to. Embeddings created under one
/// identity can only be searched with query vectors produced by the same identity — a
/// query vector from a different model lives in a different latent space, so cosine
/// similarity against existing rows is mathematically defined but semantically meaningless.
///
/// Stored as <see cref="EmbeddingProvider"/> + <see cref="EmbeddingModel"/> on every
/// row of the semantic index; search filters to rows matching the active pin.
/// </summary>
/// <param name="Provider">Provider registry key, e.g. "Ollama", "OpenAI".</param>
/// <param name="Model">Provider-native model id, e.g. "qwen3-embedding:0.6b".</param>
/// <param name="Dimension">Output vector length. Defense-in-depth filter beyond Provider/Model.</param>
/// <param name="ArtifactDigest">Exact installed artifact digest, when the provider exposes one.</param>
public sealed record EmbeddingModelIdentity(
    string Provider,
    string Model,
    int Dimension,
    string? ArtifactDigest = null)
{
    /// <summary>True when two identities produce vectors in the same latent space.</summary>
    public bool MatchesFor(string? provider, string? model) =>
        string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(model, Model, StringComparison.OrdinalIgnoreCase);

    /// <summary>True only when both identities describe the same physical vector space.</summary>
    public bool SameVectorSpace(EmbeddingModelIdentity? other) =>
        other is not null
        && Dimension == other.Dimension
        && MatchesFor(other.Provider, other.Model)
        && string.Equals(
            NormalizeDigest(ArtifactDigest),
            NormalizeDigest(other.ArtifactDigest),
            StringComparison.Ordinal);

    public static string? NormalizeDigest(string? digest)
        => string.IsNullOrWhiteSpace(digest)
            ? null
            : digest.Trim().ToLowerInvariant();

    /// <summary>
    /// Attach the release-approved digest for the shipped Ollama embedding model.
    /// Explicit non-empty digests are preserved so an approved same-tag artifact
    /// change can be rebuilt into a fresh generation.
    /// </summary>
    public EmbeddingModelIdentity WithKnownArtifactDigest()
    {
        if (!string.IsNullOrWhiteSpace(ArtifactDigest))
            return this with { ArtifactDigest = NormalizeDigest(ArtifactDigest) };
        if (!string.Equals(Provider, EmbeddingModelDefaults.Provider, StringComparison.OrdinalIgnoreCase))
            return this with { ArtifactDigest = null };
        if (Dimension == EmbeddingModelDefaults.Dimension
            && string.Equals(Model, EmbeddingModelDefaults.Model, StringComparison.OrdinalIgnoreCase))
        {
            return this with { ArtifactDigest = EmbeddingModelDefaults.ArtifactDigest };
        }
        if (Dimension == EmbeddingModelDefaults.LegacyDimension8B
            && string.Equals(Model, EmbeddingModelDefaults.LegacyModel8B, StringComparison.OrdinalIgnoreCase))
        {
            return this with { ArtifactDigest = EmbeddingModelDefaults.LegacyArtifactDigest };
        }
        return this with { ArtifactDigest = null };
    }
}

/// <summary>
/// Owns the app-wide active embedding model. Reads return the current pin; writes
/// change it (which logically invalidates the existing semantic index — handled by
/// the rebuild orchestrator, not by this interface).
///
/// The pin lives in persisted settings, not in source-code constants. This is the
/// safety contract: a release that bumps a default model id cannot silently invalidate
/// a user's existing semantic index, because the active pin survives upgrades.
/// </summary>
public interface IEmbeddingModelPin
{
    /// <summary>Return the active pin, falling back to the ship-with default on first run.</summary>
    Task<EmbeddingModelIdentity> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Force a read from durable device-local authority. Cross-process mutation
    /// barriers call this after acquiring their lease so a companion cannot persist
    /// with a stale pin snapshot. Fixed/test pins may use the normal read.
    /// </summary>
    Task<EmbeddingModelIdentity> RefreshAsync(CancellationToken ct = default)
        => GetAsync(ct);

    /// <summary>Synchronous cache-backed read for hot paths. Returns the last loaded pin.</summary>
    EmbeddingModelIdentity Current { get; }

    /// <summary>
    /// Exact device-local vector generation selected for search. The generation UUID
    /// isolates rebuild episodes, and the identity digest separately proves the physical
    /// artifact behind a mutable provider tag.
    /// <see cref="Guid.Empty"/> is a legacy/bootstrap value that may read canonical
    /// vectors until the production pin materializes a generation.
    /// </summary>
    Guid CurrentGenerationId => Guid.Empty;

    /// <summary>
    /// Validate that an identity can be selected by this pin without mutating it.
    /// Rebuild start calls this before generation GC so an unsupported route cannot
    /// destroy the retained rollback corpus.
    /// </summary>
    void ValidateCandidate(EmbeddingModelIdentity identity)
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.Provider)
            || string.IsNullOrWhiteSpace(identity.Model)
            || identity.Dimension <= 0)
        {
            throw new ArgumentException("Embedding pin requires provider, model, and a positive dimension.", nameof(identity));
        }
    }

    /// <summary>
    /// Change the active pin. This is a *logical* invalidation of the existing semantic
    /// index — rows tagged with the old identity become invisible to search until they
    /// are re-embedded under the new identity. Callers typically wrap this in a rebuild
    /// orchestrator flow; calling it bare leaves the user with a degraded search until
    /// re-embed completes.
    /// </summary>
    Task SetAsync(EmbeddingModelIdentity identity, CancellationToken ct = default);

    /// <summary>
    /// Atomically select both the model identity and the exact validated vector
    /// generation. Rebuild apply paths must use this overload.
    /// </summary>
    Task SetAsync(
        EmbeddingModelIdentity identity,
        Guid generationId,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This embedding pin does not support exact vector-generation selection.");

    /// <summary>Fired when the pin changes. UI components subscribe to update status chips.</summary>
    event EventHandler<EmbeddingModelIdentity>? PinChanged;
}
