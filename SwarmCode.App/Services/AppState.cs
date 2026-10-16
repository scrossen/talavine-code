using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCode.Hosting;
using SwarmCode.Hosting.Llm;
using SwarmCode.Hosting.Secrets;
using SwarmCore.Agent.Runtime;
using SwarmCore.Secrets;
using SwarmCore.Services.Abstractions;

namespace SwarmCode.App.Services;

/// <summary>
/// App-level state shared by every Blazor component: the open workspace (one repository,
/// one SwarmCore runtime), the session catalog, per-session conversation runners, the
/// protected secret vault, and preferences. Runners outlive page navigation so a turn
/// keeps running while the user looks at another session.
/// </summary>
public sealed class AppState : IAsyncDisposable, SwarmCode.Hosting.Agents.IConversationRunnerSource, SwarmCore.Coding.SelfImprovement.ISelfImprovementActivitySource
{
    /// <summary>The app's state once built (crash handlers report through it without resolving services).</summary>
    internal static AppState? Instance { get; private set; }

    private readonly Dictionary<string, ConversationRunner> _runners = new();
    private readonly object _gate = new();
    private readonly UiPermissionBroker _broker = new();
    private readonly ServiceProvider _secretServices;
    private readonly KeyStoreBrokerServer? _vaultBroker;

    public AppState(SwarmCodeDataPaths paths, AppPreferencesStore preferences, WorkspaceSessionCatalog sessions, UserSettingsFile userSettings, ILoggerFactory loggerFactory, IOsKeyStore? keyStore = null)
    {
        Paths = paths;
        Preferences = preferences;
        Sessions = sessions;
        UserSettings = userSettings;
        _settingsActivation.ReloadRequested += RequestSettingsReload;
        // Requests reach the broker only when a turn's permission policy asks (SwarmCodePermissions), so they always
        // wait for the person; in Auto mode nothing is escalated. Agents may run under a stricter inherited mode.
        _broker.AutoApprove = false;

        // The vault lives outside any workspace: Settings and /secret work before a repository
        // is open, and every workspace shares these instances so the console and both heads
        // read one secrets.db protected by one OS keystore.
        var secretServices = new ServiceCollection();
        secretServices.AddSingleton(loggerFactory);
        secretServices.AddLogging();
        if (keyStore is not null) secretServices.AddSingleton(keyStore);
        secretServices.AddSwarmCodeSecrets(paths);
        _secretServices = secretServices.BuildServiceProvider();
        KeyStore = _secretServices.GetRequiredService<IOsKeyStore>();
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            && SwarmCode.Hosting.Secrets.MacVaultBridge.BundledHelper(Environment.ProcessPath ?? AppContext.BaseDirectory) is { } helper)
            _vaultBroker = new KeyStoreBrokerServer(KeyStore, paths.RootDirectory,
                SwarmCode.Hosting.Secrets.MacVaultBridge.HelperPeerPolicy(helper), loggerFactory.CreateLogger<KeyStoreBrokerServer>());
        Vault = _secretServices.GetRequiredService<ISecretVault>();
        SecretIntake = _secretServices.GetRequiredService<SecretIntake>();
        PasswordImporter = _secretServices.GetRequiredService<PasswordCsvImporter>();
        var vaultKeys = _secretServices.GetRequiredService<VaultApiKeyProvider>();
        var credentialStore = _secretServices.GetRequiredService<ICredentialStore>();
        var protector = _secretServices.GetRequiredService<ISecretProtector>();

        var configuration = SwarmCodeHostComposer.BuildConfiguration(paths);
        var providerOptions = new SwarmCore.LlmRouting.Configuration.LlmProvidersOptions();
        configuration.GetSection(SwarmCore.LlmRouting.Configuration.LlmProvidersOptions.SectionName).Bind(providerOptions);
        ApiKeys = new SwarmCodeApiKeyProvider(vaultKeys, new SettingsApiKeyProvider(new JsonLlmSettingsProvider(paths)), providerOptions,
            loggerFactory.CreateLogger<SwarmCodeApiKeyProvider>());

        // Personal memory and repository-memory proposals live outside any workspace too: Settings > Memory works without
        // an open repository, and every workspace shares the same store instance (one file under SWARMCODE_HOME).
        MemoryStore = new SwarmCode.Hosting.Memory.FilePersonalMemoryStore(paths);
        MemoryOptions = SwarmCode.Hosting.Memory.SwarmCodePersonalMemoryRegistration.BindOptions(configuration);
        MemoryScope = SwarmCode.Hosting.Memory.SwarmCodeMemoryScope.ForCurrentUser();
        var memoryAvailability = new SwarmCode.Hosting.Memory.SwarmCodeMemoryAvailability(preferences);
        Memory = new SwarmCode.Hosting.Memory.SwarmCodeMemoryTools(MemoryStore, MemoryOptions, memoryAvailability,
            new SwarmCore.PersonalMemory.AmbientPersonalMemoryTurnContextProvider(), SwarmCode.Hosting.Memory.SwarmCodePersonalMemoryRegistration.SharedGate,
            loggerFactory).Service;
        RepoMemoryProposals = new SwarmCode.Hosting.Memory.FileRepoMemoryProposalStore(paths);

        // Saved sessions across every repository, for the History view: search works without an open workspace;
        // a model-written summary needs the open workspace's lanes (WorkSummaryService lives in its services).
        History = new ConversationHistorySource(paths, sessions);

        // One agent team for the app: the Agents view lists agents across workspace switches, and each open workspace
        // binds its session host to it while it runs.
        var agentOptions = new SwarmCore.Coding.Agents.AgentTeamOptions();
        configuration.GetSection(SwarmCore.Coding.Agents.AgentTeamOptions.SectionName).Bind(agentOptions);
        Agents = new SwarmCore.Coding.Agents.AgentTeam(new SwarmCore.Coding.Agents.JsonFileAgentTeamStore(paths.AgentsDirectory),
            () => agentOptions, loggerFactory.CreateLogger<SwarmCore.Coding.Agents.AgentTeam>());

        // Scheduled runs are app level: a schedule targets its own repository and fires whichever repository is open
        // (in the open workspace when it matches, otherwise in a workspace opened for the run in the background).
        ScheduledRuns = new SwarmCode.Hosting.Scheduling.ScheduledRunWorkspaces(OpenScheduledRunnerAsync,
            (root, ct) => WorkspaceHost.CreateAsync(WorkspaceOptions(root, background: true), ConfigureWorkspaceLogging, ct));
        ScheduledRuns.Changed += OnSettingsActivityChanged;
        var scheduleOptions = new SwarmCore.Coding.Scheduling.CodingSchedulerOptions();
        configuration.GetSection(SwarmCore.Coding.Scheduling.CodingSchedulerOptions.SectionName).Bind(scheduleOptions);
        Schedules = new SwarmCore.Coding.Scheduling.CodingScheduler(
            new SwarmCore.Coding.Scheduling.JsonFileCodingScheduleStore(paths.SchedulesDirectory),
            new SwarmCode.Hosting.Scheduling.SwarmCodeScheduledRunHost(sessions, preferences, ScheduledRuns),
            scheduleOptions, logger: loggerFactory.CreateLogger<SwarmCore.Coding.Scheduling.CodingScheduler>());

        // Self-improvement is app level too: one backlog and one worker above every workspace. Workspaces get the shared
        // backlog and reporter (so turns report into it) and the workspace overrides a cycle sets for its session.
        // A developer feature: Debug builds only (SelfImprovementAvailable), and inert without a source checkout.
        if (SelfImprovementAvailable)
        {
            SelfImprovementBacklog = new SwarmCore.Coding.SelfImprovement.JsonFileSelfImprovementBacklog(paths.SelfImprovementDirectory);
            SelfImprovementReporter = new SwarmCore.Coding.SelfImprovement.SelfImprovementReporter(SelfImprovementBacklog, SelfImprovementOptions,
                logger: loggerFactory.CreateLogger<SwarmCore.Coding.SelfImprovement.SelfImprovementReporter>());
            SelfImprovementReporter.Reported += (_, _) => { SelfImprovement?.Wake(); Changed?.Invoke(); };
            SelfImprovementGate = new SwarmCore.Coding.SelfImprovement.SelfImprovementIdleGate(SelfImprovementOptions);
            SelfImprovementGate.AddSource(this);
            WorkspaceOverridesForSessions = new SwarmCode.Hosting.Improvement.SessionWorkspaceOverrides();
            SelfImprovement = new SwarmCore.Coding.SelfImprovement.SelfImprovementWorker(SelfImprovementBacklog,
                new SwarmCode.Hosting.Improvement.SwarmCodeSelfImprovementCycleHost(sessions, preferences, ScheduledRuns, WorkspaceOverridesForSessions, paths,
                    SelfImprovementOptions, loggerFactory.CreateLogger<SwarmCode.Hosting.Improvement.SwarmCodeSelfImprovementCycleHost>()),
                SelfImprovementGate, SelfImprovementOptions, paused: () => preferences.Current.SelfImprovementPaused,
                logger: loggerFactory.CreateLogger<SwarmCore.Coding.SelfImprovement.SelfImprovementWorker>());
            SelfImprovement.Changed += () => Changed?.Invoke();
            // "Build and relaunch": merge verified fixes into the checkout, rebuild the Debug app and restart on it.
            SelfImprovementRebuild = new SwarmCode.Hosting.Improvement.SwarmCodeRebuild(paths, SelfImprovementOptions, SelfImprovementBacklog,
                loggerFactory.CreateLogger<SwarmCode.Hosting.Improvement.SwarmCodeRebuild>());
        }

        SwarmCodeHostOptions WorkspaceOptions(string root, bool background) => new()
        {
            RepositoryRoot = root,
            DataPaths = paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(paths, WorkspaceOverrides(root, preferences, paths, background)),
            SelfImprovement = SelfImprovementAvailable,
            ConfigureServices = services =>
            {
                services.AddSingleton(sessions);
                services.AddSingleton(preferences);
                services.AddSingleton(userSettings);
                services.AddSingleton(_settingsActivation);
                services.AddSingleton<IPermissionBroker>(_broker);
                services.AddSingleton(_broker);
                services.AddSingleton(KeyStore);
                services.AddSingleton(protector);
                services.AddSingleton(Vault);
                services.AddSingleton(credentialStore);
                services.AddSingleton(vaultKeys);
                services.AddSingleton(SecretIntake);
                services.AddSingleton(PasswordImporter);
                services.AddSingleton<SwarmCore.PersonalMemory.IPersonalMemoryStore>(MemoryStore);
                services.AddSingleton<SwarmCore.Coding.Memory.IRepoMemoryProposalStore>(RepoMemoryProposals);
                services.AddSingleton(Schedules!);
                if (SelfImprovementBacklog is not null) services.AddSingleton(SelfImprovementBacklog);
                if (SelfImprovementReporter is not null) services.AddSingleton(SelfImprovementReporter);
                if (WorkspaceOverridesForSessions is not null) services.AddSingleton(WorkspaceOverridesForSessions);
                if (background)
                {
                    // A run's background workspace never binds the app's agent team (one host at a time): agents are
                    // off there, and its bridge admits the scheduled session's runner.
                    services.AddSingleton<SwarmCore.Coding.Agents.IAgentTeamStore>(new SwarmCore.Coding.Agents.InMemoryAgentTeamStore());
                    services.AddSingleton<SwarmCode.Hosting.Agents.IConversationRunnerSource>(ScheduledRuns!);
                    return;
                }
                services.AddSingleton(Agents);
                services.AddSingleton<SwarmCode.Hosting.Agents.IConversationRunnerSource>(this);
            },
        };
        void ConfigureWorkspaceLogging(ILoggingBuilder logging)
        {
            logging.AddFilter("SwarmCore", LogLevel.Information);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.LogsDirectory, "swarmcode-app.log")));
        }

        Workspaces = new WorkspaceHostManager(paths, root => WorkspaceOptions(root, background: false), ConfigureWorkspaceLogging);
        Workspaces.Changed += () => Changed?.Invoke();
        sessions.Changed += () => Changed?.Invoke();
        preferences.Changed += () => Changed?.Invoke();
        Agents.Changed += OnSettingsActivityChanged;
        Schedules.Changed += OnSettingsActivityChanged;
        Schedules.Start();
        SelfImprovement?.Start();
        Instance = this;
    }

    /// <summary>A scheduled run in the open repository runs in this window (null sends it to a background workspace).</summary>
    private async Task<ConversationRunner?> OpenScheduledRunnerAsync(string repositoryRoot, string sessionId, CancellationToken ct)
    {
        if (IsOpeningWorkspace || Workspace is not { } workspace || !SameRepository(workspace.RepositoryRoot, repositoryRoot)) return null;
        return await OpenRunnerAsync(sessionId, ct).ConfigureAwait(false);
    }

    private static bool SameRepository(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-workspace configuration: repository root, hand-off preference, and the MCP vault bridge for native lanes.</summary>
    private static Dictionary<string, string?> WorkspaceOverrides(string root, AppPreferencesStore preferences, SwarmCodeDataPaths paths, bool background = false)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Coding:RepositoryRoot"] = root,
            ["CodingLanes:HandOffOnRefusal"] = preferences.Current.HandOffOnRefusal ? "true" : "false",
        };
        SwarmCode.Hosting.Mcp.McpBridgeLocator.AddOverrides(overrides, paths);
        if (background) overrides[SwarmCore.Coding.Agents.AgentTeamOptions.SectionName + ":Enabled"] = "false";
        return overrides;
    }

    /// <summary>BYOK key provider over the protected vault; independent of any open workspace.</summary>
    public SwarmCodeApiKeyProvider ApiKeys { get; }

    /// <summary>The OS keystore in use (DPAPI, Keychain, or none).</summary>
    public IOsKeyStore KeyStore { get; }

    /// <summary>Protected secret vault shared with the console host through the data directory.</summary>
    public ISecretVault Vault { get; }

    public SecretIntake SecretIntake { get; }

    /// <summary>The person's memory (shared with every workspace): controls for Settings &gt; Memory. Never calls a model.</summary>
    public SwarmCore.PersonalMemory.PersonalMemoryService Memory { get; }

    public SwarmCore.PersonalMemory.IPersonalMemoryStore MemoryStore { get; }

    /// <summary>Admin/global options (<c>PersonalMemory</c> section of settings.json).</summary>
    public SwarmCore.PersonalMemory.PersonalMemoryOptions MemoryOptions { get; }

    public SwarmCore.PersonalMemory.MemoryScope MemoryScope { get; }

    /// <summary>Reviewable repository-memory edits proposed by agents.</summary>
    public SwarmCore.Coding.Memory.IRepoMemoryProposalStore RepoMemoryProposals { get; }

    public PasswordCsvImporter PasswordImporter { get; }

    /// <summary>Agents that sessions started (the Agents view). Records survive restarts; running ones resume only on a message.</summary>
    public SwarmCore.Coding.Agents.AgentTeam Agents { get; }

    /// <summary>The person's scheduled runs (the Schedules view). Runs fire while the app is open.</summary>
    public SwarmCore.Coding.Scheduling.CodingScheduler Schedules { get; }

    /// <summary>Runners of scheduled sessions running outside the open workspace.</summary>
    public SwarmCode.Hosting.Scheduling.ScheduledRunWorkspaces ScheduledRuns { get; }

    /// <summary>
    /// Self-improvement is a developer feature: compiled into Debug builds only, so a shipped Release build carries no
    /// loop, no capture, no page and no setting, and nothing in settings.json can turn it on. Debug builds still stay
    /// inert unless the executable runs inside a SwarmCode source checkout (or one is configured).
    /// </summary>
    public static bool SelfImprovementAvailable =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>The self-improvement loop: works through <see cref="SelfImprovementBacklog"/> while the app is idle. Null outside Debug builds.</summary>
    public SwarmCore.Coding.SelfImprovement.SelfImprovementWorker? SelfImprovement { get; }

    /// <summary>Defects and feature requests observed in use (the Self-improvement view). Shared by every workspace.</summary>
    public SwarmCore.Coding.SelfImprovement.ISelfImprovementBacklog? SelfImprovementBacklog { get; }

    /// <summary>The one way sightings enter the backlog (turns, unknown commands, crash handlers).</summary>
    public SwarmCore.Coding.SelfImprovement.SelfImprovementReporter? SelfImprovementReporter { get; }

    /// <summary>Knows when the person is away and nothing is running, so a cycle may start.</summary>
    public SwarmCore.Coding.SelfImprovement.SelfImprovementIdleGate? SelfImprovementGate { get; }

    /// <summary>Workspace overrides a cycle sets for its session (worktree root plus its build and test checks).</summary>
    public SwarmCode.Hosting.Improvement.SessionWorkspaceOverrides? WorkspaceOverridesForSessions { get; }

    /// <summary>Merges verified fixes, rebuilds the Debug app and restarts SwarmCode on the new build (the page's "Build and relaunch").</summary>
    public SwarmCode.Hosting.Improvement.SwarmCodeRebuild? SelfImprovementRebuild { get; }

    /// <summary>The self-improvement options as settings.json has them right now (read fresh, so a save applies at the next check).</summary>
    public SwarmCore.Coding.SelfImprovement.SelfImprovementOptions SelfImprovementOptions()
        => SwarmCode.Hosting.Improvement.SwarmCodeSelfImprovement.BindOptions(SwarmCodeHostComposer.BuildConfiguration(Paths));

    /// <summary>The person did something (typed, clicked, sent, opened): the idle clock restarts.</summary>
    public void ReportUserActivity() => SelfImprovementGate?.ReportUserActivity();

    /// <summary>What is going on in the app that should keep a self-improvement cycle from starting. Empty = idle.</summary>
    public IReadOnlyList<string> BusyReasons()
    {
        var reasons = new List<string>();
        // A debugging session is the developer's own work in progress: a cycle never starts under it, unless the launch
        // profile is the self-improvement one (Coding:SelfImprovement:AllowUnderDebugger, set through SWARMCODE_ env).
        if (System.Diagnostics.Debugger.IsAttached && !SelfImprovementOptions().AllowUnderDebugger) reasons.Add("A debugger is attached.");
        if (IsOpeningWorkspace) reasons.Add("A repository is being opened.");
        var cycleSession = SelfImprovement?.ActiveSessionId;
        int running;
        lock (_gate) running = _runners.Values.Count(r => r.IsRunning || (r.QueuedMessages.Count > 0 && !r.QueuePaused));
        running += ScheduledRuns.BackgroundRunners.Count(r => r.ConversationId != cycleSession && r.IsRunning);
        if (running > 0) reasons.Add(running == 1 ? "A session is working." : $"{running} sessions are working.");
        var agents = Agents.RunningCount;
        if (agents > 0) reasons.Add(agents == 1 ? "An agent is working." : $"{agents} agents are working.");
        var shells = Workspace?.Services.GetService<SwarmCore.Coding.Tools.BackgroundShell.BackgroundShellManager>()?.RunningCount ?? 0;
        if (shells > 0) reasons.Add(shells == 1 ? "A background shell is running." : $"{shells} background shells are running.");
        var scheduled = Schedules.ActiveRunCount;
        if (scheduled > 0) reasons.Add(scheduled == 1 ? "A scheduled run is going." : $"{scheduled} scheduled runs are going.");
        return reasons;
    }

    public void ClearError()
    {
        LastError = null;
        Changed?.Invoke();
    }

    private readonly SwarmCode.Hosting.Settings.SettingsActivation _settingsActivation = new() { AppliesWhenIdle = true };
    private int _settingsReloadPending, _settingsReloadApplying;
    private void OnSettingsActivityChanged() { Changed?.Invoke(); ApplyPendingSettings(); }
    private void RequestSettingsReload() { Interlocked.Exchange(ref _settingsReloadPending, 1); ApplyPendingSettings(); }
    private void ApplyPendingSettings()
    {
        if (Volatile.Read(ref _settingsReloadPending) == 0 || IsOpeningWorkspace || AnyTurnRunning || Agents.RunningCount > 0
            || Schedules.ActiveRunCount > 0 || ScheduledRuns.BackgroundRunners.Any(r => r.IsRunning)
            || Interlocked.CompareExchange(ref _settingsReloadApplying, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                lock (_gate) if (_runners.Values.Any(r => r.IsRunning || r.QueuedMessages.Count > 0)) return;
                Interlocked.Exchange(ref _settingsReloadPending, 0);
                var memoryOptions = SwarmCode.Hosting.Memory.SwarmCodePersonalMemoryRegistration.BindOptions(SwarmCodeHostComposer.BuildConfiguration(Paths));
                MemoryOptions.Enabled = memoryOptions.Enabled; MemoryOptions.FunctionType = memoryOptions.FunctionType;
                MemoryOptions.PrivacyClass = memoryOptions.PrivacyClass; MemoryOptions.RouteProvider = memoryOptions.RouteProvider; MemoryOptions.RouteModel = memoryOptions.RouteModel;
                if (await ReloadWorkspaceAsync()) MauiProgram.LogToFile("Saved runtime settings applied after active work finished.");
            }
            catch (Exception ex) { LastError = "Settings were saved, but the workspace could not reload: " + ex.Message; Changed?.Invoke(); }
            finally { Interlocked.Exchange(ref _settingsReloadApplying, 0); }
        });
    }

    public SwarmCodeDataPaths Paths { get; }
    public AppPreferencesStore Preferences { get; }
    public WorkspaceSessionCatalog Sessions { get; }

    /// <summary>Saved sessions across every repository, with their turns, for the History view (no workspace needed).</summary>
    public ConversationHistorySource History { get; }
    public UserSettingsFile UserSettings { get; }
    public WorkspaceHostManager Workspaces { get; }
    public UiPermissionBroker PermissionBroker => _broker;

    public WorkspaceHost? Workspace => Workspaces.Current;
    public string? RepositoryRoot => Workspace?.RepositoryRoot;
    public bool IsOpeningWorkspace { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised on any thread when workspace, sessions, or preferences change. Components marshal to the renderer.</summary>
    public event Action? Changed;

    /// <summary>Opens (or rebuilds) the runtime for a repository. Running turns in the previous workspace are cancelled.</summary>
    public async Task<bool> OpenWorkspaceAsync(string repositoryRoot, CancellationToken ct = default)
    {
        ReportUserActivity();
        IsOpeningWorkspace = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            if (_vaultBroker is not null) await _vaultBroker.StartAsync(ct).ConfigureAwait(false);
            // The closing workspace's agents are interrupted first (they resume on a message), so the runner cancellation
            // that follows does not record them as stopped by the person. The new workspace attaches its own host before
            // the old one is disposed, which is why the old host's own StopAsync detach would otherwise find itself unbound.
            if (Workspace?.Services.GetService<SwarmCode.Hosting.Agents.SwarmCodeAgentHost>() is { } closingHost)
                Agents.DetachHost(closingHost, interruptRunning: true);
            await CloseRunnersAsync().ConfigureAwait(false);
            await Workspaces.OpenAsync(repositoryRoot, ct).ConfigureAwait(false);
            await Sessions.RememberRepositoryAsync(repositoryRoot, ct).ConfigureAwait(false);
            Preferences.Update(p => p.LastRepositoryRoot = Workspaces.Current!.RepositoryRoot);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            MauiProgram.LogToFile($"Open workspace failed: {ex}");
            return false;
        }
        finally
        {
            IsOpeningWorkspace = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Rebuilds the current workspace so new lanes, keys, or preferences take effect.</summary>
    public Task<bool> ReloadWorkspaceAsync(CancellationToken ct = default)
        => RepositoryRoot is { } root ? OpenWorkspaceAsync(root, ct) : Task.FromResult(false);

    public async Task<WorkspaceSessionInfo> NewSessionAsync(CancellationToken ct = default)
    {
        var root = RepositoryRoot ?? throw new InvalidOperationException("Open a repository first.");
        var lane = Preferences.Current.DefaultLane ?? Workspace?.LaneOptions.DefaultLane;
        var session = await Sessions.CreateAsync(root, laneName: lane, ct: ct,
            model: Preferences.Current.DefaultLane is not null ? Preferences.Current.DefaultModel : null).ConfigureAwait(false);
        Preferences.Update(p => p.LastSessionId = session.Id);
        return session;
    }

    /// <summary>Gets the runner for a session in the open workspace, loading its history on first use.</summary>
    public async Task<ConversationRunner?> GetRunnerAsync(string sessionId, CancellationToken ct = default)
    {
        var runner = await OpenRunnerAsync(sessionId, ct).ConfigureAwait(false);
        if (runner is not null) Preferences.Update(p => p.LastSessionId = sessionId);
        return runner;
    }

    // Agents open their sessions' runners without changing which session the person last looked at.
    Task<ConversationRunner?> SwarmCode.Hosting.Agents.IConversationRunnerSource.GetRunnerAsync(string conversationId, CancellationToken ct)
        => OpenRunnerAsync(conversationId, ct);

    private async Task<ConversationRunner?> OpenRunnerAsync(string sessionId, CancellationToken ct)
    {
        // A scheduled run in a background workspace owns its session's runner until the session is idle.
        if (ScheduledRuns.TryGetRunner(sessionId) is { } scheduled) return scheduled;
        var workspace = Workspace;
        if (workspace is null) return null;
        ConversationRunner? runner;
        Task? loading = null;
        TaskCompletionSource? load = null;
        lock (_gate)
        {
            if (!_runners.TryGetValue(sessionId, out runner))
            {
                runner = workspace.OpenSession(sessionId);
                _runners[sessionId] = runner;
                load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _loading[sessionId] = load.Task;
            }
            else _loading.TryGetValue(sessionId, out loading);
        }
        if (load is not null)
        {
            try
            {
                runner.Changed += () => { Changed?.Invoke(); ApplyPendingSettings(); };
                // Every runner reports its turns: agents' turns settle them, and a parent's idle moment receives their results.
                var agents = workspace.Services.GetService<SwarmCode.Hosting.Agents.SwarmCodeAgentHost>();
                agents?.Observe(runner);
                // The load is not tied to the caller's token: a page that navigates away must not leave a half-read runner behind.
                try { await runner.LoadHistoryAsync(CancellationToken.None).ConfigureAwait(false); }
                catch
                {
                    lock (_gate) _runners.Remove(sessionId);
                    agents?.Forget(runner);
                    await runner.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                // SessionStart hooks: "new" for a session with no history yet, "resume" for one being reopened. Their
                // context rides in front of the session's next message. Never throws; warnings become notices.
                await runner.RunSessionStartHooksAsync(runner.Entries.Count == 0 ? "new" : "resume").ConfigureAwait(false);
                load.SetResult();
            }
            catch (Exception ex)
            {
                load.SetException(ex);
                throw;
            }
            finally { lock (_gate) _loading.Remove(sessionId); }
        }
        // A second caller (the page and an agent host opening the same session at once) waits for the first load
        // instead of sending to a runner whose history is still being read.
        else if (loading is not null) await loading.WaitAsync(ct).ConfigureAwait(false);
        return runner;
    }

    private readonly Dictionary<string, Task> _loading = new();

    public ConversationRunner? TryGetRunner(string sessionId)
    {
        if (ScheduledRuns.TryGetRunner(sessionId) is { } scheduled) return scheduled;
        lock (_gate) return _runners.GetValueOrDefault(sessionId);
    }

    public bool AnyTurnRunning
    {
        get { lock (_gate) return _runners.Values.Any(r => r.IsRunning); }
    }

    /// <summary>
    /// Deletes a session and its agents' sessions. An isolated agent's worktree is removed only when it is clean and its
    /// branch is merged; otherwise it is kept and the returned notes say where (empty when there is nothing to report).
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        // A session's agents go with it: stop them, then delete their sessions first.
        foreach (var child in (await Sessions.ListAsync(ct: ct).ConfigureAwait(false)).Where(s => s.ParentSessionId == sessionId).ToList())
            notes.AddRange(await DeleteSessionAsync(child.Id, ct).ConfigureAwait(false));
        ConversationRunner? runner;
        if (Agents.Get(sessionId) is { } agent)
        {
            if (!agent.IsTerminal) { try { await Agents.CancelAsync(null, sessionId, ct).ConfigureAwait(false); } catch (InvalidOperationException) { } }
            // The agent's runner stops before its worktree is checked, so no tool is still writing there.
            lock (_gate) _runners.Remove(sessionId, out runner);
            if (runner is not null) await runner.DisposeAsync().ConfigureAwait(false);
            var forgotten = await Agents.ForgetWithCleanupAsync(sessionId, ct, force: true).ConfigureAwait(false);
            if (forgotten.Worktree is { Removed: false } kept) notes.Add(kept.Message);
        }
        ScheduledRuns.TryGetRunner(sessionId)?.Cancel();
        lock (_gate) _runners.Remove(sessionId, out runner);
        if (runner is not null) await runner.DisposeAsync().ConfigureAwait(false);
        // Background shells end with their session (process trees killed off the UI thread).
        if (Workspace?.Services.GetService(typeof(SwarmCore.Coding.Tools.BackgroundShell.BackgroundShellManager)) is SwarmCore.Coding.Tools.BackgroundShell.BackgroundShellManager shells)
            await Task.Run(() => shells.StopAll(sessionId), ct).ConfigureAwait(false);
        (Workspace?.Services.GetService(typeof(SwarmCore.Agent.Runtime.IPermissionBroker)) as UiPermissionBroker)?.ClearConversation(sessionId);
        // The session's undo checkpoints (private git refs in its repository) go with it; best effort.
        if (await Sessions.GetAsync(sessionId, ct).ConfigureAwait(false) is { } info
            && Workspace?.Services.GetService(typeof(SwarmCore.Coding.Checkpoints.TurnCheckpointService)) is SwarmCore.Coding.Checkpoints.TurnCheckpointService checkpoints)
        {
            try { await Task.Run(() => checkpoints.DeleteConversationAsync(info.RepositoryRoot, sessionId, ct), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { MauiProgram.LogToFile($"Could not delete undo checkpoints of {sessionId}: {ex.Message}"); }
        }
        await Sessions.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        if (Workspace?.Services.GetService(typeof(IConversationStore)) is IConversationStore store)
            await store.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        if (Workspace?.Services.GetService(typeof(FileAgentSessionStore)) is FileAgentSessionStore transcripts)
            await transcripts.ClearAsync(sessionId).ConfigureAwait(false);
        if (Workspace?.Services.GetService(typeof(FileConversationJobStore)) is FileConversationJobStore jobs)
            await Task.Run(() => jobs.Delete(sessionId), ct).ConfigureAwait(false);
        if (Workspace?.Services.GetService(typeof(FileTranscriptJournal)) is FileTranscriptJournal journal)
            await Task.Run(() => journal.Delete(sessionId), ct).ConfigureAwait(false);
        if (Workspace?.Services.GetService(typeof(SwarmCore.Agent.ToolUseEngine.IPendingToolCallJournal)) is SwarmCore.Agent.ToolUseEngine.IPendingToolCallJournal pendingTools)
            await pendingTools.ClearAsync(sessionId, ct).ConfigureAwait(false);
        // A kept worktree is the person's work: say where it is (the toast is dismissible).
        if (notes.Count > 0) LastError = string.Join(" ", notes);
        if (Workspace?.Services.GetService(typeof(SwarmCore.Coding.Planning.IConversationPlanStore)) is SwarmCore.Coding.Planning.IConversationPlanStore plans)
            await Task.Run(() => plans.DeleteAsync(sessionId, ct).AsTask(), ct).ConfigureAwait(false);
        Changed?.Invoke();
        return notes;
    }

    private async Task CloseRunnersAsync()
    {
        List<ConversationRunner> runners;
        lock (_gate) { runners = _runners.Values.ToList(); _runners.Clear(); }
        foreach (var r in runners) r.Dispose();
        foreach (var r in runners) await r.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Instance = null;
        _settingsActivation.ReloadRequested -= RequestSettingsReload;
        if (SelfImprovement is not null) await SelfImprovement.DisposeAsync().ConfigureAwait(false);
        await Schedules.DisposeAsync().ConfigureAwait(false);
        await ScheduledRuns.DisposeAsync().ConfigureAwait(false);
        await CloseRunnersAsync().ConfigureAwait(false);
        await Workspaces.DisposeAsync().ConfigureAwait(false);
        await Agents.DisposeAsync().ConfigureAwait(false);
        if (_vaultBroker is not null) await _vaultBroker.DisposeAsync().ConfigureAwait(false);
        await _secretServices.DisposeAsync().ConfigureAwait(false);
    }
}
