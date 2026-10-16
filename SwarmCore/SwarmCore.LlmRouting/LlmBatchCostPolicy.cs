namespace SwarmCore.LlmRouting;

/// <summary>Plans complete finite work without inventing a spending allowance.</summary>
public static class LlmBatchCostPolicy
{
    public static decimal? Forecast(IEnumerable<decimal?> workCosts)
    {
        decimal? total = 0m;
        foreach (var cost in workCosts)
        {
            if (cost is < 0m) throw new ArgumentOutOfRangeException(nameof(workCosts));
            total += cost;
        }
        return total;
    }

    public static bool CanStartNext(decimal? completePlanCost, decimal? completedExposure,
        decimal? nextCost, decimal? explicitUserLimit)
    {
        if (explicitUserLimit is <= 0m) throw new ArgumentOutOfRangeException(nameof(explicitUserLimit));
        if (completePlanCost is < 0m || completedExposure is < 0m || nextCost is < 0m)
            throw new ArgumentOutOfRangeException(nameof(nextCost));
        // A free check cannot consume a monetary allowance. With no explicit
        // ceiling, finite work ends on its own success/failure contract.
        if (nextCost == 0m || explicitUserLimit is null) return true;
        // Do not spend part of a paid plan that is already known to be unable
        // to finish. Unknown cost is not zero and cannot prove affordability.
        return completePlanCost is { } plan && completedExposure is { } spent
            && nextCost is { } next && plan <= explicitUserLimit.Value
            && spent + next <= explicitUserLimit.Value;
    }
}
