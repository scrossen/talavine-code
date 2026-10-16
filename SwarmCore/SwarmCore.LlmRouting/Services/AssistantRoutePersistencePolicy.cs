using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// One persistence rule for every automatic writer of a conversational
/// assistant route (router first-use materialization, Model Scout
/// reconciliation, local-model promotion). While a subscription route is
/// healthy, a local route may serve an assistant turn only as a transient
/// fallback; saving it as the function preference would make every later
/// surface that reads the preference start from the local model. Explicit user
/// pins never pass through here and stay sovereign.
/// </summary>
public static class AssistantRoutePersistencePolicy
{
    public static bool IsConversationalAssistant(string? functionType)
        => !string.IsNullOrWhiteSpace(functionType)
           && FunctionTypeProfiles.Resolve(ModelScoutFunctionEvidence.NormalizeFunctionType(functionType))
               .IsConversationalAssistant;

    /// <summary>
    /// False when an automatic writer would persist a local route for a
    /// conversational assistant function while a healthy subscription route
    /// could serve it. A LocalOnly preference is always persistable.
    /// </summary>
    public static bool MayPersistAutomaticRoute(
        string? functionType,
        bool routeIsLocal,
        bool healthySubscriptionAvailable,
        LlmPrivacyClass privacy = LlmPrivacyClass.CloudOk)
        => !(routeIsLocal
             && healthySubscriptionAvailable
             && privacy != LlmPrivacyClass.LocalOnly
             && IsConversationalAssistant(functionType));

    /// <summary>
    /// A subscription CLI that is enabled (not paused by a failed health
    /// check), registered, and allowed to route automatically by its reviewed
    /// pack. A usage-limit cooldown is deliberately not consulted: it is
    /// transient, so a local route that covers it is a fallback, never the
    /// function's saved route. Model Scout still admits the exact model.
    /// </summary>
    public static async Task<bool> HasSubscriptionRouteAsync(
        ILlmSettingsProvider settings,
        ILlmProviderRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var providers = await settings.GetAllProviderSettingsAsync().ConfigureAwait(false) ?? [];
        return providers.Any(provider =>
            provider.IsEnabled
            && !provider.DisabledByHealthCheck
            && SubscriptionCliProviders.IsSubscriptionCliProvider(provider.ProviderName)
            && ProviderPackCatalog.IsAutomaticRoutingAllowed(provider.ProviderName)
            && (registry is null || registry.GetProvider(provider.ProviderName) is not null));
    }
}
