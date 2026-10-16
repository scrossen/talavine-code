using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.Scheduling;

namespace SwarmCore.Coding.Scheduling;

public sealed class CodingSchedulerOptions
{
    public const string SectionName = "Coding:Schedules";

    /// <summary>Runs closer together than this are refused (cron, hourly and every-N-minutes alike). Default 5 minutes.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound between due checks (the loop also wakes for the earliest next run and after edits).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wait before the loop's first check, so a host restoring its last workspace at startup runs a due schedule for that
    /// repository in the window rather than in a background workspace. Default 15 seconds.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(15);

    public int MaxSchedules { get; set; } = 100;
    public int MaxTitleCharacters { get; set; } = 120;
    public int MaxPromptCharacters { get; set; } = 20_000;

    /// <summary>Repository check used by validation; defaults to <see cref="Directory.Exists(string?)"/>.</summary>
    public Func<string, bool>? RepositoryExists { get; set; }

    /// <summary>Resolves <see cref="CodingSchedule.TimeZoneId"/>; defaults to the system zone, local when null or unknown.</summary>
    public Func<string?, TimeZoneInfo>? ResolveTimeZone { get; set; }
}

/// <summary>The result of asking for a run now.</summary>
public sealed record ScheduledRunStart(bool Started, string? SessionId, string Message);

/// <summary>
/// Fires schedules through an <see cref="IScheduledRunHost"/>. A schedule never runs twice at once: a due time that
/// arrives while its previous run is still going is skipped with a note. After downtime, a schedule whose next run
/// passed runs once and then resumes its recurrence from now (no catch-up storm). Run bookkeeping is written only to
/// <see cref="CodingSchedule.Run"/> on the fresh stored row, so a concurrent edit is never overwritten.
/// </summary>
public sealed class CodingScheduler : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> LiveInstances = new(StringComparer.Ordinal);

    private readonly ICodingScheduleStore _store;
    private readonly IScheduledRunHost _host;
    private readonly CodingSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _instance;
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _runSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _loopGate = new();
    private TaskCompletionSource _wake = NewSignal();
    private Task? _loop;
    private bool _initialized;
    private DateTime? _earliestNext;

    public CodingScheduler(ICodingScheduleStore store, IScheduledRunHost host, CodingSchedulerOptions? options = null,
        TimeProvider? time = null, ILogger<CodingScheduler>? logger = null)
    {
        _store = store;
        _host = host;
        _options = options ?? new CodingSchedulerOptions();
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        using var self = Process.GetCurrentProcess();
        _instance = $"{Environment.ProcessId}:{self.StartTime.ToUniversalTime().Ticks}:{Guid.NewGuid():N}";
        LiveInstances[_instance] = 0;
    }

    public CodingSchedulerOptions Options => _options;

    /// <summary>Raised on any thread after a schedule or run changes.</summary>
    public event Action? Changed;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---- queries ----------------------------------------------------------------------------------

    public Task<IReadOnlyList<CodingSchedule>> ListAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    public Task<CodingSchedule?> GetAsync(string id, CancellationToken ct = default) => _store.GetAsync(id, ct);

    /// <summary>True while this scheduler's run of the schedule is going.</summary>
    public bool IsRunning(string scheduleId) => _active.ContainsKey(scheduleId);

    /// <summary>How many runs this scheduler has going right now (any schedule).</summary>
    public int ActiveRunCount => _active.Count;

    /// <summary>True when the conversation is a run this scheduler started and is still tracking (an unattended turn).</summary>
    public bool IsScheduledRunSession(string? conversationId)
        => !string.IsNullOrEmpty(conversationId) && _runSessions.ContainsKey(conversationId);

    public TimeZoneInfo ZoneOf(CodingSchedule schedule)
    {
        if (_options.ResolveTimeZone is { } resolve) return resolve(schedule.TimeZoneId);
        try { return CronScheduleHelper.ResolveTimeZone(schedule.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    /// <summary>The schedule's first occurrence strictly after <paramref name="afterUtc"/>.</summary>
    public DateTime? NextAfter(CodingSchedule schedule, DateTime afterUtc)
        => schedule.Recurrence.NextAfter(afterUtc, ZoneOf(schedule), schedule.CreatedUtc);

    // ---- definition edits -------------------------------------------------------------------------

    /// <summary>Creates a schedule. Title, prompt, repository and recurrence are required.</summary>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    public async Task<CodingSchedule> CreateAsync(CodingScheduleEdit draft, string? createdBySessionId = null, CancellationToken ct = default)
    {
        if ((await _store.ListAsync(ct).ConfigureAwait(false)).Count >= _options.MaxSchedules)
            throw new InvalidOperationException($"There are already {_options.MaxSchedules} schedules. Delete one first.");
        var now = Now;
        var schedule = new CodingSchedule
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = draft.Title?.Trim() ?? "",
            Prompt = draft.Prompt?.Trim() ?? "",
            RepositoryRoot = NormalizeRoot(draft.RepositoryRoot),
            Lane = Blank(draft.Lane),
            Model = Blank(draft.Model),
            Recurrence = draft.Recurrence ?? throw new ArgumentException("Choose when the schedule runs."),
            Enabled = draft.Enabled ?? true,
            AllowNativeLanes = draft.AllowNativeLanes ?? true,
            CreatedUtc = now,
            UpdatedUtc = now,
            CreatedBySessionId = createdBySessionId,
        };
        Validate(schedule);
        schedule = schedule with { Run = new ScheduleRunState { NextRunUtc = schedule.Enabled ? NextAfter(schedule, now) : null } };
        var stored = await _store.CreateAsync(schedule, ct).ConfigureAwait(false);
        Notify();
        Wake();
        return stored;
    }

    /// <summary>
    /// Edits the definition against the revision the caller read. Changing the recurrence, zone or enabling a disabled
    /// schedule computes the next run from now; disabling clears it.
    /// </summary>
    /// <exception cref="CodingScheduleConflictException">It changed since <paramref name="expectedRevision"/>.</exception>
    /// <exception cref="CodingScheduleNotFoundException">It was deleted.</exception>
    /// <exception cref="ArgumentException">The edited definition is invalid.</exception>
    public async Task<CodingSchedule> UpdateAsync(string id, long expectedRevision, CodingScheduleEdit edit, CancellationToken ct = default)
    {
        var now = Now;
        var stored = await _store.UpdateDefinitionAsync(id, expectedRevision, current =>
        {
            var next = current with
            {
                Title = edit.Title is null ? current.Title : edit.Title.Trim(),
                Prompt = edit.Prompt is null ? current.Prompt : edit.Prompt.Trim(),
                RepositoryRoot = edit.RepositoryRoot is null ? current.RepositoryRoot : NormalizeRoot(edit.RepositoryRoot),
                Lane = edit.Lane is null ? current.Lane : Blank(edit.Lane),
                Model = edit.Model is null ? current.Model : Blank(edit.Model),
                Recurrence = edit.Recurrence ?? current.Recurrence,
                Enabled = edit.Enabled ?? current.Enabled,
                AllowNativeLanes = edit.AllowNativeLanes ?? current.AllowNativeLanes,
                UpdatedUtc = now,
            };
            Validate(next);
            var timingChanged = next.Recurrence != current.Recurrence || next.TimeZoneId != current.TimeZoneId || !current.Enabled;
            var nextRun = !next.Enabled ? null
                : timingChanged || current.Run.NextRunUtc is null ? NextAfter(next, now)
                : current.Run.NextRunUtc;
            return next with { Run = current.Run with { NextRunUtc = nextRun } };
        }, ct).ConfigureAwait(false);
        Notify();
        Wake();
        return stored;
    }

    /// <summary>Deletes a schedule (a running run finishes on its own). False when it did not exist.</summary>
    public async Task<bool> DeleteAsync(string id, long? expectedRevision = null, CancellationToken ct = default)
    {
        var deleted = await _store.DeleteAsync(id, expectedRevision, ct).ConfigureAwait(false);
        if (deleted) Notify();
        return deleted;
    }

    // ---- running ----------------------------------------------------------------------------------

    /// <summary>
    /// Loads schedules once: a run left "running" by a SwarmCode that is no longer alive becomes
    /// <see cref="ScheduleRunOutcome.Interrupted"/>; an enabled schedule without a next run gets one.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var now = Now;
            foreach (var schedule in await _store.ListAsync(ct).ConfigureAwait(false))
            {
                await _store.UpdateRunAsync(schedule.Id, fresh =>
                {
                    var run = fresh.Run;
                    var changed = false;
                    if (run.LastOutcome == ScheduleRunOutcome.Running && run.RunOwner != _instance && !OwnerAlive(run.RunOwner))
                    {
                        run = run with { LastOutcome = ScheduleRunOutcome.Interrupted, RunOwner = null, LastFinishedUtc = now,
                            LastNote = "Talavine Code closed while this run was going." };
                        changed = true;
                    }
                    if (fresh.Enabled && run.NextRunUtc is null && NextAfter(fresh, now) is { } next) { run = run with { NextRunUtc = next }; changed = true; }
                    if (!fresh.Enabled && run.NextRunUtc is not null) { run = run with { NextRunUtc = null }; changed = true; }
                    return changed ? run : null;
                }, ct).ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally { _initGate.Release(); }
        Notify();
    }

    /// <summary>One due check: fires every enabled schedule whose next run has arrived.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Now;
            DateTime? earliest = null;
            foreach (var schedule in await _store.ListAsync(ct).ConfigureAwait(false))
            {
                if (!schedule.Enabled || schedule.Run.NextRunUtc is not { } next) continue;
                if (next > now)
                {
                    if (earliest is null || next < earliest) earliest = next;
                    continue;
                }
                try { await FireAsync(schedule.Id, manual: false, now, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Schedule {Schedule} could not fire ({Error}).", schedule.Id, ex.GetType().Name);
                }
            }
            _earliestNext = earliest;
        }
        finally { _tickGate.Release(); }
    }

    /// <summary>Starts the schedule now (its recurrence is unchanged). Refused while its previous run is going.</summary>
    public async Task<ScheduledRunStart> RunNowAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        if (await _store.GetAsync(id, ct).ConfigureAwait(false) is null) throw new CodingScheduleNotFoundException("That schedule does not exist.");
        return await FireAsync(id, manual: true, Now, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the due-check loop in the background until disposed.</summary>
    public void Start()
    {
        lock (_loopGate)
        {
            if (_loop is not null || _lifetime.IsCancellationRequested) return;
            _loop = Task.Run(() => LoopAsync(_lifetime.Token));
        }
    }

    /// <summary>Asks the loop to check again now (after an edit).</summary>
    public void Wake() => Interlocked.Exchange(ref _wake, NewSignal()).TrySetResult();

    private async Task LoopAsync(CancellationToken ct)
    {
        if (_options.StartupDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(_options.StartupDelay, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        while (!ct.IsCancellationRequested)
        {
            var wake = Volatile.Read(ref _wake);
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning("Schedule check failed ({Error}).", ex.GetType().Name); }
            var delay = _options.PollInterval;
            if (_earliestNext is { } next && next - Now < delay) delay = next - Now;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try { await Task.WhenAny(Task.Delay(delay, _time, ct), wake.Task).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<ScheduledRunStart> FireAsync(string id, bool manual, DateTime now, CancellationToken ct)
    {
        var active = new ActiveRun();
        if (!_active.TryAdd(id, active))
        {
            if (!manual) await RecordSkipAsync(id, now, ct).ConfigureAwait(false);
            return new ScheduledRunStart(false, _active.TryGetValue(id, out var running) ? running.SessionId : null,
                "The previous run of this schedule is still going.");
        }
        try
        {
            var claimed = false;
            var busyElsewhere = false;
            var stored = await _store.UpdateRunAsync(id, fresh =>
            {
                if (!manual && (!fresh.Enabled || fresh.Run.NextRunUtc is not { } due || due > now)) return null;
                if (fresh.Run.LastOutcome == ScheduleRunOutcome.Running && fresh.Run.RunOwner != _instance && OwnerAlive(fresh.Run.RunOwner))
                {
                    busyElsewhere = true;
                    return manual ? null : fresh.Run with { NextRunUtc = NextAfter(fresh, now), LastNote = SkipNote(fresh, now) };
                }
                claimed = true;
                var late = !manual && fresh.Run.NextRunUtc is { } scheduled && now - scheduled > _options.PollInterval + TimeSpan.FromMinutes(2);
                return fresh.Run with
                {
                    LastOutcome = ScheduleRunOutcome.Running,
                    LastRunUtc = now,
                    LastFinishedUtc = null,
                    RunOwner = _instance,
                    LastNote = manual ? "Started with Run now." : late ? $"Ran once to catch up the run due {Local(fresh, fresh.Run.NextRunUtc!.Value)}." : null,
                    NextRunUtc = fresh.Enabled ? NextAfter(fresh, now) : null,
                };
            }, ct).ConfigureAwait(false);

            if (stored is null) throw new CodingScheduleNotFoundException("That schedule does not exist.");
            if (!claimed)
            {
                _active.TryRemove(new KeyValuePair<string, ActiveRun>(id, active));
                Notify();
                return new ScheduledRunStart(false, null, busyElsewhere
                    ? "The previous run of this schedule is still going in another Talavine Code window."
                    : "This schedule is not due.");
            }
            Notify();

            ScheduledRunHandle handle;
            try
            {
                handle = await _host.StartRunAsync(new ScheduledRunRequest(stored.Id, stored.Title, stored.Prompt, stored.RepositoryRoot,
                    stored.Lane, stored.Model, stored.AllowNativeLanes, now, manual), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var note = ex is OperationCanceledException ? "The run did not start." : $"The run did not start: {ex.Message}";
                await FinishAsync(id, active, null, new ScheduledRunResult(ScheduleRunOutcome.Failed, note)).ConfigureAwait(false);
                return new ScheduledRunStart(false, null, note);
            }

            active.SessionId = handle.SessionId;
            _runSessions[handle.SessionId] = id;
            // Recording the session is best effort: the run's end records it again, so observation starts first.
            var observed = ObserveAsync(id, active, handle);
            try
            {
                await _store.UpdateRunAsync(id, fresh => fresh.Run.RunOwner == _instance && fresh.Run.LastOutcome == ScheduleRunOutcome.Running
                    ? fresh.Run with { LastSessionId = handle.SessionId }
                    : null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning("Could not record schedule {Schedule}'s session ({Error}).", id, ex.GetType().Name); }
            Notify();
            _ = observed;
            return new ScheduledRunStart(true, handle.SessionId, "Started.");
        }
        catch
        {
            if (active.SessionId is null) _active.TryRemove(new KeyValuePair<string, ActiveRun>(id, active));
            throw;
        }
    }

    private async Task ObserveAsync(string id, ActiveRun active, ScheduledRunHandle handle)
    {
        ScheduledRunResult result;
        try { result = await handle.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { result = new ScheduledRunResult(ScheduleRunOutcome.Cancelled, "Stopped."); }
        catch (Exception ex) { result = new ScheduledRunResult(ScheduleRunOutcome.Failed, ex.Message); }
        await FinishAsync(id, active, handle.SessionId, result).ConfigureAwait(false);
    }

    private async Task FinishAsync(string id, ActiveRun active, string? sessionId, ScheduledRunResult result)
    {
        try
        {
            var now = Now;
            await _store.UpdateRunAsync(id, fresh => fresh.Run.RunOwner != _instance ? null : fresh.Run with
            {
                LastOutcome = result.Outcome,
                LastNote = result.Note ?? fresh.Run.LastNote,
                LastFinishedUtc = now,
                RunOwner = null,
                LastSessionId = sessionId ?? fresh.Run.LastSessionId,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning("Could not record the end of schedule {Schedule}'s run ({Error}).", id, ex.GetType().Name); }
        finally
        {
            _active.TryRemove(new KeyValuePair<string, ActiveRun>(id, active));
            if (sessionId is not null) _runSessions.TryRemove(sessionId, out _);
            Notify();
            Wake();
        }
    }

    private async Task RecordSkipAsync(string id, DateTime now, CancellationToken ct)
    {
        await _store.UpdateRunAsync(id, fresh => !fresh.Enabled || fresh.Run.NextRunUtc is not { } due || due > now
            ? null
            : fresh.Run with { NextRunUtc = NextAfter(fresh, now), LastNote = SkipNote(fresh, now) }, ct).ConfigureAwait(false);
        Notify();
    }

    private string SkipNote(CodingSchedule schedule, DateTime now)
        => $"Skipped the run due {Local(schedule, schedule.Run.NextRunUtc ?? now)}: the previous run was still going.";

    private string Local(CodingSchedule schedule, DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ZoneOf(schedule)).ToString("ddd MMM d, HH:mm");

    // ---- validation -------------------------------------------------------------------------------

    private void Validate(CodingSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.Title)) throw new ArgumentException("Give the schedule a title.");
        if (schedule.Title.Length > _options.MaxTitleCharacters) throw new ArgumentException($"The title must be at most {_options.MaxTitleCharacters} characters.");
        if (string.IsNullOrWhiteSpace(schedule.Prompt)) throw new ArgumentException("Give the schedule a prompt: what each run should do.");
        if (schedule.Prompt.Length > _options.MaxPromptCharacters) throw new ArgumentException($"The prompt must be at most {_options.MaxPromptCharacters} characters.");
        if (string.IsNullOrWhiteSpace(schedule.RepositoryRoot) || !Path.IsPathFullyQualified(schedule.RepositoryRoot))
            throw new ArgumentException("Choose the repository folder (a full path).");
        if (!(_options.RepositoryExists ?? Directory.Exists)(schedule.RepositoryRoot))
            throw new ArgumentException($"The repository folder does not exist: {schedule.RepositoryRoot}");
        if (schedule.Lane is { Length: > 200 } || schedule.Model is { Length: > 200 }) throw new ArgumentException("The lane or model name is too long.");
        if (schedule.Recurrence.Validate(_options.MinimumInterval) is { } problem) throw new ArgumentException(problem);
        if (schedule.TimeZoneId is not null && _options.ResolveTimeZone is null)
        {
            try { CronScheduleHelper.ResolveTimeZone(schedule.TimeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Unknown time zone."); }
        }
    }

    private static string NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        var trimmed = root.Trim();
        if (!Path.IsPathFullyQualified(trimmed)) return trimmed;
        var full = Path.GetFullPath(trimmed);
        var rootOnly = Path.GetPathRoot(full);
        return string.Equals(full, rootOnly, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning("A schedule change listener failed ({Error}).", ex.GetType().Name); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stops the loop without waiting for it (runs in flight finish on their own).</summary>
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

    private sealed class ActiveRun
    {
        public string? SessionId { get; set; }
    }
}
