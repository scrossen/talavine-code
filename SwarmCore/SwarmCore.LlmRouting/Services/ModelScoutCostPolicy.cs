using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Shared effective-cost ordering. API dollars and subscription capacity remain
/// separate units; all CLI consumers use the reviewed pack's exact tier ratio.
/// Quota pressure cannot erase that ratio when an account window is fresh.
/// </summary>
public static class ModelScoutCostPolicy
{
    public static decimal SubscriptionCapacity(string provider, string model)
        => SubscriptionCliProviders.IsSubscriptionCliProvider(provider)
            ? ProviderPackCatalog.ResolveSubscriptionCapacityWeight(provider, model)
            : 0m;

    public static (int Channel, decimal Consumption) RoutineOrder(
        string provider, string model, bool isLocal, decimal? estimatedApiCost)
        => isLocal ? (0, 0m)
            : SubscriptionCliProviders.IsSubscriptionCliProvider(provider)
                ? (1, SubscriptionCapacity(provider, model))
                : (2, estimatedApiCost is >= 0 ? estimatedApiCost.Value : decimal.MaxValue);

    public static decimal CapacityPressure(string provider, string model, decimal burnFraction)
        => SubscriptionCapacity(provider, model) * (1m + Math.Clamp(burnFraction, 0m, 1m));

    public static decimal CapacityUtility(string provider, string model, decimal burnFraction = 0m)
        => 1m / (1m + CapacityPressure(provider, model, burnFraction) / 10m);
}
