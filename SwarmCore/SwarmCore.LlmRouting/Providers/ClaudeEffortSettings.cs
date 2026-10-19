using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Providers;

internal static class ClaudeEffortSettings
{
    internal static string? Resolve(string model, LlmGenerationParameters? parameters, IReadOnlyDictionary<string, object>? settings)
    {
        var effort = parameters is { Reasoning: not LlmReasoningMode.ProviderDefault }
            ? parameters.Reasoning.ToString().ToLowerInvariant() : settings?.GetValueOrDefault("reasoning_effort")?.ToString();
        if (string.IsNullOrWhiteSpace(effort)) return null;
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new NotSupportedException("This Claude model does not support the selected effort level.");
        return effort;
    }
}
