using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// "Shift batch work onto local during quiet hours." When the wall clock is inside the
/// user's configured quiet-hours window, route <see cref="LlmInteractivity.Background"/>
/// work to a local provider if one is available. User-waiting calls are untouched —
/// quiet hours don't mean "make your responses slow."
///
/// Defers (returns null) when:
///   - the policy has no quiet hours configured, or now isn't inside them
///   - <see cref="RoutingPolicy.QuietHoursForceLocal"/> is false
///   - the call is user-waiting (interactivity matters more than time-of-day)
///   - urgency is <see cref="LlmUrgency.Urgent"/> — urgency wins
///   - privacy is <see cref="LlmPrivacyClass.LocalOnly"/> — already going local
///   - no local provider is available
/// </summary>
public sealed class TimeOfDayRoutingAdvisor : ILlmRoutePolicyAdvisor
{
    public int Priority => LlmRoutePolicyPriorities.TimeOfDay;
    public LlmRoutePolicyKind PolicyKind => LlmRoutePolicyKind.TimeOfDay;
    public string PolicyName => nameof(TimeOfDayRoutingAdvisor);

    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILlmProviderRegistry _providerRegistry;
    private readonly ILlmProviderCooldownTracker? _cooldownTracker;
    private readonly ILogger<TimeOfDayRoutingAdvisor> _logger;

    public TimeOfDayRoutingAdvisor(
        ILlmSettingsProvider settingsProvider,
        ILlmProviderRegistry providerRegistry,
        ILogger<TimeOfDayRoutingAdvisor> logger,
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
        var policy = context.Policy;
        if (!policy.QuietHoursForceLocal) return null;
        if (!policy.IsInQuietHours(context.SystemActivity.Now)) return null;
        if (context.CallContext.Interactivity == LlmInteractivity.UserWaiting) return null;
        if (context.CallContext.Urgency == LlmUrgency.Urgent) return null;
        if (context.CallContext.PrivacyClass == LlmPrivacyClass.LocalOnly) return null;
        // Embeddings are pinned globally; never swap them based on time-of-day.
        if (context.FunctionType.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase)
            || context.FunctionType.Equals(FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase)) return null;
        if (context.RequiresVision) return null; // vision local availability is unreliable; let normal path handle it.

        // Already on a local provider? No-op.
        var current = context.CurrentPreference;
        if (current is { IsEnabled: true }
            && !string.IsNullOrWhiteSpace(current.PreferredProvider)
            && IsLocalProviderName(current.PreferredProvider))
        {
            return null;
        }

        var local = await TryPickLocalAsync(cancellationToken).ConfigureAwait(false);
        if (local is null) return null;

        _logger.LogInformation(
            "Quiet-hours shift for {FunctionType}: routing background work to {Provider}/{Model}",
            context.FunctionType, local.Value.Provider, local.Value.Model);

        return new LlmRouteAdvisorRecommendation
        {
            Provider = local.Value.Provider,
            Model = local.Value.Model,
            FallbackProvider = current?.PreferredProvider,
            FallbackModel = current?.PreferredModel,
            PersistPreference = false,
            UpdatedBy = nameof(TimeOfDayRoutingAdvisor),
            Reason = "Quiet hours — shifting background work to your local model.",
        };
    }

    private static bool IsLocalProviderName(string providerName) =>
        LlmProviderLocalityPolicy.IsLocalProviderName(providerName);

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
