using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Providers;

internal static class ProviderExecutionRequestBuilder
{
    private static readonly string[] SemanticAliases =
    [
        "max_output_tokens", "max_tokens", "max_completion_tokens", "num_predict", "temperature", "top_p", "seed",
        "structured_output", "response_format", "format", "stream", "ollama_stream", "reasoning", "think", "ollama_think",
        "enable_think", "reasoning_effort", "keep_alive",
    ];

    public static Dictionary<string, object> Build(
        ILlmProvider? provider,
        string model,
        LlmRouterOptions? options,
        LlmModelSelection? selection,
        IReadOnlyDictionary<string, object>? additionalExperimental = null)
    {
        var merged = Merge(selection?.ExecutionSettings, options?.ExecutionSettings, additionalExperimental);
        var legacy = InferLegacyParameters(merged);
        var explicitParameters = options?.GenerationParameters;
        var parameters = new LlmGenerationParameters
        {
            MaxOutputTokens = options?.SuppressOutputTokenLimit == true
                ? null
                : explicitParameters?.MaxOutputTokens ?? options?.MaxTokens ?? legacy.MaxOutputTokens,
            Temperature = explicitParameters?.Temperature ?? options?.Temperature ?? legacy.Temperature,
            TopP = explicitParameters?.TopP ?? legacy.TopP,
            Seed = explicitParameters?.Seed ?? legacy.Seed,
            Reasoning = explicitParameters is { Reasoning: not LlmReasoningMode.ProviderDefault }
                ? explicitParameters.Reasoning
                : legacy.Reasoning,
            StructuredOutput = explicitParameters is { StructuredOutput: not LlmStructuredOutputMode.None }
                ? explicitParameters.StructuredOutput
                : options?.ForceJson == true && !IsJsonArrayExpected(options.ExpectedFormat)
                    ? LlmStructuredOutputMode.JsonObject
                    : legacy.StructuredOutput,
            JsonSchema = explicitParameters?.JsonSchema,
            Stream = explicitParameters?.Stream ?? legacy.Stream,
            KeepAlive = explicitParameters?.KeepAlive ?? legacy.KeepAlive,
        };

        if (options?.ForceJson == true && LlmProviderLocalityPolicy.IsLocalProviderName(selection?.Provider))
        {
            parameters = parameters with
            {
                Reasoning = explicitParameters is { Reasoning: not LlmReasoningMode.ProviderDefault }
                    ? explicitParameters.Reasoning
                    : provider is ILlmProviderExecutionContract { ExecutionContract.Capabilities.Reasoning: true }
                        ? LlmReasoningMode.Disabled
                        : LlmReasoningMode.ProviderDefault,
                Stream = explicitParameters?.Stream ?? false,
            };
        }
        else if (parameters.Reasoning == LlmReasoningMode.ProviderDefault
                 && selection is not null
                 && OpenAICompatibleExecutionContract.IsReasoningModel(selection.Model))
        {
            parameters = parameters with
            {
                Reasoning = (selection.Context?.Interactivity ?? options?.Interactivity) == LlmInteractivity.UserWaiting
                    ? LlmReasoningMode.Low
                    : OpenAICompatibleExecutionContract.ResolveBackgroundReasoning(selection.Provider, selection.Model),
            };
        }

        var expectedDigest = ReadExpectedDigest(selection?.ExecutionSettings);
        var suppliedControl = options?.RequestControl;
        // Initialize before cloning so route repairs/retries retain the original finite budget.
        _ = suppliedControl?.PhysicalRequestBudget;
        var control = (suppliedControl ?? new LlmRequestControl()) with
        {
            CancellationToken = options?.RequestCancellationToken.CanBeCanceled == true
                ? options.RequestCancellationToken
                : suppliedControl?.CancellationToken ?? CancellationToken.None,
            RequireExactModel = options?.ModelRequest is not null || options?.DisableFallback == true || suppliedControl?.RequireExactModel == true,
            ExpectedArtifactDigest = expectedDigest ?? suppliedControl?.ExpectedArtifactDigest,
            PrivacyClass = selection?.Context?.PrivacyClass ?? options?.PrivacyClass ?? suppliedControl?.PrivacyClass ?? LlmPrivacyClass.Unspecified,
            Interactivity = selection?.Context?.Interactivity ?? options?.Interactivity ?? suppliedControl?.Interactivity ?? LlmInteractivity.Unspecified,
            Urgency = options?.Urgency ?? suppliedControl?.Urgency ?? LlmUrgency.Unspecified,
        };

        RemoveSemanticAliases(merged);
        if (provider is ILlmProviderExecutionContract contract)
        {
            var translation = contract.TranslateExecution(model, parameters, control, merged);
            var translated = new Dictionary<string, object>(
                translation.WireSettings,
                StringComparer.OrdinalIgnoreCase);
            if (translation.Diagnostics.Count > 0)
            {
                translated[LlmReservedExecutionSettings.ExecutionParameterDiagnostics] =
                    translation.Diagnostics;
            }
            return translated;
        }

        // Compatibility path for providers not yet upgraded. It is deliberately
        // centralized outside the router and emits only portable legacy fields.
        ApplyLegacyPortableSettings(merged, parameters, control);
        if (provider is CodexCliLlmProvider && parameters.Reasoning == LlmReasoningMode.Disabled)
        {
            // Background JSON and validation callers share a portable suppression
            // hint. Codex has no reviewed off switch: preserve its default (or
            // the selected @effort tier), as the legacy path did, and disclose
            // the omission instead of rejecting a healthy Scout-admitted route.
            var diagnostics = LlmReservedExecutionSettings.GetExecutionParameterDiagnostics(merged).ToList();
            diagnostics.Add(new("reasoning", LlmExecutionParameterDisposition.Omitted,
                "Codex CLI does not support disabling reasoning; the selected model's default or pinned effort is retained."));
            merged[LlmReservedExecutionSettings.ExecutionParameterDiagnostics] = diagnostics;
        }
        else if (provider is CodexCliLlmProvider && parameters.Reasoning != LlmReasoningMode.ProviderDefault)
        {
            var effort = parameters.Reasoning.ToString().ToLowerInvariant();
            if (!ModelRouteCapabilities.ReasoningEfforts(provider.ProviderName, model).Contains(effort, StringComparer.Ordinal))
                throw new NotSupportedException($"The selected Codex CLI route does not support reasoning effort {effort}.");
            merged["reasoning_effort"] = effort;
        }
        return merged;
    }

    private static Dictionary<string, object> Merge(params IReadOnlyDictionary<string, object>?[] sources)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            if (source is not null)
                foreach (var pair in source)
                    result[pair.Key] = pair.Value;
        return result;
    }

    private static LlmGenerationParameters InferLegacyParameters(IReadOnlyDictionary<string, object> settings)
    {
        var max = ReadInt(settings, "max_output_tokens") ?? ReadInt(settings, "max_tokens") ?? ReadInt(settings, "max_completion_tokens") ?? ReadInt(settings, "num_predict");
        var temperature = ReadDouble(settings, "temperature");
        var topP = ReadDouble(settings, "top_p");
        var seed = ReadLong(settings, "seed");
        var reasoning = ReadReasoning(settings);
        var structured = settings.ContainsKey("structured_output") || settings.ContainsKey("response_format") || settings.ContainsKey("format")
            ? LlmStructuredOutputMode.JsonObject
            : LlmStructuredOutputMode.None;
        var stream = ReadBool(settings, "ollama_stream") ?? ReadBool(settings, "stream");
        var keepAlive = ReadDuration(settings, "keep_alive");
        return new()
        {
            MaxOutputTokens = max,
            Temperature = temperature,
            TopP = topP,
            Seed = seed,
            Reasoning = reasoning,
            StructuredOutput = structured,
            Stream = stream,
            KeepAlive = keepAlive,
        };
    }

    private static void ApplyLegacyPortableSettings(
        Dictionary<string, object> settings,
        LlmGenerationParameters parameters,
        LlmRequestControl control)
    {
        if (parameters.MaxOutputTokens is { } max) settings["max_tokens"] = max;
        if (parameters.Temperature is { } temperature) settings["temperature"] = temperature;
        if (parameters.TopP is { } topP) settings["top_p"] = topP;
        if (parameters.Seed is { } seed) settings["seed"] = seed;
        if (parameters.StructuredOutput != LlmStructuredOutputMode.None)
            settings["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" };
        if (parameters.Stream is { } stream) settings["stream"] = stream;
        LlmProviderWireControls.Apply(settings, control);
    }

    private static void RemoveSemanticAliases(Dictionary<string, object> settings)
    {
        foreach (var alias in SemanticAliases) settings.Remove(alias);
    }

    private static string? ReadExpectedDigest(IReadOnlyDictionary<string, object>? settings)
        => LlmReservedExecutionSettings.GetExpectedModelArtifactDigest(settings);

    private static int? ReadInt(IReadOnlyDictionary<string, object> values, string key)
        => values.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out var value) ? value : null;
    private static long? ReadLong(IReadOnlyDictionary<string, object> values, string key)
        => values.TryGetValue(key, out var raw) && long.TryParse(raw?.ToString(), out var value) ? value : null;
    private static double? ReadDouble(IReadOnlyDictionary<string, object> values, string key)
        => values.TryGetValue(key, out var raw) && double.TryParse(raw?.ToString(), out var value) ? value : null;
    private static bool? ReadBool(IReadOnlyDictionary<string, object> values, string key)
        => values.TryGetValue(key, out var raw) && bool.TryParse(raw?.ToString(), out var value) ? value : null;
    private static TimeSpan? ReadDuration(IReadOnlyDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw?.ToString())) return null;
        var text = raw!.ToString()!.Trim();
        if (text.EndsWith('m') && double.TryParse(text[..^1], out var minutes)) return TimeSpan.FromMinutes(minutes);
        if (text.EndsWith('s') && double.TryParse(text[..^1], out var seconds)) return TimeSpan.FromSeconds(seconds);
        return TimeSpan.TryParse(text, out var parsed) ? parsed : null;
    }

    private static LlmReasoningMode ReadReasoning(IReadOnlyDictionary<string, object> values)
    {
        if (values.TryGetValue("reasoning", out var semanticReasoning))
        {
            return semanticReasoning?.ToString()?.Trim().ToLowerInvariant() switch
            {
                "disabled" or "none" or "false" => LlmReasoningMode.Disabled,
                "low" => LlmReasoningMode.Low,
                "medium" => LlmReasoningMode.Medium,
                "high" => LlmReasoningMode.High,
                "xhigh" => LlmReasoningMode.XHigh,
                "max" => LlmReasoningMode.Max,
                _ => LlmReasoningMode.ProviderDefault,
            };
        }
        var disabled = ReadBool(values, "enable_think") == false
                       || ReadBool(values, "ollama_think") == false
                       || ReadBool(values, "think") == false;
        if (disabled) return LlmReasoningMode.Disabled;
        if (!values.TryGetValue("reasoning_effort", out var effort)) return LlmReasoningMode.ProviderDefault;
        return effort?.ToString()?.Trim().ToLowerInvariant() switch
        {
            "none" or "minimal" => LlmReasoningMode.Disabled,
            "low" => LlmReasoningMode.Low,
            "medium" => LlmReasoningMode.Medium,
            "high" => LlmReasoningMode.High,
            "xhigh" => LlmReasoningMode.XHigh,
            "max" => LlmReasoningMode.Max,
            _ => LlmReasoningMode.ProviderDefault,
        };
    }

    private static bool IsJsonArrayExpected(string? expectedFormat)
        => expectedFormat?.Contains("array", StringComparison.OrdinalIgnoreCase) == true;
}

internal static class ProviderExecutionSettingsMutator
{
    public static void DisableReasoning(Dictionary<string, object> settings)
    {
        foreach (var key in new[] { "think", "ollama_think", "enable_think" }) settings[key] = false;
        settings["reasoning_effort"] = "none";
        settings["thinking_budget"] = 0;
    }

    public static void EnsureOutputBudget(Dictionary<string, object> settings, int minimum)
    {
        var keys = OutputBudgetKeys(settings);
        if (keys.Count == 0) keys.Add("max_tokens");
        foreach (var key in keys)
            if (!TryInt(settings, key, out var value) || value < minimum) settings[key] = minimum;
    }

    public static int? ReadOutputBudget(IReadOnlyDictionary<string, object> settings)
    {
        foreach (var key in new[] { "max_output_tokens", "max_completion_tokens", "num_predict", "max_tokens" })
            if (TryInt(settings, key, out var value)) return value;
        return null;
    }

    public static void SetOutputBudget(Dictionary<string, object> settings, int value)
    {
        var keys = OutputBudgetKeys(settings);
        if (keys.Count == 0) keys.Add("max_tokens");
        foreach (var key in keys) settings[key] = value;
    }

    public static bool IsOutputLimitDiagnostic(string diagnostic)
        => diagnostic.Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase)
           || diagnostic.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
           || diagnostic.Contains("finish_reason=length", StringComparison.OrdinalIgnoreCase);

    private static List<string> OutputBudgetKeys(IReadOnlyDictionary<string, object> settings)
        => new[] { "max_output_tokens", "max_completion_tokens", "num_predict", "max_tokens" }
            .Where(settings.ContainsKey).ToList();

    private static bool TryInt(IReadOnlyDictionary<string, object> settings, string key, out int value)
    {
        value = 0;
        return settings.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out value);
    }
}
