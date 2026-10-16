using System.Collections.Concurrent;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// The shared burn-window computation behind every
/// <see cref="ISubscriptionQuotaTracker"/> implementation. Trackers own their
/// storage (an in-memory queue per provider, optionally seeded from persisted
/// telemetry) and delegate the math — window pruning, hint reconciliation,
/// burn fraction, snapshot assembly — here so the in-memory and persistent
/// trackers cannot drift. Previously this logic was duplicated near-verbatim
/// in both.
/// </summary>
public static class SubscriptionQuotaTrackerCore
{
    public sealed record Usage(DateTimeOffset ObservedAt, decimal CapacityUnits);

    /// <summary>
    /// A provider-reported remaining-usage observation ("10% of the window
    /// left"), timestamped so it can expire with the window.
    /// </summary>
    public sealed record UsageHint(decimal RemainingFraction, DateTimeOffset ObservedAt);

    /// <summary>Build a clamped hint. Fractions outside [0,1] are clamped, not rejected.</summary>
    public static UsageHint CreateHint(decimal remainingFraction, DateTimeOffset? observedAt = null)
        => new(Math.Clamp(remainingFraction, 0m, 1m), observedAt ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// Compute the current snapshot for one provider. Prunes
    /// <paramref name="queue"/> in place to the config window, reconciles any
    /// live usage hint in <paramref name="remainingHints"/> (removing it once
    /// it ages out of the window), and returns the assembled snapshot.
    /// </summary>
    public static SubscriptionQuotaSnapshot BuildSnapshot(
        string providerName,
        SubscriptionQuotaConfig config,
        ConcurrentQueue<Usage> queue,
        ConcurrentDictionary<string, UsageHint> remainingHints,
        DateTimeOffset now,
        DateTimeOffset? windowResetAt = null)
    {
        var cutoff = now - config.Window;
        // A provider-reported reset that has already happened refilled the
        // window: everything counted before it belongs to the drained window.
        if (windowResetAt is { } reset && reset <= now && reset > cutoff)
        {
            cutoff = reset;
            if (remainingHints.TryGetValue(providerName, out var staleHint) && staleHint.ObservedAt < reset)
                remainingHints.TryRemove(providerName, out _);
        }

        PruneOlderThan(queue, cutoff);
        var count = GetEffectiveCount(providerName, config, queue, remainingHints, now, out var hintObservedAt);
        var burn = config.MessageCap > 0
            ? Math.Clamp((decimal)count / config.MessageCap, 0m, 1m)
            : 0m;
        var windowStart = queue.TryPeek(out var oldest) ? oldest.ObservedAt : (DateTimeOffset?)null;
        if (hintObservedAt is not null)
        {
            windowStart = windowStart is null || hintObservedAt < windowStart
                ? hintObservedAt
                : windowStart;
        }

        return new SubscriptionQuotaSnapshot(
            ProviderName: providerName,
            CallsInWindow: count,
            MessageCap: config.MessageCap,
            Window: config.Window,
            BurnFraction: burn,
            WindowStartsAt: windowStart);
    }

    /// <summary>Drop queue entries that have aged out of the window.</summary>
    public static void PruneOlderThan(ConcurrentQueue<Usage> queue, DateTimeOffset cutoff)
    {
        while (queue.TryPeek(out var oldest) && oldest.ObservedAt < cutoff)
        {
            queue.TryDequeue(out _);
        }
    }

    private static int GetEffectiveCount(
        string providerName,
        SubscriptionQuotaConfig config,
        ConcurrentQueue<Usage> queue,
        ConcurrentDictionary<string, UsageHint> remainingHints,
        DateTimeOffset now,
        out DateTimeOffset? hintObservedAt)
    {
        hintObservedAt = null;
        var actualCount = CapacityUnits(queue);
        if (!remainingHints.TryGetValue(providerName, out var hint))
        {
            return actualCount;
        }

        if (hint.ObservedAt < now - config.Window)
        {
            remainingHints.TryRemove(providerName, out _);
            return actualCount;
        }

        var hintedBaseCount = (int)Math.Ceiling((1m - hint.RemainingFraction) * config.MessageCap);
        var callsSinceHint = CapacityUnits(queue.Where(call => call.ObservedAt >= hint.ObservedAt));
        hintObservedAt = hint.ObservedAt;
        // The CLI-reported remaining fraction is ground truth from the
        // provider's own session; the raw call count is only an estimate
        // against a guessed MessageCap. A fresh hint therefore wins outright —
        // taking Math.Max with the raw count let a miscalibrated cap (e.g.
        // ClaudeCode's conservative 45/5h) report 400%+ burn while the CLI
        // itself said plenty of headroom remained.
        return hintedBaseCount + callsSinceHint;
    }

    private static int CapacityUnits(IEnumerable<Usage> usage)
        => (int)Math.Ceiling(usage.Sum(item => Math.Max(0m, item.CapacityUnits)));
}
