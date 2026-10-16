using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// The reasoning level of an Ollama-family tool turn. Ollama enables thinking
/// by default for reasoning models, and in a tool loop that can spend the whole
/// output budget in the private reasoning channel and return no final content
/// after a successful tool call. Tool turns therefore default to no reasoning;
/// a caller opts back in through typed generation parameters or, failing
/// those, the legacy think/reasoning_effort execution settings.
/// </summary>
internal static class OllamaToolUseReasoning
{
    public const string None = "none";

    /// <summary>One of "none", "low", "medium" or "high".</summary>
    public static string Resolve(LlmRouterOptions? options, LlmReasoningMode typedReasoning)
    {
        if (typedReasoning != LlmReasoningMode.ProviderDefault)
        {
            return typedReasoning switch
            {
                LlmReasoningMode.Low => "low",
                LlmReasoningMode.Medium => "medium",
                LlmReasoningMode.High => "high",
                _ => None,
            };
        }

        var settings = options?.ExecutionSettings;
        if (settings is not null)
        {
            if (TryReadBoolean(settings, "think", out var think)
                || TryReadBoolean(settings, "ollama_think", out think)
                || TryReadBoolean(settings, "enable_think", out think))
            {
                return think ? "medium" : None;
            }

            if (settings.TryGetValue("reasoning_effort", out var rawEffort))
            {
                var effort = rawEffort?.ToString()?.Trim().ToLowerInvariant();
                if (effort is "none" or "low" or "medium" or "high")
                {
                    return effort;
                }
                if (effort == "minimal")
                {
                    return None;
                }
            }
        }

        return None;
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, object> settings,
        string key,
        out bool value)
    {
        value = false;
        return settings.TryGetValue(key, out var raw)
               && bool.TryParse(raw?.ToString(), out value);
    }
}
