using SwarmCore.Abstractions.Compute;

namespace SwarmCore.Coding.Peers;

public sealed record PeerIdentity(string DeviceId, string DisplayName, string Platform, bool Online);
public sealed record PeerProvider(string ProviderId, string DisplayName, string Kind, bool Available,
    string Status, string DefaultModel, IReadOnlyList<string> Models, IReadOnlyList<string> ExecutionModes, bool LocalInference = false,
    IReadOnlyList<string>? LocalModels = null);
public sealed record PeerJob(string JobId, bool Available, string Status, string Description = "",
    bool AcceptsArguments = false, bool ScriptSelectedByFirstArgument = false, IReadOnlyList<string>? Scripts = null);
public sealed record PeerCapabilities
{
    public string DeviceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Architecture { get; init; } = "";
    public bool AgentBridgeEnabled { get; init; }
    public int LaneVersion { get; init; }
    public long GeneratedUtcMs { get; init; }
    public PeerHardware? Hardware { get; init; }
    public string DefaultProviderId { get; init; } = "";
    public IReadOnlyList<PeerProvider> Providers { get; init; } = [];
    public IReadOnlyList<PeerJob> Jobs { get; init; } = [];
}

public sealed record PeerRequirements(string? Platform = null, string? Architecture = null,
    int? MinimumLogicalProcessors = null, long? MinimumMemoryBytes = null, bool RequireGpu = false,
    string? GpuName = null, long? MinimumGpuMemoryBytes = null, bool RequireLocalInference = false,
    string? Provider = null, string? Model = null, string ExecutionMode = "headless", string? Job = null);

public sealed record PeerCandidate(PeerIdentity Peer, PeerCapabilities? Capabilities, bool Matches,
    string? Reason, string? Provider = null, string? Model = null);

public sealed record PeerAssignment(string PeerDeviceId, string Title, string Goal, string? Context,
    IReadOnlyList<string> SuccessCriteria, IReadOnlyList<string> ExpectedArtifacts, string ConversationId,
    string? Provider, string? Model, string ExecutionMode, string? Job, IReadOnlyList<string> JobArguments,
    bool ReturnArtifactsToRequester = true);
public sealed record PeerWorkHandle(string WorkId, string ConversationId, string MessageId);
public sealed record PeerWork
{
    public string WorkId { get; init; } = "";
    public string ConversationId { get; init; } = "";
    public string PeerDeviceId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    public IReadOnlyList<string> SuccessCriteria { get; init; } = [];
    public IReadOnlyList<string> ExpectedArtifacts { get; init; } = [];
    public string Status { get; init; } = "";
    public bool IsTerminal { get; init; }
    public long UpdatedUtcMs { get; init; }
    public string? LastNote { get; init; }
    public string? LastReplyText { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    /// <summary>A completed agent turn is not host-verified completion of its delegated goal.</summary>
    public string? CompletionNotice => string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase)
        && ExpectedArtifacts.Count > 0 && Artifacts.Count == 0
            ? "The worker reported completion without attaching the expected artifacts. Review its report and verify outputs; a detached process may still be running. Do not repeat the work merely because its agent turn ended."
            : null;
}

/// <summary>The host owns transport, pairing and authentication. Shared routing never accesses secrets or a host assembly.</summary>
public interface IPeerWorkBridge
{
    Task<IReadOnlyList<PeerIdentity>> ListPeersAsync(CancellationToken ct);
    Task<PeerCapabilities> CapabilitiesAsync(string deviceId, CancellationToken ct);
    Task<PeerWorkHandle> AssignAsync(PeerAssignment assignment, CancellationToken ct);
    Task<IReadOnlyList<PeerWork>> ListWorkAsync(CancellationToken ct);
    Task<PeerWork> WorkAsync(string workId, CancellationToken ct);
    Task<PeerWork> WaitAsync(string workId, long since, int timeoutSeconds, CancellationToken ct);
    Task<PeerWork> CancelAsync(string workId, CancellationToken ct);
}
