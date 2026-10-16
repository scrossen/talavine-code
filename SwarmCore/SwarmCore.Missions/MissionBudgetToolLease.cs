using System.Diagnostics;
using SwarmCore.Abstractions.Budgets;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Missions;

/// <summary>Budget the existing direct native-tool path using the same ledger as scheduled tools and providers.</summary>
public sealed class MissionBudgetToolLease : IScheduledToolBudgetLease
{
    private readonly IMissionBudgetLedger? _ledger;
    private readonly Guid _profile;
    private readonly string? _reference;
    private readonly string _operation;
    private readonly decimal? _cost;
    private readonly Stopwatch _clock = new();
    private bool _started;
    private bool _complete;
    private MissionBudgetToolLease(IMissionBudgetLedger? ledger, Guid profile, string? reference, string operation, decimal? cost)
    { _ledger = ledger; _profile = profile; _reference = reference; _operation = operation; _cost = cost; }

    public static async Task<MissionBudgetToolLease> ReserveAsync(IMissionBudgetLedger? ledger, Guid profile, string? reference,
        Guid mission, string operationId, TimeSpan? maxRuntime, decimal? costUpperBoundUsd = null,
        string? priceVersion = null, CancellationToken ct = default)
    {
        var lease = new MissionBudgetToolLease(ledger, profile, reference, "direct-tool:" + operationId, costUpperBoundUsd);
        if (reference is null) return lease;
        await (ledger ?? throw new InvalidOperationException("Configured mission budget ledger unavailable."))
            .ReserveAsync(profile, reference, mission, lease._operation,
                new(costUpperBoundUsd, 0, 0, 1, maxRuntime is { } runtime ? Math.Max(0, checked((long)Math.Ceiling(runtime.TotalMilliseconds))) : null), priceVersion, ct);
        return lease;
    }
    public async Task MarkDispatchedAsync(CancellationToken ct)
    {
        if (_reference is not null) await _ledger!.MarkDispatchedAsync(_profile, _reference, _operation, ct);
        _started = true; _clock.Start();
    }
    public async Task CompleteAsync(ScheduledToolResult result, CancellationToken ct)
    {
        _clock.Stop();
        if (_reference is not null) await _ledger!.SettleAsync(_profile, _reference, _operation,
            result.WasDispatched ? new(_cost, 0, 0, 1, _clock.ElapsedMilliseconds) : MissionBudgetAmount.Zero,
            !result.WasDispatched || result.LocalProcessCompleted || (!result.WasCancelled && !result.TimedOut), _operation, ct);
        _complete = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (_reference is not null && !_started && !_complete)
            await _ledger!.ReleaseUnusedAsync(_profile, _reference, _operation, CancellationToken.None);
    }
}
