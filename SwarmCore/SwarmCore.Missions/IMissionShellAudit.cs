namespace SwarmCore.Missions;

/// <summary>Optional host audit sink after execution. Implementations must redact
/// command text and must not turn an audit-storage failure into an execution failure.</summary>
public interface IMissionShellAudit
{
    Task RecordAsync(string toolId, string? command, bool succeeded, string outcome,
        object? evidence = null, string? error = null);
}
