namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Quota policy for a subscription provider. Subscription windows are well-known but
/// not officially published as a machine-readable number, so values here are conservative
/// defaults the user can tune via <c>RoutingPolicy</c> or a settings page.
/// </summary>
/// <param name="ProviderName">Subscription provider key, e.g. "ClaudeCode", "CodexCli".</param>
/// <param name="Window">Sliding window length the provider counts against.</param>
/// <param name="MessageCap">
/// Soft cap on calls per window. Used to compute a burn fraction; not enforced by the
/// router — the <see cref="ILlmProviderCooldownTracker"/> is still the hard kill-switch
/// when an actual 429 fires.
/// </param>
public sealed record SubscriptionQuotaConfig(
    string ProviderName,
    TimeSpan Window,
    int MessageCap);

/// <summary>
/// Per-provider quota usage snapshot for UI / diagnostics. CallsInWindow is a
/// normalized capacity-unit estimate rather than a literal request count when
/// a provider exposes model tiers with materially different allowance burn.
/// </summary>
public sealed record SubscriptionQuotaSnapshot(
    string ProviderName,
    int CallsInWindow,
    int MessageCap,
    TimeSpan Window,
    decimal BurnFraction,
    DateTimeOffset? WindowStartsAt)
{
    public bool ExceedsThreshold(decimal threshold) => BurnFraction >= threshold;
}

/// <summary>
/// Soft, statistical quota tracker for subscription providers (Claude Pro 5-hour window,
/// ChatGPT 3-hour window, etc). Records each successful subscription call and exposes a
/// burn-fraction the <c>SubscriptionQuotaRoutingAdvisor</c> reads on every selection.
///
/// Complementary to <see cref="ILlmProviderCooldownTracker"/>:
///   - The cooldown tracker is the *hard* trigger that fires after a 429 with a real
///     Retry-After header. It rotates the provider out of the pool until the deadline.
///   - This soft tracker is the *predictive* signal: it tells the router "you're at
///     70% of the 5-hour Claude Pro window — start shifting load to Codex/API now"
///     before any 429 has fired.
///
/// Implementations are expected to be process-local; persistence across restarts is
/// optional. The first window after a restart will under-count, which is the safer
/// failure direction (more headroom for the user, not less).
/// </summary>
public interface ISubscriptionQuotaTracker
{
    /// <summary>Register a config for a provider. Called from setup/configurator code.</summary>
    void Configure(SubscriptionQuotaConfig config);

    /// <summary>
    /// Record successful subscription usage. <paramref name="capacityUnits"/>
    /// is 1 for the provider's cheapest baseline tier and may be larger for a
    /// tier that consumes more of the same subscription allowance.
    /// </summary>
    void RecordCall(
        string providerName,
        DateTimeOffset? now = null,
        decimal capacityUnits = 1m);

    /// <summary>
    /// Apply a provider-reported remaining-usage hint, for example "10% usage remaining"
    /// from a subscription CLI/app. Implementations should treat this as a burn-floor
    /// for the current provider window so routing can rotate before the hard limit fires.
    /// </summary>
    void ApplyRemainingFractionHint(string providerName, decimal remainingFraction, DateTimeOffset? observedAt = null);

    /// <summary>
    /// The provider reported that its usage window is exhausted and resets at
    /// <paramref name="resetsAt"/> (a subscription usage limit). Once that time
    /// passes, usage and hints recorded before it no longer count: the window
    /// the provider refilled must not keep ranking the route as drained.
    /// </summary>
    void RecordWindowReset(string providerName, DateTimeOffset resetsAt) { }

    /// <summary>
    /// Synchronous burn fraction for hot-path advisor selection. Returns 0 when the
    /// provider has no config or no recent calls.
    /// </summary>
    decimal GetBurnFraction(string providerName);

    SubscriptionQuotaSnapshot? GetSnapshot(string providerName);

    IReadOnlyList<SubscriptionQuotaSnapshot> GetAllSnapshots();
}
