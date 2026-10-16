using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Selection;

/// <summary>
/// Ranks services by semantic similarity between the need and each service's
/// description, using an <see cref="IServiceEmbedder"/>. Embeddings of service
/// text are cached by content hash so searches don't re-embed unchanged services.
/// Falls back to the keyword selector whenever embeddings are unavailable — so it
/// degrades gracefully and works offline.
/// </summary>
public sealed class EmbeddingServiceSelector : IServiceSelector
{
    private const int CacheCapacity = 512;
    private readonly IServiceEmbedder _embedder;
    private readonly IServiceSelector _fallback;
    private readonly ConcurrentDictionary<string, float[]> _cache = new();

    public EmbeddingServiceSelector(IServiceEmbedder embedder, IServiceSelector? fallback = null)
    {
        _embedder = embedder;
        _fallback = fallback ?? new KeywordServiceSelector();
    }

    public async Task<IReadOnlyList<ServiceDefinition>> SelectAsync(
        string query, IReadOnlyList<ServiceDefinition> candidates, ServiceKind? kind, int limit, CancellationToken ct = default)
    {
        var pool = (kind is { } k ? candidates.Where(c => c.Kind == k) : candidates).Where(c => c.Enabled).ToList();
        if (pool.Count == 0 || string.IsNullOrWhiteSpace(query)) return await _fallback.SelectAsync(query, candidates, kind, limit, ct).ConfigureAwait(false);

        var identityBeforeQuery = NormalizeIdentity(_embedder.CacheIdentity);
        float[]? queryVec;
        try { queryVec = await _embedder.EmbedAsync(query, ct).ConfigureAwait(false); }
        catch { queryVec = null; }
        if (queryVec is null) return await _fallback.SelectAsync(query, candidates, kind, limit, ct).ConfigureAwait(false);
        var identity = NormalizeIdentity(_embedder.CacheIdentity);
        if (!string.Equals(identityBeforeQuery, identity, StringComparison.Ordinal))
            return await _fallback.SelectAsync(query, candidates, kind, limit, ct).ConfigureAwait(false);

        var scored = new List<(ServiceDefinition svc, double score)>();
        foreach (var s in pool)
        {
            var text = $"{s.Name}. {s.Description}. When to use: {s.WhenToUse}. {string.Join(", ", s.Tags)}";
            var key = identity is null ? null : BuildCacheKey(identity, queryVec.Length, text);
            float[]? vec = null;
            var hasCompatibleCachedVector = key is not null
                && _cache.TryGetValue(key, out vec)
                && vec.Length == queryVec.Length;
            if (!hasCompatibleCachedVector)
            {
                if (key is not null) _cache.TryRemove(key, out _);
                var identityBeforeCandidate = NormalizeIdentity(_embedder.CacheIdentity);
                if (!string.Equals(identity, identityBeforeCandidate, StringComparison.Ordinal))
                    return await _fallback.SelectAsync(query, candidates, kind, limit, ct).ConfigureAwait(false);
                try { vec = await _embedder.EmbedAsync(text, ct).ConfigureAwait(false); }
                catch { vec = null; }
                var identityAfterCandidate = NormalizeIdentity(_embedder.CacheIdentity);
                if (vec is null
                    || vec.Length != queryVec.Length
                    || !string.Equals(identity, identityAfterCandidate, StringComparison.Ordinal))
                    return await _fallback.SelectAsync(query, candidates, kind, limit, ct).ConfigureAwait(false);
                if (key is not null)
                {
                    if (_cache.Count >= CacheCapacity)
                    {
                        var oldestApproximation = _cache.Keys.FirstOrDefault();
                        if (oldestApproximation is not null)
                            _cache.TryRemove(oldestApproximation, out _);
                    }
                    _cache[key] = vec;
                }
            }
            scored.Add((s, Cosine(queryVec, vec!)));
        }

        return scored.OrderByDescending(x => x.score).Take(limit).Select(x => x.svc).ToList();
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }

    private static string? NormalizeIdentity(string? identity)
        => string.IsNullOrWhiteSpace(identity) ? null : identity.Trim().ToUpperInvariant();

    private static string BuildCacheKey(string identity, int dimension, string text)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"{identity}:{dimension}:{contentHash}";
    }
}
