namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Snapshot of cloud API spend vs. the user's configured monthly cap. Read by the
/// <c>BudgetPressureRoutingAdvisor</c> on every call (cached briefly by the source
/// implementation so it doesn't hit the usage DB on every selection).
/// </summary>
/// <param name="MonthToDateUsd">Cloud-API spend so far this calendar month.</param>
/// <param name="MonthlyBudgetUsd">User's configured cap; null when unlimited.</param>
/// <param name="BurnFraction">
/// <c>MonthToDateUsd / MonthlyBudgetUsd</c>, or 0 when the budget is null. Can exceed
/// 1.0 when spend has overshot the cap.
/// </param>
/// <param name="ProjectedMonthUsd">
/// Naive linear projection of total month spend at the current burn rate. Useful for
/// "will overshoot" detection before <see cref="BurnFraction"/> alone trips.
/// </param>
public sealed record BudgetPressureSnapshot(
    decimal MonthToDateUsd,
    decimal? MonthlyBudgetUsd,
    decimal BurnFraction,
    decimal ProjectedMonthUsd)
{
    public static BudgetPressureSnapshot Unconstrained { get; } =
        new(MonthToDateUsd: 0m, MonthlyBudgetUsd: null, BurnFraction: 0m, ProjectedMonthUsd: 0m);

    /// <summary>True when current burn or projected spend has crossed the supplied threshold.</summary>
    public bool ExceedsThreshold(decimal threshold) =>
        MonthlyBudgetUsd is not null
        && (BurnFraction >= threshold || (MonthlyBudgetUsd > 0 && ProjectedMonthUsd / MonthlyBudgetUsd.Value >= threshold));
}

/// <summary>
/// Reads recent LLM usage and returns a current budget-pressure snapshot. The default
/// SwarmCore implementation queries <see cref="ILlmUsageTracker.GetCostSummaryAsync"/>
/// for the current month and caches briefly. Apps with their own spend dashboards can
/// register a richer implementation that aligns the cap with the SpendGuard config.
/// </summary>
public interface IBudgetPressureSource
{
    Task<BudgetPressureSnapshot> GetCurrentAsync(CancellationToken ct = default);
}
