namespace SwarmCore.Abstractions.Business;

/// <summary>Bounded delegation to one owner-allowlisted job. No model, provider, script or shell command crosses this contract.</summary>
public sealed record BusinessDeviceTask(string PeerDeviceId, string JobId, IReadOnlyList<string> Arguments,
    int TimeoutSeconds = 120, int IdleTimeoutSeconds = 60);
