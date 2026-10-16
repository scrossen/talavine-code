namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Ambient, privacy-safe identity for one user-approved Model Scout control.
/// The host-neutral chat tools append the intent and terminal receipts; host
/// adapters use this correlation when they perform their own atomic writes or
/// enqueue longer-running work.
/// </summary>
public sealed record ModelScoutHostControlContext(
    string CorrelationId,
    string IntentAuditId,
    string? SessionId,
    string? TurnId);

/// <summary>Active agent-profile route exposed by a host-specific Scout adapter.</summary>
public sealed record ModelScoutCurrentProfileRoute(
    string? ProfileName,
    string? Provider,
    string? Model,
    string Mode);

/// <summary>
/// Optional host state that cannot be represented by the database-agnostic
/// routing settings contract.
/// </summary>
public sealed record ModelScoutHostControlStatus(
    bool Available,
    string AvailabilityMessage,
    bool? AutomaticManagementEnabled = null,
    ModelScoutCurrentProfileRoute? CurrentProfileRoute = null);

/// <summary>Request for a host-owned optimization pass.</summary>
public sealed record ModelScoutHostOptimizeRequest(string? FunctionType);

/// <summary>Request to benchmark one exact function route.</summary>
public sealed record ModelScoutHostBenchmarkRequest(
    string FunctionType,
    string Provider,
    string Model,
    bool AllowMeteredProvider);

/// <summary>Request for host-owned proof recovery for one canonical function.</summary>
public sealed record ModelScoutHostRecoveryRequest(string FunctionType);

/// <summary>
/// Result of a host control. Messages are returned to the user after bounded
/// diagnostic sanitization; the host-neutral tool writes a canonical terminal
/// receipt and never persists this free-form message.
/// </summary>
public enum ModelScoutHostControlOutcome
{
    NotApplied = 0,
    Completed = 1,
    Accepted = 2,
    Queued = 3,
}

public sealed record ModelScoutHostControlResult(
    bool Success,
    string Message,
    string? BeforeProvider = null,
    string? BeforeModel = null,
    string? AfterProvider = null,
    string? AfterModel = null,
    string? FallbackProvider = null,
    string? FallbackModel = null,
    ModelScoutHostControlOutcome Outcome = ModelScoutHostControlOutcome.Completed);

/// <summary>
/// Optional application bridge for Model Scout controls that are inherently
/// host-specific: automation, benchmark orchestration, proof recovery, and the
/// active agent-profile route. SwarmCore registers an explicit unavailable
/// implementation by default; an application can replace it before calling
/// <c>AddModelScoutChatTools</c>.
/// </summary>
/// <remarks>
/// Implementations must not retain raw prompts, user-supplied reasons, or other
/// private chat content. A successful method means the requested write or durable
/// work enqueue completed. Use <see cref="ModelScoutHostControlOutcome.Accepted"/>
/// or <see cref="ModelScoutHostControlOutcome.Queued"/> when the durable handoff,
/// rather than the underlying long-running operation, is the completed action.
/// Throwing or returning false must not be described as a successful mutation.
/// </remarks>
public interface IModelScoutHostControl
{
    Task<ModelScoutHostControlStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<ModelScoutHostControlResult> SetAutomaticManagementAsync(
        bool enabled,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default);

    Task<ModelScoutHostControlResult> OptimizeAsync(
        ModelScoutHostOptimizeRequest request,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default);

    Task<ModelScoutHostControlResult> BenchmarkAsync(
        ModelScoutHostBenchmarkRequest request,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default);

    Task<ModelScoutHostControlResult> RecoverAsync(
        ModelScoutHostRecoveryRequest request,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default);

    Task<ModelScoutHostControlResult> PinCurrentProfileAsync(
        string provider,
        string model,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default);

    Task<ModelScoutHostControlResult> UnpinCurrentProfileAsync(
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default);
}
