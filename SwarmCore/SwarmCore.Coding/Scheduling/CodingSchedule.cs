using System.Text.Json.Serialization;

namespace SwarmCore.Coding.Scheduling;

/// <summary>
/// A recurring coding run the person set up ("every weekday at 8:00 run the tests and report"): at each occurrence the
/// host starts a new top-level session in <see cref="RepositoryRoot"/> with <see cref="Prompt"/>.
/// </summary>
/// <remarks>
/// Two writers own disjoint fields. Definition edits (person, UI, tools) change everything except <see cref="Run"/> and
/// must carry the <see cref="Revision"/> they read; the store rejects a stale one. The scheduler owns <see cref="Run"/>
/// and changes it on the fresh stored row without bumping <see cref="Revision"/>, so its bookkeeping and an edit never
/// overwrite each other (see <see cref="ICodingScheduleStore"/>).
/// </remarks>
public sealed record CodingSchedule
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    public required string RepositoryRoot { get; init; }
    public string? Lane { get; init; }
    public string? Model { get; init; }
    public required ScheduleRecurrence Recurrence { get; init; }

    /// <summary>IANA/Windows zone id for the recurrence's wall-clock fields. Null = this device's local zone.</summary>
    public string? TimeZoneId { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Let native CLI lanes (which bypass the host's approval broker) run this schedule's unattended turns.
    /// On by default (the person's decision, 2026-10-14): a schedule is the person's own standing request, so it runs on
    /// their default lane like any direct request. Off marks the turn unattended and native lanes are skipped.
    /// </summary>
    public bool AllowNativeLanes { get; init; } = true;

    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    /// <summary>The session that created this schedule through a tool (trusted turn context), when not created in the UI.</summary>
    public string? CreatedBySessionId { get; init; }

    /// <summary>Definition revision; incremented by every definition edit, never by the scheduler's run bookkeeping.</summary>
    public long Revision { get; init; }

    public ScheduleRunState Run { get; init; } = new();
}

/// <summary>Scheduler-owned run bookkeeping. Written only through <see cref="ICodingScheduleStore.UpdateRunAsync"/>.</summary>
public sealed record ScheduleRunState
{
    public DateTime? NextRunUtc { get; init; }
    public DateTime? LastRunUtc { get; init; }
    public DateTime? LastFinishedUtc { get; init; }
    public ScheduleRunOutcome LastOutcome { get; init; }
    public string? LastSessionId { get; init; }

    /// <summary>Plain-language note about the last run or skip.</summary>
    public string? LastNote { get; init; }

    /// <summary>Scheduler instance that marked the run as running (<c>pid:startTicks:nonce</c>).</summary>
    public string? RunOwner { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ScheduleRunOutcome>))]
public enum ScheduleRunOutcome
{
    None,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>The app stopped while the run was going.</summary>
    Interrupted,
}

/// <summary>What the person or a tool sets when creating or editing a schedule. Null leaves a field unchanged on edit.</summary>
public sealed record CodingScheduleEdit
{
    public string? Title { get; init; }
    public string? Prompt { get; init; }
    public string? RepositoryRoot { get; init; }
    /// <summary>Empty string clears the lane (use the person's default).</summary>
    public string? Lane { get; init; }
    /// <summary>Empty string clears the model.</summary>
    public string? Model { get; init; }
    public ScheduleRecurrence? Recurrence { get; init; }
    public bool? Enabled { get; init; }
    public bool? AllowNativeLanes { get; init; }
}

/// <summary>A definition edit lost to a newer one (or a scheduler/UI interleaving): reload and retry.</summary>
public sealed class CodingScheduleConflictException(string message) : InvalidOperationException(message);

/// <summary>The schedule was deleted; a delayed edit or run update never recreates it.</summary>
public sealed class CodingScheduleNotFoundException(string message) : InvalidOperationException(message);
