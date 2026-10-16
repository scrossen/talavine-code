using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;

namespace SwarmCore.Coding.Scheduling;

public sealed record SchedulesCreateInput(
    string Title,
    string Prompt,
    string? Preset = null,
    string? Time = null,
    int? Minute = null,
    int? IntervalMinutes = null,
    string? Cron = null,
    string? RepositoryRoot = null,
    string? Lane = null,
    string? Model = null,
    bool Enabled = true,
    bool AllowNativeLanes = true);

public sealed record SchedulesListInput(bool IncludeDisabled = true);

public sealed record SchedulesUpdateInput(
    string ScheduleId,
    string? Title = null,
    string? Prompt = null,
    string? Preset = null,
    string? Time = null,
    int? Minute = null,
    int? IntervalMinutes = null,
    string? Cron = null,
    string? Lane = null,
    string? Model = null,
    bool? Enabled = null,
    bool? AllowNativeLanes = null,
    long? Revision = null);

public sealed record SchedulesIdInput(string ScheduleId);

/// <summary>What the model sees of a schedule. Times are in the schedule's zone (local by default).</summary>
public sealed record ScheduleView(
    string ScheduleId, string Title, string Prompt, string RepositoryRoot, string Recurrence, string? Cron, bool Enabled,
    string? NextRun, string? LastRun, string LastOutcome, string? LastNote, string? LastSessionId, bool Running,
    string? Lane, string? Model, bool AllowNativeLanes, long Revision)
{
    public static ScheduleView From(CodingSchedule s, CodingScheduler scheduler)
    {
        var zone = scheduler.ZoneOf(s);
        string? Local(DateTime? utc) => utc is { } u
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm") : null;
        return new(s.Id, s.Title, s.Prompt, s.RepositoryRoot, s.Recurrence.Describe(), s.Recurrence.ToCron(), s.Enabled,
            Local(s.Run.NextRunUtc), Local(s.Run.LastRunUtc), s.Run.LastOutcome.ToString().ToLowerInvariant(), s.Run.LastNote,
            s.Run.LastSessionId, scheduler.IsRunning(s.Id), s.Lane, s.Model, s.AllowNativeLanes, s.Revision);
    }
}

public sealed record SchedulesResult(bool Success, string? Message, IReadOnlyList<ScheduleView> Schedules, string? SessionId = null)
{
    public static SchedulesResult Fail(string message) => new(false, message, []);
}

/// <summary>
/// The <c>schedules.*</c> tools: the person's recurring runs (a new session in a repository at each occurrence).
/// <paramref name="conversation"/> resolves the trusted calling conversation; a scheduled run's own unattended turn may
/// list schedules but never create, change, delete or start one. The repository defaults to the conversation's
/// workspace. Opt-in: these are not coding harness tools, so a host registers them explicitly
/// (<see cref="ScheduleServiceCollectionExtensions.AddSwarmCoreCodingScheduleTools"/>).
/// </summary>
public static class ScheduleTools
{
    public const string OwnerAgentId = "coding.schedules";
    public const string CreateToolId = "schedules.create";
    public const string ListToolId = "schedules.list";
    public const string UpdateToolId = "schedules.update";
    public const string DeleteToolId = "schedules.delete";
    public const string RunNowToolId = "schedules.run_now";
    public static readonly IReadOnlyList<string> AllToolIds = [CreateToolId, ListToolId, UpdateToolId, DeleteToolId, RunNowToolId];

    private const string RecurrenceHelp = "Recurrence: preset daily|weekdays (with time \"HH:mm\", 24-hour, local time), hourly (minute past the hour), "
        + "every_minutes (intervalMinutes), or cron (a 5-field expression in local time).";

    public static IReadOnlyList<ToolDescriptor> Create(CodingScheduler scheduler, Func<string?> conversation, Func<string?> defaultRepositoryRoot) =>
    [
        ToolDescriptor.Create<SchedulesCreateInput, SchedulesResult>(CreateToolId, OwnerAgentId,
            "Create a recurring run for the person: at each occurrence Talavine Code starts a new session in the repository with prompt "
            + "(for example every weekday at 08:00 \"run the tests and report failures\"). prompt must be complete and self-contained: "
            + "each run starts fresh and cannot see this conversation. " + RecurrenceHelp + " repositoryRoot defaults to this "
            + "workspace. Only create a schedule the person asked for.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (input, ct) => Mutate(scheduler, conversation, async id =>
            {
                var (recurrence, error) = ScheduleRecurrence.Parse(input.Preset, input.Time, input.Minute, input.IntervalMinutes, input.Cron);
                if (recurrence is null) return SchedulesResult.Fail(error!);
                var root = string.IsNullOrWhiteSpace(input.RepositoryRoot) ? defaultRepositoryRoot() : input.RepositoryRoot;
                var created = await scheduler.CreateAsync(new CodingScheduleEdit
                {
                    Title = input.Title, Prompt = input.Prompt, RepositoryRoot = root, Lane = input.Lane, Model = input.Model,
                    Recurrence = recurrence, Enabled = input.Enabled, AllowNativeLanes = input.AllowNativeLanes,
                }, id, ct).ConfigureAwait(false);
                return new SchedulesResult(true, "Created. The person can see and change it on the Schedules page.", [ScheduleView.From(created, scheduler)]);
            }),
            keywords: ["schedule", "recurring", "cron", "every day", "weekday", "daily", "hourly", "timer", "automation", "later"]),
        ToolDescriptor.Create<SchedulesListInput, SchedulesResult>(ListToolId, OwnerAgentId,
            "List the person's scheduled runs: recurrence in words, next and last run, last outcome and its session.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            async (input, ct) =>
            {
                if (conversation() is not { Length: > 0 }) return SchedulesResult.Fail("Schedules can be used only inside a conversation turn.");
                var all = await scheduler.ListAsync(ct).ConfigureAwait(false);
                var shown = all.Where(s => input.IncludeDisabled || s.Enabled).Select(s => ScheduleView.From(s, scheduler)).ToList();
                return new SchedulesResult(true, shown.Count == 0 ? "No schedules." : null, shown);
            },
            keywords: ["schedule", "list", "recurring", "cron"]),
        ToolDescriptor.Create<SchedulesUpdateInput, SchedulesResult>(UpdateToolId, OwnerAgentId,
            "Change a schedule: enable/disable it, or edit its title, prompt, lane, model or recurrence (" + RecurrenceHelp + ") "
            + "Fields left out stay as they are. Pass revision from schedules.list to refuse the edit if it changed meanwhile.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (input, ct) => Mutate(scheduler, conversation, async _ =>
            {
                ScheduleRecurrence? recurrence = null;
                if (input.Preset is not null || input.Cron is not null || input.IntervalMinutes is not null || input.Time is not null || input.Minute is not null)
                {
                    var (parsed, error) = ScheduleRecurrence.Parse(input.Preset, input.Time, input.Minute, input.IntervalMinutes, input.Cron);
                    if (parsed is null) return SchedulesResult.Fail(error!);
                    recurrence = parsed;
                }
                var revision = input.Revision ?? (await scheduler.GetAsync(input.ScheduleId, ct).ConfigureAwait(false))?.Revision
                    ?? throw new CodingScheduleNotFoundException("That schedule does not exist.");
                var updated = await scheduler.UpdateAsync(input.ScheduleId, revision, new CodingScheduleEdit
                {
                    Title = input.Title, Prompt = input.Prompt, Lane = input.Lane, Model = input.Model, Recurrence = recurrence,
                    Enabled = input.Enabled, AllowNativeLanes = input.AllowNativeLanes,
                }, ct).ConfigureAwait(false);
                return new SchedulesResult(true, "Updated.", [ScheduleView.From(updated, scheduler)]);
            }),
            keywords: ["schedule", "edit", "enable", "disable", "pause"]),
        ToolDescriptor.Create<SchedulesIdInput, SchedulesResult>(DeleteToolId, OwnerAgentId,
            "Delete a schedule the person no longer wants (its past sessions stay).",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (input, ct) => Mutate(scheduler, conversation, async _ =>
                await scheduler.DeleteAsync(input.ScheduleId, null, ct).ConfigureAwait(false)
                    ? new SchedulesResult(true, "Deleted.", [])
                    : SchedulesResult.Fail("That schedule does not exist.")),
            keywords: ["schedule", "delete", "remove"]),
        ToolDescriptor.Create<SchedulesIdInput, SchedulesResult>(RunNowToolId, OwnerAgentId,
            "Start a schedule's run now in its own new session (the recurrence is unchanged). Returns the session id.",
            ToolCostClass.Expensive, ToolSideEffect.Exec,
            (input, ct) => Mutate(scheduler, conversation, async _ =>
            {
                var start = await scheduler.RunNowAsync(input.ScheduleId, ct).ConfigureAwait(false);
                var schedule = await scheduler.GetAsync(input.ScheduleId, ct).ConfigureAwait(false);
                return new SchedulesResult(start.Started, start.Message,
                    schedule is null ? [] : [ScheduleView.From(schedule, scheduler)], start.SessionId);
            }),
            keywords: ["schedule", "run", "now", "trigger"]),
    ];

    private static async Task<SchedulesResult> Mutate(CodingScheduler scheduler, Func<string?> conversation, Func<string, Task<SchedulesResult>> call)
    {
        if (conversation() is not { Length: > 0 } id) return SchedulesResult.Fail("Schedules can be used only inside a conversation turn.");
        if (scheduler.IsScheduledRunSession(id))
            return SchedulesResult.Fail("A scheduled run cannot create, change, delete or start schedules. Report what should change instead.");
        try { return await call(id).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return SchedulesResult.Fail(ex.Message); }
    }
}

/// <summary>Publishes the <c>schedules.*</c> tools for engine lanes when the host registered a <see cref="CodingScheduler"/>.</summary>
public sealed class ScheduleToolRegistration(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<CodingScheduler>() is not { } scheduler) return Task.CompletedTask;
        var turns = services.GetRequiredService<ICurrentTurnContextAccessor>();
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        foreach (var tool in ScheduleTools.Create(scheduler, () => turns.Current?.ConversationId,
                     () => turns.Current?.WorkspaceRoot ?? services.GetService<IOptions<CodingOptions>>()?.Value.RepositoryRoot))
            catalog.Register(tool);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ScheduleServiceCollectionExtensions
{
    /// <summary>
    /// Opt-in <c>schedules.*</c> tools for engine lanes (native lanes reach them through the host tool bridge). Requires a <see cref="CodingScheduler"/>
    /// registered earlier (hosts keep one scheduler above their workspaces); without one nothing is registered.
    /// Deliberately not part of <see cref="CodingToolRegistrar.CodingToolIds"/>.
    /// </summary>
    public static IServiceCollection AddSwarmCoreCodingScheduleTools(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(CodingScheduler))) return services;
        if (services.Any(d => d.ImplementationType == typeof(ScheduleToolRegistration))) return services;
        services.AddHostedService<ScheduleToolRegistration>();
        return services;
    }
}
