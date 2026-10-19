using SwarmCore.Coding.Permissions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.App.Services;
using SwarmCode.Hosting;
using SwarmCore.Chat.Components;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Extensions;

namespace SwarmCode.App.Components.Pages;

public partial class Session
{

    /// <summary>True while a turn runs but nothing is streaming or executing yet, so the page shows a working hint.</summary>
    private static bool ShowWorkingIndicator(IReadOnlyList<TranscriptEntry> entries)
        => entries.Count == 0 || entries[^1] is not (AssistantEntry { IsStreaming: true }
            or ToolCallEntry { State: ToolCallState.Running }
            or PermissionEntry { IsPending: true });

    private static readonly string[] Examples =
    [
        "Explain how this repository is organized and where to start reading.",
        "Find and fix the most likely bug in the failing tests.",
        "Add unit tests for the least-covered public class.",
    ];

    internal static readonly IReadOnlyList<SlashCommand> BuiltInSlashCommands =
    [
        new("help", "List the available commands"),
        new(GreetingCommand.Name, GreetingCommand.Description),
        new("bug", "Record a Talavine Code defect without sending a model prompt: /bug <description>"),
        new("lane", "Switch lane: /lane <name>"),
        new("lanes", "List configured lanes"),
        new("auto", "Reassess model selection for the next message"),
        new("new", "Start a new session"),
        new("fork", "Start an independent conversation with this chat's saved history"),
        new("clear", "Clear the visible transcript"),
        new("plan", "Toggle plan first: investigate read-only and propose a plan"),
        new("compare", "Send one prompt to two or more models you pick; they review each other and a judge settles disagreements: /compare [prompt]"),
        new("review", "Same as /compare"),
        new("diff", "Open the Changes panel"),
        new("status", "Refresh git status"),
        new("undo", "Undo the file changes of the last turn that changed files"),
        new("rewind", "Same as /undo"),
        new("compact", "Compact the conversation context at the start of the next message"),
        new("cost", "Tokens and cost reported for this session, plus tracked API usage"),
        new("export", "Save this session's transcript as Markdown: /export [file]"),
        new("init", "Write or improve the repository's AGENTS.md"),
        new("agents", "Open the agents this session started"),
        new("schedules", "Open scheduled runs"),
        new("self-improvement", "Open Talavine Code's own defect and feature backlog"),
        new("mcp", "Open the connected data (MCP servers) settings"),
        new("hooks", "Open the extensions (commands, skills, hooks) settings"),
        new("memory", "Open memory settings"),
        new("settings", "Open settings"),
        new("history", "Search every session across your repositories: /history <words>"),
        new("summary", "Summarize the work done in your sessions: /summary <period, e.g. past week>"),
        new("secret", "Save a key or password to the vault: /secret <name> <value>"),
        new("secrets", "List stored secret names (never values)"),
        new("forget", "Delete a stored secret: /forget <name>"),
        new("import-passwords", "Import a browser or password-manager CSV export"),
    ];

    // Custom commands (.swarmcode/commands, .claude/commands, <data>/commands); built-in names always win.
    private IReadOnlyList<SwarmCore.Coding.Extensions.CustomCommand> _customCommands = [];
    private IReadOnlyList<SlashCommand> _slashCommands = BuiltInSlashCommands;
    private IReadOnlyList<SlashCommand> SlashCommands => _slashCommands;

    private async Task LoadCustomCommandsAsync(string? repositoryRoot)
    {
        var dataRoot = App.Paths.RootDirectory;
        var names = BuiltInSlashCommands.Select(c => c.Name).ToArray();
        try
        {
            _customCommands = await Task.Run(() => SwarmCore.Coding.Extensions.CustomCommandCatalog.Discover(repositoryRoot, dataRoot, names));
        }
        catch (Exception ex)
        {
            _customCommands = [];
            DesktopHost.LogToFile($"Could not read custom commands: {ex.Message}");
        }
        _slashCommands = BuiltInSlashCommands.Concat(_customCommands.Select(c => new SlashCommand(c.Name, c.Description))).ToList();
    }

    /// <summary>The message a custom <c>/name args</c> expands to, or null when the text names no custom command.</summary>
    private string? ExpandCustomCommand(string text)
    {
        if (!SwarmCore.Coding.Extensions.CustomCommandCatalog.TryParseInvocation(text, out var name, out var arguments)) return null;
        return SwarmCore.Coding.Extensions.CustomCommandCatalog.Find(_customCommands, name) is { } command
            ? SwarmCore.Coding.Extensions.CustomCommandExpander.Expand(command, arguments)
            : null;
    }

    private sealed record Note(string Kind, string Text, string? Details)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
    }

    private readonly List<Note> _notes = [];
    private readonly HashSet<string> _expanded = [];
    private readonly object _renderGate = new();

    private ConversationRunner? _runner;
    private WorkspaceSessionInfo? _session;
    private string? _parentTitle;
    private string? _attachedSessionId;
    private string? _attachedRoot;
    private string _lane = "";
    private string _draft = "";
    private IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> _attachments = [];
    private bool _attachmentsBusy;
    private string _model = "", _sendMode = "queue";
    private IReadOnlyList<string> _models = [];
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _catalogEfforts = new Dictionary<string, IReadOnlyList<string>>();
    private bool _modelsLoading, _showArtifacts;
    private string? _modelError, _artifactPath;
    private int _modelGeneration;
    private string? _openError;
    private bool _loading = true;
    private bool _attaching;
    private bool _showChanges;
    private bool _forceScroll;
    private bool _disposed;
    private int _changeCount;
    private int _refreshToken;
    private int _renderScheduled;
    private long _lastRender;
    private int _attachGeneration;

    private ChatScrollRegion? _scroll;
    private ChatComposer? _composer;
    private Components.Chat.AttachmentIntake? _attachmentIntake;

    [Parameter] public string SessionId { get; set; } = "";

    private IReadOnlyList<CodingLane> Lanes => App.Workspace?.Lanes ?? [];

    private WorkspaceChangesSource? _changesSource;

    /// <summary>The shared Changes panel's source for the open repository; a new workspace host gets a new source (and a reset panel).</summary>
    private WorkspaceChangesSource? ChangesSource
    {
        get
        {
            var host = App.Workspace;
            if (host is null) return null;
            if (_changesSource?.Host != host) _changesSource = new WorkspaceChangesSource(host);
            return _changesSource;
        }
    }
    private IEnumerable<string> ModelChoices => _models.Prepend(_model).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal);
    private IReadOnlyList<SwarmCore.Abstractions.LlmRouting.LlmCatalogModel> _modelDetails = [];

    protected override void OnInitialized()
    {
        App.Changed += OnAppChanged;
        AttachIde();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!string.Equals(_attachedSessionId, SessionId, StringComparison.Ordinal))
            await AttachAsync();
    }

    // ---- runner attachment -------------------------------------------------

    private async Task AttachAsync()
    {
        var generation = ++_attachGeneration;
        _attaching = true;
        _loading = true;
        try
        {
            Detach();
            _attachedSessionId = SessionId;
            _notes.Clear();
            _expanded.Clear();
            _draft = "";
            _openError = null;
            _changeCount = 0;

            _session = await App.Sessions.GetAsync(SessionId);
            _parentTitle = _session?.ParentSessionId is { } parentId ? (await App.Sessions.GetAsync(parentId))?.Title : null;
            if (_session is not null && App.Paths.IsChatWorkspace(_session.RepositoryRoot) && !App.HasProject && App.Workspace is null)
                await App.OpenWorkspaceAsync(_session.RepositoryRoot);
            var workspace = App.Workspace;
            _attachedRoot = workspace?.RepositoryRoot;
            if (generation != _attachGeneration) return;

            // A background run's session (a scheduled run or a self-improvement cycle in another repository or worktree) is
            // served by its own runner while the run lives, so it opens live whichever repository the window has open.
            var background = App.ScheduledRuns.TryGetRunner(SessionId);
            if (background is not null || (workspace is not null && _session is not null && SameRepository(_session.RepositoryRoot, workspace.RepositoryRoot)))
            {
                var runner = background ?? await App.GetRunnerAsync(SessionId);
                // Opening the runner can recover a legacy conversation's model from its saved job.
                _session = await App.Sessions.GetAsync(SessionId);
                if (generation != _attachGeneration) return;
                if (runner is not null && _session is not null)
                {
                    _runner = runner;
                    if (workspace is not null) AttachCards(workspace);
                    runner.Changed += OnRunnerChanged;
                    runner.TurnFinished += OnTurnFinished;
                    _lane = ResolveLane(_session.LaneName);
                    await LoadCustomCommandsAsync(_session.RepositoryRoot);
                    await LoadModelsAsync();
                    _forceScroll = true;
                    TakeIdeSelection(); // a selection the VS Code extension routed here before the page attached
                }
            }
        }
        catch (Exception ex)
        {
            _notes.Add(new Note("error", "Could not open this session.", ex.Message));
        }
        finally
        {
            if (generation == _attachGeneration)
            {
                _loading = false;
                _attaching = false;
            }
        }
    }

    private void Detach()
    {
        ++_modelGeneration;
        _attachments = [];
        _attachmentsBusy = false;
        if (_runner is { } runner)
        {
            runner.Changed -= OnRunnerChanged;
            runner.TurnFinished -= OnTurnFinished;
        }
        DetachCards();
        _runner = null;
    }

    private static bool SameRepository(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private string ResolveLane(string? preferred)
    {
        var lanes = Lanes;
        foreach (var candidate in new[] { preferred, App.Preferences.Current.DefaultLane, App.Workspace?.LaneOptions.DefaultLane })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && lanes.FirstOrDefault(l => string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match.Name;
        }
        return lanes.FirstOrDefault()?.Name ?? "";
    }

    private async Task OpenSessionRepositoryAsync()
    {
        if (_session is null) return;
        _openError = null;
        _loading = true;
        StateHasChanged();
        var ok = await App.OpenWorkspaceAsync(_session.RepositoryRoot);
        if (!ok) _openError = App.LastError ?? "Could not open the repository.";
        _attachedSessionId = null;
        await AttachAsync();
    }

    // ---- change notifications / throttled rendering ------------------------

    private void OnAppChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed || _attaching || App.IsOpeningWorkspace) { RequestRender(); return; }
            var root = App.Workspace?.RepositoryRoot;
            // Stale when the open repository changed, the session's runner was swapped, or a background run's runner
            // appeared for a session this page could not attach yet (its workspace was still being created).
            var stale = !string.Equals(root, _attachedRoot, StringComparison.OrdinalIgnoreCase)
                || (_runner is not null && !ReferenceEquals(App.TryGetRunner(SessionId), _runner))
                || (_runner is null && App.ScheduledRuns.TryGetRunner(SessionId) is not null);
            if (stale && _attachedSessionId == SessionId)
            {
                _attachedSessionId = null;
                await AttachAsync();
                StateHasChanged();
                return;
            }
            RequestRender();
        });
    }

    private void OnRunnerChanged() => RequestRender();

    /// <summary>Coalesces re-renders to at most one per 100 ms; always trailing so the last update lands.</summary>
    private void RequestRender()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 1) return;
        var wait = (int)Math.Max(0, 100 - (Environment.TickCount64 - Interlocked.Read(ref _lastRender)));
        _ = Task.Run(async () =>
        {
            if (wait > 0) await Task.Delay(wait);
            Interlocked.Exchange(ref _renderScheduled, 0);
            Interlocked.Exchange(ref _lastRender, Environment.TickCount64);
            if (_disposed) return;
            try
            {
                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    try { _session = await App.Sessions.GetAsync(SessionId) ?? _session; } catch { }
                    StateHasChanged();
                });
            }
            catch (ObjectDisposedException) { }
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_runner is null || _scroll is null) return;
        if (_runner.IsRunning || _forceScroll)
        {
            var force = _forceScroll;
            _forceScroll = false;
            try { await _scroll.ScrollToBottomAsync(force); } catch { }
        }
    }

    private void OnTurnFinished(TurnResult result)
    {
        if (_disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            if (result.WorkspaceChanged) _refreshToken++;
            _session = await App.Sessions.GetAsync(SessionId) ?? _session;
            _lane = ResolveLane(_session?.LaneName);
            await LoadModelsAsync();
            StateHasChanged();
        });
    }

    // ---- sending -----------------------------------------------------------

    private Task OnDraftChanged(string value)
    {
        _draft = value;
        return Task.CompletedTask;
    }

    private async Task OnSendAsync(string raw)
    {
        var runner = _runner;
        var text = (raw ?? "").Trim();
        if (_attachmentsBusy || (text.Length == 0 && _attachments.Count == 0)) return;
        var attachments = _attachments.ToArray();

        // /bug is explicit local intake, not a coding turn. It therefore works before a session runner is available.
        if (UserBugIntake.IsCommand(text))
        {
            if (attachments.Length > 0)
            {
                AddNote("warn", "Remove attachments before filing a bug report. Your draft was kept.");
                return;
            }

            var draftAtAdmission = _draft;
            var result = await App.UserBugIntake.SubmitAsync(App.SelfImprovementReporter, text, SessionId);
            if (result.Outcome == UserBugIntakeOutcome.Saved)
            {
                // A user may have started a newer draft while the durable save was in flight.
                // Keep that newer text, but always acknowledge the report that was successfully saved.
                if (UserBugIntake.ShouldClearDraft(draftAtAdmission, _draft))
                    _draft = "";

                AddNote("info", "Bug report saved: " + result.Item!.Id,
                    "Current stage: " + result.Item.Stage + ". It was saved locally without starting a model turn.");
            }
            else if (result.Outcome != UserBugIntakeOutcome.NotHandled)
            {
                AddNote("warn", result.Message ?? "Bug report was not saved. Your draft was kept.");
            }
            return;
        }

        if (runner is null) return;
        if (text.Length == 0) text = "Inspect the attached files.";
        _draft = "";

        if (text.StartsWith('/') && ExpandCustomCommand(text) is { Length: > 0 } expanded) text = expanded;
        else if (attachments.Length == 0 && text.StartsWith('/') && await TryRunCommandAsync(text)) return;

        var lane = string.IsNullOrEmpty(_lane) ? null : _lane;
        if (text.StartsWith('@'))
        {
            var split = text.IndexOfAny([' ', '\t', '\n']);
            if (split > 1)
            {
                var name = text[1..split];
                var rest = text[(split + 1)..].Trim();
                var match = Lanes.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null && rest.Length > 0)
                {
                    lane = match.Name;
                    await App.Sessions.SetUserLaneAsync(SessionId, match.Name, match.Model);
                    text = rest;
                }
                else if (match is null)
                {
                    AddNote("warn", $"No lane named '{name}'. Use /lanes to list them.");
                    return;
                }
            }
        }

        if (runner.IsRunning)
        {
            var prefs = App.Preferences.Current;
            var model = string.Equals(lane, _lane, StringComparison.OrdinalIgnoreCase) ? _model : Lanes.FirstOrDefault(l => l.Name == lane)?.Model;
            var accepted = _sendMode == "steer"
                ? await runner.SteerAsync(text, lane, prefs.Mode, prefs.HandOffOnRefusal, model, attachments)
                : await runner.EnqueueAsync(text, lane, prefs.Mode, prefs.HandOffOnRefusal, model, attachments);
            if (accepted) _attachments = [];
            if (!accepted) { _draft = raw ?? text; AddNote("warn", "The message was not submitted. It remains in the composer; try again after the current turn settles."); }
            return;
        }
        await StartTurnAsync(text, lane, attachments);
    }

    private void TogglePlanMode() => _runner?.SetPlanMode(!_runner.PlanMode);

    private async Task ApprovePlanAsync()
    {
        if (_runner is not { } runner) return;
        var prefs = App.Preferences.Current;
        var lane = string.IsNullOrEmpty(_lane) ? null : _lane;
        if (!await runner.ApprovePlanAsync(lane, prefs.Mode, prefs.HandOffOnRefusal, _model.Length > 0 ? _model : null))
            AddNote("warn", "The approval was not sent. Plan first is off; ask the agent to implement the plan.");
        _forceScroll = true;
    }

    private async Task StartTurnAsync(string text, string? lane, IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> attachments)
    {
        var runner = _runner;
        if (runner is null) return;
        var prefs = App.Preferences.Current;
        var model = string.Equals(lane, _lane, StringComparison.OrdinalIgnoreCase) ? _model : Lanes.FirstOrDefault(l => l.Name == lane)?.Model;
        if (!runner.TrySend(text, lane, prefs.Mode, prefs.HandOffOnRefusal, model, attachments: attachments))
        {
            if (!await runner.EnqueueAsync(text, lane, prefs.Mode, prefs.HandOffOnRefusal, model, attachments)) _draft = text;
            else _attachments = [];
            return;
        }
        _notes.Clear();
        _attachments = [];
        _forceScroll = true;
        StateHasChanged();
    }

    private async Task RemoveQueued(string id)
    {
        if (_runner is not null && !await _runner.RemoveQueuedAsync(id)) AddNote("warn", "This prompt is no longer queued. It may have started already.");
    }
    private async Task<bool> EditQueuedAsync(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { AddNote("warn", "Enter a prompt before saving the edit."); return false; }
        var saved = _runner is not null && await _runner.UpdateQueuedAsync(id, text);
        if (!saved) AddNote("warn", "The queued prompt could not be changed. It may already have started; your edit remains visible.");
        return saved;
    }
    private async Task SteerQueuedAsync(string id)
    {
        if (_runner is not null && !await _runner.SteerQueuedAsync(id)) AddNote("warn", "The current turn ended or a correction is already pending. The prompt remains queued.");
    }

    private Task ResumeQueueAsync() => _runner?.ResumeQueueAsync() ?? Task.CompletedTask;

    private void ContinueInterruptedWork()
    {
        var prefs = App.Preferences.Current;
        _runner?.ContinueInterruptedWork(string.IsNullOrEmpty(_lane) ? null : _lane, prefs.Mode, prefs.HandOffOnRefusal, _model);
    }

    /// <summary>The composer chip: Auto, then Ask risky, then Ask, then Auto again. Applies to the next message (and its agents).</summary>
    private void CyclePermissionMode()
    {
        var next = App.Preferences.Current.Mode switch
        {
            CodingPermissionMode.Auto => CodingPermissionMode.AskRisky,
            CodingPermissionMode.AskRisky => CodingPermissionMode.Ask,
            _ => CodingPermissionMode.Auto,
        };
        App.Preferences.Update(p => p.SetMode(next));
    }
    private Task StopAsync()
    {
        _runner?.Cancel();
        return Task.CompletedTask;
    }

    private void OnPageKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && _runner is { IsRunning: true }) _runner.Cancel();
    }

    private async Task UseExampleAsync(string text)
    {
        _draft = text;
        if (_composer is not null)
        {
            try { await _composer.FocusAsync(); } catch { }
        }
    }

    // ---- slash commands ----------------------------------------------------

    private async Task<bool> TryRunCommandAsync(string text)
    {
        var space = text.IndexOfAny([' ', '\t', '\n']);
        var command = (space < 0 ? text[1..] : text[1..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : text[(space + 1)..].Trim();
        if (!BuiltInSlashCommands.Any(c => c.Name == command))
        {
            // A path such as /usr/bin/env is a message, not a command.
            if (space < 0 && !command.Contains('/'))
            {
                _draft = text;
                AddNote("warn", $"Unknown command /{command}. Type /help for the list.");
                // A command that does not exist (and is not a typo of one) is something the person wanted SwarmCode to have.
                SwarmCode.Hosting.Improvement.SelfImprovementCapture.UnknownCommand(App.SelfImprovementReporter, text,
                    _slashCommands.Select(c => c.Name), SessionId);
                return true;
            }
            return false;
        }

        switch (command)
        {
            case GreetingCommand.Name:
                AddNote("info", GreetingCommand.Message);
                break;
            case "help":
                await LoadCustomCommandsAsync(App.Workspace?.RepositoryRoot);
                AddNote("info", "Commands", string.Join("\n", BuiltInSlashCommands.Select(c => $"/{c.Name} - {c.Description}"))
                    + "\n@<lane> <message> - run one message on a specific lane"
                    + (_customCommands.Count == 0
                        ? "\nCustom commands: add markdown files to .swarmcode/commands/ or the Talavine Code data folder's commands/."
                        : "\n\nCustom commands\n" + string.Join("\n", _customCommands.Select(c => $"/{c.Name} - {c.Description} ({SwarmCore.Coding.Extensions.ExtensionFiles.Label(c.Source)})"))));
                break;
            case "lanes":
                AddNote("info", "Lanes", Lanes.Count == 0
                    ? "No lanes are configured. Add one in Settings."
                    : string.Join("\n", Lanes.Select(l => $"{(l.Name == _lane ? "* " : "  ")}{l.Name} ({l.Route})")));
                break;
            case "auto":
                await App.Sessions.RequestReassessmentAsync(SessionId);
                _session = await App.Sessions.GetAsync(SessionId) ?? _session;
                AddNote("info", "Automatic selection will reassess your next message when enabled in Settings.");
                break;
            case "lane":
                if (arg.Length == 0) { AddNote("info", $"Current lane: {(_lane.Length == 0 ? "none" : _lane)}. Usage: /lane <name>"); break; }
                var match = Lanes.FirstOrDefault(l => string.Equals(l.Name, arg, StringComparison.OrdinalIgnoreCase));
                if (match is null) AddNote("warn", $"No lane named '{arg}'. Use /lanes to list them.");
                else { await SetLaneAsync(match.Name); AddNote("info", $"Lane set to {match.Name}."); }
                break;
            case "new":
                await NewSessionAsync();
                break;
            case "fork":
                await ForkSessionAsync();
                break;
            case "clear":
                _runner?.ClearTranscript();
                _notes.Clear();
                break;
            case "plan":
                TogglePlanMode();
                AddNote("info", _runner?.PlanMode == true
                    ? "Plan first is on: the next turns investigate read-only and end with a plan to approve."
                    : "Plan first is off.");
                break;
            case "compare":
            case "review":
                if (Lanes.Count(l => !l.IsNative) < 2) { AddNote("warn", "Comparing models needs at least two API model lanes (native CLI lanes cannot answer a plain prompt). Add provider keys in Settings."); break; }
                if (_compare is not null) await _compare.OpenAsync(arg);
                break;
            case "diff":
                _showChanges = true;
                _refreshToken++;
                break;
            case "status":
                _showChanges = true;
                _refreshToken++;
                break;
            case "undo":
            case "rewind":
                await UndoLastTurnAsync();
                break;
            case "secret":
            case "secrets":
            case "forget":
                await HandleSecretCommandAsync(text);
                break;
            case "import-passwords":
                await ImportPasswordsAsync(arg);
                break;
            case "settings":
                Nav.NavigateTo("/settings");
                break;
            case "agents":
                Nav.NavigateTo("/agents");
                break;
            case "schedules":
                Nav.NavigateTo("/schedules");
                break;
            case "self-improvement":
                Nav.NavigateTo("/self-improvement");
                break;
            case "mcp":
            case "hooks":
                Nav.NavigateTo("/settings");
                break;
            case "memory":
                Nav.NavigateTo("/settings/memory");
                break;
            case "compact":
                if (_runner is null) break;
                AddNote("info", _runner.RequestCompaction()
                    ? "The next message compacts the conversation context first (API lanes; native CLIs manage their own context)."
                    : "A turn is running. Ask again when it has finished.");
                break;
            case "cost":
                await ShowCostAsync();
                break;
            case "export":
                await ExportTranscriptAsync(arg);
                break;
            case "init":
                await StartTurnAsync(SwarmCore.Coding.Prompting.CodingInitPrompt.Text, string.IsNullOrEmpty(_lane) ? null : _lane, []);
                break;
            case "history":
                Nav.NavigateTo(AppNavigation.HistoryUrl(query: arg));
                break;
            case "summary":
                Nav.NavigateTo(AppNavigation.HistoryUrl(summary: arg.Length == 0 ? "past week" : arg));
                break;
        }
        StateHasChanged();
        _forceScroll = true;
        return true;
    }

    private void AddNote(string kind, string text, string? details = null) => _notes.Add(new Note(kind, text, details));

    /// <summary>/cost: this session's reported usage, then the tracked API usage for today and overall (read off the renderer).</summary>
    private async Task ShowCostAsync()
    {
        var runner = _runner;
        var session = runner?.ReportedTokens is { } tokens
            ? $"This session: {tokens:N0} reported tokens{(runner!.ReportedCostUsd is { } cost ? $", ${cost:0.0000} reported cost" : ", no cost reported")}."
            : "This session: no provider usage reported yet (subscription CLIs do not always report).";
        var tracker = App.Workspace?.Services.GetService<SwarmCore.Abstractions.LlmRouting.ILlmUsageTracker>();
        if (tracker is null) { AddNote("info", "Usage", session); return; }
        try
        {
            var (today, all) = await Task.Run(async () => (
                await tracker.GetCostSummaryAsync(null, DateTime.UtcNow.Date, null),
                await tracker.GetCostSummaryAsync()));
            var top = string.Join("\n", all.CostByModel.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}: ${kv.Value:0.0000}"));
            AddNote("info", "Usage", session
                + $"\nTracked API usage today: {today.TotalRequests:N0} requests, {today.TotalTokens:N0} tokens, ${today.TotalCost:0.0000}."
                + $"\nTracked API usage overall: {all.TotalRequests:N0} requests, {all.TotalTokens:N0} tokens, ${all.TotalCost:0.0000}."
                + (top.Length == 0 ? "" : "\nBy model:\n" + top));
        }
        catch (Exception ex) { AddNote("warn", "Usage", session + $"\nUsage history is unavailable: {ex.Message}"); }
    }

    /// <summary>/export [file]: the durable transcript as Markdown, under the data folder's exports/ unless a path is given (relative to the repository).</summary>
    private async Task ExportTranscriptAsync(string path)
    {
        var store = App.Workspace?.Services.GetService<SwarmCore.Agent.ToolUseEngine.IAgentSessionStore>();
        if (store is null) { AddNote("warn", "Export is unavailable without an open workspace."); return; }
        try
        {
            var title = _session?.Title;
            var root = App.RepositoryRoot;
            var target = await Task.Run(async () =>
            {
                var messages = await store.LoadAsync(SessionId);
                if (messages.Count == 0) return null;
                var markdown = SwarmCore.Coding.Prompting.TranscriptExporter.ToMarkdown(messages, title);
                var file = !string.IsNullOrWhiteSpace(path) && root is not null
                    ? Path.GetFullPath(path.Trim().Trim('"'), root)
                    : Path.Combine(App.Paths.RootDirectory, "exports", SwarmCore.Coding.Prompting.TranscriptExporter.SuggestFileName(title));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, markdown);
                return file;
            });
            AddNote("info", target is null ? "Nothing to export yet." : $"Transcript exported to {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddNote("warn", $"Could not write the export: {ex.Message}"); }
    }

    private SwarmCode.App.Components.Shared.ModelCompareDialog? _compare;

    /// <summary>/compare: the picked models answer and debate off the renderer; the notice is replaced when the verdict arrives.</summary>
    private async Task RunComparisonAsync(ModelCompareStart start)
    {
        var service = App.Workspace?.Services.GetService(typeof(SwarmCore.Coding.Review.ModelPanelReviewService)) as SwarmCore.Coding.Review.ModelPanelReviewService;
        if (service is null) { AddNote("warn", "Comparing models is unavailable in this workspace."); return; }
        var lanes = Lanes;
        var models = start.Models.Select(p => ModelCompareChoices.ForRequest(p, lanes)).ToList();
        var judge = ModelCompareChoices.ForRequest(start.Judge, lanes);
        var pending = new Note("info", $"Comparing {models.Count} models: each answers, then they review each other's answers and may concede. "
            + $"{judge} judges only if they still disagree. This can take a few minutes.", "Prompt:\n" + start.Question);
        _notes.Add(pending);
        _forceScroll = true;
        StateHasChanged();
        SwarmCore.Coding.Review.ModelPanelReviewResult result;
        try
        {
            result = await Task.Run(() => service.ReviewAsync(new SwarmCore.Coding.Review.ModelPanelReviewRequest(start.Question, ConversationId: SessionId,
                Models: models, Judge: judge)));
        }
        catch (Exception ex)
        {
            _notes.Remove(pending);
            AddNote("error", "The model comparison failed.", ex.Message);
            await InvokeAsync(StateHasChanged);
            return;
        }
        _notes.Remove(pending);
        var headline = !result.Success ? "The model comparison did not run."
            : result.Winner is not { } winner ? "The model comparison finished."
            : result.Debate?.DecidedBy switch
            {
                "consensus" => $"The models agreed: {winner} gave the best answer.",
                "judge" => $"The models disagreed; the judge {result.Debate.Judge} picked {winner}.",
                _ => $"Model comparison: {winner} gave the best answer.",
            };
        AddNote(result.Success ? "info" : "warn", headline, result.Summary);
        _forceScroll = true;
        await InvokeAsync(StateHasChanged);
    }

    // ---- header / lane / panel handlers ------------------------------------

    private async Task OnLaneChanged(ChangeEventArgs e)
    {
        if (e.Value?.ToString() is { Length: > 0 } name) await SetLaneAsync(name);
    }

    private async Task SetLaneAsync(string name)
    {
        _lane = name;
        // Replace the old provider's choices before the first persistence await renders.
        var loadModels = LoadModelsAsync();
        var sessionId = SessionId;
        await App.Sessions.SetUserLaneAsync(sessionId, name,
            _session?.ModelChoices.GetValueOrDefault(name) ?? Lanes.First(l => l.Name == name).Model);
        _session = await App.Sessions.GetAsync(sessionId) ?? _session;
        await loadModels;
    }

    private string? _modelSource;
    private async Task RefreshModelsAsync()
    {
        if (App.Workspace is { } host)
            await Task.Run(() => host.DiscoverLocalModelsAsync());
        await LoadModelsAsync();
    }
    private async Task LoadModelsAsync()
    {
        var lane = Lanes.FirstOrDefault(l => l.Name == _lane);
        var host = App.Workspace;
        if (lane is null || host is null) return;
        var generation = ++_modelGeneration;
        _model = _session?.ModelChoices.GetValueOrDefault(lane.Name) ?? lane.Model;
        _models = [lane.Model]; _modelDetails = []; _catalogEfforts = new Dictionary<string, IReadOnlyList<string>>(); _modelError = null; _modelSource = null; _modelsLoading = true;
        StateHasChanged();
        try
        {
            var models = await Task.Run(async () =>
            {
                using var scope = host.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<CodingModelCatalog>().GetCatalogAsync(lane);
            });
            if (!_disposed && generation == _modelGeneration) { _models = models.Models; _catalogEfforts = models.ReasoningEfforts; _modelDetails = models.Details; _modelSource = models.Source; }
        }
        catch (Exception ex) { if (!_disposed && generation == _modelGeneration) _modelError = ex is OperationCanceledException ? "Model lookup timed out. Refresh to retry or enter a custom model ID." : ex.Message; }
        finally { if (!_disposed && generation == _modelGeneration) _modelsLoading = false; }
    }
    private async Task SaveModelAsync(string model)
    {
        _model = model;
        try { await App.Sessions.SetModelAsync(SessionId, _lane, model); _session = await App.Sessions.GetAsync(SessionId) ?? _session; }
        catch (Exception) { AddNote("warn", "This model will be used for the next message, but the choice could not be saved for future sessions."); }
    }
    private void ToggleArtifacts() { _showArtifacts = !_showArtifacts; if (_showArtifacts) _showChanges = false; }
    private void OpenArtifact(string path) { _artifactPath = path; _showArtifacts = true; _showChanges = false; }

    private async Task RenameAsync(string title)
    {
        try { await App.Sessions.RenameAsync(SessionId, title); } catch { }
        _session = await App.Sessions.GetAsync(SessionId) ?? _session;
    }

    private async Task NewSessionAsync()
    {
        try
        {
            var created = await App.NewSessionAsync();
            Nav.NavigateTo(AppNavigation.SessionUrl(created.Id));
        }
        catch (Exception ex)
        {
            AddNote("error", "Could not create a session.", ex.Message);
        }
    }

    private bool _forking;
    private async Task ForkSessionAsync()
    {
        if (_runner is null || _forking) return;
        _forking = true;
        try
        {
            var fork = await _runner.ForkAsync();
            App.Preferences.Update(p => p.LastSessionId = fork.Id);
            Nav.NavigateTo(AppNavigation.SessionUrl(fork.Id));
        }
        catch (Exception ex) { AddNote("error", "Could not fork the conversation.", ex.Message); }
        finally { _forking = false; }
    }

    private void ToggleChanges()
    {
        _showChanges = !_showChanges;
        if (_showChanges) _showArtifacts = false;
        if (_showChanges) _refreshToken++;
    }

    private void OnChangeCount(int count)
    {
        _changeCount = count;
        RequestRender();
    }

    private void ToggleExpanded(string id)
    {
        if (!_expanded.Remove(id)) _expanded.Add(id);
    }

    // ---- mapping helpers ---------------------------------------------------

    private static ToolCallCardState MapState(ToolCallState state) => state switch
    {
        ToolCallState.Completed => ToolCallCardState.Completed,
        ToolCallState.Failed => ToolCallCardState.Failed,
        ToolCallState.Unknown => ToolCallCardState.Unknown,
        _ => ToolCallCardState.Running,
    };

    private static string NoticeKindName(NoticeKind kind) => kind switch
    {
        NoticeKind.LaneSelected => "lane",
        NoticeKind.HandOff => "handoff",
        NoticeKind.LanesExhausted => "exhausted",
        NoticeKind.Error => "error",
        NoticeKind.Cancelled => "cancelled",
        NoticeKind.Compaction => "compaction",
        NoticeKind.Secret => "secret",
        _ => "info",
    };

    /// <summary>Time the message was sent; the day is the divider above it, the full date is the bubble's tooltip.</summary>
    private static string UserMeta(UserEntry entry) => AppNavigation.ClockTime(entry.AtUtc);

    /// <summary>Time the answer started, then the lane and how long it took.</summary>
    private static string AssistantMeta(AssistantEntry entry)
    {
        var parts = new List<string> { AppNavigation.ClockTime(entry.AtUtc) };
        if (!string.IsNullOrEmpty(entry.LaneName)) parts.Add(entry.LaneName);
        if (entry.Duration is { } d) parts.Add(d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.#}s" : $"{(int)d.TotalMinutes}m {d.Seconds}s");
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        _disposed = true;
        App.Changed -= OnAppChanged;
        DetachIde();
        Detach();
    }

    /// <summary>Temporary session: personal memory is neither read nor written for its next turns.</summary>
    private async Task ToggleTemporaryAsync(ChangeEventArgs e)
    {
        var temporary = e.Value is true || string.Equals(e.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() => App.Sessions.SetTemporaryAsync(SessionId, temporary));
            _session = await App.Sessions.GetAsync(SessionId) ?? _session;
        }
        catch (Exception ex) { DesktopHost.LogToFile($"Could not change the temporary flag: {ex.Message}"); }
    }
}
