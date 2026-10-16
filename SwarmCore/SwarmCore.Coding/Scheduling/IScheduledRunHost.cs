namespace SwarmCore.Coding.Scheduling;

/// <summary>One firing of a schedule, as the host receives it.</summary>
public sealed record ScheduledRunRequest(
    string ScheduleId,
    string Title,
    string Prompt,
    string RepositoryRoot,
    string? Lane,
    string? Model,
    bool AllowNativeLanes,
    DateTime FiredUtc,
    bool Manual);

/// <summary>How a run ended. <paramref name="Note"/> is a short plain-language detail (error, stop reason).</summary>
public sealed record ScheduledRunResult(ScheduleRunOutcome Outcome, string? Note = null);

/// <summary>A started run: the session the host created for it, and its first turn's outcome.</summary>
public sealed record ScheduledRunHandle(string SessionId, Task<ScheduledRunResult> Completion);

/// <summary>
/// Where scheduled runs execute. The host creates a new top-level session in the request's repository, sends the
/// prompt as one turn under its own authority policy, and completes <see cref="ScheduledRunHandle.Completion"/> when
/// that turn ends (including when it is stopped or its workspace closes). It must always complete.
/// </summary>
public interface IScheduledRunHost
{
    Task<ScheduledRunHandle> StartRunAsync(ScheduledRunRequest request, CancellationToken ct);
}
