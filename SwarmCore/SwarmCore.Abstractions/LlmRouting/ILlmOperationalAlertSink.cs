namespace SwarmCore.Abstractions.LlmRouting;

public enum LlmOperationalAlertSeverity
{
    Warning = 0,
    Error = 1,
    Urgent = 2
}

public sealed record LlmOperationalAlert
{
    public required string FunctionType { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Reason { get; init; }
    public required string Summary { get; init; }
    public bool RequiresVision { get; init; }
    public LlmInteractivity Interactivity { get; init; } = LlmInteractivity.Background;
    public LlmOperationalAlertSeverity Severity { get; init; } = LlmOperationalAlertSeverity.Error;
}

/// <summary>
/// Optional sink for surfacing unrecoverable LLM routing failures to the host
/// application. SwarmCore owns detection; applications decide how to notify a
/// human without SwarmCore depending on app-specific task stores.
/// </summary>
public interface ILlmOperationalAlertSink
{
    Task ReportAsync(LlmOperationalAlert alert, CancellationToken ct = default);
}
