namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// User-configurable routing policy that the dynamic advisors read on every call. Stored
/// once globally (per profile data root), not per function type — function-specific
/// preferences are still owned by <see cref="ILlmSettingsProvider"/>.
/// </summary>
/// <param name="MonthlyCloudBudgetUsd">
/// Soft cap on cloud API spend per calendar month. Null = unlimited. The
/// <c>BudgetPressureRoutingAdvisor</c> uses this to decide when to demote tiers
/// proactively; <c>SpendGuard</c> remains the hard kill-switch.
/// </param>
/// <param name="UrgencyBoostMonthlyBudgetUsd">
/// Optional separate ceiling for spend triggered by <see cref="LlmUrgency.Urgent"/>.
/// When set, the <c>UrgencyRoutingAdvisor</c> stops promoting once this is reached.
/// Null = share the main budget.
/// </param>
/// <param name="QuietHoursStart">Local time of day quiet hours begin. Null = no quiet hours.</param>
/// <param name="QuietHoursEnd">Local time of day quiet hours end (may wrap past midnight).</param>
/// <param name="BudgetPressureThreshold">
/// Burn fraction (0..1) at which the budget-pressure advisor starts demoting tiers.
/// 0.7 means demotion kicks in at 70% of the monthly cap. Below this, no action.
/// </param>
/// <param name="SubscriptionQuotaThreshold">
/// Burn fraction (0..1) at which the subscription-quota advisor starts demoting away
/// from a subscription toward paid API. 0.7 means demotion kicks in at 70% of the
/// per-window quota estimate.
/// </param>
/// <param name="AllowUrgencyBoost">
/// When false, the urgency advisor is disabled — even <see cref="LlmUrgency.Urgent"/>
/// calls stay at the configured tier. Lets the user globally cap spend behavior.
/// </param>
/// <param name="QuietHoursForceLocal">
/// During quiet hours, force background work to a local provider if available. When false,
/// quiet hours only nudge preferences but don't override.
/// </param>
/// <param name="AllowMeteredPipelineFallback">
/// When false (the default), the message-pipeline route guard will never fall over to a
/// metered cloud API: it prefers a currently proven $0 route (subscription/local) and
/// otherwise pauses the pipeline. Unproven routes are never used for production work.
/// Set true only to explicitly allow paying per message when no proven free route is usable.
/// </param>
/// <param name="MeteredPipelineDailyCapUsd">
/// Hard daily ceiling (USD, local-day) on metered message-pipeline spend. A finite,
/// positive value is required whenever <see cref="AllowMeteredPipelineFallback"/> is
/// true. Once today's metered pipeline spend reaches the cap, the guard stops using
/// the metered route and pauses instead of spending more.
/// </param>
/// <param name="ModelScoutCapabilityWeight">
/// Relative capability weight used by Model Scout when a recommendation request does not
/// provide its own weights. Must be non-negative and at least one Scout weight must be positive.
/// </param>
/// <param name="ModelScoutSpeedWeight">Relative measured-speed weight used by Model Scout.</param>
/// <param name="ModelScoutCostWeight">Relative effective-cost weight used by Model Scout.</param>
/// <param name="UseCustomModelScoutWeights">
/// True only after a user explicitly chooses Scout weights. False preserves the
/// ranker's adaptive interactive/background defaults.
/// </param>
public sealed record RoutingPolicy(
    decimal? MonthlyCloudBudgetUsd = null,
    decimal? UrgencyBoostMonthlyBudgetUsd = null,
    TimeOnly? QuietHoursStart = null,
    TimeOnly? QuietHoursEnd = null,
    decimal BudgetPressureThreshold = 0.7m,
    decimal SubscriptionQuotaThreshold = 0.7m,
    bool AllowUrgencyBoost = true,
    bool QuietHoursForceLocal = true,
    bool AllowMeteredPipelineFallback = false,
    decimal? MeteredPipelineDailyCapUsd = null,
    decimal ModelScoutCapabilityWeight = 1m,
    decimal ModelScoutSpeedWeight = 1m,
    decimal ModelScoutCostWeight = 1m,
    bool UseCustomModelScoutWeights = false,
    ModelScoutConnectionPreference PreferredModelConnection = ModelScoutConnectionPreference.SubscriptionFirst)
{
    public static RoutingPolicy Default { get; } = new();

    /// <summary>
    /// Normalizes a policy received from an untrusted persistence or sync boundary.
    /// Invalid fields are reset independently to safe defaults so every consumer,
    /// including audit rendering, describes the same effective policy.
    /// </summary>
    public static RoutingPolicy NormalizeUntrusted(RoutingPolicy? policy)
    {
        policy ??= Default;
        var defaults = Default;
        var meteredCapValid = IsNonNegativeBudget(policy.MeteredPipelineDailyCapUsd);
        var meteredFallbackValid = !policy.AllowMeteredPipelineFallback
                                   || policy.MeteredPipelineDailyCapUsd is > 0 and <= 1_000_000m;
        var weightsValid = policy.ModelScoutCapabilityWeight is >= 0 and <= 100
                           && policy.ModelScoutSpeedWeight is >= 0 and <= 100
                           && policy.ModelScoutCostWeight is >= 0 and <= 100
                           && (!policy.UseCustomModelScoutWeights
                               || policy.ModelScoutCapabilityWeight
                                  + policy.ModelScoutSpeedWeight
                                  + policy.ModelScoutCostWeight > 0);
        return policy with
        {
            MonthlyCloudBudgetUsd = IsPositiveBudget(policy.MonthlyCloudBudgetUsd)
                ? policy.MonthlyCloudBudgetUsd
                : defaults.MonthlyCloudBudgetUsd,
            UrgencyBoostMonthlyBudgetUsd = IsPositiveBudget(policy.UrgencyBoostMonthlyBudgetUsd)
                ? policy.UrgencyBoostMonthlyBudgetUsd
                : defaults.UrgencyBoostMonthlyBudgetUsd,
            AllowMeteredPipelineFallback = meteredFallbackValid
                && policy.AllowMeteredPipelineFallback,
            MeteredPipelineDailyCapUsd = meteredCapValid
                ? policy.MeteredPipelineDailyCapUsd
                : defaults.MeteredPipelineDailyCapUsd,
            BudgetPressureThreshold = IsUnitInterval(policy.BudgetPressureThreshold)
                ? policy.BudgetPressureThreshold
                : defaults.BudgetPressureThreshold,
            SubscriptionQuotaThreshold = IsUnitInterval(policy.SubscriptionQuotaThreshold)
                ? policy.SubscriptionQuotaThreshold
                : defaults.SubscriptionQuotaThreshold,
            QuietHoursStart = policy.QuietHoursStart.HasValue == policy.QuietHoursEnd.HasValue
                ? policy.QuietHoursStart
                : defaults.QuietHoursStart,
            QuietHoursEnd = policy.QuietHoursStart.HasValue == policy.QuietHoursEnd.HasValue
                ? policy.QuietHoursEnd
                : defaults.QuietHoursEnd,
            ModelScoutCapabilityWeight = weightsValid
                ? policy.ModelScoutCapabilityWeight
                : defaults.ModelScoutCapabilityWeight,
            ModelScoutSpeedWeight = weightsValid
                ? policy.ModelScoutSpeedWeight
                : defaults.ModelScoutSpeedWeight,
            ModelScoutCostWeight = weightsValid
                ? policy.ModelScoutCostWeight
                : defaults.ModelScoutCostWeight,
            UseCustomModelScoutWeights = weightsValid && policy.UseCustomModelScoutWeights,
            PreferredModelConnection = Enum.IsDefined(policy.PreferredModelConnection)
                ? policy.PreferredModelConnection : defaults.PreferredModelConnection,
        };
    }

    /// <summary>
    /// True if <paramref name="now"/>'s time-of-day falls inside the configured quiet-hours
    /// window. Handles windows that wrap past midnight (e.g. 22:00–06:00).
    /// </summary>
    public bool IsInQuietHours(DateTimeOffset now)
    {
        if (QuietHoursStart is not { } start || QuietHoursEnd is not { } end)
        {
            return false;
        }
        var localTime = TimeOnly.FromDateTime(now.LocalDateTime);
        if (start == end) return false;
        return start < end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }

    /// <summary>
    /// Three-way merge for a policy editor. Applies only the fields the editor
    /// changed (<paramref name="edited"/> compared with the <paramref name="baseline"/>
    /// it loaded) onto the <paramref name="current"/> stored policy, so a field
    /// another writer changed meanwhile (for example the connection preference on
    /// AI Connections) survives. Returns false, with the conflicting field names,
    /// when the editor and another writer changed the same field to different values.
    /// </summary>
    public static bool TryMergeEdit(
        RoutingPolicy baseline,
        RoutingPolicy edited,
        RoutingPolicy current,
        out RoutingPolicy merged,
        out IReadOnlyList<string> conflicts)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(current);

        var conflicted = new List<string>();
        T Pick<T>(string name, T before, T requested, T fresh)
        {
            var comparer = EqualityComparer<T>.Default;
            if (comparer.Equals(before, requested))
            {
                return fresh;
            }

            if (!comparer.Equals(before, fresh) && !comparer.Equals(requested, fresh))
            {
                conflicted.Add(name);
            }

            return requested;
        }

        // Start from the current policy so any field not listed here keeps its
        // stored value rather than a constructor default.
        merged = current with
        {
            MonthlyCloudBudgetUsd = Pick(nameof(MonthlyCloudBudgetUsd), baseline.MonthlyCloudBudgetUsd, edited.MonthlyCloudBudgetUsd, current.MonthlyCloudBudgetUsd),
            UrgencyBoostMonthlyBudgetUsd = Pick(nameof(UrgencyBoostMonthlyBudgetUsd), baseline.UrgencyBoostMonthlyBudgetUsd, edited.UrgencyBoostMonthlyBudgetUsd, current.UrgencyBoostMonthlyBudgetUsd),
            QuietHoursStart = Pick(nameof(QuietHoursStart), baseline.QuietHoursStart, edited.QuietHoursStart, current.QuietHoursStart),
            QuietHoursEnd = Pick(nameof(QuietHoursEnd), baseline.QuietHoursEnd, edited.QuietHoursEnd, current.QuietHoursEnd),
            BudgetPressureThreshold = Pick(nameof(BudgetPressureThreshold), baseline.BudgetPressureThreshold, edited.BudgetPressureThreshold, current.BudgetPressureThreshold),
            SubscriptionQuotaThreshold = Pick(nameof(SubscriptionQuotaThreshold), baseline.SubscriptionQuotaThreshold, edited.SubscriptionQuotaThreshold, current.SubscriptionQuotaThreshold),
            AllowUrgencyBoost = Pick(nameof(AllowUrgencyBoost), baseline.AllowUrgencyBoost, edited.AllowUrgencyBoost, current.AllowUrgencyBoost),
            QuietHoursForceLocal = Pick(nameof(QuietHoursForceLocal), baseline.QuietHoursForceLocal, edited.QuietHoursForceLocal, current.QuietHoursForceLocal),
            AllowMeteredPipelineFallback = Pick(nameof(AllowMeteredPipelineFallback), baseline.AllowMeteredPipelineFallback, edited.AllowMeteredPipelineFallback, current.AllowMeteredPipelineFallback),
            MeteredPipelineDailyCapUsd = Pick(nameof(MeteredPipelineDailyCapUsd), baseline.MeteredPipelineDailyCapUsd, edited.MeteredPipelineDailyCapUsd, current.MeteredPipelineDailyCapUsd),
            ModelScoutCapabilityWeight = Pick(nameof(ModelScoutCapabilityWeight), baseline.ModelScoutCapabilityWeight, edited.ModelScoutCapabilityWeight, current.ModelScoutCapabilityWeight),
            ModelScoutSpeedWeight = Pick(nameof(ModelScoutSpeedWeight), baseline.ModelScoutSpeedWeight, edited.ModelScoutSpeedWeight, current.ModelScoutSpeedWeight),
            ModelScoutCostWeight = Pick(nameof(ModelScoutCostWeight), baseline.ModelScoutCostWeight, edited.ModelScoutCostWeight, current.ModelScoutCostWeight),
            UseCustomModelScoutWeights = Pick(nameof(UseCustomModelScoutWeights), baseline.UseCustomModelScoutWeights, edited.UseCustomModelScoutWeights, current.UseCustomModelScoutWeights),
            PreferredModelConnection = Pick(nameof(PreferredModelConnection), baseline.PreferredModelConnection, edited.PreferredModelConnection, current.PreferredModelConnection),
        };
        conflicts = conflicted;
        return conflicted.Count == 0;
    }

    private static bool IsPositiveBudget(decimal? value) =>
        value is null or (> 0 and <= 1_000_000m);

    private static bool IsNonNegativeBudget(decimal? value) =>
        value is null or (>= 0 and <= 1_000_000m);

    private static bool IsUnitInterval(decimal value) => value is >= 0 and <= 1;
}

/// <summary>
/// Persistence boundary for the user's <see cref="RoutingPolicy"/>. Apps wire a
/// SQLite-backed implementation; the default registered by SwarmCore returns
/// <see cref="RoutingPolicy.Default"/> and ignores writes (so unconfigured installs
/// still route correctly).
/// </summary>
public interface IRoutingPolicyStore
{
    Task<RoutingPolicy> GetAsync(CancellationToken ct = default);
    Task SetAsync(RoutingPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Reads the effective policy together with its opaque storage revision. The
    /// revision changes on every write, including a same-value user or sync write,
    /// so a later writer cannot be mistaken for the caller that owns a rollback.
    /// </summary>
    Task<RoutingPolicySnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the policy only when storage still exactly matches
    /// <paramref name="expected"/>, and persists the caller-owned
    /// <paramref name="mutationRevision"/> with the write. The caller can therefore
    /// compensate a commit-then-throw outcome without re-reading and claiming a
    /// later identical-looking write. Every implementation must provide this
    /// operation atomically; value-only stores cannot prevent ABA.
    /// </summary>
    Task<RoutingPolicyConditionalWriteResult> TrySetAsync(
        RoutingPolicy policy,
        RoutingPolicySnapshot expected,
        string mutationRevision,
        CancellationToken ct = default);
}

public sealed record RoutingPolicySnapshot(
    RoutingPolicy Policy,
    string Revision);

public enum RoutingPolicyConditionalWriteStatus
{
    Applied,
    Conflict,
}

public sealed record RoutingPolicyConditionalWriteResult(
    RoutingPolicyConditionalWriteStatus Status,
    RoutingPolicy Current,
    string Revision)
{
    public bool Applied => Status == RoutingPolicyConditionalWriteStatus.Applied;

    public RoutingPolicySnapshot Snapshot => new(Current, Revision);
}
