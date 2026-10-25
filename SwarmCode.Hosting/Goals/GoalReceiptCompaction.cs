using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Goals;

/// <summary>What one compaction pass did, for the log line and the settings page.</summary>
public sealed record GoalReceiptCompactionReport(DateTimeOffset StartedUtc, TimeSpan Duration, IReadOnlyList<GoalReceiptCompactionResult> Goals, bool Manual, string? Error = null)
{
    public int ReceiptsCompacted => Goals.Sum(goal => goal.ReceiptsCompacted);
    public long BytesCompacted => Goals.Sum(goal => goal.BytesCompacted);
    public long LogBytesReleased => Goals.Sum(goal => goal.LogBytesBefore - goal.LogBytesAfter);
    public int GoalsHeld => Goals.Count(goal => goal.GoalHold != ContinualReceiptRetentionHold.None);
    public int GoalsChanged => Goals.Count(goal => goal.Changed);
}

/// <summary>
/// The idle-time step that compacts old goal receipt bodies. Off until the person turns on
/// <see cref="AppPreferences.CompactGoalReceipts"/>; then it runs on the dreaming schedule while no turn is running, at most
/// once per <see cref="GoalReceiptCompactionOptions.Interval"/> per process. The store refuses every goal that is working,
/// leased, verifying or uncertain, and this step additionally skips goals this process is executing right now, so an
/// active attempt's evidence is never touched. One pass at a time per process (several workspaces share one store).
/// </summary>
public sealed class GoalReceiptCompaction(
    FileCodingGoalStore store,
    AppPreferencesStore preferences,
    GoalReceiptCompactionOptions options,
    CodingGoalRuntimeState? runtime = null,
    ILogger<GoalReceiptCompaction>? logger = null,
    TimeProvider? time = null) : IIdleMaintenanceStep
{
    private static readonly SemaphoreSlim ProcessWide = new(1, 1);
    private static long _lastRunTicks;
    private static int _strayChecked;
    private static GoalReceiptCompactionReport? _lastReport;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => "goal receipt compaction";

    public bool IsEnabled => preferences.Current.CompactGoalReceipts;

    /// <summary>The last pass in this process, from any workspace.</summary>
    public static GoalReceiptCompactionReport? LastReport => Volatile.Read(ref _lastReport);

    /// <summary>Due once per interval while switched on; switched off, due once per process to report unused files in the goals folder.</summary>
    public bool IsDue(DateTimeOffset now)
        => IsEnabled ? now.UtcTicks - Interlocked.Read(ref _lastRunTicks) >= options.Interval.Ticks : Volatile.Read(ref _strayChecked) == 0;

    public async Task RunAsync(CancellationToken ct)
    {
        if (IsEnabled) await RunOnceAsync(manual: false, ct).ConfigureAwait(false);
        else if (Interlocked.Exchange(ref _strayChecked, 1) == 0) WarnAboutStrayFiles();
    }

    /// <summary>
    /// One pass over every goal. <paramref name="manual"/> is the settings page's button: it ignores the switch and the
    /// interval, never the store's holds. Returns null when another pass is already running in this process.
    /// </summary>
    public async Task<GoalReceiptCompactionReport?> RunOnceAsync(bool manual, CancellationToken ct = default)
    {
        if (!manual && !IsEnabled) return null;
        if (!await ProcessWide.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            var started = _time.GetUtcNow();
            Interlocked.Exchange(ref _lastRunTicks, started.UtcTicks);
            var results = new List<GoalReceiptCompactionResult>();
            GoalReceiptCompactionReport report;
            try
            {
                foreach (var id in await store.ListGoalIdsAsync(ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    // The store checks the durable lease; this process also knows which goals it is running right now.
                    if (runtime?.IsExecuting(id) == true) continue;
                    if (await store.CompactAsync(id, options, _time.GetUtcNow(), ct).ConfigureAwait(false) is { } result) results.Add(result);
                }
                report = new GoalReceiptCompactionReport(started, _time.GetUtcNow() - started, results, manual);
                Log(report);
                WarnAboutStrayFiles();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                report = new GoalReceiptCompactionReport(started, _time.GetUtcNow() - started, results, manual, ex.GetType().Name + ": " + ex.Message);
                _logger.LogWarning("Goal receipt compaction failed ({Error}); goals are unchanged beyond what was already saved. It will try again at the next idle pass.", ex.GetType().Name);
            }
            Volatile.Write(ref _lastReport, report);
            return report;
        }
        finally { ProcessWide.Release(); }
    }

    private void Log(GoalReceiptCompactionReport report)
    {
        if (report.GoalsChanged == 0)
        {
            _logger.LogInformation("Goal receipt compaction: nothing to release ({Goals} goals checked, {Held} held by activity) in {Ms} ms.",
                report.Goals.Count, report.GoalsHeld, (long)report.Duration.TotalMilliseconds);
            return;
        }
        _logger.LogInformation("Goal receipt compaction: {Receipts} receipt texts compacted ({Bytes}), body logs shrank by {Released} across {Changed} of {Goals} goals ({Held} held by activity) in {Ms} ms.",
            report.ReceiptsCompacted, Size(report.BytesCompacted), Size(report.LogBytesReleased), report.GoalsChanged, report.Goals.Count, report.GoalsHeld, (long)report.Duration.TotalMilliseconds);
    }

    private void WarnAboutStrayFiles()
    {
        Volatile.Write(ref _strayChecked, 1);
        if (options.StrayWarningBytes <= 0) return;
        GoalStoreInventoryReport inventory;
        try { inventory = store.Inspect(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (inventory.StrayBytes < options.StrayWarningBytes) return;
        _logger.LogWarning("The goals folder holds {Count} files Talavine Code does not use ({Bytes}): {Largest}. Settings > Data and logs lists them; remove them yourself if you no longer need them.",
            inventory.Stray.Count, Size(inventory.StrayBytes), string.Join(", ", inventory.Stray.Take(3).Select(file => Path.GetFileName(file.Path) + " " + Size(file.Bytes))));
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##") + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#") + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0") + " KB",
        _ => bytes + " B",
    };

    /// <summary>Tests only: forget the process-wide last run.</summary>
    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _lastRunTicks, 0);
        Volatile.Write(ref _strayChecked, 0);
        Volatile.Write(ref _lastReport, null);
    }
}
