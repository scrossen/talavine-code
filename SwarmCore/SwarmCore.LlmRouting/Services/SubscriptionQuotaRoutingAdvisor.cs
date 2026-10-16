using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// "Step off the subscription before it taps out." When the soft-quota tracker says
/// a subscription provider is approaching its window cap (default 70%), demote routing
/// to another subscription, a trusted local provider, a paid API of the same provider
/// family — or to the next-best cloud provider — so the user has headroom for whatever
/// they actually wanted to use the subscription for later. Local sits ahead of the
/// metered lanes because its marginal cost is zero; a healthy local model should soak
/// up quota pressure before any paid API key does.
///
/// Sits ahead of <c>SubscriptionFirstRouteAdvisor</c> only by skipping subscription
/// picks; it doesn't fight that advisor head-on. The order is: this advisor demotes
/// off a saturated subscription, then SubscriptionFirst picks from what's left.
///
/// Defers (returns null) when:
///   - <see cref="LlmUrgency.Urgent"/> — user explicitly asked us to spend
///   - <see cref="LlmPrivacyClass.LocalOnly"/> — privacy beats quota
///   - the current preference isn't a subscription CLI (this advisor only steps off subscriptions)
///   - no subscription is configured or none are over the threshold
///   - no eligible non-subscription provider is available to receive the load
/// </summary>
public sealed class SubscriptionQuotaRoutingAdvisor : ILlmRoutePolicyAdvisor
{
    public int Priority => LlmRoutePolicyPriorities.SubscriptionQuota;
    public LlmRoutePolicyKind PolicyKind => LlmRoutePolicyKind.SubscriptionQuota;
    public string PolicyName => nameof(SubscriptionQuotaRoutingAdvisor);

    private readonly ISubscriptionQuotaTracker _quotaTracker;
    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILlmProviderRegistry _providerRegistry;
    private readonly ILlmProviderCooldownTracker? _cooldownTracker;
    private readonly LlmProviderDiscoveryCoordinator _providerDiscovery;
    private readonly ILogger<SubscriptionQuotaRoutingAdvisor> _logger;

    private static readonly TimeSpan ProviderDiscoveryTimeout = TimeSpan.FromSeconds(2);

    public SubscriptionQuotaRoutingAdvisor(
        ISubscriptionQuotaTracker quotaTracker,
        ILlmSettingsProvider settingsProvider,
        ILlmProviderRegistry providerRegistry,
        ILogger<SubscriptionQuotaRoutingAdvisor> logger,
        ILlmProviderCooldownTracker? cooldownTracker = null,
        LlmProviderDiscoveryCoordinator? providerDiscovery = null)
    {
        _quotaTracker = quotaTracker;
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
        if (context.CallContext.Urgency == LlmUrgency.Urgent) return null;
        if (context.CallContext.PrivacyClass == LlmPrivacyClass.LocalOnly) return null;
        // Subscription CLIs don't serve embeddings anyway, but guard explicitly so a
        // future refactor that exposes embedding endpoints on the CLIs doesn't
        // accidentally bypass the pin.
        if (context.FunctionType.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase)
            || context.FunctionType.Equals(FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase)) return null;

        var current = context.CurrentPreference;
        var threshold = context.Policy.SubscriptionQuotaThreshold;

        // We're only here to step *off* a saturated subscription. If the current preference
        // isn't a subscription CLI, leave it alone — let SubscriptionFirstRouteAdvisor decide
        // whether to switch into one.
        if (current is not { IsEnabled: true }
            || string.IsNullOrWhiteSpace(current.PreferredProvider)
            || !SubscriptionCliProviders.IsSubscriptionCliProvider(current.PreferredProvider))
        {
            return null;
        }

        var snapshot = _quotaTracker.GetSnapshot(current.PreferredProvider);
        if (snapshot is null || !snapshot.ExceedsThreshold(threshold)) return null;

        // Find an eligible destination. Prefer another subscription first, then a
        // trusted local provider (zero marginal cost), then a paid API of the same
        // provider family when configured (e.g. ClaudeCode -> Anthropic), else any cloud.
        var anotherSubscription = await TryPickAnotherSubscriptionAsync(
                current.PreferredProvider,
                threshold,
                cancellationToken)
            .ConfigureAwait(false);
        (string Provider, string Model)? local = null;
        if (anotherSubscription is null)
        {
            local = await TryPickLocalAsync(cancellationToken).ConfigureAwait(false);
        }
        (string Provider, string Model)? sameFamily = null;
        if (anotherSubscription is null && local is null)
        {
            sameFamily = await TryPickSameFamilyCloudAsync(current.PreferredProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        var destination = anotherSubscription
            ?? local
            ?? sameFamily
            ?? await TryPickAnyCloudAsync(cancellationToken).ConfigureAwait(false);
        if (destination is null) return null;

        _logger.LogInformation(
            "Subscription quota demotion for {FunctionType}: {OldProvider} at {Burn:P0} → {NewProvider}/{NewModel}",
            context.FunctionType, current.PreferredProvider, snapshot.BurnFraction,
            destination.Value.Provider, destination.Value.Model);

        return new LlmRouteAdvisorRecommendation
        {
            Provider = destination.Value.Provider,
            Model = destination.Value.Model,
            FallbackProvider = current.PreferredProvider,
            FallbackModel = current.PreferredModel,
            PersistPreference = false,
            UpdatedBy = nameof(SubscriptionQuotaRoutingAdvisor),
            Reason = local is not null
                ? $"Your {current.PreferredProvider} subscription is at {snapshot.BurnFraction:P0} of its " +
                  $"{snapshot.Window.TotalHours:0}h window — stepping down to local {destination.Value.Provider} to preserve headroom at no cost."
                : $"Your {current.PreferredProvider} subscription is at {snapshot.BurnFraction:P0} of its " +
                  $"{snapshot.Window.TotalHours:0}h window — switching to {destination.Value.Provider} to preserve headroom.",
        };
    }

    private async Task<(string Provider, string Model)?> TryPickAnotherSubscriptionAsync(
        string currentProvider,
        decimal threshold,
        CancellationToken ct)
    {
        var settings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
        var probeCandidates = new List<(LlmProviderSettings Setting, ILlmProvider Provider, decimal Burn)>();
        foreach (var setting in settings
                     .Where(s => s.IsEnabled
                                 && SubscriptionCliProviders.IsSubscriptionCliProvider(s.ProviderName)
                                 && !s.ProviderName.Equals(currentProvider, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(s => s.Priority))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsProviderEligible(setting.ProviderName)) continue;

            var snapshot = _quotaTracker.GetSnapshot(setting.ProviderName);
            var burn = snapshot?.BurnFraction ?? 0m;
            if (snapshot is not null && snapshot.ExceedsThreshold(threshold)) continue;

            var provider = _providerRegistry.GetProvider(setting.ProviderName);
            if (provider is null) continue;

            probeCandidates.Add((setting, provider, burn));
        }

        var candidates = (await Task.WhenAll(probeCandidates.Select(candidate =>
                ProbeSubscriptionCandidateAsync(
                    candidate.Setting,
                    candidate.Provider,
                    candidate.Burn,
                    ct)))
            .ConfigureAwait(false))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .ToList();

        return candidates
            .OrderBy(c => c.Burn)
            .ThenBy(c => c.Priority)
            .ThenBy(c => c.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(c => ((string Provider, string Model)?)(c.Provider, c.Model))
            .FirstOrDefault();
    }

    private async Task<(string Provider, string Model, decimal Burn, int Priority)?> ProbeSubscriptionCandidateAsync(
        LlmProviderSettings setting,
        ILlmProvider provider,
        decimal burn,
        CancellationToken ct)
    {
        try
        {
            var configurationIdentity = LlmProviderDiscoveryCoordinator.CreateConfigurationIdentity(setting);
            var connectivity = await _providerDiscovery.ProbeConnectivityAsync(
                    provider,
                    ProviderDiscoveryTimeout,
                    ct,
                    highPriority: true,
                    configurationIdentity: configurationIdentity)
                .ConfigureAwait(false);
            if (!connectivity.Completed || !connectivity.IsHealthy)
            {
                _logger.LogDebug(
                    "Skipping subscription quota rotation candidate {Provider}: health check failed: {Message}",
                    setting.ProviderName,
                    connectivity.Message);
                return null;
            }

            var catalog = await _providerDiscovery.GetModelCatalogAsync(
                    provider,
                    ProviderDiscoveryTimeout,
                    ct,
                    highPriority: true,
                    configurationIdentity: configurationIdentity)
                .ConfigureAwait(false);
            if (!catalog.Completed || catalog.Models.Count == 0)
                return null;

            var frontier = ModelDefaults.Frontier(setting.ProviderName);
            var model = !string.IsNullOrWhiteSpace(frontier)
                        && catalog.Models.Contains(frontier, StringComparer.OrdinalIgnoreCase)
                ? frontier!
                : !string.IsNullOrWhiteSpace(setting.DefaultModel)
                  && catalog.Models.Contains(setting.DefaultModel, StringComparer.OrdinalIgnoreCase)
                    ? setting.DefaultModel!
                    : catalog.Models[0];
            return (setting.ProviderName, model, burn, setting.Priority);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Skipping subscription quota rotation candidate {Provider}: model listing failed.", setting.ProviderName);
            return null;
        }
    }

    private async Task<(string Provider, string Model)?> TryPickLocalAsync(CancellationToken ct)
    {
        // Trusted local is free — prefer it over any metered API when the user has one
        // running. Locality is decided by LlmProviderLocalityPolicy (never a hardcoded
        // provider name) so custom local endpoints qualify too.
        var settings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
        foreach (var setting in settings
                     .Where(s => s.IsEnabled
                                 && LlmProviderLocalityPolicy.IsTrustedLocalProvider(s))
                     .OrderBy(s => s.Priority))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsProviderEligible(setting.ProviderName)) continue;
            var model = !string.IsNullOrWhiteSpace(setting.DefaultModel)
                ? setting.DefaultModel!
                : _settingsProvider.GetDefaultModel(setting.ProviderName);
            if (string.IsNullOrWhiteSpace(model)) continue;
            return (setting.ProviderName, model);
        }
        return null;
    }

    private async Task<(string Provider, string Model)?> TryPickSameFamilyCloudAsync(string subscriptionName, CancellationToken ct)
    {
        // Subscription → paid-API family map. ClaudeCode → Claude/Anthropic, CodexCli → OpenAI, GeminiCli → Gemini.
        string? apiFamily = subscriptionName switch
        {
            var n when n.Equals("ClaudeCode", StringComparison.OrdinalIgnoreCase) => "Claude",
            var n when n.Equals("CodexCli", StringComparison.OrdinalIgnoreCase) => "OpenAI",
            var n when n.Equals("GeminiCli", StringComparison.OrdinalIgnoreCase) => "Gemini",
            _ => null
        };
        if (apiFamily is null) return null;

        var setting = (await _settingsProvider.GetProviderSettingsAsync(apiFamily).ConfigureAwait(false));
        if (setting is null || !setting.IsEnabled) return null;
        if (!IsProviderEligible(apiFamily)) return null;

        var model = !string.IsNullOrWhiteSpace(setting.DefaultModel)
            ? setting.DefaultModel!
            : ModelDefaults.Balanced(apiFamily) ?? _settingsProvider.GetDefaultModel(apiFamily);
        if (string.IsNullOrWhiteSpace(model)) return null;

        return (apiFamily, model);
    }

    private async Task<(string Provider, string Model)?> TryPickAnyCloudAsync(CancellationToken ct)
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
            var model = !string.IsNullOrWhiteSpace(setting.DefaultModel)
                ? setting.DefaultModel!
                : ModelDefaults.Balanced(setting.ProviderName) ?? _settingsProvider.GetDefaultModel(setting.ProviderName);
            if (string.IsNullOrWhiteSpace(model)) continue;
            return (setting.ProviderName, model);
        }
        return null;
    }

    private bool IsProviderEligible(string providerName)
    {
        if (_cooldownTracker is not null && !_cooldownTracker.IsAvailable(providerName)) return false;
        return _providerRegistry.GetProvider(providerName) is not null;
    }
}
