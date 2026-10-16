using SwarmCore.Abstractions.Security;

namespace SwarmCore.Agent.Continual;

/// <summary>Host-specific continuation implementation selected by ExecutorKey.</summary>
public interface IContinualObjectiveExecutor
{
    string ExecutorKey { get; }

    Task<ContinualObjectiveExecutionResult> ExecuteAsync(
        ContinualObjectiveExecutionRequest request,
        IContinualObjectiveExecutionReporter reporter,
        CancellationToken ct = default);
}

public sealed record ContinualObjectiveExecutionRequest(
    ContinualObjectiveSnapshot Objective,
    string ContinuationPrompt,
    string LeaseToken);

public sealed record ContinualObjectiveExecutionResult(
    string FinalOutput,
    bool TurnErrored = false,
    string? Error = null,
    /// <summary>
    /// The host executor already verified the durable side effect and the returned
    /// output. This skips the generic model-based completion auditor, which must
    /// not second-guess a stronger host-owned receipt or repeat completed work.
    /// </summary>
    bool CompletionVerifiedByExecutor = false,
    /// <summary>
    /// The failure is the terminal outcome of this user request. The processor
    /// records it as Failed instead of silently scheduling another physical run.
    /// </summary>
    bool FailureIsTerminal = false,
    /// <summary>The host recorded an owned-work wait; do not spend a model call auditing known incomplete work.</summary>
    bool AwaitingOwnedWork = false)
{
    /// <summary>
    /// Only a person can clear this turn error, for example because the model
    /// provider reports no credit left or rejected its credential. The
    /// processor parks the objective in AttentionRequired on the long
    /// attention backoff instead of repeating the same failing call on the
    /// transient retry backoff.
    /// </summary>
    public bool FailureRequiresAttention { get; init; }
}

/// <summary>
/// Executor event boundary. Implementations must await Started before invoking
/// a tool and Completed after it returns; the future supervisor supplies a
/// reporter backed by <see cref="IContinualObjectiveStore"/>.
/// </summary>
public interface IContinualObjectiveExecutionReporter
{
    Task ToolStartedAsync(ContinualToolReceipt receipt, CancellationToken ct = default);
    Task ToolCompletedAsync(ContinualToolReceipt receipt, CancellationToken ct = default);
    Task ProgressAsync(
        long sequence,
        string? toolName,
        string status,
        double? fraction = null,
        CancellationToken ct = default);
}

public interface IContinualObjectiveExecutorRegistry
{
    IContinualObjectiveExecutor? Get(string executorKey);
}

internal sealed class ContinualObjectiveExecutorRegistry : IContinualObjectiveExecutorRegistry
{
    private readonly IReadOnlyDictionary<string, IContinualObjectiveExecutor> _executors;

    public ContinualObjectiveExecutorRegistry(IEnumerable<IContinualObjectiveExecutor> executors)
    {
        var map = new Dictionary<string, IContinualObjectiveExecutor>(StringComparer.OrdinalIgnoreCase);
        foreach (var executor in executors)
        {
            if (string.IsNullOrWhiteSpace(executor.ExecutorKey))
                throw new InvalidOperationException($"{executor.GetType().FullName} has an empty continual executor key.");
            if (!map.TryAdd(executor.ExecutorKey.Trim(), executor))
                throw new InvalidOperationException($"Duplicate continual executor key '{executor.ExecutorKey}'.");
        }
        _executors = map;
    }

    public IContinualObjectiveExecutor? Get(string executorKey)
        => string.IsNullOrWhiteSpace(executorKey)
            ? null
            : _executors.GetValueOrDefault(executorKey.Trim());
}

public enum ContinualObjectiveActivityKind
{
    Started = 0,
    Resumed = 1,
    ToolStarted = 2,
    ToolProgress = 3,
    ToolCompleted = 4,
    StatusChanged = 5,
    Stopped = 6,
    Error = 7,
    LeaseHeartbeat = 8,
}

public sealed record ContinualObjectiveActivity(
    Guid ObjectiveId,
    string PartitionKey,
    string Surface,
    ContinualObjectiveActivityKind Kind,
    string Description,
    DateTimeOffset AtUtc,
    string? Detail = null,
    string? ToolCallId = null,
    string? ToolName = null,
    double? Fraction = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface IContinualObjectiveActivitySink
{
    Task PublishAsync(ContinualObjectiveActivity activity, CancellationToken ct = default);
}

internal sealed class NullContinualObjectiveActivitySink : IContinualObjectiveActivitySink
{
    public Task PublishAsync(ContinualObjectiveActivity activity, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Host hook for secret removal before objective evidence is persisted.</summary>
public interface IContinualObjectiveTextSanitizer
{
    string? Sanitize(string? value, int maximumLength);
}

/// <summary>
/// Conservative default persistence sanitizer. Hosts can replace this to also
/// remove exact credential values known only to them, but every host gets the
/// shared credential-shape scrub before continual evidence reaches a store.
/// </summary>
public sealed class CredentialScrubbingContinualObjectiveTextSanitizer : IContinualObjectiveTextSanitizer
{
    public string? Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (maximumLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        var scrubbed = CredentialPatterns.Scrub(value, "[redacted]");
        return scrubbed.Length <= maximumLength
            ? scrubbed
            : scrubbed[..maximumLength] + " …[truncated]";
    }
}

public interface IContinualObjectiveContinuationPromptBuilder
{
    string Build(ContinualObjectiveSnapshot objective);
}

/// <summary>
/// Receipt-aware continuation prompt. It tells the executor to verify saved
/// side effects rather than replaying them and changes strategy after repeated
/// no-progress output.
/// </summary>
public sealed class ContinualObjectiveContinuationPromptBuilder
    : IContinualObjectiveContinuationPromptBuilder
{
    public string Build(ContinualObjectiveSnapshot objective)
    {
        ArgumentNullException.ThrowIfNull(objective);
        var receipts = objective.Receipts.Count == 0
            ? "No saved tool receipts."
            : string.Join("\n", objective.Receipts.TakeLast(20).Select(receipt =>
                $"- {receipt.ToolName}: "
                + $"{(receipt.State == ContinualToolReceiptState.Started ? "STARTED; OUTCOME UNKNOWN" : receipt.IsError ? "ERROR" : "ok")}; "
                + $"side-effect={receipt.SideEffect}; result={(!MissionSteeringPolicy.CanUseReceiptContent(objective, receipt)
                    ? "Prior-scope content omitted; retain the operation identity and revalidate current permitted evidence."
                    : Truncate(receipt.Result, 500))}"));
        var updates = string.Join("\n", objective.SteeringHistory.Where(s => s.Kind == SwarmCore.Agent.Runtime.AgentSteeringKind.TaskCorrection)
            .Select(s => $"- Update {s.Sequence}: {s.Message}"));
        var noProgress = objective.ConsecutiveNoProgress > 0
            ? $"The last {objective.ConsecutiveNoProgress} continuation(s) ended without accepted progress. Use a materially different strategy."
            : string.Empty;
        var objectiveText = objective.Mission is null ? Truncate(objective.ObjectiveText, 5000) : objective.ObjectiveText;
        var objectiveSection = ContinualObjectivePromptEnvelope.FormatOriginalObjectiveSection(objectiveText);

        return $"""
            Continue the durable objective below. A prior model turn ending did not prove completion, so the host kept the objective active.

            {objectiveSection}

            {ContinualObjectivePromptEnvelope.TaskCorrectionsHeading}
            {updates}

            ## Current host reminder constraints
            {(objective.Mission is null ? "None." : System.Text.Json.JsonSerializer.Serialize(new { objective.Mission.ReminderDueDate, objective.Mission.ReminderDueAtUtc, objective.Mission.ReminderTimeZoneId }))}
            A date-only reminder remains a local calendar date in its recorded time zone; do not invent a time or reinterpret it as the mission execution deadline.

            ## Why it remains active
            {Truncate(objective.LastFailure ?? objective.ContinuationFeedback, 2000)}
            {noProgress}

            ## Host acceptance criteria (immutable for this assignment)
            {(objective.Mission is null ? "Use the assigned objective." : System.Text.Json.JsonSerializer.Serialize(MissionVerification.Criteria(objective)))}

            ## Last output
            {Truncate(objective.LastOutput, 4000)}

            ## Saved tool receipts
            {receipts}

            Resume from actual current state. A successful or outcome-unknown write/send/exec receipt means the side effect may already have happened: verify it and continue with missing steps instead of blindly repeating it. If one approach failed, inspect the failure, repair it, or use another available capability. Do the work now; do not merely promise a future cycle. Ask the user only for a fact, credential, decision, physical action, or authority that truly cannot be obtained or inferred from the objective and available tools.
            """;
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value.Length <= max ? value : value[..max] + " …[truncated]";
}
