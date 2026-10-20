namespace SwarmCore.Agent.Runtime;

/// <summary>Ephemeral host contract. No model argument or persisted transcript can grant this authority.</summary>
public sealed record AgentVisionRecovery(string RequiredToolId,
    Func<string, string, CancellationToken, Task<string?>> RecoverAsync);
