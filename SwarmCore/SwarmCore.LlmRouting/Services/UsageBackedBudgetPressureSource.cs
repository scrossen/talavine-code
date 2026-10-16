using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Computes <see cref="BudgetPressureSnapshot"/> from current-month usage via
/// <see cref="ILlmUsageTracker.GetCostSummaryAsync"/>, divided by the user's configured
/// monthly cap on <see cref="RoutingPolicy"/>. Caches the result for 60 seconds so the
/// router doesn't hit the usage DB on every call.
/// </summary>
public sealed class UsageBackedBudgetPressureSource : IBudgetPressureSource
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ILlmUsageTracker _usageTracker;
    private readonly IRoutingPolicyStore _policyStore;
    private readonly ILogger<UsageBackedBudgetPressureSource> _logger;

    private BudgetPressureSnapshot _cached = BudgetPressureSnapshot.Unconstrained;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsageBackedBudgetPressureSource(
        ILlmUsageTracker usageTracker,
        IRoutingPolicyStore policyStore,
        ILogger<UsageBackedBudgetPressureSource> logger)
    {
        _usageTracker = usageTracker;
        _policyStore = policyStore;
        _logger = logger;
    }

    public async Task<BudgetPressureSnapshot> GetCurrentAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            {
                return _cached;
            }

            var policy = await _policyStore.GetAsync(ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endOfMonth = startOfMonth.AddMonths(1);

            decimal monthToDate;
            try
            {
                var summary = await _usageTracker.GetCostSummaryAsync(null, startOfMonth, endOfMonth)
                    .ConfigureAwait(false);
                monthToDate = summary.TotalCost;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Budget pressure: failed to read month-to-date usage; assuming unconstrained");
                _cached = BudgetPressureSnapshot.Unconstrained;
                _cachedAt = DateTimeOffset.UtcNow;
                return _cached;
            }

            var daysElapsed = Math.Max(1, (now - startOfMonth).TotalDays);
            var daysInMonth = (endOfMonth - startOfMonth).TotalDays;
            var projected = daysInMonth > 0
                ? monthToDate * (decimal)(daysInMonth / daysElapsed)
                : monthToDate;

            var burn = policy.MonthlyCloudBudgetUsd is > 0
                ? monthToDate / policy.MonthlyCloudBudgetUsd.Value
                : 0m;

            _cached = new BudgetPressureSnapshot(
                MonthToDateUsd: monthToDate,
                MonthlyBudgetUsd: policy.MonthlyCloudBudgetUsd,
                BurnFraction: burn,
                ProjectedMonthUsd: projected);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
