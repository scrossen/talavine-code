namespace SwarmCore.Abstractions.Acceleration;

public sealed record OnDeviceAccelerationStatus(
    bool IsSupported,
    bool IsInstalled,
    string RuntimeName,
    string? RuntimeVersion,
    string? AcceleratorName,
    string? Problem = null,
    string? QairtRuntimeVersion = null,
    string? LlamaCppRuntimeHash = null);

public sealed record OnDeviceModel(
    string Id,
    string DisplayName,
    string Precision,
    long SizeBytes,
    string Runtime,
    string Accelerator,
    string? Digest = null,
    int? MaxContextTokens = null);

public sealed record OnDeviceGenerationRequest(
    string ModelId,
    string Prompt,
    int MaxTokens = 256,
    int ContextTokens = 4096,
    bool JsonOutput = false,
    string? JsonSchema = null,
    double? Temperature = null);

public sealed record OnDeviceGenerationResult(
    bool Success,
    string Text,
    string ModelId,
    string Runtime,
    string Accelerator,
    bool CpuFallbackAllowed,
    TimeSpan Elapsed,
    string? Error = null,
    string? FinishReason = null,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>
/// Platform-neutral contract for optional device accelerators. Product code in
/// Talavine and Vibes consumes this interface; chipset-specific assemblies
/// are included only in matching native builds.
/// </summary>
public interface IOnDeviceAccelerationRuntime
{
    Task<OnDeviceAccelerationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OnDeviceModel>> GetInstalledModelsAsync(
        CancellationToken cancellationToken = default);

    Task<OnDeviceGenerationResult> GenerateAsync(
        OnDeviceGenerationRequest request,
        CancellationToken cancellationToken = default);
}
