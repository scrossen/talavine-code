namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Tracks rate-limit / usage-cap exhaustion events per provider so the router
/// can rotate around a temporarily-unavailable subscription or API key. A
/// provider that hit "use again at 8:42 PM" doesn't need to be tried again
/// every turn until then — every failed attempt is a network roundtrip and
/// a self-imposed bounce on the user's experience.
///
/// State is persisted (SQLite) so a five-hour Claude Pro window or an
/// overnight Gemini quota lockout survives app relaunches. Implementations
/// SHOULD cache hot-path reads in memory so <see cref="IsAvailable"/> can
/// be called from inside the router's per-call selection hot path without
/// hitting the DB every time.
/// </summary>
public interface ILlmProviderCooldownTracker
{
    /// <summary>
    /// Record an exhaustion event for <paramref name="providerName"/>. The
    /// provider stays out of rotation until <paramref name="retryAfter"/>
    /// has elapsed. Calling this for a provider already in cooldown extends
    /// the cooldown if the new <paramref name="retryAfter"/> is further
    /// out — never shortens it (a fresh "try again in 1 hour" after a
    /// 5-hour cooldown shouldn't unlock the provider early).
    /// </summary>
    /// <param name="providerName">Provider key, e.g. "OpenAI", "ClaudeCode".</param>
    /// <param name="retryAfter">Best-effort estimate of how long until the
    /// provider should be considered eligible again. Implementations clamp
    /// to a reasonable maximum.</param>
    /// <param name="reason">Short human-readable explanation (typically the
    /// trimmed error message) for the chat UI's cooldown chip.</param>
    /// <param name="category">Typed failure category resolved by the router's
    /// error policy at detection time. Persisted with the row so consumers
    /// (health latch, probe clearing, UI chips) can branch on the category
    /// directly instead of keyword-sniffing <paramref name="reason"/>. Null
    /// for legacy callers that only know the reason string — consumers then
    /// fall back to string classification.</param>
    Task MarkExhaustedAsync(
        string providerName,
        TimeSpan retryAfter,
        string? reason,
        LlmFailureCategory? category = null,
        CancellationToken ct = default);

    /// <summary>Synchronous, cache-backed check: is this provider currently in cooldown?</summary>
    bool IsAvailable(string providerName);

    /// <summary>
    /// Returns the cooldown end time for <paramref name="providerName"/>, or
    /// null when the provider is not in cooldown. Used by the UI to render
    /// "Claude paused until 8:42 PM" chips.
    /// </summary>
    DateTime? GetCooldownUntil(string providerName);

    /// <summary>Snapshot every active cooldown for UI rendering / diagnostics.</summary>
    IReadOnlyList<LlmProviderCooldownRecord> GetActiveCooldowns();

    /// <summary>Fresh exact-provider state for recovery, including before cache hydration.</summary>
    Task<LlmProviderCooldownRecord?> GetCooldownAsync(string providerName, CancellationToken ct = default)
        => Task.FromResult(GetActiveCooldowns().FirstOrDefault(row =>
            row.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Clear an active cooldown manually (e.g. user-initiated "I bought
    /// more credit, try again now"). No-op if there's no active row.
    /// </summary>
    Task ClearAsync(string providerName, CancellationToken ct = default);

    /// <summary>
    /// Clears only an unchanged observed cooldown. Recovery must never erase a
    /// newer quota/auth failure which arrived while its probe was in flight.
    /// Legacy implementations fail closed until they support conditional writes.
    /// </summary>
    Task<bool> TryClearAsync(LlmProviderCooldownRecord expected, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Sweep stale rows (ExhaustedUntilUtc &lt; now). Safe to call periodically
    /// from a background sweeper; the in-memory cache is refreshed from disk
    /// each time so the on-disk and in-memory views stay aligned.
    /// </summary>
    Task<int> ClearExpiredAsync(CancellationToken ct = default);

    /// <summary>
    /// Fired when a cooldown is recorded, cleared, or expires. UI components
    /// subscribe to refresh themselves without polling. Marshal to the UI
    /// thread yourself — events fire on the producer's thread.
    /// </summary>
    event EventHandler<LlmProviderCooldownChange>? CooldownChanged;
}

/// <param name="Category">Typed failure category recorded with the cooldown,
/// or null for rows written before the category column existed (or by legacy
/// string-only callers). Consumers should prefer this over re-classifying
/// <paramref name="Reason"/>.</param>
public sealed record LlmProviderCooldownRecord(
    string ProviderName,
    DateTime ExhaustedUntilUtc,
    string? Reason,
    DateTime RecordedAtUtc,
    LlmFailureCategory? Category = null);

public sealed record LlmProviderCooldownChange(
    string ProviderName,
    DateTime? ExhaustedUntilUtc,
    LlmProviderCooldownChangeKind Kind);

public enum LlmProviderCooldownChangeKind
{
    Recorded,
    Extended,
    Cleared,
    Expired
}
