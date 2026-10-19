using System.Globalization;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

public enum OpenAICompatibleContractFlavor
{
    OpenAI,
    OpenRouter,
    LMStudio,
    DeepSeek,
}

public sealed record OpenRouterCatalogSemanticCapabilities(
    int? MaxOutputTokens,
    bool SupportsStructuredOutput,
    bool SupportsTools);

public sealed record OpenRouterCatalogModel(
    string Id,
    string DisplayName,
    string? Description,
    decimal? PromptPricePerToken,
    decimal? CompletionPricePerToken,
    int? ContextTokens,
    long? CreatedUnixSeconds,
    IReadOnlySet<string> InputModalities,
    OpenRouterCatalogSemanticCapabilities Capabilities);

/// <summary>
/// Owns translation of OpenRouter's catalog schema into portable capabilities.
/// Model Scout consumes these semantics and never interprets vendor field names.
/// </summary>
public static class OpenRouterCatalogContract
{
    public const int MaximumCatalogBytes = 16 * 1024 * 1024;
    private const int MaximumCatalogTokenCount = 10_000_000;
    private const int MaximumDisplayNameCharacters = 200;
    private const int MaximumDescriptionCharacters = 500;
    private const int MaximumModalityCount = 16;
    private const int MaximumModalityCharacters = 64;
    private const int MaximumSupportedParameterCount = 64;
    private const int MaximumSupportedParameterCharacters = 100;
    private const long MaximumUnixSeconds = 253402300799;
    private const decimal MaximumPricePerToken = 1m;

    public static bool TryReadModel(JsonElement item, out OpenRouterCatalogModel model)
    {
        model = default!;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var idProperty)
            || idProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var id = idProperty.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(id)
            || id.Length > 300
            || id.Any(char.IsWhiteSpace)
            || id.Any(char.IsControl))
        {
            return false;
        }

        var displayName = ReadBoundedText(item, "name", MaximumDisplayNameCharacters) ?? id;
        var description = ReadBoundedText(item, "description", MaximumDescriptionCharacters);
        var contextTokens = item.TryGetProperty("context_length", out var contextProperty)
                            && contextProperty.ValueKind == JsonValueKind.Number
                            && contextProperty.TryGetInt32(out var context)
                            && context is > 0 and <= MaximumCatalogTokenCount
            ? context
            : (int?)null;
        var createdUnixSeconds = item.TryGetProperty("created", out var createdProperty)
                                 && createdProperty.ValueKind == JsonValueKind.Number
                                 && createdProperty.TryGetInt64(out var created)
                                 && created is > 0 and <= MaximumUnixSeconds
            ? created
            : (long?)null;

        var inputModalities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("architecture", out var architecture)
            && architecture.ValueKind == JsonValueKind.Object
            && architecture.TryGetProperty("input_modalities", out var modalities)
            && modalities.ValueKind == JsonValueKind.Array)
        {
            foreach (var modality in modalities.EnumerateArray())
            {
                if (inputModalities.Count >= MaximumModalityCount)
                {
                    break;
                }

                if (modality.ValueKind == JsonValueKind.String
                    && ReadBoundedText(modality.GetString(), MaximumModalityCharacters) is { } value
                    && !value.Any(char.IsWhiteSpace))
                {
                    inputModalities.Add(value);
                }
            }
        }

        model = new(
            id,
            displayName,
            description,
            ReadPrice(item, "prompt"),
            ReadPrice(item, "completion"),
            contextTokens,
            createdUnixSeconds,
            inputModalities,
            ReadCapabilities(item));
        return true;
    }

    public static OpenRouterCatalogSemanticCapabilities ReadCapabilities(JsonElement item)
    {
        int? maxOutputTokens = null;
        if (item.TryGetProperty("top_provider", out var topProvider)
            && topProvider.ValueKind == JsonValueKind.Object
            && topProvider.TryGetProperty("max_completion_tokens", out var maxOutput)
            && maxOutput.ValueKind == JsonValueKind.Number
            && maxOutput.TryGetInt32(out var parsedMaxOutput)
            && parsedMaxOutput is > 0 and <= MaximumCatalogTokenCount)
            maxOutputTokens = parsedMaxOutput;

        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("supported_parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                if (supported.Count >= MaximumSupportedParameterCount)
                {
                    break;
                }

                if (parameter.ValueKind == JsonValueKind.String
                    && ReadBoundedText(parameter.GetString(), MaximumSupportedParameterCharacters) is { } value
                    && !value.Any(char.IsWhiteSpace))
                {
                    supported.Add(value);
                }
            }
        }
        return new(
            maxOutputTokens,
            supported.Contains("response_format") || supported.Contains("structured_outputs"),
            supported.Contains("tools") || supported.Contains("tool_choice"));
    }

    public static async Task<JsonDocument> ReadBoundedDocumentAsync(
        HttpContent content,
        CancellationToken ct = default)
    {
        if (content.Headers.ContentLength is > MaximumCatalogBytes)
        {
            throw new InvalidDataException("OpenRouter catalog response exceeded the reviewed size limit.");
        }

        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var buffered = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffered.Length + read > MaximumCatalogBytes)
            {
                throw new InvalidDataException("OpenRouter catalog response exceeded the reviewed size limit.");
            }

            await buffered.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        buffered.Position = 0;
        return await JsonDocument.ParseAsync(buffered, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? ReadBoundedText(JsonElement item, string propertyName, int maximumCharacters)
        => item.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? ReadBoundedText(property.GetString(), maximumCharacters)
            : null;

    private static string? ReadBoundedText(string? value, int maximumCharacters)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
               && trimmed.Length <= maximumCharacters
               && !trimmed.Any(char.IsControl)
            ? trimmed
            : null;
    }

    private static decimal? ReadPrice(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty("pricing", out var pricing)
            || pricing.ValueKind != JsonValueKind.Object
            || !pricing.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        decimal? value = property.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var stringValue)
                ? stringValue
                : null,
            JsonValueKind.Number => property.TryGetDecimal(out var numericValue)
                ? numericValue
                : null,
            _ => null,
        };
        return value is >= 0m and <= MaximumPricePerToken ? value : null;
    }
}

/// <summary>
/// Shared OpenAI-compatible semantic mapping used by text and tool-use paths.
/// Provider adapters select a flavor; callers and the router never select wire keys.
/// </summary>
public sealed class OpenAICompatibleExecutionContract(OpenAICompatibleContractFlavor flavor)
{
    public static LlmReasoningMode ResolveBackgroundReasoning(string provider, string model)
    {
        if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)
            && ProviderPackCatalog.GetRequired(provider).OpenRouter!.MinimumReasoningEffort.TryGetValue(model, out var minimum)
            && Enum.TryParse<LlmReasoningMode>(minimum, ignoreCase: true, out var mode)
            && mode is not LlmReasoningMode.ProviderDefault and not LlmReasoningMode.Disabled)
            return mode;
        return LlmReasoningMode.Disabled;
    }

    public LlmProviderExecutionContractDescriptor Descriptor { get; } = new(
        flavor switch
        {
            OpenAICompatibleContractFlavor.OpenAI => LlmProviderExecutionContractIds.OpenAIChatCompletionsV2,
            OpenAICompatibleContractFlavor.OpenRouter => LlmProviderExecutionContractIds.OpenRouterChatCompletionsV2,
            OpenAICompatibleContractFlavor.DeepSeek => LlmProviderExecutionContractIds.DeepSeekChatCompletionsV1,
            _ => LlmProviderExecutionContractIds.LMStudioChatCompletionsV2,
        },
        new(
            MaxOutputTokens: true,
            Temperature: true,
            TopP: true,
            Seed: flavor != OpenAICompatibleContractFlavor.LMStudio,
            Reasoning: flavor != OpenAICompatibleContractFlavor.LMStudio,
            StructuredOutput: true,
            Streaming: flavor != OpenAICompatibleContractFlavor.LMStudio,
            KeepAlive: false));

    public LlmProviderExecutionTranslation Translate(
        string model,
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? experimental)
    {
        var wire = CopyExperimental(experimental);
        var diagnostics = new List<LlmExecutionParameterDiagnostic>();
        var effectiveModel = NormalizeOpenAIModelId(model);
        var reasoningModel = IsReasoningModel(effectiveModel);

        Apply(wire, diagnostics, parameters.MaxOutputTokens,
            reasoningModel ? "max_completion_tokens" : "max_tokens", "max_output_tokens");

        if (parameters.Temperature is { } temperature)
        {
            if (reasoningModel && flavor != OpenAICompatibleContractFlavor.LMStudio)
                Omit(diagnostics, "temperature", "The selected reasoning model owns its sampler.");
            else
                Apply(wire, diagnostics, temperature, "temperature", "temperature");
        }

        if (parameters.TopP is { } topP)
        {
            if (reasoningModel && flavor != OpenAICompatibleContractFlavor.LMStudio)
                Omit(diagnostics, "top_p", "The selected reasoning model owns its sampler.");
            else
                Apply(wire, diagnostics, topP, "top_p", "top_p");
        }

        if (parameters.Seed is { } seed)
        {
            if (flavor == OpenAICompatibleContractFlavor.LMStudio)
                Omit(diagnostics, "seed", "LM Studio's loaded-model contract does not advertise stable seed support.");
            else if (reasoningModel)
                Omit(diagnostics, "seed", "The selected reasoning model does not accept a deterministic seed.");
            else
                Apply(wire, diagnostics, seed, "seed", "seed");
        }

        if (parameters.Reasoning != LlmReasoningMode.ProviderDefault)
        {
            if (flavor == OpenAICompatibleContractFlavor.OpenRouter)
            {
                // OpenRouter owns a provider-neutral reasoning object. An
                // OpenAI model-name heuristic must not discard the requested
                // control for DeepSeek, Anthropic, or another gateway model.
                wire.Remove("reasoning_effort");
                wire["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = ReasoningValue(parameters.Reasoning)
                };
                Applied(diagnostics, "reasoning");
            }
            else if (flavor == OpenAICompatibleContractFlavor.DeepSeek)
            {
                wire["thinking"] = new Dictionary<string, object>
                {
                    ["type"] = parameters.Reasoning == LlmReasoningMode.Disabled ? "disabled" : "enabled"
                };
                wire.Remove("reasoning_effort");
                if (parameters.Reasoning != LlmReasoningMode.Disabled)
                    wire["reasoning_effort"] = ReasoningValue(parameters.Reasoning);
                Applied(diagnostics, "reasoning");
            }
            else if (flavor == OpenAICompatibleContractFlavor.LMStudio)
                Reject(diagnostics, "reasoning", "LM Studio reasoning control is not portable across loaded models.");
            else if (!reasoningModel)
                Omit(diagnostics, "reasoning", "The selected model has no reasoning-effort contract.");
            else
                Apply(wire, diagnostics, ReasoningValue(parameters.Reasoning), "reasoning_effort", "reasoning");
        }

        if (parameters.StructuredOutput != LlmStructuredOutputMode.None)
        {
            if (parameters.StructuredOutput == LlmStructuredOutputMode.JsonSchema
                && string.IsNullOrWhiteSpace(parameters.JsonSchema))
            {
                Reject(diagnostics, "structured_output", "JSON-schema mode requires a schema.");
            }
            else
            {
                wire["response_format"] = parameters.StructuredOutput == LlmStructuredOutputMode.JsonSchema
                    ? new Dictionary<string, object> { ["type"] = "json_schema", ["json_schema"] = parameters.JsonSchema! }
                    : new Dictionary<string, object> { ["type"] = "json_object" };
                Applied(diagnostics, "structured_output");
            }
        }

        if (parameters.Stream is { } stream)
        {
            if (flavor == OpenAICompatibleContractFlavor.LMStudio && stream)
                Reject(diagnostics, "stream", "This LM Studio adapter does not implement streaming.");
            else
                Apply(wire, diagnostics, stream, "stream", "stream");
        }

        if (parameters.KeepAlive is not null)
            Omit(diagnostics, "keep_alive", "OpenAI-compatible chat completions do not define portable keep-alive semantics.");

        LlmProviderWireControls.Apply(wire, control);
        return Finish(wire, diagnostics, Descriptor);
    }

    internal static string NormalizeOpenAIModelId(string model)
    {
        var normalized = model.Trim();
        if (normalized.StartsWith('~')) normalized = normalized[1..];
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    internal static bool IsReasoningModel(string model)
    {
        var normalized = NormalizeOpenAIModelId(model);
        return normalized.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("gpt-latest", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object> CopyExperimental(IReadOnlyDictionary<string, object>? source)
        => source is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(source, StringComparer.OrdinalIgnoreCase);

    private static string ReasoningValue(LlmReasoningMode mode) => mode switch
    {
        LlmReasoningMode.Disabled => "none",
        LlmReasoningMode.Low => "low",
        LlmReasoningMode.Medium => "medium",
        LlmReasoningMode.High => "high",
        LlmReasoningMode.XHigh => "xhigh",
        LlmReasoningMode.Max => "max",
        _ => "medium",
    };

    internal static LlmProviderExecutionTranslation Finish(
        Dictionary<string, object> wire,
        List<LlmExecutionParameterDiagnostic> diagnostics,
        LlmProviderExecutionContractDescriptor descriptor)
    {
        var result = new LlmProviderExecutionTranslation(wire, diagnostics);
        if (result.IsRejected)
            throw new LlmExecutionParameterRejectedException("provider", descriptor.ExecutionContractId, diagnostics);
        return result;
    }

    internal static void Apply<T>(Dictionary<string, object> wire, List<LlmExecutionParameterDiagnostic> diagnostics,
        T? value, string wireKey, string semanticName)
    {
        if (value is null) return;
        wire[wireKey] = value;
        Applied(diagnostics, semanticName);
    }

    internal static void Applied(List<LlmExecutionParameterDiagnostic> diagnostics, string parameter)
        => diagnostics.Add(new(parameter, LlmExecutionParameterDisposition.Applied, "Mapped by the provider execution contract."));
    internal static void Omit(List<LlmExecutionParameterDiagnostic> diagnostics, string parameter, string reason)
        => diagnostics.Add(new(parameter, LlmExecutionParameterDisposition.Omitted, reason));
    internal static void Reject(List<LlmExecutionParameterDiagnostic> diagnostics, string parameter, string reason)
        => diagnostics.Add(new(parameter, LlmExecutionParameterDisposition.Rejected, reason));
}

public sealed class OllamaExecutionContract
{
    public LlmProviderExecutionContractDescriptor Descriptor { get; } = new(
        LlmProviderExecutionContractIds.OllamaNativeChatV2,
        new(true, true, true, true, true, true, true, true));

    public LlmProviderExecutionTranslation Translate(
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? experimental)
    {
        var wire = experimental is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(experimental, StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<LlmExecutionParameterDiagnostic>();
        OpenAICompatibleExecutionContract.Apply(wire, diagnostics, parameters.MaxOutputTokens, "num_predict", "max_output_tokens");
        OpenAICompatibleExecutionContract.Apply(wire, diagnostics, parameters.Temperature, "temperature", "temperature");
        OpenAICompatibleExecutionContract.Apply(wire, diagnostics, parameters.TopP, "top_p", "top_p");
        OpenAICompatibleExecutionContract.Apply(wire, diagnostics, parameters.Seed, "seed", "seed");
        if (parameters.Reasoning != LlmReasoningMode.ProviderDefault)
        {
            wire["think"] = parameters.Reasoning != LlmReasoningMode.Disabled;
            OpenAICompatibleExecutionContract.Applied(diagnostics, "reasoning");
        }
        if (parameters.StructuredOutput != LlmStructuredOutputMode.None)
        {
            if (parameters.StructuredOutput == LlmStructuredOutputMode.JsonSchema)
                OpenAICompatibleExecutionContract.Omit(diagnostics, "structured_output", "Ollama schema enforcement is not yet exposed by this adapter; JSON object mode is used.");
            wire["format"] = "json";
            OpenAICompatibleExecutionContract.Applied(diagnostics, "structured_output");
        }
        OpenAICompatibleExecutionContract.Apply(wire, diagnostics, parameters.Stream, "ollama_stream", "stream");
        if (parameters.KeepAlive is { } keepAlive)
        {
            wire["keep_alive"] = FormatDuration(keepAlive);
            OpenAICompatibleExecutionContract.Applied(diagnostics, "keep_alive");
        }
        LlmProviderWireControls.Apply(wire, control, supportsPhysicalRequestBudget: true);
        return OpenAICompatibleExecutionContract.Finish(wire, diagnostics, Descriptor);
    }

    private static string FormatDuration(TimeSpan value)
        => value.TotalMinutes == Math.Truncate(value.TotalMinutes)
            ? $"{value.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)}m"
            : $"{value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s";
}

internal static class LlmProviderWireControls
{
    public static void Apply(Dictionary<string, object> wire, LlmRequestControl control, bool supportsPhysicalRequestBudget = false)
    {
        var existingBudget = LlmReservedExecutionSettings.GetPhysicalRequestBudget(wire);
        var requestedBudget = control.PhysicalRequestBudget;
        if (existingBudget is not null && requestedBudget is not null && !ReferenceEquals(existingBudget, requestedBudget))
            throw new InvalidOperationException("A physical inference request budget cannot be replaced during translation.");
        if ((requestedBudget ?? existingBudget) is { } budget)
        {
            if (!supportsPhysicalRequestBudget)
                throw new NotSupportedException("This provider adapter cannot guarantee a bounded physical inference request count.");
            wire[LlmReservedExecutionSettings.PhysicalRequestBudget] = budget;
        }
        wire[LlmReservedExecutionSettings.ExactModelOnly] = control.RequireExactModel;
        if (control.CancellationToken.CanBeCanceled)
            wire[LlmReservedExecutionSettings.RequestCancellationToken] = control.CancellationToken;
        if (control.PrivacyClass != LlmPrivacyClass.Unspecified)
            wire[LlmReservedExecutionSettings.RequestPrivacyClass] = control.PrivacyClass;
        if (!string.IsNullOrWhiteSpace(control.ExpectedArtifactDigest))
            wire[LlmReservedExecutionSettings.ExpectedModelArtifactDigest] = control.ExpectedArtifactDigest!;
    }
}
