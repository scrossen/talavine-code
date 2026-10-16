namespace SwarmCore.Abstractions.Compute;

/// <summary>
/// A compute node's self-description, POSTed periodically to a coordinator's announce
/// endpoint (e.g. the CRM's <c>POST api/crm/compute/announce</c>) so admins can see the
/// machine and opt it into a LAN LLM work pool. The announcement is an OFFER only —
/// nothing runs on the node until an admin approves it on the coordinator, and the node
/// re-announcing keeps the offer fresh (stale nodes drop out of the pool).
/// </summary>
public sealed record ComputeNodeAnnouncement
{
    /// <summary>Stable machine identity shown to admins (e.g. <c>Environment.MachineName</c>).</summary>
    public required string MachineName { get; init; }

    public string? OperatingSystem { get; init; }
    public string? CpuDescription { get; init; }
    public int CpuCores { get; init; }
    public double TotalRamGb { get; init; }
    public double AvailableRamGb { get; init; }
    public string? GpuName { get; init; }
    public double GpuVramGb { get; init; }

    /// <summary>Local LLM runtime the node offers, e.g. <c>Ollama</c> or <c>LMStudio</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// LAN-reachable base URL of the node's LLM runtime (e.g. <c>http://192.168.1.20:11434</c>).
    /// Null when the runtime is loopback-only — the node still appears in the admin list
    /// (with a "not reachable" hint) but cannot be dispatched to.
    /// </summary>
    public string? LlmBaseUrl { get; init; }

    /// <summary>Model names the runtime currently has available.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>0–100 composite utilization score at announce time (higher = busier).</summary>
    public int LoadScore { get; init; }

    /// <summary>Announcing app + version, for admin display and diagnostics.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Opt-in to outbound-only bounded inference polling; never arbitrary remote execution.</summary>
    public bool SupportsOutboundPolling { get; init; }
    public bool SupportsSubscriptionInference { get; init; }

    /// <summary>Device-generated random 32-byte base64 secret, kept in the desktop secret store.
    /// Sent only over HTTPS (or loopback development); the coordinator persists its hash only.</summary>
    public string? OutboundDeviceToken { get; init; }
}

/// <summary>Coordinator's reply to an announcement.</summary>
public sealed record ComputeNodeAnnounceResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>Whether an admin has opted this node into the pool (informational for the node's UI).</summary>
    public bool Approved { get; init; }

    public long NodeId { get; init; }

    /// <summary>How often the node should re-announce, in seconds.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    public static ComputeNodeAnnounceResult Fail(string error) => new() { Success = false, Error = error };
}
