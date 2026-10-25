using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>The result of asking for a cycle now.</summary>
public sealed record SelfImprovementStart(bool Started, string Message);

/// <summary>
/// Works through the backlog while SwarmCode is idle. One cycle at a time: it claims the item (so a second host over the
/// same data directory leaves it alone), asks the <see cref="ISelfImprovementCycleHost"/> to run it, and records the
/// outcome. A cycle never starts while the <see cref="SelfImprovementIdleGate"/> reports activity; a running cycle is
/// left alone unless <see cref="SelfImprovementOptions.StopWhenUserReturns"/> asks for it to yield to the person.
/// </summary>
public sealed class SelfImprovementWorker : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> LiveInstances = new(StringComparer.Ordinal);

    private readonly ISelfImprovementBacklog _backlog;
    private readonly ISelfImprovementCycleHost _host;
    private readonly SelfImprovementIdleGate _gate;
    private readonly Func<SelfImprovementOptions> _options;
    private readonly Func<bool> _paused;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _instance;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _loopGate = new();
    private TaskCompletionSource _wake = NewSignal();
    private Task? _loop;
    private ActiveCycle? _active;
    private bool _initialized;
    private string _status = "Starting.";
    private IReadOnlyList<string> _waiting = [];

    public SelfImprovementWorker(ISelfImprovementBacklog backlog, ISelfImprovementCycleHost host, SelfImprovementIdleGate gate,
        Func<SelfImprovementOptions> options, Func<bool>? paused = null, TimeProvider? time = null, ILogger<SelfImprovementWorker>? logger = null)
    {
        _backlog = backlog;
        _host = host;
        _gate = gate;
        _options = options;
        _paused = paused ?? (() => false);
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        using var self = Process.GetCurrentProcess();
        _instance = $"{Environment.ProcessId}:{self.StartTime.ToUniversalTime().Ticks}:{Guid.NewGuid():N}";
        LiveInstances[_instance] = 0;
    }

    /// <summary>Raised on any thread after an item or the worker's status changes.</summary>
    public event Action? Changed;

    /// <summary>Natural idle check boundary, including unchanged checks; hosts can retry a postponed verified installation.</summary>
    public event Action? Checkpoint;

    /// <summary>What the worker is doing, in plain words (for a status line).</summary>
    public string Status { get { lock (_loopGate) return _status; } }

    /// <summary>Why the last check did not start a cycle (empty when it did, or nothing was due).</summary>
    public IReadOnlyList<string> WaitingReasons { get { lock (_loopGate) return _waiting; } }

    public bool IsRunning => _active is not null;
    public string? ActiveItemId => _active?.Item.Id;
    public string? ActiveSessionId => _active?.SessionId;
    public DateTime? ActiveSinceUtc => _active?.StartedUtc;

    public string Instance => _instance;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>An item left in progress by a worker that is no longer alive goes back to the queue.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        foreach (var item in await _backlog.ListAsync(ct).ConfigureAwait(false))
        {
            if (item.Stage != SelfImprovementStage.InProgress || OwnerAlive(item.Owner)) continue;
            await _backlog.UpdateAsync(item.Id, fresh => fresh.Stage == SelfImprovementStage.InProgress && !OwnerAlive(fresh.Owner)
                ? fresh with { Stage = SelfImprovementStage.Queued, Owner = null, Note = "Talavine Code closed while this cycle was going; it will run again." }
                : null, ct: ct).ConfigureAwait(false);
        }
        _initialized = true;
        Notify();
    }

    public void Start()
    {
        lock (_loopGate)
        {
            if (_loop is not null || _lifetime.IsCancellationRequested) return;
            _loop = Task.Run(() => LoopAsync(_lifetime.Token));
        }
    }

    /// <summary>Asks the loop to check again now (a new item, a settings change, the person left).</summary>
    public void Wake() => Interlocked.Exchange(ref _wake, NewSignal()).TrySetResult();

    private async Task LoopAsync(CancellationToken ct)
    {
        var startup = _options().StartupDelay;
        if (startup > TimeSpan.Zero)
        {
            try { await Task.Delay(startup, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        while (!ct.IsCancellationRequested)
        {
            var wake = Volatile.Read(ref _wake);
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Self-improvement check failed."); }
            var delay = _options().PollInterval;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try { await Task.WhenAny(Task.Delay(delay, _time, ct), wake.Task).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One check: starts the next due cycle when the item, the host and the idle gate allow it.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var options = _options();
            if (_active is { } running) { SetStatus($"Working on \"{running.Item.Title}\".", []); return; }
            if (!options.Enabled) { SetStatus("Off. Turn self-improvement on in Settings.", []); return; }
            if (_paused()) { SetStatus("Paused.", []); return; }
            var root = _host.ResolveRepositoryRoot();
            var items = await _backlog.ListAsync(ct).ConfigureAwait(false);
            // Leave the worker idle while the owning host installs an applicable goal repair. Starting another
            // cycle at this boundary could otherwise starve the host's asynchronous checkpoint listener forever.
            foreach (var ready in items.Where(i => PersistentGoalRepair(i, options)
                         && i.Stage is SelfImprovementStage.Verified or SelfImprovementStage.Merged))
                if (await _host.CanRunAsync(ready, ct).ConfigureAwait(false))
                {
                    SetStatus("A verified goal repair is waiting for installation.", []);
                    return;
                }
            var queued = items.Where(i => i.Stage == SelfImprovementStage.Queued).ToList();
            if (root is null)
            {
                SetStatus(queued.Count == 0 ? "Nothing to do." : $"{queued.Count} item(s) waiting: the Talavine Code source repository was not found. Set it in Settings.", []);
                return;
            }
            var now = Now;
            var candidates = queued
                .Where(i => PersistentGoalRepair(i, options) || i.Attempts < Math.Max(1, options.MaxAttemptsPerItem))
                .Where(i => i.LastAttemptUtc is not { } last || now - last >= (PersistentGoalRepair(i, options)
                    ? TimeSpan.FromMinutes(Math.Min(60, 2 * Math.Pow(2, Math.Clamp(i.Attempts - 1, 0, 5)))) : options.RetryCooldown))
                .OrderBy(i => PersistentGoalRepair(i, options) ? 0 : 1)
                .ThenBy(i => i.Kind == SelfImprovementKind.Defect ? 0 : 1)
                .ThenByDescending(i => i.Occurrences)
                .ThenBy(i => i.FirstSeenUtc)
                .ToList();
            SelfImprovementItem? due = null;
            foreach (var candidate in candidates)
                if (await _host.CanRunAsync(candidate, ct).ConfigureAwait(false)) { due = candidate; break; }
            if (due is null)
            {
                SetStatus(queued.Count == 0 ? "Nothing to do." : $"{queued.Count} item(s) waiting for their retry time.", []);
                return;
            }
            if (!_gate.IsIdle(out var reasons))
            {
                SetStatus($"{queued.Count} item(s) waiting for Talavine Code to be idle.", reasons);
                return;
            }
            await FireAsync(due, root, options, manual: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
            try { Checkpoint?.Invoke(); } catch (Exception ex) { _logger.LogWarning(ex, "A self-improvement checkpoint listener failed."); }
        }
    }

    /// <summary>Runs an item's cycle now, idle or not (the person asked). Refused while another cycle runs or the repository is missing.</summary>
    public async Task<SelfImprovementStart> RunNowAsync(string id, CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            if (_active is { } running) return new(false, $"Another cycle is running (\"{running.Item.Title}\").");
            var options = _options();
            var root = _host.ResolveRepositoryRoot();
            if (root is null) return new(false, "The Talavine Code source repository was not found. Set it in Settings.");
            var item = await _backlog.GetAsync(id, ct).ConfigureAwait(false);
            if (item is null) return new(false, "That item no longer exists.");
            if (!await _host.CanRunAsync(item, ct).ConfigureAwait(false)) return new(false, "The original goal changed or is paused; no repair was started.");
            if (item.Stage is SelfImprovementStage.InProgress) return new(false, "That item is already in progress.");
            if (item.Stage is SelfImprovementStage.Verified or SelfImprovementStage.Merged) return new(false, "That item already has a verified fix.");
            if (item.Stage is SelfImprovementStage.Dismissed or SelfImprovementStage.NeedsHuman)
            {
                item = await _backlog.UpdateAsync(id, fresh => fresh with { Stage = SelfImprovementStage.Queued, Note = "Requeued by you." }, ct: ct).ConfigureAwait(false);
                if (item is null) return new(false, "That item no longer exists.");
            }
            await FireAsync(item, root, options, manual: true, ct).ConfigureAwait(false);
            return new(true, "Started.");
        }
        finally { _tickGate.Release(); }
    }

    private async Task FireAsync(SelfImprovementItem item, string root, SelfImprovementOptions options, bool manual, CancellationToken ct)
    {
        var now = Now;
        var claimed = await _backlog.UpdateAsync(item.Id, fresh => fresh.Stage == SelfImprovementStage.Queued
            ? fresh with { Stage = SelfImprovementStage.InProgress, Owner = _instance, Note = manual ? "Running now, as you asked." : "Running while Talavine Code is idle." }
            : null, item.Revision, ct).ConfigureAwait(false);
        if (claimed is null || claimed.Stage != SelfImprovementStage.InProgress) return;
        var active = new ActiveCycle(claimed, now);
        _active = active;
        SetStatus($"Working on \"{claimed.Title}\".", []);
        // The cycle outlives this tick: it runs on its own task and records its outcome when it ends.
        _ = Task.Run(() => RunCycleAsync(active, root, options, manual), CancellationToken.None);
    }

    private async Task RunCycleAsync(ActiveCycle active, string root, SelfImprovementOptions options, bool manual)
    {
        var item = active.Item;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (options.CycleTimeout > TimeSpan.Zero) cts.CancelAfter(options.CycleTimeout);
        var deferred = false;
        var watcher = options.StopWhenUserReturns && !manual ? Task.Run(async () =>
        {
            // The person came back: the cycle yields, and the item waits for the next quiet moment.
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), _time, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (_gate.UserIdleFor < TimeSpan.FromSeconds(30)) { deferred = true; cts.Cancel(); return; }
            }
        }) : Task.CompletedTask;

        SelfImprovementCycleResult result;
        try
        {
            var context = new SelfImprovementCycleContext(_instance, root, options)
            {
                SessionStarted = sessionId => { active.SessionId = sessionId; Notify(); },
            };
            result = await _host.RunAsync(item, context, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            result = _lifetime.IsCancellationRequested ? SelfImprovementCycleResult.Cancelled("Talavine Code closed while this cycle was going.", active.SessionId)
                : deferred ? SelfImprovementCycleResult.Cancelled("Paused because you came back; it will run again when Talavine Code is idle.", active.SessionId)
                : new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Failed, $"The cycle did not finish within {SelfImprovementIdleGate.Describe(options.CycleTimeout)}.", SessionId: active.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-improvement cycle for {Item} failed.", item.Id);
            result = new SelfImprovementCycleResult(SelfImprovementCycleOutcome.Failed, "The cycle failed: " + ex.Message, SessionId: active.SessionId);
        }
        finally
        {
            cts.Cancel();
            try { await watcher.ConfigureAwait(false); } catch (Exception) { }
        }

        try { await RecordAsync(item.Id, result, options).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not record the self-improvement cycle outcome for {Item}.", item.Id); }
        finally
        {
            _active = null;
            Notify();
            Wake();
        }
    }

    private async Task RecordAsync(string id, SelfImprovementCycleResult result, SelfImprovementOptions options)
    {
        var now = Now;
        var evidence = result.Evidence is { Length: > 0 } e ? (e.Length <= options.MaxEvidenceCharacters ? e : e[..Math.Max(0, options.MaxEvidenceCharacters - 3)] + "...") : null;
        await _backlog.UpdateAsync(id, fresh =>
        {
            if (fresh.Stage != SelfImprovementStage.InProgress || fresh.Owner != _instance) return null; // someone else owns it now
            var common = fresh with { Owner = null, Note = result.Note, SessionId = result.SessionId ?? fresh.SessionId, Evidence = evidence ?? fresh.Evidence };
            switch (result.Outcome)
            {
                case SelfImprovementCycleOutcome.Verified:
                    return common with
                    {
                        Stage = result.Merged ? SelfImprovementStage.Merged : SelfImprovementStage.Verified,
                        Attempts = fresh.Attempts + 1, LastAttemptUtc = now,
                        Branch = result.Branch, Commit = result.Commit, WorktreePath = result.WorktreePath,
                    };
                case SelfImprovementCycleOutcome.Failed:
                    var attempts = fresh.Attempts + 1;
                    return common with
                    {
                        Attempts = attempts, LastAttemptUtc = now,
                        Stage = !PersistentGoalRepair(fresh, options) && attempts >= Math.Max(1, options.MaxAttemptsPerItem)
                            ? SelfImprovementStage.NeedsHuman : SelfImprovementStage.Queued,
                        WorktreePath = result.WorktreePath ?? fresh.WorktreePath,
                    };
                case SelfImprovementCycleOutcome.Unverifiable:
                    return common with { Stage = SelfImprovementStage.NeedsHuman };
                default:
                    return common with { Stage = SelfImprovementStage.Queued,
                        Branch = result.Branch ?? fresh.Branch, WorktreePath = result.WorktreePath ?? fresh.WorktreePath,
                        Commit = result.Branch is not null || result.WorktreePath is not null ? null : fresh.Commit };
            }
        }).ConfigureAwait(false);
    }

    private static bool PersistentGoalRepair(SelfImprovementItem item, SelfImprovementOptions options)
        => options.AutoApplyGoalRepairs && item.GoalRepair is not null;

    private bool OwnerAlive(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        if (owner == _instance) return true;
        var parts = owner.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var started)) return false;
        if (pid == Environment.ProcessId) return LiveInstances.ContainsKey(owner);
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private void SetStatus(string status, IReadOnlyList<string> waiting)
    {
        bool changed;
        lock (_loopGate)
        {
            changed = _status != status || !_waiting.SequenceEqual(waiting);
            _status = status;
            _waiting = waiting;
        }
        if (changed) Notify();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning("A self-improvement listener failed ({Error}).", ex.GetType().Name); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stops the loop and cancels a running cycle without waiting.</summary>
    public void Dispose()
    {
        lock (_loopGate) { if (!_lifetime.IsCancellationRequested) _lifetime.Cancel(); }
        LiveInstances.TryRemove(_instance, out _);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_loopGate)
        {
            if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
            loop = _loop;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (Exception) { /* stopping */ }
        }
        LiveInstances.TryRemove(_instance, out _);
    }

    private sealed class ActiveCycle(SelfImprovementItem item, DateTime startedUtc)
    {
        public SelfImprovementItem Item { get; } = item;
        public DateTime StartedUtc { get; } = startedUtc;
        public volatile string? SessionId;
    }
}
