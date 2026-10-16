using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// "Make this one fast and smart." When the caller marks a request
/// <see cref="LlmUrgency.Urgent"/>, promote routing one tier above whatever the
/// baseline preference is — subscription CLI if healthy, else frontier model on
/// the current provider, else frontier on any cloud provider that's available.
///
/// Defers (returns null) when:
///   - urgency is anything other than <see cref="LlmUrgency.Urgent"/>
///   - the user has disabled urgency boosts via <see cref="RoutingPolicy.AllowUrgencyBoost"/>
///   - <see cref="LlmPrivacyClass.LocalOnly"/> is in effect — privacy beats urgency
///   - the urgency-boost budget has been exhausted (<see cref="RoutingPolicy.UrgencyBoostMonthlyBudgetUsd"/>)
///   - no eligible faster/smarter route is available
///
/// Never persists — urgency is a per-call decision, not a baseline preference change.
/// </summary>
public sealed class UrgencyRoutingAdvisor : ILlmRoutePolicyAdvisor
{
    public int Priority => LlmRoutePolicyPriorities.Urgency;
    public LlmRoutePolicyKind PolicyKind => LlmRoutePolicyKind.Urgency;
    public string PolicyName => nameof(UrgencyRoutingAdvisor);

    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILlmProviderRegistry _providerRegistry;
    private readonly ILlmProviderCooldownTracker? _cooldownTracker;
    private readonly LlmProviderDiscoveryCoordinator _providerDiscovery;
    private readonly ILogger<UrgencyRoutingAdvisor> _logger;

    private static readonly TimeSpan ProviderDiscoveryTimeout = TimeSpan.FromSeconds(2);

    public UrgencyRoutingAdvisor(
        ILlmSettingsProvider settingsProvider,
        ILlmProviderRegistry providerRegistry,
        ILogger<UrgencyRoutingAdvisor> logger,
        ILlmProviderCooldownTracker? cooldownTracker = null,
        LlmProviderDiscoveryCoordinator? providerDiscovery = null)
    {
        _settingsProvider = settingsProvider;
        _providerRegistry = providerRegistry;
        _cooldownTracker = cooldownTracker;
        _providerDiscovery = providerDiscovery ?? LlmProviderDiscoveryCoordinator.Shared;
        _logger = logger;
    }

    public async Task<LlmRouteAdvisorRecommendation?> GetRecommendationAsync(
        LlmRouteAdvisorContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.CallContext.Urgency != LlmUrgency.Urgent) return null;
        if (!context.Policy.AllowUrgencyBoost) return null;
        if (context.CallContext.PrivacyClass == LlmPrivacyClass.LocalOnly) return null;
        // Embeddings are pinned globally; routing decisions can't switch the model
        // mid-corpus without invalidating the entire semantic index. Belt-and-braces
        // guard — LlmRouter.SelectEmbeddingModelAsync also bypasses advisors, but a
        // future refactor mustn't accidentally drift these.
        if (IsEmbeddingFunction(context.FunctionType)) return null;

        // If a separate urgency-boost budget is configured and already exhausted, decline.
        var policy = context.Policy;
        if (policy.UrgencyBoostMonthlyBudgetUsd is > 0
            && context.BudgetPressure.MonthToDateUsd >= policy.UrgencyBoostMonthlyBudgetUsd.Value)
        {
            _logger.LogDebug(
                "Urgency boost declined for {FunctionType}: month-to-date {Mtd:C} >= urgency budget {Cap:C}",
                context.FunctionType, context.BudgetPressure.MonthToDateUsd, policy.UrgencyBoostMonthlyBudgetUsd);
            return null;
        }

        // First choice: an installed subscription CLI on its frontier model — no marginal cost.
        var subscriptionPick = await TryPickSubscriptionFrontierAsync(cancellationToken).ConfigureAwait(false);
        if (subscriptionPick is not null)
        {
            return BuildRecommendation(
                subscriptionPick.Value.Provider,
                subscriptionPick.Value.Model,
                context.CurrentPreference,
                reason: $"Urgency boost: routing to your {subscriptionPick.Value.Provider} subscription at frontier tier.");
        }

        // Second choice: frontier model on the current preference's provider.
        var current = context.CurrentPreference;
        if (current is { IsEnabled: true } && !string.IsNullOrWhiteSpace(current.PreferredProvider))
        {
            var frontierOnCurrent = ModelDefaults.Frontier(current.PreferredProvider);
            if (!string.IsNullOrWhiteSpace(frontierOnCurrent)
                && !string.Equals(frontierOnCurrent, current.PreferredModel, StringComparison.OrdinalIgnoreCase)
                && IsProviderEligible(current.PreferredProvider))
            {
                return BuildRecommendation(
                    current.PreferredProvider,
                    frontierOnCurrent!,
                    current,
                    reason: $"Urgency boost: promoting {current.PreferredProvider} to frontier tier ({frontierOnCurrent}).");
            }
        }

        // Third choice: any cloud provider's frontier tier that's enabled and not in cooldown.
        var anyFrontier = await TryPickAnyCloudFrontierAsync(cancellationToken).ConfigureAwait(false);
        if (anyFrontier is not null)
        {
            return BuildRecommendation(
                anyFrontier.Value.Provider,
                anyFrontier.Value.Model,
                context.CurrentPreference,
                reason: $"Urgency boost: routing to frontier model {anyFrontier.Value.Provider}/{anyFrontier.Value.Model}.");
        }

        return null;
    }

    private async Task<(string Provider, string Model)?> TryPickSubscriptionFrontierAsync(CancellationToken ct)
    {
        var settings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
        var candidates = settings
            .Where(s => s.IsEnabled && SubscriptionCliProviders.IsSubscriptionCliProvider(s.ProviderName))
            .OrderBy(s => s.Priority)
            .Where(setting => !string.IsNullOrWhiteSpace(ModelDefaults.Frontier(setting.ProviderName)))
            .ToList();
        var eligibility = await Task.WhenAll(candidates.Select(async setting =>
            (Setting: setting,
             Eligible: await IsSubscriptionProviderEligibleAsync(setting.ProviderName, ct)
                 .ConfigureAwait(false)))).ConfigureAwait(false);
        var selected = eligibility.FirstOrDefault(candidate => candidate.Eligible).Setting;
        return selected is null
            ? null
            : (selected.ProviderName, ModelDefaults.Frontier(selected.ProviderName)!);
    }

    private async Task<(string Provider, string Model)?> TryPickAnyCloudFrontierAsync(CancellationToken ct)
    {
        var settings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
        foreach (var setting in settings
                     .Where(s => s.IsEnabled
                                 && !LlmProviderLocalityPolicy.IsTrustedLocalProvider(s)
                                 && !SubscriptionCliProviders.IsSubscriptionCliProvider(s.ProviderName))
                     .OrderBy(s => s.Priority))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsProviderEligible(setting.ProviderName)) continue;
            var frontier = ModelDefaults.Frontier(setting.ProviderName);
            if (string.IsNullOrWhiteSpace(frontier)) continue;
            return (setting.ProviderName, frontier!);
        }
        return null;
    }

    private bool IsProviderEligible(string providerName)
    {
        if (_cooldownTracker is not null && !_cooldownTracker.IsAvailable(providerName)) return false;
        var provider = _providerRegistry.GetProvider(providerName);
        return provider is not null;
    }

    private async Task<bool> IsSubscriptionProviderEligibleAsync(string providerName, CancellationToken ct)
    {
        if (_cooldownTracker is not null && !_cooldownTracker.IsAvailable(providerName)) return false;
        var provider = _providerRegistry.GetProvider(providerName);
        if (provider is null) return false;

        try
        {
            var connectivity = await _providerDiscovery.ProbeConnectivityAsync(
                    provider,
                    ProviderDiscoveryTimeout,
                    ct,
                    highPriority: true)
                .ConfigureAwait(false);
            if (!connectivity.Completed || !connectivity.IsHealthy)
            {
                _logger.LogDebug(
                    "Urgency boost skipping subscription CLI {Provider}: {Message}",
                    providerName,
                    connectivity.Message);
            }

            return connectivity.Completed && connectivity.IsHealthy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Urgency boost skipping subscription CLI {Provider}: health check threw.", providerName);
            return false;
        }
    }

    private static bool IsEmbeddingFunction(string functionType) =>
        functionType.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase)
        || functionType.Equals(FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase);

    private static LlmRouteAdvisorRecommendation BuildRecommendation(
        string provider,
        string model,
        LlmFunctionPreference? fallback,
        string reason) => new()
        {
            Provider = provider,
            Model = model,
            FallbackProvider = fallback?.PreferredProvider,
            FallbackModel = fallback?.PreferredModel,
            PersistPreference = false,
            UpdatedBy = nameof(UrgencyRoutingAdvisor),
            Reason = reason,
        };
}
