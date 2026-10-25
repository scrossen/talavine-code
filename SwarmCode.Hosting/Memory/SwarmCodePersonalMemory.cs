using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Memory;
using SwarmCore.PersonalMemory;

namespace SwarmCode.Hosting.Memory;

/// <summary>Who SwarmCode's personal memory is about: the signed-in OS user of this data directory.</summary>
public static class SwarmCodeMemoryScope
{
    public const string Tenant = "local";
    public const string Partition = "swarmcode";

    /// <summary>
    /// A stable, opaque id for the current OS account (a hash, so the user name is not stored in every row). The data
    /// directory is already per user; the id keeps rows attributable if a data directory is ever shared or moved.
    /// </summary>
    public static MemoryScope ForCurrentUser()
    {
        var user = (Environment.UserName ?? "user").Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("swarmcode-user\n" + user))).ToLowerInvariant()[..16];
        return new MemoryScope(Tenant, "os-" + hash, Partition);
    }
}

/// <summary>The person's own on/off switch (Settings &gt; Memory), stored in <see cref="AppPreferences.PersonalMemoryEnabled"/>.</summary>
public sealed class SwarmCodeMemoryAvailability(AppPreferencesStore? preferences) : IPersonalMemoryAvailability
{
    public ValueTask<bool> IsEnabledAsync(MemoryScope scope, CancellationToken ct = default)
        => ValueTask.FromResult(preferences?.Current.PersonalMemoryEnabled ?? true);
}

/// <summary>
/// The memory context of the turns running in one workspace. A turn registers its context (scope, temporary flag, the
/// person's own message) for its duration and sets it as the ambient context, so the recall injector and the memory tools
/// of API lanes see it. The native-lane bridge, which runs in another process, resolves the single active turn here.
/// Also tracks activity so background consolidation ("dreaming") only runs while the workspace is idle.
/// </summary>
public sealed class SwarmCodeMemoryTurns(MemoryScope scope, TimeProvider? time = null) : IPersonalMemoryTurnContextProvider
{
    private readonly ConcurrentDictionary<string, MemoryTurnContext> _active = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _dirtyGeneration;
    private long _lastActivityTicks = (time ?? TimeProvider.System).GetUtcNow().UtcTicks;

    public MemoryScope Scope { get; } = scope;

    public int ActiveCount => _active.Count;

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>Incremented whenever new claims may be waiting for consolidation.</summary>
    public long DirtyGeneration => Interlocked.Read(ref _dirtyGeneration);

    public void MarkDirty() { Interlocked.Increment(ref _dirtyGeneration); Touch(); }

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);

    /// <summary>The context for one turn of a conversation.</summary>
    public MemoryTurnContext Create(string conversationId, bool temporary, string? authenticatedUserMessage, string turnId)
        => new(Scope, temporary, authenticatedUserMessage, conversationId, turnId);

    /// <summary>Registers the turn and makes it ambient until disposed. Call on the task that runs the turn.</summary>
    public IDisposable Begin(MemoryTurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = context.ConversationId ?? context.TurnId ?? Guid.NewGuid().ToString("N");
        _active[key] = context;
        Touch();
        var ambient = AmbientMemoryTurnContext.Begin(context);
        return new Registration(this, key, context, ambient);
    }

    public MemoryTurnContext? Resolve(ContextInjectionRequest? request)
    {
        var ambient = AmbientMemoryTurnContext.Current;
        if (request is null) return ambient is not null && ambient.Scope == Scope ? ambient : null;
        if (ambient is not null && ambient.Scope == Scope && string.Equals(ambient.ConversationId, request.ConversationId, StringComparison.Ordinal)) return ambient;
        return _active.TryGetValue(request.ConversationId, out var registered) ? registered : null;
    }

    /// <summary>
    /// Makes the conversation's running turn ambient for one call a native agent made through the host tool bridge (its
    /// authenticated message and temporary flag apply), or returns null when that conversation has no running turn.
    /// </summary>
    public IDisposable? BeginBridgedCall(string conversationId)
        => _active.TryGetValue(conversationId, out var turn) ? AmbientMemoryTurnContext.Begin(turn) : null;

    private sealed class Registration(SwarmCodeMemoryTurns owner, string key, MemoryTurnContext context, IDisposable ambient) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._active.TryRemove(new KeyValuePair<string, MemoryTurnContext>(key, context));
            owner.Touch();
            ambient.Dispose();
        }
    }
}

/// <summary>
/// The memory tools for SwarmCode lanes (API lanes through the capability catalog, native lanes through the bridge) and a
/// router-less <see cref="PersonalMemoryService"/> for controls. Forget and "don't bring this up" require the person's own
/// message for the turn to ask for it (the shared <see cref="MemoryUserIntent"/> guard every host's tools carry), so text
/// the agent read elsewhere cannot erase or hide memories.
/// </summary>
public sealed class SwarmCodeMemoryTools
{
    public SwarmCodeMemoryTools(
        IPersonalMemoryStore store,
        PersonalMemoryOptions options,
        IPersonalMemoryAvailability availability,
        IPersonalMemoryTurnContextProvider turns,
        PersonalMemoryWriteGate gate,
        ILoggerFactory? loggers = null,
        TimeProvider? time = null)
    {
        Store = store;
        Turns = turns;
        var consolidation = new MemoryConsolidationService(store, options, availability, router: null, time,
            loggers?.CreateLogger<MemoryConsolidationService>(), gate);
        Service = new PersonalMemoryService(store, consolidation, options, availability, router: null, time, loggers?.CreateLogger<PersonalMemoryService>());
        Descriptors = PersonalMemoryTools.Create(Service, store, turns, new PersonalMemoryToolOptions { RequireUserRequestForForgetAndHide = true }).ToList();
    }

    public IPersonalMemoryStore Store { get; }
    public IPersonalMemoryTurnContextProvider Turns { get; }

    /// <summary>Controls (inventory, edit, suppress, forget, suggestions) and deterministic remember. No LLM.</summary>
    public PersonalMemoryService Service { get; }

    public IReadOnlyList<ToolDescriptor> Descriptors { get; }

    public ToolDescriptor? Find(string toolId) => Descriptors.FirstOrDefault(d => string.Equals(d.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the person's own message for the turn asks to forget (or to stop mentioning) something.</summary>
    public static bool UserAsked(string? userMessage, bool forget)
        => forget ? MemoryUserIntent.AsksToForget(userMessage) : MemoryUserIntent.AsksToHide(userMessage);
}

/// <summary>Post-turn capture: runs the shared extraction over the person's own message, off the UI thread.</summary>
public sealed class SwarmCodeMemoryCapture(IServiceScopeFactory scopes, SwarmCodeMemoryTurns turns, ILogger<SwarmCodeMemoryCapture>? logger = null) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// Starts capture for a finished turn. Only the person's typed message is considered (never repository files, tool
    /// output or the assistant's answer, which are third-party or agent text). No-op for a temporary turn.
    /// </summary>
    public Task Schedule(MemoryTurnContext turn)
    {
        if (turn.Incognito || string.IsNullOrWhiteSpace(turn.AuthenticatedUserMessage) || _lifetime.IsCancellationRequested) return Task.CompletedTask;
        var task = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PersonalMemoryService>();
                var results = await service.CaptureTurnAsync(turn, _lifetime.Token).ConfigureAwait(false);
                var dirty = results.Any(r => r.Captured && !r.Duplicate);
                // The shared "recent conversations" summary (every two turns of a session).
                if (scope.ServiceProvider.GetService<MemoryConversationSummarizer>() is { } summarizer
                    && await summarizer.ObserveTurnAsync(service, turn, _lifetime.Token).ConfigureAwait(false) is { Captured: true, Duplicate: false })
                    dirty = true;
                if (dirty) turns.MarkDirty();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning("Personal memory capture failed ({Error}); the turn is unaffected.", ex.GetType().Name);
            }
        });
        _pending[task] = 0;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
        return task;
    }

    /// <summary>Waits for scheduled captures (tests and shutdown).</summary>
    public Task DrainAsync() => Task.WhenAll(_pending.Keys.ToArray());

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>Timing for background consolidation (<c>PersonalMemory:Dreaming</c>).</summary>
public sealed class SwarmCodeDreamingOptions
{
    /// <summary>First pass after the workspace starts (catches claims left pending by a previous run).</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long the workspace must be idle (no running turn) before new claims are consolidated.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often an idle workspace also runs a time-only pass (past plans, stale facts, old summaries) even when no new
    /// claims arrived, so "is going to Singapore" becomes "went to Singapore" without new activity.
    /// </summary>
    public TimeSpan TimeSweepInterval { get; set; } = TimeSpan.FromDays(1);

    public int MaxScopesPerRun { get; set; } = 4;
}

/// <summary>
/// "Dreaming": consolidates pending claims into facts in the background on start and whenever the workspace is idle after
/// new claims arrived. One pass at a time per process (several workspaces share one memory file); the store's version
/// checks keep a concurrent console or second app instance safe.
/// </summary>
public sealed class SwarmCodeMemoryMaintenance(
    IServiceScopeFactory scopes,
    SwarmCodeMemoryTurns turns,
    PersonalMemoryOptions options,
    SwarmCodeDreamingOptions dreaming,
    ILogger<SwarmCodeMemoryMaintenance>? logger = null,
    TimeProvider? time = null,
    IEnumerable<IIdleMaintenanceStep>? steps = null) : IHostedService, IAsyncDisposable
{
    private static readonly SemaphoreSlim ProcessWide = new(1, 1);
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IReadOnlyList<IIdleMaintenanceStep> _steps = steps?.ToArray() ?? [];
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private DateTimeOffset _started;
    private bool _startupPassDone;
    private long _consolidatedGeneration;
    private DateTimeOffset _lastTimeSweep = DateTimeOffset.MinValue;

    public int Runs { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = _time.GetUtcNow();
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stop = _stop;
        if (stop is null || Volatile.Read(ref _disposed) != 0) return;
        try { await stop.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>True when a pass is due now.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (!options.Enabled || turns.ActiveCount > 0) return false;
        if (!_startupPassDone) return now - _started >= dreaming.StartupDelay;
        if (now - turns.LastActivity < dreaming.IdleDelay) return false;
        return turns.DirtyGeneration != Interlocked.Read(ref _consolidatedGeneration) || IsTimeSweepDue(now);
    }

    private bool IsTimeSweepDue(DateTimeOffset now) => now - _lastTimeSweep >= dreaming.TimeSweepInterval;

    /// <summary>
    /// True while no turn is running and the workspace has been quiet for the idle delay (after the startup delay). The
    /// idle steps (goal receipt compaction and the like) run on this alone; they do not depend on memory being enabled.
    /// </summary>
    public bool IsIdle(DateTimeOffset now)
        => turns.ActiveCount == 0 && now - _started >= dreaming.StartupDelay && now - turns.LastActivity >= dreaming.IdleDelay;

    /// <summary>The idle steps that are due now, in registration order.</summary>
    public IReadOnlyList<IIdleMaintenanceStep> DueSteps(DateTimeOffset now)
        => IsIdle(now) ? _steps.Where(step => step.IsDue(now)).ToArray() : [];

    /// <summary>Runs each due idle step once; a failing step is logged and does not stop the others.</summary>
    public async Task<int> RunDueStepsAsync(CancellationToken ct)
    {
        var ran = 0;
        foreach (var step in DueSteps(_time.GetUtcNow()))
        {
            ct.ThrowIfCancellationRequested();
            // Re-check between steps: a turn may have started while the previous step ran.
            if (turns.ActiveCount > 0) break;
            try
            {
                await step.RunAsync(ct).ConfigureAwait(false);
                ran++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Idle maintenance step {Step} failed ({Error}); it will be tried again when due.", step.Name, ex.GetType().Name);
            }
        }
        return ran;
    }

    /// <summary>One consolidation pass (no-op when another pass is running in this process).</summary>
    public async Task<IReadOnlyList<MemoryConsolidationReport>> RunOnceAsync(CancellationToken ct)
    {
        if (!await ProcessWide.WaitAsync(0, ct).ConfigureAwait(false)) return [];
        try
        {
            var generation = turns.DirtyGeneration;
            var now = _time.GetUtcNow();
            var sweep = IsTimeSweepDue(now);
            using var scope = scopes.CreateScope();
            var consolidation = scope.ServiceProvider.GetRequiredService<MemoryConsolidationService>();
            var reports = await consolidation.ConsolidatePendingAsync(Math.Max(1, dreaming.MaxScopesPerRun), includeTimeDueScopes: sweep, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _consolidatedGeneration, generation);
            if (sweep) _lastTimeSweep = now;
            _startupPassDone = true;
            Runs++;
            foreach (var report in reports.Where(r => !r.Skipped))
                _logger.LogInformation("Personal memory consolidated: {Added} added, {Updated} updated, {Superseded} superseded, {Expired} expired, {Rejected} rejected, {Suggested} suggestions{Llm}.",
                    report.Added, report.Updated, report.Superseded, report.Expired, report.Rejected, report.AwaitingConfirmation + report.AwaitingCorroboration,
                    report.LlmUsed ? " (model-assisted)" : report.LlmError is null ? "" : " (deterministic; model unavailable)");
            return reports;
        }
        finally { ProcessWide.Release(); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(dreaming.PollInterval, _time, ct).ConfigureAwait(false);
                if (IsDue(_time.GetUtcNow())) await RunOnceAsync(ct).ConfigureAwait(false);
                await RunDueStepsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _startupPassDone = true; // do not retry a failing startup pass in a tight loop
                _logger.LogWarning("Personal memory consolidation failed ({Error}); it will retry when new memories arrive.", ex.GetType().Name);
            }
        }
    }

    // The provider can dispose this instance twice (as itself and as the hosted service it is registered as).
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop?.Dispose();
    }
}

/// <summary>Publishes the personal-memory and repository-memory tools to the capability catalog (API lanes).</summary>
public sealed class SwarmCodeMemoryToolRegistration(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        foreach (var descriptor in services.GetRequiredService<SwarmCodeMemoryTools>().Descriptors) catalog.Register(descriptor);
        var coding = services.GetRequiredService<IOptions<CodingOptions>>();
        foreach (var descriptor in RepoMemoryTools.Create(
                     () => coding.Value.RepositoryRoot,
                     () => coding.Value.RepoMemory,
                     services.GetRequiredService<IRepoMemoryProposalStore>()))
            catalog.Register(descriptor);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class SwarmCodePersonalMemoryRegistration
{
    public const string ConfigurationSection = "PersonalMemory";

    /// <summary>One write gate per process: every workspace and the settings page share one memory file.</summary>
    public static PersonalMemoryWriteGate SharedGate { get; } = new();

    /// <summary>Binds <see cref="PersonalMemoryOptions"/> from <c>PersonalMemory</c> (the admin/global switch is <c>PersonalMemory:Enabled</c>).</summary>
    public static PersonalMemoryOptions BindOptions(IConfiguration? configuration)
    {
        var options = new PersonalMemoryOptions();
        configuration?.GetSection(ConfigurationSection).Bind(options);
        return options;
    }

    /// <summary>
    /// SwarmCode's router has no automatic selection, so memory calls (extraction, summaries, dreaming) need a route.
    /// An explicit <c>PersonalMemory:RouteProvider/RouteModel</c> wins; otherwise the person's own default lane is used
    /// (or the highest-priority lane) when it is a router route. Native agent lanes run their own loop and can't serve
    /// these calls.
    /// </summary>
    public static PersonalMemoryOptions WithDefaultRoute(PersonalMemoryOptions options, SwarmCore.Coding.Routing.CodingLaneOptions? lanes)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.RouteProvider) && !string.IsNullOrWhiteSpace(options.RouteModel)) return options;
        if (lanes is null || lanes.Lanes.Count == 0) return options;
        var routable = lanes.Lanes.Where(l => !l.IsNative && !string.IsNullOrWhiteSpace(l.Provider) && !string.IsNullOrWhiteSpace(l.Model)).ToList();
        var lane = routable.FirstOrDefault(l => string.Equals(l.Name, lanes.DefaultLane, StringComparison.OrdinalIgnoreCase))
                   ?? routable.OrderBy(l => l.Priority).FirstOrDefault();
        if (lane is null) return options;
        options.RouteProvider = lane.Provider;
        options.RouteModel = lane.Model;
        return options;
    }

    /// <summary>
    /// Personal memory and repository memory for a SwarmCode workspace: the file store under SWARMCODE_HOME (a host may
    /// pre-register a shared instance), the per-user switch, turn contexts, recall injectors for API and native lanes, the
    /// tools, post-turn capture, background consolidation, the native-lane bridge endpoint, and durable proposals.
    /// Call before <c>AddSwarmCoreCoding</c> so the durable proposal store wins over the harness default.
    /// </summary>
    public static IServiceCollection AddSwarmCodePersonalMemory(this IServiceCollection services, SwarmCodeDataPaths paths, IConfiguration? configuration)
    {
        services.TryAddSingleton<IPersonalMemoryStore>(_ => new FilePersonalMemoryStore(paths));
        services.TryAddSingleton(SharedGate);
        services.TryAddSingleton<IPersonalMemoryAvailability>(sp => new SwarmCodeMemoryAvailability(sp.GetService<AppPreferencesStore>()));
        services.TryAddSingleton(sp => new SwarmCodeMemoryTurns(SwarmCodeMemoryScope.ForCurrentUser(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPersonalMemoryTurnContextProvider>(sp => sp.GetRequiredService<SwarmCodeMemoryTurns>());
        services.TryAddSingleton(sp => WithDefaultRoute(BindOptions(configuration),
            sp.GetService<Microsoft.Extensions.Options.IOptions<SwarmCore.Coding.Routing.CodingLaneOptions>>()?.Value));
        services.AddSwarmPersonalMemory();
        services.TryAddSingleton(sp => new SwarmCodeMemoryTools(
            sp.GetRequiredService<IPersonalMemoryStore>(),
            sp.GetRequiredService<PersonalMemoryOptions>(),
            sp.GetRequiredService<IPersonalMemoryAvailability>(),
            sp.GetRequiredService<IPersonalMemoryTurnContextProvider>(),
            sp.GetRequiredService<PersonalMemoryWriteGate>(),
            sp.GetService<ILoggerFactory>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<SwarmCodeMemoryCapture>();
        services.TryAddSingleton(_ =>
        {
            var dreaming = new SwarmCodeDreamingOptions();
            configuration?.GetSection(ConfigurationSection + ":Dreaming").Bind(dreaming);
            return dreaming;
        });
        services.TryAddSingleton(sp => new SwarmCodeMemoryMaintenance(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<SwarmCodeMemoryTurns>(), sp.GetRequiredService<PersonalMemoryOptions>(),
            sp.GetRequiredService<SwarmCodeDreamingOptions>(), sp.GetService<ILogger<SwarmCodeMemoryMaintenance>>(), sp.GetService<TimeProvider>(),
            sp.GetServices<IIdleMaintenanceStep>()));
        services.AddHostedService(sp => sp.GetRequiredService<SwarmCodeMemoryMaintenance>());
        services.AddHostedService<SwarmCodeMemoryToolRegistration>();

        services.TryAddSingleton<IRepoMemoryProposalStore>(_ => new FileRepoMemoryProposalStore(paths));

        // API lanes run these injectors after the repository instructions; native lanes get the same blocks in their prompt.
        services.PostConfigure<CodingOptions>(coding =>
        {
            AddName(coding.AdditionalInjectorNames, RepoMemoryIndexInjector.InjectorName);
            AddName(coding.AdditionalInjectorNames, PersonalMemoryContextInjector.DefaultName);
            // Native lanes share one budget, filled in order: the small, bounded personal digest first, then the index.
            AddName(coding.NativeLaneInjectorNames, PersonalMemoryContextInjector.DefaultName);
            AddName(coding.NativeLaneInjectorNames, RepoMemoryIndexInjector.InjectorName);
        });
        return services;
    }

    private static void AddName(List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }
}
