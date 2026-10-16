using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Subscription-CLI view of the shared child-process constraint.
/// <para>
/// The serialization itself is NOT specific to subscription CLIs — the
/// interpreted runtime fails under concurrent child-process churn whatever
/// spawned the children — so the gate lives with the other child-process
/// primitives in <see cref="ChildProcessExecutionPolicy"/> and every recurring
/// spawner shares one slot. This type only carries the CLI-specific lifetime
/// keys used for orphan containment.
/// </para>
/// <para>
/// Lifetime keys are PER PROVIDER (<c>subscription-cli-claudecode</c>, …) so a
/// live child of one CLI never declines a launch of a different CLI: one
/// worker-owned Claude Code child must not kill ClaudeCode→CodexCli fallback,
/// let alone every subscription route at once (the 2026-07-29 chat-death
/// incident). The pre-scoping shared key <see cref="LegacyLifetimeWorkloadKey"/>
/// is never written anymore but is still SWEPT before each launch while
/// markers remain under it, so children left by an older build are found and
/// reaped exactly as before.
/// </para>
/// </summary>
internal static class SubscriptionCliProcessExecutionPolicy
{
    /// <summary>
    /// The single shared workload key every CLI provider used before
    /// containment keys were provider-scoped. Retained read-only for the
    /// transition sweep and as the conservative fallback for requests that
    /// carry no provider name.
    /// </summary>
    internal const string LegacyLifetimeWorkloadKey = "subscription-cli";

    internal static ChildProcessExecutionGate SharedGate
        => ChildProcessExecutionPolicy.SharedGate;

    internal static bool RequiresSerialization()
        => ChildProcessExecutionPolicy.RequiresSerialization();

    /// <summary>
    /// Provider-scoped containment key. Canonicalized through
    /// <see cref="SubscriptionCliCatalog"/> so every caller spelling of the
    /// same provider ("ClaudeCode", "claudecode") lands in one marker
    /// directory; an unknown provider still gets its own scoped key, and a
    /// missing provider falls back to the legacy shared key rather than
    /// silently escaping containment.
    /// </summary>
    internal static string GetLifetimeWorkloadKey(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return LegacyLifetimeWorkloadKey;
        }

        var trimmed = providerName.Trim();
        var canonical =
            trimmed.Equals(SubscriptionCliCatalog.ClaudeCodeProvider, StringComparison.OrdinalIgnoreCase)
                ? SubscriptionCliCatalog.ClaudeCodeProvider
            : trimmed.Equals(SubscriptionCliCatalog.CodexCliProvider, StringComparison.OrdinalIgnoreCase)
                ? SubscriptionCliCatalog.CodexCliProvider
            : trimmed.Equals(SubscriptionCliCatalog.GeminiCliProvider, StringComparison.OrdinalIgnoreCase)
                ? SubscriptionCliCatalog.GeminiCliProvider
            : trimmed;
        return $"{LegacyLifetimeWorkloadKey}-{canonical.ToLowerInvariant()}";
    }

    /// <summary>
    /// Pre-launch orphan sweep for one provider-scoped key, plus a one-time
    /// transition sweep of the legacy shared key while markers still exist
    /// there. Both sweeps are the existing idempotent, lock-guarded
    /// <see cref="ChildProcessLifetimeContainment.ReapOrphansAsync"/>, so
    /// racing processes stay safe. Results combine conservatively: an
    /// unverifiable marker in EITHER directory fails the sweep, and a live
    /// legacy marker counts as a live owner — its provider is unknowable, so
    /// during the (short) transition it must be assumed to conflict.
    /// </summary>
    internal static async Task<ChildProcessOrphanSweepResult> ReapOrphansWithLegacyTransitionAsync(
        string workloadKey,
        string? legacyWorkloadKey,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var result = await ChildProcessLifetimeContainment.ReapOrphansAsync(
                workloadKey,
                logger,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacyWorkloadKey)
            || string.Equals(workloadKey, legacyWorkloadKey, StringComparison.Ordinal)
            || !ChildProcessLifetimeContainment.HasOwnershipMarkers(legacyWorkloadKey))
        {
            return result;
        }

        var legacy = await ChildProcessLifetimeContainment.ReapOrphansAsync(
                legacyWorkloadKey,
                logger,
                ct)
            .ConfigureAwait(false);
        return new ChildProcessOrphanSweepResult(
            result.Succeeded && legacy.Succeeded,
            result.ReapedProcesses + legacy.ReapedProcesses,
            result.LiveOwnerMarkers + legacy.LiveOwnerMarkers,
            result.UnverifiableMarkers + legacy.UnverifiableMarkers);
    }
}
