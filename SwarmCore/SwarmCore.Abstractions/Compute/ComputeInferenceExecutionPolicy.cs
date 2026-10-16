using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Abstractions.Compute;

/// <summary>Coarse transport allowlist, not a substitute for receiver runtime/endpoint/proof admission.</summary>
public static class ComputeInferenceExecutionPolicy
{
    public static bool IsContainedSubscription(string provider)
        => string.Equals(provider, SubscriptionCliCatalog.ClaudeCodeProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, SubscriptionCliCatalog.CodexCliProvider, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedRoute(string provider, string model, ComputeInferenceRequest request)
        => LlmProviderLocalityPolicy.IsLocalProviderName(provider)
            && (!OllamaProviderFamily.IsOllamaFamily(provider) || !OllamaModelLocalityPolicy.IsCloudBacked(model))
            || request.AllowSubscription && !request.LocalOnly && IsContainedSubscription(provider);
}
