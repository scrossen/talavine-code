using System.Text.Json.Nodes;

namespace SwarmCore.Agent.ToolUseEngine;

public enum ScheduledToolState { Queued, Running, Completed, Failed, Cancelled, Uncertain }

/// <summary>Trusted adapter metadata, never inferred from a model or a read-only label.</summary>
public sealed record ToolSchedulingCapability
{
    public bool Reentrant { get; init; }
    public IReadOnlyList<string> ResourceKeys { get; init; } = [];
    public IReadOnlyList<string> DependsOnCallIds { get; init; } = [];
    public TimeSpan? Timeout { get; init; }
    /// <summary>Trusted adapter price ceiling; null means unknown, never free.</summary>
    public decimal? CostUpperBoundUsd { get; init; }
    public string? PriceVersion { get; init; }
}

public sealed record ToolScheduleIdentity(string Profile, string Conversation, string Turn, string? Mission, int Attempt)
{
    public string? AuthorityReference { get; init; }
    public static ToolScheduleIdentity For(AgentSession session)
    {
        var context = session.TurnContext ?? throw new InvalidOperationException("Scheduled calls require a host turn context.");
        return new(context.ProfileId!.Value.ToString("D"), session.ConversationId,
            session.TurnId ?? throw new InvalidOperationException("Scheduled calls require a turn ID."),
            context.MissionObjectiveId?.ToString("D"), context.MissionAttempt)
        {
            AuthorityReference = FileScheduledToolStore.Hash(System.Text.Json.JsonSerializer.Serialize(new
            {
                Accounts = context.AllowedSourceAccountIds?.Order().ToArray(), context.CommunicationScopeKind,
                context.CommunicationScopeAuthority, context.CommunicationScopeUserInitiated,
                context.MissionResources, context.UserMessage, context.ActingAgentId, session.UserRequestAuthorized
            }))
        };
    }
}
public sealed record ScheduledToolResult(string Content, bool IsError, bool WasDispatched, bool LocalProcessCompleted = false,
    bool WasCancelled = false, bool TimedOut = false);
public sealed record ScheduledToolRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string OperationId { get; init; }
    public required ToolScheduleIdentity Identity { get; init; }
    public required string CallId { get; init; }
    public required string ToolId { get; init; }
    public required string ArgumentsReference { get; init; }
    public required ToolSchedulingCapability Capability { get; init; }
    public ScheduledToolState State { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public ScheduledToolResult? Result { get; init; }
    public long Version { get; init; }
}

/// <summary>Atomic registration and CAS transitions are required across host instances.</summary>
public interface IScheduledToolStore
{
    Task<IAsyncDisposable> AcquireProfileLeaseAsync(string profile, CancellationToken ct);
    Task<ScheduledToolRecord> RegisterAsync(ScheduledToolRecord record, JsonObject arguments, CancellationToken ct);
    Task<bool> UpdateAsync(ScheduledToolRecord record, long expectedVersion, CancellationToken ct);
    Task<IReadOnlyList<ScheduledToolRecord>> ReadTurnAsync(ToolScheduleIdentity identity, CancellationToken ct);
}

public interface IToolJournalProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public interface IToolSchedulerFactory
{
    DurableToolScheduler Create(AgentSession session);
    bool NativeAsyncEnabled => false;
}

public sealed record ToolSchedulerOptions
{
    public bool EnableNativeAsync { get; init; } = true;
    public int MaxConcurrency { get; init; } = 4;
    public int MaxQueuedCalls { get; init; } = 64;
    /// <summary>
    /// How long a turn whose journaled calls all finished keeps its receipts for redelivery
    /// replay before the host's retention sweep removes it. Uncertain and pending calls are kept.
    /// </summary>
    public TimeSpan JournalRetention { get; init; } = TimeSpan.FromDays(30);
    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 32 || MaxQueuedCalls < MaxConcurrency || MaxQueuedCalls > 1024)
            throw new ArgumentException("Tool scheduler requires concurrency 1..32 and queue capacity between concurrency and 1024.");
        if (JournalRetention < TimeSpan.FromDays(1))
            throw new ArgumentException("Tool journal retention must be at least one day.");
    }
}

/// <summary>Shared by every scheduler in a host, rather than recreated for each model batch.</summary>
public sealed class ToolSchedulerCapacity
{
    internal SemaphoreSlim Queue { get; }
    internal SemaphoreSlim Workers { get; }
    public ToolSchedulerCapacity(ToolSchedulerOptions options)
    {
        options.Validate(); Queue = new(options.MaxQueuedCalls); Workers = new(options.MaxConcurrency);
    }
}

/// <summary>Adapter to the mission budget owner; absence is valid only for an unlimited mission.</summary>
public interface IScheduledToolBudgetReservation
{
    Task<IAsyncDisposable> ReserveAsync(ScheduledToolRecord call, string budgetReference, CancellationToken ct);
}

public interface IScheduledToolBudgetLease : IAsyncDisposable
{
    Task MarkDispatchedAsync(CancellationToken ct);
    Task CompleteAsync(ScheduledToolResult result, CancellationToken ct);
}

/// <summary>
/// Hosts check current authority and atomically reserve any configured budget here.
/// The scheduler deliberately does not invent a second mission budget ledger.
/// </summary>
public interface IScheduledToolAdmission
{
    Task<IAsyncDisposable> AdmitAsync(ScheduledToolRecord call, CancellationToken ct);
    Task ValidateReplayAsync(ScheduledToolRecord call, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Inspection must establish outcome; an absent/ambiguous outcome stays uncertain.</summary>
public interface IScheduledToolOutcomeInspector
{
    Task<ScheduledToolResult?> InspectAsync(ScheduledToolRecord call, CancellationToken ct);
}
