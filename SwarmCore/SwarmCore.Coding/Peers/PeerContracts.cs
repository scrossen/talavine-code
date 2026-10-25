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

public sealed record PeerMessageHandle(string MessageId, string ConversationId);
public sealed record PeerMessage(string PeerDeviceId, string PeerDisplayName, string MessageId, string ConversationId,
    bool IsReply, string Kind, string Text, long ReceivedUtcMs);

public sealed record PeerScreenImage(string Subject, int Width, int Height, string PngBase64);
public sealed record PeerScreenStep(string Command, bool Succeeded, string Text, string? FailureCategory, long ElapsedMs, IReadOnlyList<PeerScreenImage> Images);
/// <summary>A run's state on the controlled device: returned by start, every poll, and (aggregated) at the end.</summary>
public sealed record PeerScreenResult(bool Ok, string Detail, IReadOnlyList<PeerScreenStep> Steps, IReadOnlyList<string> Notes)
{
    public string RunId { get; init; } = "";
    /// <summary>queued, running, completed, failed, canceled, unknown-run or lost-contact.</summary>
    public string State { get; init; } = "";
    public int NextAfter { get; init; }
    public int TotalStepsDone { get; init; }
    /// <summary>The pulse: the command running now and for how long.</summary>
    public string? Current { get; init; }
    public long CurrentElapsedMs { get; init; }
    public long ElapsedMs { get; init; }
    public bool IsFinal => State is "completed" or "failed" or "canceled" or "unknown-run" or "lost-contact";
}

/// <summary>
/// Conversation with a peer's agents and control of its desktop. The host owns transport, pairing and the
/// controlled device's consent; shared code never sees credentials or retries a dispatched input.
/// </summary>
public interface IPeerCollaborationBridge
{
    Task<PeerMessageHandle> SendMessageAsync(string peerDeviceId, string text, string conversationId, CancellationToken ct);
    /// <summary>Drains only this conversation's inbound messages.</summary>
    Task<IReadOnlyList<PeerMessage>> InboxAsync(string conversationId, CancellationToken ct);
    /// <summary>Starts text remote-control commands on a peer and returns at once with a run id. Sent once, never retried.</summary>
    Task<PeerScreenResult> StartScreenAsync(string peerDeviceId, string commands, CancellationToken ct);
    /// <summary>One pulse: waits up to <paramref name="waitMs"/> for news, returns steps after <paramref name="after"/> and what is running now.</summary>
    Task<PeerScreenResult> PollScreenAsync(string peerDeviceId, string runId, int after, int waitMs, bool cancel, CancellationToken ct);
}
