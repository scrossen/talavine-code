using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// "Slow the spend before it overshoots." When month-to-date cloud spend crosses the
/// user's configured pressure threshold (default 70% of the cap), demote routing one
/// tier — to local if a local provider is available, otherwise to the economy tier on
/// the current provider.
///
/// This is a soft step-down. The hard kill-switch is SpendGuard's anomaly pause; this
/// advisor's job is to bend the spend curve before SpendGuard has to intervene.
///
/// Defers (returns null) when:
///   - <see cref="LlmUrgency.Urgent"/> — the user explicitly opted into the spend on this call
///   - <see cref="LlmPrivacyClass.LocalOnly"/> — already going local
///   - the budget pressure snapshot has no cap configured
///   - the burn fraction is below the threshold and projected spend is on track
///   - no demotion target is available (no local provider, no cheaper tier)
/// </summary>
public sealed class BudgetPressureRoutingAdvisor : ILlmRoutePolicyAdvisor
{
    // Sits just after the existing Budget priority so explicit budget advisors run first.
    public int Priority => LlmRoutePolicyPriorities.Budget + 5;
    public LlmRoutePolicyKind PolicyKind => LlmRoutePolicyKind.Budget;
    public string PolicyName => nameof(BudgetPressureRoutingAdvisor);

    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILlmProviderRegistry _providerRegistry;
    private readonly ILlmProviderCooldownTracker? _cooldownTracker;
    private readonly ILogger<BudgetPressureRoutingAdvisor> _logger;

    public BudgetPressureRoutingAdvisor(
        ILlmSettingsProvider settingsProvider,
        ILlmProviderRegistry providerRegistry,
        ILogger<BudgetPressureRoutingAdvisor> logger,
        ILlmProviderCooldownTracker? cooldownTracker = null)
    {
        _settingsProvider = settingsProvider;
        _providerRegistry = providerRegistry;
        _cooldownTracker = cooldownTracker;
        _logger = logger;
    }

    public async Task<LlmRouteAdvisorRecommendation?> GetRecommendationAsync(
        LlmRouteAdvisorContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.CallContext.Urgency == LlmUrgency.Urgent) return null;
        if (context.CallContext.PrivacyClass == LlmPrivacyClass.LocalOnly) return null;
        // Embeddings are pinned — never demote off the pinned model based on budget.
        if (context.FunctionType.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase)
            || context.FunctionType.Equals(FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase)) return null;
        if (!context.BudgetPressure.ExceedsThreshold(context.Policy.BudgetPressureThreshold)) return null;
        if (context.RequiresVision) return null; // vision demotion path is not safe without capability re-check

        // First choice: shift to local if one is available. Cheapest possible.
        var local = await TryPickLocalAsync(cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            _logger.LogInformation(
                "Budget pressure demotion for {FunctionType}: routing to local {Provider}/{Model} " +
                "(burn {Burn:P0}, threshold {Threshold:P0})",
                context.FunctionType, local.Value.Provider, local.Value.Model,
                context.BudgetPressure.BurnFraction, context.Policy.BudgetPressureThreshold);

            return new LlmRouteAdvisorRecommendation
            {
                Provider = local.Value.Provider,
                Model = local.Value.Model,
                FallbackProvider = context.CurrentPreference?.PreferredProvider,
                FallbackModel = context.CurrentPreference?.PreferredModel,
                PersistPreference = false,
                UpdatedBy = nameof(BudgetPressureRoutingAdvisor),
                Reason = $"Budget pressure ({context.BudgetPressure.BurnFraction:P0} of cap) — running locally.",
            };
        }

        // Second choice: drop to the economy tier on the current provider.
        var current = context.CurrentPreference;
        if (current is { IsEnabled: true } && !string.IsNullOrWhiteSpace(current.PreferredProvider))
        {
            var economy = ModelDefaults.Economy(current.PreferredProvider);
            if (!string.IsNullOrWhiteSpace(economy)
                && !string.Equals(economy, current.PreferredModel, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Budget pressure demotion for {FunctionType}: {Provider} {OldModel} → economy {NewModel}",
                    context.FunctionType, current.PreferredProvider, current.PreferredModel, economy);

                return new LlmRouteAdvisorRecommendation
                {
                    Provider = current.PreferredProvider,
                    Model = economy!,
                    FallbackProvider = current.FallbackProvider,
                    FallbackModel = current.FallbackModel,
                    PersistPreference = false,
                    UpdatedBy = nameof(BudgetPressureRoutingAdvisor),
                    Reason = $"Budget pressure ({context.BudgetPressure.BurnFraction:P0}) — using economy tier.",
                };
            }
        }

        return null;
    }

    private async Task<(string Provider, string Model)?> TryPickLocalAsync(CancellationToken ct)
    {
        var settings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
        foreach (var setting in settings
                     .Where(s => s.IsEnabled
                                 && LlmProviderLocalityPolicy.IsTrustedLocalProvider(s)
                                 && !string.IsNullOrWhiteSpace(s.ProviderName))
                     .OrderBy(s => s.Priority))
        {
            ct.ThrowIfCancellationRequested();
            if (_cooldownTracker is not null && !_cooldownTracker.IsAvailable(setting.ProviderName)) continue;
            var provider = _providerRegistry.GetProvider(setting.ProviderName);
            if (provider is null) continue;
            var model = !string.IsNullOrWhiteSpace(setting.DefaultModel)
                ? setting.DefaultModel!
                : _settingsProvider.GetDefaultModel(setting.ProviderName);
            if (string.IsNullOrWhiteSpace(model)) continue;
            return (setting.ProviderName, model);
        }
        return null;
    }
}
