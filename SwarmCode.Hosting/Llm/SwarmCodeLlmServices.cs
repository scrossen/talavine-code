using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Llm;

/// <summary>
/// Capability service. Vision support now matters — the build/edit/troubleshoot chat
/// surfaces gate image-attach on it — so <see cref="SupportsVision"/> asks the actual
/// registered provider (the definitive, per-model answer) when one is available, falling
/// back to the static <see cref="VisionModelFamilies"/> table otherwise. The family table
/// alone answers <see cref="GetVisionCapableModels"/>/<see cref="GetVisionCapableProviders"/>
/// so those stay synchronous and I/O-free (no blocking on a provider's live model list).
/// </summary>
public sealed class SwarmCodeLlmCapabilitiesService : ILlmCapabilitiesService
{
    private readonly ILlmProviderRegistry _registry;

    public SwarmCodeLlmCapabilitiesService(ILlmProviderRegistry registry)
    {
        _registry = registry;
    }

    public bool SupportsVision(string provider, string model) =>
        _registry.GetProvider(provider)?.SupportsVision(model) ?? VisionModelFamilies.SupportsVision(provider, model);

    public IEnumerable<string> GetVisionCapableModels(string provider) => VisionModelFamilies.GetVisionCapableModels(provider);

    public IEnumerable<string> GetVisionCapableProviders() => VisionModelFamilies.GetVisionCapableProviders();

    public Task RefreshCapabilitiesAsync() => Task.CompletedTask;

    public LlmModelCapability? GetModelCapability(string provider, string model) => new()
    {
        Provider = provider,
        Model = model,
        SupportsVision = SupportsVision(provider, model),
        SupportsFunctionCalling = true,
        SupportsStreaming = true,
        SupportsEmbeddings = false,
    };
}

/// <summary>
/// Minimal cost service. SwarmCode does not ship a price table; it reports zero cost
/// (the Settings page focuses on request/token counts). Hosts that need precise
/// billing can replace this seam.
/// </summary>
public sealed class SwarmCodeLlmCostService : ILlmCostService
{
    private static readonly Dictionary<string, (decimal Input, decimal Output)> Empty = new();

    public Task<Dictionary<string, (decimal Input, decimal Output)>> GetCostsAsync() => Task.FromResult(Empty);

    public Task<Dictionary<string, (decimal Input, decimal Output)>> RefreshAllCostsAsync() => Task.FromResult(Empty);

    public (decimal Input, decimal Output) GetModelCost(string modelKey) => (0m, 0m);

    public decimal CalculateCost(string modelKey, int inputTokens, int outputTokens) => 0m;

    public decimal CalculateCost(string modelKey, int freshInputTokens, int cacheReadTokens, int cacheCreationTokens, int outputTokens) => 0m;

    public decimal CalculateEmbeddingCost(string modelKey, int tokens) => 0m;

    public Task ReloadCostsAsync() => Task.CompletedTask;
}
