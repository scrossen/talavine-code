namespace SwarmCore.Abstractions.LlmRouting;

public enum LocalLlmRuntimeQueuePhase { WaitingInApp, WaitingForDevice, UsingModel }

/// <summary>Scheduling metadata only. Never includes a prompt, profile path or credentials.</summary>
public sealed record LocalLlmRuntimeQueueItem(
    string Id, int ProcessId, DateTime ProcessStartedUtc, string Application,
    string? OwnerId, string Provider, string Model, LocalLlmWorkloadKind Kind,
    LlmInteractivity Interactivity, LlmWorkloadKind Workload,
    LocalLlmRuntimeQueuePhase Phase, DateTimeOffset QueuedUtc,
    DateTimeOffset PhaseStartedUtc, DateTimeOffset UpdatedUtc)
{
    public int SchemaVersion { get; init; } = 2;
    public DateTimeOffset? AdmissionDeadlineUtc { get; init; }
    public LocalLlmSchedulingClass SchedulingClass { get; init; }
    public string? SchedulingOwnerId { get; init; }
}

public sealed record LocalLlmRuntimeQueueSnapshot(IReadOnlyList<LocalLlmRuntimeQueueItem> Items, bool IsPartial);

/// <summary>Hosts identify their own conversation using an opaque ID, never its title or text.</summary>
public static class LocalLlmRuntimeOwnerScope
{
    private static readonly AsyncLocal<string?> Owner = new();
    private static readonly AsyncLocal<string?> Application = new();
    private static readonly AsyncLocal<string?> SchedulingOwner = new();
    public static string? Current => Owner.Value;
    public static string? CurrentApplication => Application.Value;
    public static string? CurrentSchedulingOwner => SchedulingOwner.Value;
    public static IDisposable Enter(string ownerId, string? application = null, string? schedulingOwnerId = null)
    {
        var previous = Owner.Value;
        var previousApplication = Application.Value;
        var previousSchedulingOwner = SchedulingOwner.Value;
        Owner.Value = ownerId;
        Application.Value = application;
        SchedulingOwner.Value = schedulingOwnerId ?? ownerId;
        return new Scope(previous, previousApplication, previousSchedulingOwner);
    }
    private sealed class Scope(string? previous, string? previousApplication, string? previousSchedulingOwner) : IDisposable
    {
        public void Dispose() { Owner.Value = previous; Application.Value = previousApplication; SchedulingOwner.Value = previousSchedulingOwner; }
    }
}
