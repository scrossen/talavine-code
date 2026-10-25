using System.Collections.Concurrent;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Files;
using SwarmCore.Coding.Routing;
using System.Text.Json;

namespace SwarmCore.Coding.Goals;

public enum CodingGoalControl { Pause, Resume, Cancel }

/// <summary>The host owns durable storage and deletion; the shared continual engine owns execution transitions.</summary>
public interface ICodingGoalStore : IContinualObjectiveStore
{
    /// <summary>Trusted installed-repair continuation only. CAS the exact stopped baseline; never fabricate a user control.</summary>
    Task<bool> TryResumeAfterRepairAsync(SwarmCore.Coding.SelfImprovement.GoalRepairLink baseline,
        string repairItemId, string installedFixEvidence, DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(false);
    Task<bool> ControlAsync(Guid id, long expectedVersion, CodingGoalControl control, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> UpdateAsync(Guid id, long expectedVersion, string objective, DateTimeOffset now, CancellationToken ct = default,
        IReadOnlyList<CodingAttachment>? attachments = null);
    Task<bool> HoldAsync(Guid id, long expectedVersion, string reason, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ReconcileAsync(Guid id, string leaseToken, string evidence, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Trusted host verifier only; never expose this proof writer as a model tool.</summary>
    Task<bool> ResolveReceiptAsync(Guid id, string leaseToken, ContinualReceiptResolution resolution,
        DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(false);
    Task DeleteConversationAsync(string conversationId, CancellationToken ct = default);
}

public sealed record CodingGoalChanged(string TurnId, ContinualObjectiveSnapshot Goal) : TurnEvent(TurnId);

public sealed record CodingGoalPayload(IReadOnlyList<CodingAttachment> Attachments, string? RepositoryRoot)
{
    // Explicit objective replacement has its own authority position. Execution heartbeats and
    // inbox receipt cursors must never become the chronology of authenticated instructions.
    public DateTimeOffset? AuthorityUpdatedAtUtc { get; init; }
    public int? AuthorityAfterMessageCount { get; init; }
    public CodingGoalUserControl? LastUserControl { get; init; }
    /// <summary>A host-verified repair scheduled this exact saved version; survives a process exit before dispatch.</summary>
    public CodingGoalRepairContinuation? RepairContinuation { get; init; }

    public static CodingGoalPayload Read(ContinualObjectiveSnapshot goal)
        => goal.ExecutorPayloadJson is null ? new([], null)
            : JsonSerializer.Deserialize<CodingGoalPayload>(goal.ExecutorPayloadJson)
                ?? throw new InvalidDataException("The saved goal references could not be read.");
}

/// <summary>A host-accepted explicit control; retries and leases never create this authority.</summary>
public sealed record CodingGoalUserControl(Guid GoalId, CodingGoalControl Control, DateTimeOffset AtUtc,
    long GoalVersion, ContinualObjectiveStatus PreviousStatus, ContinualObjectiveStatus ResultingStatus);

public sealed record CodingGoalRepairContinuation(string ItemId, long ScheduledVersion, string IntentDigest);

/// <summary>Host revalidates workspace identity and current permissions before every automatic attempt.</summary>
public interface ICodingGoalRequestPolicy
{
    Task<CodingTurnRequest> PrepareAsync(CodingTurnRequest request, CancellationToken ct = default);
}

/// <summary>Independently verifies exact outcomes using host-owned protocols; model claims are not proof.</summary>
public interface ICodingGoalReceiptVerifier
{
    Task<IReadOnlyList<ContinualReceiptResolution>> VerifyAsync(ContinualObjectiveSnapshot objective,
        IReadOnlyList<ContinualToolReceipt> observations, CancellationToken ct = default);
}

/// <summary>Shared within one host; store leases also exclude independent host processes.</summary>
public sealed class CodingGoalRuntimeState
{
    internal ConcurrentDictionary<Guid, CancellationTokenSource> Active { get; } = new();

    /// <summary>True while this host process is executing an attempt of the goal (the durable lease is the cross-process truth).</summary>
    public bool IsExecuting(Guid goalId) => Active.ContainsKey(goalId);
}

public static class CodingGoalStatus
{
    public static string Format(ContinualObjectiveSnapshot? goal) => goal is null
        ? "No goal is set. Use /goal followed by what you want achieved."
        : $"Goal: {goal.ObjectiveSummary}\nStatus: {Label(goal.Status)} · {goal.AttemptCount} attempt{(goal.AttemptCount == 1 ? "" : "s")}" +
          (string.IsNullOrWhiteSpace(goal.LastFailure) ? "" : "\n" + goal.LastFailure);

    public static string Label(ContinualObjectiveStatus status) => status switch
    {
        ContinualObjectiveStatus.InProgress => "Working",
        ContinualObjectiveStatus.RetryScheduled => "Continuing",
        ContinualObjectiveStatus.VerificationPending => "Verifying",
        ContinualObjectiveStatus.Completed => "Achieved",
        ContinualObjectiveStatus.WaitingForInput => "Needs your input",
        ContinualObjectiveStatus.AttentionRequired => "Retry scheduled",
        ContinualObjectiveStatus.Paused => "Paused",
        ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Idle => "Cancelled",
        ContinualObjectiveStatus.Uncertain => "Inspect prior actions before resuming",
        _ => status.ToString(),
    };
}
