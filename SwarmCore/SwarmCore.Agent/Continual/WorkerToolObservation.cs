namespace SwarmCore.Agent.Continual;

/// <summary>Host-observed tool evidence supplied to an independent worker verifier.</summary>
public sealed record WorkerToolObservation(string CallId, string ToolName, string? InputJson,
    string Result, bool IsError, bool WasDispatched, bool LocalProcessCompleted, DateTimeOffset ObservedAtUtc);
