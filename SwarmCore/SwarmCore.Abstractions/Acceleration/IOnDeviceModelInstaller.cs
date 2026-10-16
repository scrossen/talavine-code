namespace SwarmCore.Abstractions.Acceleration;

/// <summary>
/// The small, structured vocabulary an accelerator adapter may translate into
/// its own model-install command. Callers never supply command-line arguments.
/// </summary>
public enum OnDeviceModelHub
{
    AiHub = 0,
}

public enum OnDeviceModelType
{
    Llm = 0,
    VisionLanguageModel = 1,
}

public sealed record OnDeviceRuntimeSetupDescriptor(
    string ProviderName,
    string RuntimeName,
    string DisplayName,
    string Endpoint,
    Uri OfficialSetupUri,
    string MinimumRuntimeVersion,
    bool CanInstallModels,
    string SetupSummary);

/// <summary>
/// Exact artifact requested by trusted application metadata. Expected identity
/// is mandatory so a successful downloader exit can never activate an unknown
/// or same-tag replacement.
/// </summary>
public sealed record OnDeviceModelInstallDescriptor(
    string ProviderName,
    string ModelId,
    string Precision,
    OnDeviceModelHub Hub,
    OnDeviceModelType ModelType,
    string MinimumRuntimeVersion,
    string ExpectedDigest,
    long ExpectedSizeBytes)
{
    public string ExactRoute => $"{ModelId}:{Precision}";
}

public enum OnDeviceModelInstallPhase
{
    CheckingRuntime = 0,
    Downloading = 1,
    Verifying = 2,
    Completed = 3,
}

public sealed record OnDeviceModelInstallProgress(
    OnDeviceModelInstallPhase Phase,
    string Message,
    double? Fraction = null);

public sealed record OnDeviceModelInstallResult(
    bool Success,
    OnDeviceModelInstallDescriptor Requested,
    OnDeviceAccelerationStatus RuntimeStatus,
    OnDeviceModel? InstalledModel = null,
    string? Problem = null)
{
    public bool RequiresRuntimeSetup => !RuntimeStatus.IsInstalled;
}

/// <summary>
/// Optional native capability registered only by a matching platform adapter.
/// It accepts one typed descriptor, owns its child process, supports cooperative
/// cancellation, and verifies the exact installed artifact before success.
/// </summary>
public interface IOnDeviceModelInstaller
{
    OnDeviceRuntimeSetupDescriptor Descriptor { get; }

    /// <summary>
    /// Adapter-owned readiness gate for the full native runtime identity. An
    /// executable merely being present is not sufficient to install or route.
    /// </summary>
    bool IsRuntimeReady(OnDeviceAccelerationStatus status)
        => status.IsSupported
           && status.IsInstalled
           && string.IsNullOrWhiteSpace(status.Problem)
           && !string.IsNullOrWhiteSpace(status.RuntimeVersion);

    Task<OnDeviceAccelerationStatus> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OnDeviceModel>> GetInstalledModelsAsync(
        CancellationToken cancellationToken = default);

    Task<OnDeviceModelInstallResult> InstallAsync(
        OnDeviceModelInstallDescriptor descriptor,
        IProgress<OnDeviceModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
