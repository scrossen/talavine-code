namespace SwarmCore.Abstractions.Budgets;

/// <summary>USD and whole tokens/calls/milliseconds. Null is unlimited; zero admits no consumption.</summary>
public sealed record MissionBudgetLimits(decimal? CostUsd = null, long? InputTokens = null,
    long? OutputTokens = null, long? TotalTokens = null, long? ToolCalls = null,
    long? RuntimeMilliseconds = null, DateTimeOffset? DeadlineUtc = null)
{
    public void Validate()
    {
        if (CostUsd < 0 || InputTokens < 0 || OutputTokens < 0 || TotalTokens < 0 || ToolCalls < 0 || RuntimeMilliseconds < 0)
            throw new ArgumentException("Mission budget limits cannot be negative.");
    }
}

/// <summary>Unknown is not zero. Output includes reasoning tokens; cached input is included in input.</summary>
public sealed record MissionBudgetAmount(decimal? CostUsd = null, long? InputTokens = null,
    long? OutputTokens = null, long? ToolCalls = null, long? RuntimeMilliseconds = null)
{
    public long? TotalTokens => InputTokens is { } i && OutputTokens is { } o ? checked(i + o) : null;
    public void Validate()
    {
        if (CostUsd < 0 || InputTokens < 0 || OutputTokens < 0 || ToolCalls < 0 || RuntimeMilliseconds < 0)
            throw new ArgumentException("Mission usage cannot be negative.");
        _ = TotalTokens;
    }
    public static MissionBudgetAmount Zero { get; } = new(0, 0, 0, 0, 0);
}

public enum MissionBudgetReservationState { Reserved, Dispatched, Settled, Released }
public sealed record MissionBudgetReservation(string OperationId, Guid MissionId, MissionBudgetAmount Estimate,
    string? PriceVersion, MissionBudgetReservationState State = MissionBudgetReservationState.Reserved,
    MissionBudgetAmount? Actual = null, string? UsageReference = null)
{
    public Guid? AllocationParentMissionId { get; init; }
    public long? AllocatedToolCalls { get; init; }
}
public sealed record MissionBudgetSnapshot(Guid ProfileId, string Reference, Guid OwnerMissionId,
    MissionBudgetLimits Limits, IReadOnlyList<Guid> Members, IReadOnlyList<MissionBudgetReservation> Reservations,
    long Version = 1, string? LastAdmissionFailure = null)
{
    public MissionBudgetAmount Used => Sum(Reservations.Where(r => r.State == MissionBudgetReservationState.Settled).Select(r => r.Actual!));
    public MissionBudgetAmount Reserved => Sum(Reservations.Where(r => r.State is MissionBudgetReservationState.Reserved or MissionBudgetReservationState.Dispatched).Select(r => r.Estimate));
    public MissionBudgetAmount Remaining
    {
        get
        {
            var used = Used; var reserved = Reserved;
            decimal? D(decimal? cap, decimal? a, decimal? b) => cap is { } limit && a is { } x && b is { } y ? Math.Max(0, limit - x - y) : null;
            long? L(long? cap, long? a, long? b) => cap is { } limit && a is { } x && b is { } y ? Math.Max(0, checked(limit - x - y)) : null;
            return new(D(Limits.CostUsd, used.CostUsd, reserved.CostUsd), L(Limits.InputTokens, used.InputTokens, reserved.InputTokens),
                L(Limits.OutputTokens, used.OutputTokens, reserved.OutputTokens), L(Limits.ToolCalls, used.ToolCalls, reserved.ToolCalls),
                L(Limits.RuntimeMilliseconds, used.RuntimeMilliseconds, reserved.RuntimeMilliseconds));
        }
    }
    private static MissionBudgetAmount Sum(IEnumerable<MissionBudgetAmount> source)
    {
        var rows = source.ToArray();
        decimal? D(Func<MissionBudgetAmount, decimal?> field) => rows.Any(r => field(r) is null) ? null : rows.Sum(r => field(r)!.Value);
        long? L(Func<MissionBudgetAmount, long?> field) => rows.Any(r => field(r) is null) ? null : rows.Sum(r => field(r)!.Value);
        return new(D(r => r.CostUsd), L(r => r.InputTokens), L(r => r.OutputTokens), L(r => r.ToolCalls), L(r => r.RuntimeMilliseconds));
    }
}

/// <summary>A durable, best-effort notification of a budget-refusal or a settled-usage threshold
/// crossing. Not part of admission correctness: losing one never changes any reservation state.</summary>
public sealed record MissionBudgetEvent(Guid MissionId, DateTimeOffset AtUtc, string Kind, string? Detail);

public interface IMissionBudgetLedger
{
    Task<MissionBudgetSnapshot> CreateAsync(Guid profile, string reference, Guid ownerMission, MissionBudgetLimits limits, CancellationToken ct = default);
    Task<MissionBudgetSnapshot?> ReadAsync(Guid profile, string reference, CancellationToken ct = default);
    Task AddMemberAsync(Guid profile, string reference, Guid parentMission, Guid childMission, CancellationToken ct = default);
    Task AllocateChildAsync(Guid profile, string reference, Guid parentMission, Guid childMission, long toolCalls, CancellationToken ct = default);
    Task ReleaseChildAsync(Guid profile, string reference, Guid childMission, CancellationToken ct = default);
    /// <summary>
    /// Undoes a speculative allocation/membership for a child that was never actually admitted (the owning
    /// store rejected or conflicted on creation), leaving no reservation or membership trace and crediting
    /// any nested parent allocation back in full. Distinct from <see cref="ReleaseChildAsync"/>, which closes
    /// a real, durably created child's unused capacity but retains its reservation as released history.
    /// </summary>
    Task AbortChildAsync(Guid profile, string reference, Guid childMission, CancellationToken ct = default);
    Task<MissionBudgetSnapshot> UpdateLimitsAsync(Guid profile, string reference, Guid ownerMission, long expectedVersion, MissionBudgetLimits limits, CancellationToken ct = default);
    Task<MissionBudgetReservation> ReserveAsync(Guid profile, string reference, Guid mission, string operationId, MissionBudgetAmount estimate, string? priceVersion, CancellationToken ct = default);
    Task MarkDispatchedAsync(Guid profile, string reference, string operationId, CancellationToken ct = default);
    Task SettleAsync(Guid profile, string reference, string operationId, MissionBudgetAmount actual, bool authoritative, string? usageReference, CancellationToken ct = default);
    Task ReleaseUnusedAsync(Guid profile, string reference, string operationId, CancellationToken ct = default);
    /// <summary>Durable refusal/threshold notifications recorded for this account, oldest first.
    /// Optional: an implementation that does not persist them returns an empty list.</summary>
    Task<IReadOnlyList<MissionBudgetEvent>> EventsAsync(Guid profile, string reference, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MissionBudgetEvent>>([]);
}

public sealed class MissionBudgetExceededException(string reason) : InvalidOperationException(reason);

/// <summary>Shared deterministic admission policy. Unknown estimates cannot satisfy a configured hard bound.</summary>
public static class MissionBudgetPolicy
{
    public static string? Check(MissionBudgetSnapshot account, MissionBudgetAmount next, DateTimeOffset now)
    {
        account.Limits.Validate(); next.Validate();
        if (account.Limits.DeadlineUtc <= now) return "mission-budget-deadline";
        var used = account.Used; var reserved = account.Reserved; var limits = account.Limits;
        bool Exceeds(decimal? cap, decimal? a, decimal? b, decimal? c) => cap is not null &&
            (a is null || b is null || c is null || a.Value + b.Value + c.Value > cap.Value);
        if (Exceeds(limits.CostUsd, used.CostUsd, reserved.CostUsd, next.CostUsd)) return "mission-budget-cost-or-unknown-price";
        if (Exceeds(limits.InputTokens, used.InputTokens, reserved.InputTokens, next.InputTokens)) return "mission-budget-input-tokens";
        if (Exceeds(limits.OutputTokens, used.OutputTokens, reserved.OutputTokens, next.OutputTokens)) return "mission-budget-output-tokens";
        if (Exceeds(limits.TotalTokens, used.TotalTokens, reserved.TotalTokens, next.TotalTokens)) return "mission-budget-total-tokens";
        if (Exceeds(limits.ToolCalls, used.ToolCalls, reserved.ToolCalls, next.ToolCalls)) return "mission-budget-tool-calls";
        if (Exceeds(limits.RuntimeMilliseconds, used.RuntimeMilliseconds, reserved.RuntimeMilliseconds, next.RuntimeMilliseconds)) return "mission-budget-runtime";
        return null;
    }
}
