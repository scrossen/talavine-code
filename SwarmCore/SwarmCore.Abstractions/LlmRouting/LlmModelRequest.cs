namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Caller intent, not a provider override or permission to execute.</summary>
public sealed record LlmModelRequest
{
    /// <summary>Exact model IDs in preference order, independent of the connection (for example gpt-6-astra).</summary>
    public IReadOnlyList<string> PreferredModels { get; init; } = [];
    public bool RequirePreferredModel { get; init; }
    public IReadOnlyList<LlmRequiredCapability> RequiredCapabilities { get; init; } = [];

    public LlmModelRequest Snapshot()
    {
        if (PreferredModels is null || RequiredCapabilities is null
            || PreferredModels.Count > 32 || RequiredCapabilities.Count > 16
            || PreferredModels.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 200 || m.Any(char.IsControl))
            || RequiredCapabilities.Any(c => !Enum.IsDefined(c)))
            throw new ArgumentException("Invalid model preference or capability request.");
        if (RequirePreferredModel && PreferredModels.Count == 0)
            throw new ArgumentException("Requiring a preferred model needs at least one model ID.");
        return this with
        {
            PreferredModels = PreferredModels.Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            RequiredCapabilities = RequiredCapabilities.Distinct().ToArray()
        };
    }
}

public enum LlmRequiredCapability
{
    ToolCalling, Vision, StructuredOutput, ReasoningEffort,
    NativeAsyncTools, NativeSteering, NativeCompaction, NativeComputerUse
}

/// <summary>User policy for connections serving the same explicitly requested model. Never grants API spend.</summary>
public enum ModelScoutConnectionPreference { SubscriptionFirst, ApiFirst, Balanced }

public sealed record ModelRouteRejection(string? Provider, string Model, string Reason);

/// <summary>A required model/capability could not be admitted. No incompatible substitution is allowed.</summary>
public sealed class LlmModelRequestUnavailableException : InvalidOperationException
{
    public string FunctionType { get; }
    public LlmModelRequest Request { get; }
    public IReadOnlyList<ModelRouteRejection> Rejections { get; }

    public LlmModelRequestUnavailableException(string functionType, LlmModelRequest request,
        IEnumerable<ModelRouteRejection> rejections)
        : this(functionType, request, rejections.ToArray()) { }

    private LlmModelRequestUnavailableException(string functionType, LlmModelRequest request,
        ModelRouteRejection[] rejections)
        : base(BuildMessage(functionType, rejections))
    {
        FunctionType = functionType;
        Request = request.Snapshot();
        Rejections = Array.AsReadOnly(rejections);
    }

    private static string BuildMessage(string functionType, IReadOnlyList<ModelRouteRejection> reasons)
        => $"Model Scout could not provide the requested model/capabilities for {functionType}. "
            + string.Join("; ", reasons.Take(6).Select(r => $"{r.Provider ?? "Model"}/{r.Model}: {r.Reason}"));
}
