using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Scheduling;

namespace SwarmCore.Coding.Tests;

public sealed class CodingSchedulerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "swarmcore-schedules-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero)); // a Wednesday
    private readonly FakeRunHost _host = new();

    public CodingSchedulerTests()
    {
        _repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private string StoreDirectory => Path.Combine(_tmp, "schedules");

    private CodingScheduler NewScheduler(ICodingScheduleStore? store = null) => new(store ?? new JsonFileCodingScheduleStore(StoreDirectory), _host,
        new CodingSchedulerOptions { ResolveTimeZone = _ => TimeZoneInfo.Utc }, _time);

    private CodingScheduleEdit Draft(ScheduleRecurrence recurrence, string title = "Tests") => new()
    {
        Title = title, Prompt = "Run the tests and report.", RepositoryRoot = _repo, Recurrence = recurrence,
    };

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ---- recurrence -------------------------------------------------------------------------------

    [Fact]
    public void Presets_compute_the_next_wall_clock_occurrence()
    {
        var utc = TimeZoneInfo.Utc;
        var anchor = Utc(10, 14, 7);
        Assert.Equal(Utc(10, 14, 8), ScheduleRecurrence.DailyAt(8, 0).NextAfter(Utc(10, 14, 7), utc, anchor));
        Assert.Equal(Utc(10, 15, 8), ScheduleRecurrence.DailyAt(8, 0).NextAfter(Utc(10, 14, 8), utc, anchor)); // strictly after
        // Friday 2026-10-16 09:00 -> Monday 2026-10-19 08:00.
        Assert.Equal(Utc(10, 19, 8), ScheduleRecurrence.WeekdaysAt(8, 0).NextAfter(Utc(10, 16, 9), utc, anchor));
        Assert.Equal(Utc(10, 14, 7, 15), ScheduleRecurrence.HourlyAt(15).NextAfter(Utc(10, 14, 7, 1), utc, anchor));
        Assert.Equal(Utc(10, 14, 8, 30), ScheduleRecurrence.Every(90).NextAfter(Utc(10, 14, 7, 0), utc, anchor));
        Assert.Equal(Utc(10, 14, 10), ScheduleRecurrence.Every(90).NextAfter(Utc(10, 14, 9, 45), utc, anchor));
        Assert.Equal(Utc(10, 19, 6, 30), ScheduleRecurrence.FromCron("30 6 * * 1").NextAfter(Utc(10, 14, 7), utc, anchor));
        Assert.Equal("Weekdays at 8:00 AM", ScheduleRecurrence.WeekdaysAt(8, 0).Describe().Replace(' ', ' '));
        Assert.Equal("Every 90 minutes", ScheduleRecurrence.Every(90).Describe());
    }

    [Fact]
    public void Daylight_saving_gaps_run_after_the_gap_and_fall_back_runs_once()
    {
        // A zone at UTC-5 with an hour of daylight saving from 8 March 02:00 to 1 November 02:00 (local).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Shifted", TimeSpan.FromHours(-5), "Test", "Test Standard", "Test Daylight", [rule]);
        var anchor = Utc(1, 1, 0);

        // 02:30 does not exist on 8 March: the run happens at 03:30 daylight time (07:30 UTC), once.
        var gap = ScheduleRecurrence.DailyAt(2, 30);
        var spring = gap.NextAfter(Utc(3, 8, 6), zone, anchor);
        Assert.Equal(Utc(3, 8, 7, 30), spring);
        Assert.Equal(Utc(3, 9, 6, 30), gap.NextAfter(spring!.Value, zone, anchor));

        // 01:30 happens twice on 1 November: the schedule still runs once that day.
        var twice = ScheduleRecurrence.DailyAt(1, 30);
        var first = twice.NextAfter(Utc(11, 1, 4), zone, anchor)!.Value;
        Assert.Equal(new DateTime(2026, 11, 1), TimeZoneInfo.ConvertTimeFromUtc(first, zone).Date);
        var second = twice.NextAfter(first, zone, anchor)!.Value;
        Assert.Equal(new DateTime(2026, 11, 2), TimeZoneInfo.ConvertTimeFromUtc(second, zone).Date);
        Assert.True(second - first >= TimeSpan.FromHours(23));
    }

    [Theory]
    [InlineData("every_minutes", null, null, 1, null, false)]
    [InlineData("cron", null, null, null, "* * * * *", false)]
    [InlineData("cron", null, null, null, "not cron", false)]
    [InlineData("daily", "25:00", null, null, null, false)]
    [InlineData("weekdays", "08:00", null, null, null, true)]
    [InlineData(null, null, null, null, "0 */2 * * *", true)]
    [InlineData("every_minutes", null, null, 15, null, true)]
    public void Recurrence_input_is_validated(string? preset, string? time, int? minute, int? interval, string? cron, bool valid)
    {
        var (recurrence, error) = ScheduleRecurrence.Parse(preset, time, minute, interval, cron);
        var problem = error ?? recurrence!.Validate(TimeSpan.FromMinutes(5));
        Assert.Equal(valid, problem is null);
    }

    // ---- due detection, concurrency, downtime -------------------------------------------------------

    [Fact]
    public async Task A_due_schedule_starts_one_run_and_records_its_outcome()
    {
        await using var scheduler = NewScheduler();
        var schedule = await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
        Assert.Equal(Utc(10, 14, 8), schedule.Run.NextRunUtc);

        _time.Set(Utc(10, 14, 7, 59));
        await scheduler.TickAsync();
        Assert.Empty(_host.Requests);

        _time.Set(Utc(10, 14, 8));
        await scheduler.TickAsync();
        var request = Assert.Single(_host.Requests);
        Assert.Equal(schedule.Id, request.ScheduleId);
        Assert.Equal("Run the tests and report.", request.Prompt);
        Assert.Equal(_repo, request.RepositoryRoot);
        Assert.False(request.Manual);
        Assert.True(request.AllowNativeLanes); // native lanes by default (person's decision 2026-10-14)
        Assert.True(scheduler.IsRunning(schedule.Id));
        var running = (await scheduler.GetAsync(schedule.Id))!;
        Assert.Equal(ScheduleRunOutcome.Running, running.Run.LastOutcome);
        Assert.Equal(Utc(10, 15, 8), running.Run.NextRunUtc);
        Assert.Equal(_host.SessionIds[0], running.Run.LastSessionId);
        Assert.True(scheduler.IsScheduledRunSession(_host.SessionIds[0]));

        _host.Complete(0, new ScheduledRunResult(ScheduleRunOutcome.Succeeded));
        await Until(() => !scheduler.IsRunning(schedule.Id));
        var done = (await scheduler.GetAsync(schedule.Id))!;
        Assert.Equal(ScheduleRunOutcome.Succeeded, done.Run.LastOutcome);
        Assert.Equal(_host.SessionIds[0], done.Run.LastSessionId);
        Assert.Null(done.Run.RunOwner);
        Assert.False(scheduler.IsScheduledRunSession(_host.SessionIds[0]));
        Assert.Equal(1, done.Revision); // run bookkeeping never bumps the definition revision
    }

    [Fact]
    public async Task A_schedule_never_runs_twice_at_once()
    {
        await using var scheduler = NewScheduler();
        var schedule = await scheduler.CreateAsync(Draft(ScheduleRecurrence.Every(10)));
        _time.Advance(TimeSpan.FromMinutes(10));
        await scheduler.TickAsync();
        Assert.Single(_host.Requests);

        // The next occurrence arrives while the first run is still going: skipped with a note, not queued.
        _time.Advance(TimeSpan.FromMinutes(10));
        await scheduler.TickAsync();
        Assert.Single(_host.Requests);
        var skipped = (await scheduler.GetAsync(schedule.Id))!;
        Assert.Contains("still going", skipped.Run.LastNote);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddMinutes(10), skipped.Run.NextRunUtc);
        var refused = await scheduler.RunNowAsync(schedule.Id);
        Assert.False(refused.Started);
        Assert.Single(_host.Requests);

        _host.Complete(0, new ScheduledRunResult(ScheduleRunOutcome.Failed, "tests failed"));
        await Until(() => !scheduler.IsRunning(schedule.Id));
        Assert.Equal("tests failed", (await scheduler.GetAsync(schedule.Id))!.Run.LastNote);
        _time.Advance(TimeSpan.FromMinutes(10));
        await scheduler.TickAsync();
        Assert.Equal(2, _host.Requests.Count);
    }

    [Fact]
    public async Task After_downtime_a_missed_schedule_runs_once_then_resumes_from_now()
    {
        string id;
        await using (var before = NewScheduler())
            id = (await before.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)))).Id;

        // SwarmCode was closed for three days: three occurrences passed.
        _time.Set(Utc(10, 17, 12));
        await using var after = NewScheduler();
        await after.TickAsync();
        await after.TickAsync();
        var request = Assert.Single(_host.Requests);
        Assert.Equal(id, request.ScheduleId);
        var schedule = (await after.GetAsync(id))!;
        Assert.Equal(Utc(10, 18, 8), schedule.Run.NextRunUtc);
        Assert.Contains("catch up", schedule.Run.LastNote);
    }

    [Fact]
    public async Task The_background_loop_fires_due_schedules_and_wakes_after_edits()
    {
        await using var scheduler = new CodingScheduler(new JsonFileCodingScheduleStore(StoreDirectory), _host,
            new CodingSchedulerOptions { ResolveTimeZone = _ => TimeZoneInfo.Utc, StartupDelay = TimeSpan.Zero, PollInterval = TimeSpan.FromMinutes(10) }, _time);
        scheduler.Start();
        await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
        await Task.Delay(200);
        Assert.Empty(_host.Requests);
        _time.Set(Utc(10, 14, 8));
        scheduler.Wake();
        await Until(() => _host.Requests.Count == 1);
    }

    [Fact]
    public async Task A_run_left_running_by_a_closed_app_becomes_interrupted()
    {
        var store = new JsonFileCodingScheduleStore(StoreDirectory);
        await using (var first = NewScheduler(store))
        {
            var created = await first.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
            _time.Set(Utc(10, 14, 8));
            await first.TickAsync();
            Assert.True(first.IsRunning(created.Id));
        } // the app closes; the run's completion never arrives

        await using var second = NewScheduler(store);
        await second.InitializeAsync();
        var schedule = Assert.Single(await second.ListAsync());
        Assert.Equal(ScheduleRunOutcome.Interrupted, schedule.Run.LastOutcome);
        Assert.Null(schedule.Run.RunOwner);
        Assert.Equal(_host.SessionIds[0], schedule.Run.LastSessionId);
    }

    [Fact]
    public async Task A_disabled_schedule_does_not_run_and_enabling_starts_from_now()
    {
        await using var scheduler = NewScheduler();
        var schedule = await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
        var paused = await scheduler.UpdateAsync(schedule.Id, schedule.Revision, new CodingScheduleEdit { Enabled = false });
        Assert.Null(paused.Run.NextRunUtc);
        _time.Set(Utc(10, 16, 9));
        await scheduler.TickAsync();
        Assert.Empty(_host.Requests);

        var resumed = await scheduler.UpdateAsync(schedule.Id, paused.Revision, new CodingScheduleEdit { Enabled = true });
        Assert.Equal(Utc(10, 17, 8), resumed.Run.NextRunUtc); // no catch-up for paused days
    }

    // ---- store: revision checks and deletion --------------------------------------------------------

    [Fact]
    public async Task Scheduler_bookkeeping_and_a_person_edit_never_overwrite_each_other()
    {
        await using var scheduler = NewScheduler();
        var created = await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));

        // The person opens the schedule (reads revision 1); meanwhile the schedule fires.
        var seenByPerson = (await scheduler.GetAsync(created.Id))!;
        _time.Set(Utc(10, 14, 8));
        await scheduler.TickAsync();

        // The person's edit lands on the fresh row: the run in progress (outcome, session, owner) survives.
        var edited = await scheduler.UpdateAsync(created.Id, seenByPerson.Revision, new CodingScheduleEdit { Title = "Nightly tests", Prompt = "Run all tests." });
        Assert.Equal("Nightly tests", edited.Title);
        Assert.Equal(ScheduleRunOutcome.Running, edited.Run.LastOutcome);
        Assert.Equal(_host.SessionIds[0], edited.Run.LastSessionId);
        Assert.Equal(2, edited.Revision);

        // The run finishes after the edit: its bookkeeping keeps the person's title and prompt.
        _host.Complete(0, new ScheduledRunResult(ScheduleRunOutcome.Succeeded));
        await Until(() => !scheduler.IsRunning(created.Id));
        var final = (await scheduler.GetAsync(created.Id))!;
        Assert.Equal("Nightly tests", final.Title);
        Assert.Equal("Run all tests.", final.Prompt);
        Assert.Equal(ScheduleRunOutcome.Succeeded, final.Run.LastOutcome);

        // A second editor still holding revision 1 is refused instead of replacing the newer definition.
        await Assert.ThrowsAsync<CodingScheduleConflictException>(() =>
            scheduler.UpdateAsync(created.Id, seenByPerson.Revision, new CodingScheduleEdit { Title = "Stale" }));
        Assert.Equal("Nightly tests", (await scheduler.GetAsync(created.Id))!.Title);
    }

    [Fact]
    public async Task Pausing_during_a_run_is_kept_when_the_run_finishes()
    {
        await using var scheduler = NewScheduler();
        var created = await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
        _time.Set(Utc(10, 14, 8));
        await scheduler.TickAsync();
        var current = (await scheduler.GetAsync(created.Id))!;
        await scheduler.UpdateAsync(created.Id, current.Revision, new CodingScheduleEdit { Enabled = false });
        _host.Complete(0, new ScheduledRunResult(ScheduleRunOutcome.Succeeded));
        await Until(() => !scheduler.IsRunning(created.Id));
        var final = (await scheduler.GetAsync(created.Id))!;
        Assert.False(final.Enabled);
        Assert.Null(final.Run.NextRunUtc);
    }

    [Fact]
    public async Task A_deleted_schedule_is_never_recreated_by_a_delayed_write()
    {
        await using var scheduler = NewScheduler();
        var store = new JsonFileCodingScheduleStore(StoreDirectory);
        var created = await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
        _time.Set(Utc(10, 14, 8));
        await scheduler.TickAsync();

        Assert.True(await scheduler.DeleteAsync(created.Id));
        // The run in flight finishes, a stale edit arrives, and a direct run update is attempted: none recreate it.
        _host.Complete(0, new ScheduledRunResult(ScheduleRunOutcome.Succeeded));
        await Until(() => !scheduler.IsRunning(created.Id));
        await Assert.ThrowsAsync<CodingScheduleNotFoundException>(() =>
            scheduler.UpdateAsync(created.Id, created.Revision, new CodingScheduleEdit { Title = "Back?" }));
        Assert.Null(await store.UpdateRunAsync(created.Id, s => s.Run with { LastNote = "late" }));
        Assert.Null(await store.GetAsync(created.Id));
        Assert.Empty(await store.ListAsync());
        Assert.False(File.Exists(Path.Combine(StoreDirectory, created.Id + ".json")));
    }

    [Fact]
    public async Task Delete_with_a_stale_revision_is_refused()
    {
        await using var scheduler = NewScheduler();
        var created = await scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)));
        await scheduler.UpdateAsync(created.Id, created.Revision, new CodingScheduleEdit { Title = "Renamed" });
        await Assert.ThrowsAsync<CodingScheduleConflictException>(() => scheduler.DeleteAsync(created.Id, created.Revision));
        Assert.NotNull(await scheduler.GetAsync(created.Id));
    }

    [Fact]
    public async Task Definitions_are_validated()
    {
        await using var scheduler = NewScheduler();
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0), title: " ")));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(Draft(ScheduleRecurrence.Every(1))));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)) with { RepositoryRoot = Path.Combine(_tmp, "missing") }));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)) with { RepositoryRoot = "relative/path" }));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(Draft(ScheduleRecurrence.DailyAt(8, 0)) with { Prompt = "" }));
        Assert.Empty(await scheduler.ListAsync());
    }

    // ---- tools ---------------------------------------------------------------------------------------

    private static async Task<SchedulesResult> Call(IReadOnlyList<ToolDescriptor> tools, string id, object input)
        => (SchedulesResult)(await tools.Single(t => t.ToolId == id).Invoke(input, default))!;

    [Fact]
    public async Task Tools_act_for_the_calling_conversation_and_default_to_its_repository()
    {
        await using var scheduler = NewScheduler();
        string? conversation = null;
        var tools = ScheduleTools.Create(scheduler, () => conversation, () => _repo);

        var outside = await Call(tools, ScheduleTools.CreateToolId, new SchedulesCreateInput("Tests", "Run the tests.", "weekdays", "08:00"));
        Assert.False(outside.Success);

        conversation = "person-session";
        var bad = await Call(tools, ScheduleTools.CreateToolId, new SchedulesCreateInput("Tests", "Run the tests.", "fortnightly"));
        Assert.False(bad.Success);
        var created = await Call(tools, ScheduleTools.CreateToolId, new SchedulesCreateInput("Tests", "Run the tests.", "weekdays", "08:00"));
        Assert.True(created.Success, created.Message);
        var view = Assert.Single(created.Schedules);
        Assert.Equal(_repo, view.RepositoryRoot);
        Assert.Equal("0 8 * * 1-5", view.Cron);
        Assert.Equal("person-session", (await scheduler.GetAsync(view.ScheduleId))!.CreatedBySessionId);

        var paused = await Call(tools, ScheduleTools.UpdateToolId, new SchedulesUpdateInput(view.ScheduleId, Enabled: false));
        Assert.True(paused.Success, paused.Message);
        Assert.False(paused.Schedules[0].Enabled);
        var stale = await Call(tools, ScheduleTools.UpdateToolId, new SchedulesUpdateInput(view.ScheduleId, Title: "x", Revision: view.Revision));
        Assert.False(stale.Success); // revision 1 is no longer current

        var started = await Call(tools, ScheduleTools.RunNowToolId, new SchedulesIdInput(view.ScheduleId));
        Assert.True(started.Success, started.Message);
        Assert.Equal(_host.SessionIds[0], started.SessionId);
        Assert.True(_host.Requests[0].Manual);

        // The scheduled run's own unattended turn may look, but never create, change, start or delete schedules.
        conversation = started.SessionId;
        Assert.True((await Call(tools, ScheduleTools.ListToolId, new SchedulesListInput())).Success);
        Assert.False((await Call(tools, ScheduleTools.CreateToolId, new SchedulesCreateInput("More", "Again.", "daily", "09:00"))).Success);
        Assert.False((await Call(tools, ScheduleTools.UpdateToolId, new SchedulesUpdateInput(view.ScheduleId, Enabled: true))).Success);
        Assert.False((await Call(tools, ScheduleTools.DeleteToolId, new SchedulesIdInput(view.ScheduleId))).Success);
        Assert.False((await Call(tools, ScheduleTools.RunNowToolId, new SchedulesIdInput(view.ScheduleId))).Success);
        Assert.Single(await scheduler.ListAsync());

        conversation = "person-session";
        Assert.True((await Call(tools, ScheduleTools.DeleteToolId, new SchedulesIdInput(view.ScheduleId))).Success);
        Assert.Empty(await scheduler.ListAsync());
    }

    [Fact]
    public void Schedule_tools_are_opt_in_and_not_coding_harness_tools()
    {
        foreach (var id in ScheduleTools.AllToolIds) Assert.DoesNotContain(id, CodingToolRegistrar.CodingToolIds);

        var without = new ServiceCollection().AddSwarmCoreCodingScheduleTools();
        Assert.Empty(without);

        var services = new ServiceCollection();
        services.AddSingleton(_ => NewScheduler());
        services.AddSwarmCoreCodingScheduleTools();
        services.AddSwarmCoreCodingScheduleTools(); // idempotent
        Assert.Single(services, d => d.ImplementationType == typeof(ScheduleToolRegistration));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeRunHost : IScheduledRunHost
    {
        private readonly List<TaskCompletionSource<ScheduledRunResult>> _runs = [];
        public List<ScheduledRunRequest> Requests { get; } = [];
        public List<string> SessionIds { get; } = [];

        public Task<ScheduledRunHandle> StartRunAsync(ScheduledRunRequest request, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<ScheduledRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_runs)
            {
                Requests.Add(request);
                SessionIds.Add("run-" + Requests.Count);
                _runs.Add(completion);
                return Task.FromResult(new ScheduledRunHandle(SessionIds[^1], completion.Task));
            }
        }

        public void Complete(int index, ScheduledRunResult result) => _runs[index].TrySetResult(result);
    }
}
