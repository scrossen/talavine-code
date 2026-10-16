using System.Text;
using SwarmCode.Hosting.Secrets;
using SwarmCode.Hosting.Web;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.Secrets;

namespace SwarmCode.Ui;

/// <summary>Interactive read-eval-print loop with slash commands.</summary>
public sealed class Repl
{
    private static readonly string[] KeyProviders = ["OpenAI", "Claude", "Gemini", "OpenRouter", "DeepSeek", "Grok", "HuggingFace", "Venice"];

    private readonly ICodingTurnOrchestrator _orchestrator;
    private readonly TurnRunner _runner;
    private readonly ILlmApiKeyProvider _keys;
    private readonly ILlmProviderRegistry _registry;
    private readonly SecretIntake? _secrets;
    private readonly PasswordCsvImporter? _passwordImporter;
    private readonly string _repoRoot;
    private string _conversationId = NewConversationId();
    private string? _lane;
    private bool _explicitLane;
    private CancellationTokenSource? _turnCts;
    private volatile bool _exitRequested;
    private DateTime _lastIdleInterrupt = DateTime.MinValue;

    /// <summary>Built-in command names; a custom command with one of these names is ignored.</summary>
    private static readonly string[] BuiltInCommands =
        ["quit", "exit", "q", "help", "?", "lanes", "lane", "models", "keys", "key", "import-passwords", "new", "clear", "auto", "secret", "secrets", "forget", "hooks",
         "undo", "rewind", "review", "sessions", "resume", "plan", "approve", "compact", "cost", "status", "agents", "mcp", "export", "model", "mode", "init", "permissions"];

    /// <summary>The session to start in (<c>--continue</c>, <c>--resume</c>); a new one when null.</summary>
    public string? InitialConversationId { get; init; }

    /// <summary>The workspace's services (hook approvals, configured hooks). Null: no hooks support.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>SwarmCode's data directory: the person's own commands/ and skills/ folders.</summary>
    public SwarmCode.Hosting.SwarmCodeDataPaths? DataPaths { get; init; }

    public Repl(
        ICodingTurnOrchestrator orchestrator,
        TurnRunner runner,
        ILlmApiKeyProvider keys,
        ILlmProviderRegistry registry,
        string repoRoot,
        string? initialLane,
        string? defaultLane,
        SecretIntake? secrets = null,
        PasswordCsvImporter? passwordImporter = null)
    {
        _orchestrator = orchestrator;
        _runner = runner;
        _keys = keys;
        _registry = registry;
        _repoRoot = repoRoot;
        _secrets = secrets;
        _passwordImporter = passwordImporter;
        _lane = initialLane ?? defaultLane ?? orchestrator.Lanes.FirstOrDefault()?.Name;
        _explicitLane = initialLane is not null;
    }

    public async Task RunAsync()
    {
        if (InitialConversationId is not null) _conversationId = InitialConversationId;
        Console.WriteLine($"Talavine Code - repo: {_repoRoot}");
        if (InitialConversationId is not null)
        {
            var info = _runner.Sessions is { } catalog ? await catalog.GetAsync(_conversationId).ConfigureAwait(false) : null;
            Console.WriteLine($"Resumed session {_conversationId[..Math.Min(8, _conversationId.Length)]}{(info is null ? "" : $" \"{info.Title}\"")} (last activity {info?.LastActivityUtc.ToLocalTime():g}).");
        }
        Console.WriteLine("Type a request, or /help. Ctrl+C cancels a running turn.");
        if (_orchestrator.Lanes.Count == 0)
            Console.WriteLine("No lanes configured. Install the Claude Code or Codex CLI, or set an API key with /key.");

        await OfferRepositoryHooksAsync(onlyWhenPending: true).ConfigureAwait(false);
        await OfferRepositoryPermissionsAsync(onlyWhenPending: true).ConfigureAwait(false);
        await RunSessionStartAsync("startup").ConfigureAwait(false);

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            while (!_exitRequested)
            {
                Console.Write($"swarmcode ({_lane ?? "none"})> ");
                var line = Console.ReadLine();
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith('/'))
                {
                    if (!await HandleCommandAsync(line).ConfigureAwait(false)) break;
                    continue;
                }

                await RunMessageAsync(line).ConfigureAwait(false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            await RunSessionEndAsync("exit").ConfigureAwait(false);
            // A console conversation ends with the process, and so does its undo.
            await _runner.ForgetConversationAsync(_conversationId).ConfigureAwait(false);
        }
    }

    /// <summary>SessionStart hooks for the current conversation; their context rides on the next message.</summary>
    private async Task RunSessionStartAsync(string source)
    {
        if (Services is null) return;
        var result = await SwarmCode.Hosting.Extensions.SwarmCodeTurnHooks.SessionStartAsync(Services, _conversationId, source).ConfigureAwait(false);
        foreach (var warning in result.Warnings) Console.Error.WriteLine($"[hooks] {warning}");
        if (result.Context is not null)
            _runner.PendingContext = _runner.PendingContext is null ? result.Context : _runner.PendingContext + "\n\n" + result.Context;
    }

    /// <summary>SessionEnd hooks for the current conversation (reason: exit or new). Informational.</summary>
    private async Task RunSessionEndAsync(string reason)
    {
        if (Services is null) return;
        foreach (var warning in await SwarmCode.Hosting.Extensions.SwarmCodeTurnHooks.SessionEndAsync(Services, _conversationId, reason).ConfigureAwait(false))
            Console.Error.WriteLine($"[hooks] {warning}");
    }

    /// <summary>
    /// /compare (alias /review): the picked models answer the prompt. With --judge they review each other and may concede,
    /// and the judge decides only a disagreement; without one every model ranks every answer blind. The report is printed.
    /// </summary>
    private async Task RunPanelReviewAsync(string arguments)
    {
        var (question, models, judge) = ParseCompare(arguments);
        if (question.Length == 0)
        {
            Console.WriteLine("Usage: /compare [--models lane[:model],lane[:model],...] [--judge lane[:model]] <prompt>");
            Console.WriteLine("  Without --models the first configured API lanes answer. With --judge the models review each other's answers and may concede;");
            Console.WriteLine("  the judge decides only if they still disagree. Without --judge every model ranks every answer blind.");
            return;
        }
        if (Services?.GetService(typeof(SwarmCore.Coding.Review.ModelPanelReviewService)) is not SwarmCore.Coding.Review.ModelPanelReviewService service)
        {
            Console.WriteLine("Comparing models is unavailable in this host.");
            return;
        }
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Console.Error.WriteLine(judge is null
                ? "[compare] Asking the models, then having them rank the answers blind. Ctrl+C cancels."
                : $"[compare] Asking the models; they review each other's answers and may concede, and {judge} judges a disagreement. Ctrl+C cancels.");
            var result = await service.ReviewAsync(new SwarmCore.Coding.Review.ModelPanelReviewRequest(question, ConversationId: _conversationId,
                Models: models, Judge: judge), cts.Token).ConfigureAwait(false);
            Console.WriteLine(result.Summary);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("[compare] Cancelled."); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>Reads leading --models a,b and --judge c options; the rest is the prompt.</summary>
    public static (string Question, IReadOnlyList<SwarmCore.Coding.Review.ModelPanelPick>? Models, SwarmCore.Coding.Review.ModelPanelPick? Judge) ParseCompare(string arguments)
    {
        var rest = arguments.Trim();
        List<SwarmCore.Coding.Review.ModelPanelPick>? models = null;
        SwarmCore.Coding.Review.ModelPanelPick? judge = null;
        while (rest.StartsWith("--", StringComparison.Ordinal))
        {
            var space = rest.IndexOfAny([' ', '\t']);
            var option = space < 0 ? rest : rest[..space];
            if (option is not ("--models" or "--judge")) break; // anything else starts the prompt
            var after = space < 0 ? "" : rest[space..].TrimStart();
            var valueEnd = after.IndexOfAny([' ', '\t']);
            var value = valueEnd < 0 ? after : after[..valueEnd];
            rest = valueEnd < 0 ? "" : after[valueEnd..].TrimStart();
            if (option == "--judge") judge = SwarmCore.Coding.Review.ModelPanelPick.Parse(value);
            else models = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(SwarmCore.Coding.Review.ModelPanelPick.Parse).OfType<SwarmCore.Coding.Review.ModelPanelPick>().ToList();
        }
        return (rest, models is { Count: > 0 } ? models : null, judge);
    }

    /// <summary>/undo: shows the last turn's changed files and any that changed since, asks, then restores them.</summary>
    private async Task UndoLastTurnAsync()
    {
        if (_runner.Checkpoints is not { } checkpoints || _runner.LastUndoable(_conversationId) is not { } record)
        {
            Console.WriteLine("No turn in this conversation has file changes to undo.");
            return;
        }
        var preview = await checkpoints.PreviewUndoAsync(record.RepositoryRoot, _conversationId, record.CheckpointId).ConfigureAwait(false);
        if (preview.Status is not (SwarmCore.Coding.Checkpoints.TurnUndoStatus.Undone or SwarmCore.Coding.Checkpoints.TurnUndoStatus.ConflictsNeedDecision))
        {
            Console.WriteLine(preview.Summary);
            if (preview.Status == SwarmCore.Coding.Checkpoints.TurnUndoStatus.NothingToUndo) _runner.MarkUndone(record.CheckpointId);
            return;
        }
        Console.WriteLine(preview.Summary);
        foreach (var file in preview.Changes.Where(f => !preview.AlreadyRestored.Contains(f.Path)))
        {
            var verb = file.Kind switch { SwarmCore.Coding.Checkpoints.TurnFileChangeKind.Added => "delete ", SwarmCore.Coding.Checkpoints.TurnFileChangeKind.Deleted => "restore", _ => "revert " };
            Console.WriteLine($"  {verb} {file.Path}{(preview.Conflicts.Contains(file.Path) ? "   (changed since this turn)" : "")}");
        }
        SwarmCore.Coding.Checkpoints.TurnUndoConflictMode mode;
        if (preview.Conflicts.Count == 0)
        {
            Console.Write("Undo these changes? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Nothing changed."); return; }
            mode = SwarmCore.Coding.Checkpoints.TurnUndoConflictMode.Refuse;
        }
        else
        {
            Console.Write("[a] undo anyway (overwrite changed files)  [s] skip changed files  [c] cancel: ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is "a") mode = SwarmCore.Coding.Checkpoints.TurnUndoConflictMode.Force;
            else if (answer is "s") mode = SwarmCore.Coding.Checkpoints.TurnUndoConflictMode.Skip;
            else { Console.WriteLine("Nothing changed."); return; }
        }
        var outcome = await checkpoints.UndoAsync(record.RepositoryRoot, _conversationId, record.CheckpointId, mode).ConfigureAwait(false);
        Console.WriteLine(outcome.Summary);
        foreach (var kept in outcome.Kept) Console.WriteLine($"  kept {kept}");
        if (outcome.BackupRef is { } backup && outcome.Status == SwarmCore.Coding.Checkpoints.TurnUndoStatus.Undone)
            Console.WriteLine($"  (replaced content saved as {backup})");
        if (outcome.Status is SwarmCore.Coding.Checkpoints.TurnUndoStatus.Undone or SwarmCore.Coding.Checkpoints.TurnUndoStatus.NothingToUndo && outcome.Kept.Count == 0)
            _runner.MarkUndone(record.CheckpointId);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        var cts = _turnCts;
        if (cts is not null)
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }

        // Idle at the prompt: first Ctrl+C warns, second (within 5 s) exits.
        if (DateTime.UtcNow - _lastIdleInterrupt < TimeSpan.FromSeconds(5))
        {
            _exitRequested = true;
            e.Cancel = false;
            return;
        }
        _lastIdleInterrupt = DateTime.UtcNow;
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("(press Ctrl+C again or type /quit to exit)");
        Console.Write($"swarmcode ({_lane ?? "none"})> ");
    }

    private async Task RunMessageAsync(string line)
    {
        var lane = _lane;
        var explicitLane = _explicitLane;
        var message = line;
        if (line.StartsWith('@'))
        {
            var space = line.IndexOfAny([' ', '\t']);
            var name = space < 0 ? line[1..] : line[1..space];
            message = space < 0 ? "" : line[(space + 1)..].Trim();
            if (_orchestrator.ResolveLane(name) is null)
            {
                Console.WriteLine($"Unknown lane '{name}'. Use /lanes to list lanes.");
                return;
            }
            if (message.Length == 0)
            {
                Console.WriteLine($"Usage: @{name} <message>");
                return;
            }
            lane = name;
            explicitLane = true;
        }

        using var cts = new CancellationTokenSource();
        _turnCts = cts;
        try
        {
            await _runner.RunAsync(_conversationId, message, lane, cts.Token, explicitLane).ConfigureAwait(false);
            _explicitLane = false; // persisted selection now owns the preference, including later handoffs
        }
        finally
        {
            _turnCts = null;
        }
        _lastIdleInterrupt = DateTime.MinValue;
    }

    /// <returns>false to quit.</returns>
    private async Task<bool> HandleCommandAsync(string line)
    {
        if (_secrets is not null && SecretIntake.IsCommand(line))
        {
            await HandleSecretCommandAsync(line).ConfigureAwait(false);
            return true;
        }

        var parts = line.Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        switch (command)
        {
            case "/quit":
            case "/exit":
            case "/q":
                return false;

            case "/help":
            case "/?":
                PrintHelp();
                break;

            case "/lanes":
                PrintLanes();
                break;

            case "/lane":
                if (parts.Length < 2)
                {
                    Console.WriteLine($"Current lane: {_lane ?? "none"}");
                }
                else if (_orchestrator.ResolveLane(parts[1]) is { } lane)
                {
                    _lane = lane.Name;
                    _explicitLane = true;
                    Console.WriteLine($"Lane set to {lane.Name} ({lane.Provider}/{lane.Model}).");
                }
                else
                {
                    Console.WriteLine($"Unknown lane '{parts[1]}'. Use /lanes to list lanes.");
                }
                break;

            case "/models":
                using (var cts = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try { await ModelLister.PrintAsync(_registry, cts.Token).ConfigureAwait(false); }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                break;

            case "/keys":
                foreach (var provider in KeyProviders)
                {
                    var has = await _keys.HasApiKeyAsync(provider).ConfigureAwait(false);
                    Console.WriteLine($"{provider,-12} {(has ? "set" : "missing")}");
                }
                if (_secrets is not null)
                {
                    var hasBrave = await SwarmCodeWebSearch.IsConfiguredAsync(_secrets.Vault).ConfigureAwait(false);
                    Console.WriteLine($"{"Brave",-12} {(hasBrave ? "set" : "missing")} (web search)");
                }
                break;

            case "/key":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: /key <provider> [value]   (providers: openai, anthropic, gemini, openrouter, deepseek, grok, huggingface, venice, brave (web search); omit the value to type it hidden)");
                    break;
                }
                if (parts[1].ToLowerInvariant() is "brave" or "brave-search")
                {
                    if (_secrets is null) { Console.WriteLine("The vault is unavailable in this host."); break; }
                    var braveKey = parts.Length >= 3 ? parts[2].Trim() : ReadHidden("Brave Search API key: ");
                    if (braveKey.Length == 0) { Console.WriteLine("No key entered."); break; }
                    await SwarmCodeWebSearch.SaveKeyAsync(_secrets.Vault, braveKey).ConfigureAwait(false);
                    Console.WriteLine("Brave Search key saved in the protected vault. web.search is available now.");
                    break;
                }
                var providerName = ResolveKeyProvider(parts[1]);
                if (providerName is null)
                {
                    Console.WriteLine($"Unknown provider '{parts[1]}'.");
                    break;
                }
                var keyValue = parts.Length >= 3 ? parts[2].Trim() : ReadHidden($"{providerName} API key: ");
                if (keyValue.Length == 0) { Console.WriteLine("No key entered."); break; }
                await _keys.SetApiKeyAsync(providerName, keyValue).ConfigureAwait(false);
                Console.WriteLine($"Key saved for {providerName} in the protected vault. Restart Talavine Code (or add a lane in settings.json) to use it as a lane.");
                break;

            case "/import-passwords":
                if (_passwordImporter is null) { Console.WriteLine("Password import is unavailable in this host."); break; }
                if (parts.Length < 2) { Console.WriteLine("Usage: /import-passwords <path-to-export.csv>   (Chrome, Edge, Firefox, Bitwarden, 1Password, LastPass exports)"); break; }
                var csvPath = line[parts[0].Length..].Trim().Trim('"');
                var summary = await _passwordImporter.ImportFileAsync(Path.GetFullPath(csvPath, _repoRoot)).ConfigureAwait(false);
                Console.WriteLine(summary.Describe());
                if (summary.Success && summary.Names.Count > 0)
                    Console.WriteLine("Logins: " + string.Join(", ", summary.Names.Take(20)) + (summary.Names.Count > 20 ? $" (+{summary.Names.Count - 20} more)" : ""));
                break;

            case "/undo":
            case "/rewind":
                await UndoLastTurnAsync().ConfigureAwait(false);
                break;

            case "/compare":
            case "/review":
                await RunPanelReviewAsync(line[parts[0].Length..].Trim()).ConfigureAwait(false);
                break;

            case "/new":
            case "/clear":
                await RunSessionEndAsync("new").ConfigureAwait(false);
                await _runner.ForgetConversationAsync(_conversationId).ConfigureAwait(false);
                _conversationId = NewConversationId();
                _explicitLane = false;
                _runner.PendingContext = null;
                Console.WriteLine("Started a new conversation.");
                await RunSessionStartAsync("new").ConfigureAwait(false);
                break;

            case "/sessions":
                await PrintSessionsAsync().ConfigureAwait(false);
                break;

            case "/resume":
                if (parts.Length < 2) await PrintSessionsAsync().ConfigureAwait(false);
                else await ResumeAsync(parts[1]).ConfigureAwait(false);
                break;

            case "/plan":
                _runner.PlanMode = !_runner.PlanMode;
                Console.WriteLine(_runner.PlanMode
                    ? "Plan mode is on: the next turns investigate read-only and end with a plan. /approve implements it."
                    : "Plan mode is off.");
                break;

            case "/approve":
                if (!_runner.PlanMode) { Console.WriteLine("Plan mode is off; there is no plan waiting for approval."); break; }
                if (!_runner.PlanAwaitingApproval) { Console.WriteLine("No plan has been delivered yet. Ask for one first (plan mode is on)."); break; }
                _runner.PlanMode = false;
                await RunMessageAsync(SwarmCore.Coding.Planning.CodingPlanMode.ApprovalMessage).ConfigureAwait(false);
                break;

            case "/compact":
                _runner.CompactNextTurn = true;
                Console.WriteLine("The next message will compact the conversation's working context first (API lanes; native CLIs manage their own context).");
                break;

            case "/cost":
                await PrintCostAsync().ConfigureAwait(false);
                break;

            case "/status":
                await PrintStatusAsync().ConfigureAwait(false);
                break;

            case "/agents":
                PrintAgents();
                break;

            case "/mcp":
                PrintMcpServers();
                break;

            case "/export":
                await ExportAsync(parts.Length < 2 ? null : line[parts[0].Length..].Trim().Trim('"')).ConfigureAwait(false);
                break;

            case "/model":
                if (parts.Length < 2) { Console.WriteLine($"Model override: {_runner.ModelOverride ?? "none (the lane's configured model)"}. Usage: /model <id> or /model auto"); break; }
                if (string.Equals(parts[1], "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[1], "none", StringComparison.OrdinalIgnoreCase))
                { _runner.ModelOverride = null; Console.WriteLine("Model override cleared."); break; }
                _runner.ModelOverride = parts[1].Trim();
                Console.WriteLine($"Model set to {_runner.ModelOverride} for lane {_lane ?? "none"} (the lane's provider stays).");
                break;

            case "/mode":
                if (parts.Length < 2) { Console.WriteLine($"Permission mode: {SwarmCore.Coding.Permissions.CodingPermissionPolicy.Name(_runner.PermissionMode)}. Usage: /mode auto|ask-risky|ask"); break; }
                if (!SwarmCore.Coding.Permissions.CodingPermissionPolicy.TryParse(parts[1], out var mode)) { Console.WriteLine($"Unknown mode '{parts[1]}'. Use auto, ask-risky or ask."); break; }
                _runner.PermissionMode = mode;
                Console.WriteLine($"Permission mode set to {SwarmCore.Coding.Permissions.CodingPermissionPolicy.Name(mode)}." + (mode != SwarmCore.Coding.Permissions.CodingPermissionMode.Auto ? " Native CLI lanes are skipped while a mode asks." : ""));
                break;

            case "/init":
                await RunMessageAsync(SwarmCore.Coding.Prompting.CodingInitPrompt.Text).ConfigureAwait(false);
                break;
            case "/auto":
                if (_runner.Sessions is { } sessions)
                {
                    await sessions.EnsureAsync(_conversationId, _repoRoot);
                    await sessions.RequestReassessmentAsync(_conversationId);
                    _explicitLane = false;
                    Console.WriteLine("Automatic selection will reassess the next prompt when enabled in Settings.");
                }
                break;

            case "/hooks":
                await OfferRepositoryHooksAsync(onlyWhenPending: false).ConfigureAwait(false);
                break;

            case "/permissions":
                await OfferRepositoryPermissionsAsync(onlyWhenPending: false).ConfigureAwait(false);
                break;

            default:
                if (FindCustomCommand(command[1..]) is { } custom)
                {
                    var arguments = line.Length > parts[0].Length ? line[parts[0].Length..].Trim() : "";
                    await RunMessageAsync(SwarmCore.Coding.Extensions.CustomCommandExpander.Expand(custom, arguments)).ConfigureAwait(false);
                    break;
                }
                Console.WriteLine($"Unknown command '{parts[0]}'. Type /help.");
                // A command that does not exist (and is not a typo of one) is something the person wanted SwarmCode to have.
                SwarmCode.Hosting.Improvement.SelfImprovementCapture.UnknownCommand(
                    Services?.GetService(typeof(SwarmCore.Coding.SelfImprovement.SelfImprovementReporter)) as SwarmCore.Coding.SelfImprovement.SelfImprovementReporter,
                    line, BuiltInCommands.Select(c => c.TrimStart('/')), _conversationId);
                break;
        }
        return true;
    }

    /// <summary>Recent sessions of this repository, newest first, numbered for <c>/resume n</c>.</summary>
    private async Task<IReadOnlyList<SwarmCode.Hosting.WorkspaceSessionInfo>> RecentSessionsAsync()
    {
        if (_runner.Sessions is not { } catalog) return [];
        var sessions = await catalog.ListAsync(_repoRoot).ConfigureAwait(false);
        return sessions.Where(s => s.ParentSessionId is null).OrderByDescending(s => s.LastActivityUtc).Take(15).ToList();
    }

    private async Task PrintSessionsAsync()
    {
        var sessions = await RecentSessionsAsync().ConfigureAwait(false);
        if (sessions.Count == 0) { Console.WriteLine("No saved sessions for this repository yet. Sessions are saved as you work."); return; }
        Console.WriteLine("Recent sessions (newest first); /resume <n> or /resume <id>:");
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var marker = s.Id == _conversationId ? "*" : " ";
            Console.WriteLine($"{marker}{i + 1,3}. {s.Id[..Math.Min(8, s.Id.Length)]}  {s.LastActivityUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {Truncate(s.Title, 60)}{(s.Temporary ? "  (temporary)" : "")}");
        }
    }

    private async Task ResumeAsync(string selector)
    {
        var sessions = await RecentSessionsAsync().ConfigureAwait(false);
        SwarmCode.Hosting.WorkspaceSessionInfo? target = null;
        if (int.TryParse(selector, out var index) && index >= 1 && index <= sessions.Count) target = sessions[index - 1];
        else
        {
            var all = _runner.Sessions is { } catalog ? await catalog.ListAsync(_repoRoot).ConfigureAwait(false) : [];
            var matches = all.Where(s => s.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) target = matches[0];
            else if (matches.Count > 1) { Console.WriteLine($"'{selector}' matches {matches.Count} sessions; give more of the id."); return; }
        }
        if (target is null) { Console.WriteLine($"No session '{selector}'. /sessions lists them."); return; }
        if (target.Id == _conversationId) { Console.WriteLine("That is the current session."); return; }
        await _runner.ForgetConversationAsync(_conversationId).ConfigureAwait(false);
        _conversationId = target.Id;
        _explicitLane = false;
        if (target.LaneName is { } lane && _orchestrator.ResolveLane(lane) is not null) _lane = lane;
        Console.WriteLine($"Resumed session {target.Id[..Math.Min(8, target.Id.Length)]} \"{target.Title}\" (last activity {target.LastActivityUtc.ToLocalTime():g}). The earlier transcript is restored for the next message.");
    }

    private async Task PrintCostAsync()
    {
        Console.WriteLine(_runner.SessionTokens is { } tokens
            ? $"This process: {tokens:N0} reported tokens{(_runner.SessionCostUsd is { } cost ? $", ${cost:0.0000} reported cost" : ", no cost reported")}."
            : "This process: no provider usage reported yet (subscription CLIs do not always report).");
        if (Services?.GetService(typeof(SwarmCore.Abstractions.LlmRouting.ILlmUsageTracker)) is not SwarmCore.Abstractions.LlmRouting.ILlmUsageTracker tracker) return;
        try
        {
            var today = await tracker.GetCostSummaryAsync(null, DateTime.UtcNow.Date, null).ConfigureAwait(false);
            var all = await tracker.GetCostSummaryAsync().ConfigureAwait(false);
            Console.WriteLine($"Tracked API usage today: {today.TotalRequests:N0} requests, {today.TotalTokens:N0} tokens, ${today.TotalCost:0.0000}.");
            Console.WriteLine($"Tracked API usage overall: {all.TotalRequests:N0} requests, {all.TotalTokens:N0} tokens, ${all.TotalCost:0.0000}.");
            foreach (var (model, modelCost) in all.CostByModel.OrderByDescending(kv => kv.Value).Take(5))
                Console.WriteLine($"  {model,-40} ${modelCost:0.0000}");
        }
        catch (Exception ex) { Console.WriteLine($"Usage history is unavailable: {ex.Message}"); }
    }

    private async Task PrintStatusAsync()
    {
        var info = _runner.Sessions is { } catalog ? await catalog.GetAsync(_conversationId).ConfigureAwait(false) : null;
        Console.WriteLine($"Repository:  {_repoRoot}");
        Console.WriteLine($"Session:     {_conversationId}{(info is null ? " (not saved yet)" : $"  \"{info.Title}\"")}");
        Console.WriteLine($"Lane:        {_lane ?? "none"}{(_runner.ModelOverride is { } m ? $"  model {m}" : "")}");
        Console.WriteLine($"Permissions: {SwarmCore.Coding.Permissions.CodingPermissionPolicy.Name(_runner.PermissionMode)}");
        Console.WriteLine($"Plan mode:   {(_runner.PlanMode ? (_runner.PlanAwaitingApproval ? "on (plan delivered; /approve)" : "on") : "off")}");
        Console.WriteLine($"Memory:      {(_runner.Temporary ? "off (temporary)" : "on")}");
        Console.WriteLine($"Usage:       {(_runner.SessionTokens is { } t ? $"{t:N0} reported tokens" : "no usage reported")}{(_runner.SessionCostUsd is { } c ? $", ${c:0.0000}" : "")}");
        if (_runner.CompactNextTurn) Console.WriteLine("Next turn:   compacts the working context first");
        var agents = _runner.Agents?.Team.Children(_conversationId) ?? [];
        if (agents.Count > 0) Console.WriteLine($"Agents:      {agents.Count(a => !a.IsTerminal)} working, {agents.Count(a => a.IsTerminal)} finished (/agents)");
    }

    private void PrintAgents()
    {
        if (_runner.Agents is not { } host) { Console.WriteLine("Agents are unavailable in this host."); return; }
        var children = host.Team.Children(_conversationId);
        if (children.Count == 0) { Console.WriteLine("This session has started no agents."); return; }
        Print(children, 0);

        void Print(IReadOnlyList<SwarmCore.Coding.Agents.AgentRecord> agents, int indent)
        {
            foreach (var agent in agents)
            {
                var elapsed = agent.StartedUtc is { } s ? (agent.FinishedUtc ?? DateTime.UtcNow) - s : TimeSpan.Zero;
                Console.WriteLine($"{new string(' ', indent * 2)}{agent.Id[..Math.Min(8, agent.Id.Length)]}  {agent.State.ToString().ToLowerInvariant(),-11} {Truncate(agent.Title, 50)}"
                    + $"{(agent.Agent is { } role ? $"  [{role}]" : "")}{(agent.Lane is { } lane ? $"  {lane}" : "")}  {elapsed.TotalSeconds:0}s, {agent.ToolCalls} tools"
                    + $"{(agent.Activity is { } activity && !agent.IsTerminal ? $"  {activity}" : "")}{(agent.Worktree is { } wt ? $"  worktree {wt.Branch}" : "")}");
                Print(host.Team.Children(agent.Id), indent + 1);
            }
        }
    }

    private void PrintMcpServers()
    {
        if (DataPaths is null) { Console.WriteLine("MCP servers are unavailable in this host."); return; }
        var file = new SwarmCode.Hosting.Mcp.ExternalMcpServersFile(DataPaths);
        var snapshot = file.Read();
        Console.WriteLine($"External MCP servers ({file.Path}):");
        if (snapshot.Servers.Count == 0) Console.WriteLine("  none configured (Settings > Connected data in the desktop app, or edit the file)");
        foreach (var server in snapshot.Servers)
        {
            var transport = server.Url is { } url ? $"http {url}" : $"stdio {server.Launch?.Command} {string.Join(' ', server.Launch?.Arguments ?? [])}".TrimEnd();
            Console.WriteLine($"  {server.Name,-20} {transport}{(server.Trusted ? "  trusted" : "")}{(server.Required ? "  required" : "")}{(server.BearerTokenReference is null ? "" : "  (bearer from vault)")}");
        }
        foreach (var problem in snapshot.Problems) Console.WriteLine($"  skipped: {problem}");
        var bridge = (Services?.GetService(typeof(Microsoft.Extensions.Options.IOptions<SwarmCore.Coding.CodingOptions>)) as Microsoft.Extensions.Options.IOptions<SwarmCore.Coding.CodingOptions>)?.Value.NativeAgents;
        if (bridge is not null)
            Console.WriteLine(bridge.McpServer is { IsConfigured: true }
                ? "Host tool bridge for native CLIs: available (swarmcode --mcp-server)."
                : $"Host tool bridge for native CLIs: unavailable{(bridge.McpServerUnavailableReason is { } why ? $" ({why})" : "")}.");
    }

    private async Task ExportAsync(string? path)
    {
        if (Services?.GetService(typeof(SwarmCore.Agent.ToolUseEngine.IAgentSessionStore)) is not SwarmCore.Agent.ToolUseEngine.IAgentSessionStore store)
        { Console.WriteLine("Export is unavailable in this host."); return; }
        var messages = await store.LoadAsync(_conversationId).ConfigureAwait(false);
        if (messages.Count == 0) { Console.WriteLine("Nothing to export yet."); return; }
        var info = _runner.Sessions is { } catalog ? await catalog.GetAsync(_conversationId).ConfigureAwait(false) : null;
        var markdown = SwarmCore.Coding.Prompting.TranscriptExporter.ToMarkdown(messages, info?.Title, includeReasoning: false);
        string target;
        if (!string.IsNullOrWhiteSpace(path)) target = Path.GetFullPath(path, _repoRoot);
        else
        {
            var folder = DataPaths is null ? _repoRoot : Path.Combine(DataPaths.RootDirectory, "exports");
            target = Path.Combine(folder, SwarmCore.Coding.Prompting.TranscriptExporter.SuggestFileName(info?.Title));
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, markdown).ConfigureAwait(false);
            Console.WriteLine($"Exported {messages.Count} messages to {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"Could not write the export: {ex.Message}"); }
    }

    private static string Truncate(string text, int max)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private async Task HandleSecretCommandAsync(string line)
    {
        var outcome = await _secrets!.HandleCommandAsync(line).ConfigureAwait(false);
        if (outcome.PromptForValueName is { } name)
        {
            var value = ReadHidden($"Value for {name}: ");
            if (value.Length == 0) { Console.WriteLine("Nothing saved."); return; }
            await _secrets.StoreAsync(name, value).ConfigureAwait(false);
            Console.WriteLine($"Saved {name}. Reference it as {SecretReference.Placeholder(name)}; the value is never shown to the model.");
            return;
        }
        if (outcome.Message is not null) Console.WriteLine(outcome.Message);
    }

    /// <summary>Reads a line without echoing it so secrets stay out of the terminal scrollback.</summary>
    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine()?.Trim() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (key.KeyChar != '\0') sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString().Trim();
    }

    private static string? ResolveKeyProvider(string input) => input.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "anthropic" or "claude" => "Claude",
        "gemini" or "google" => "Gemini",
        "openrouter" => "OpenRouter",
        "deepseek" => "DeepSeek",
        "grok" or "xai" => "Grok",
        "huggingface" or "hf" => "HuggingFace",
        "venice" => "Venice",
        _ => null,
    };

    private void PrintLanes()
    {
        if (_orchestrator.Lanes.Count == 0)
        {
            Console.WriteLine("No lanes configured.");
            return;
        }
        foreach (var lane in _orchestrator.Lanes.OrderBy(l => l.Priority))
        {
            var marker = string.Equals(lane.Name, _lane, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            Console.WriteLine($"{marker} {lane.Name,-12} {lane.Provider}/{lane.Model}  (priority {lane.Priority})");
        }
    }

    private IReadOnlyList<SwarmCore.Coding.Extensions.CustomCommand> CustomCommands()
        => SwarmCore.Coding.Extensions.CustomCommandCatalog.Discover(_repoRoot, DataPaths?.RootDirectory, BuiltInCommands);

    private SwarmCore.Coding.Extensions.CustomCommand? FindCustomCommand(string name)
        => SwarmCore.Coding.Extensions.CustomCommandCatalog.Find(CustomCommands(), name);

    /// <summary>Shows the repository's hooks.json and asks once per content hash before any of it may run.</summary>
    private async Task OfferRepositoryHooksAsync(bool onlyWhenPending)
    {
        if (Services?.GetService(typeof(SwarmCore.Coding.Extensions.ICodingHookSource)) is not SwarmCore.Coding.Extensions.ICodingHookSource source
            || Services.GetService(typeof(SwarmCore.Coding.Extensions.ICodingHookApprovalStore)) is not SwarmCore.Coding.Extensions.ICodingHookApprovalStore approvals)
        {
            if (!onlyWhenPending) Console.WriteLine("Hooks are unavailable in this host.");
            return;
        }
        var inspection = await source.InspectRepositoryAsync(_repoRoot).ConfigureAwait(false);
        if (inspection is null || !inspection.Exists)
        {
            if (!onlyWhenPending) Console.WriteLine("This repository has no .swarmcode/hooks.json. User hooks live in settings.json under Coding:Hooks.");
            return;
        }
        if (inspection.Problem is not null) { Console.WriteLine($"Repository hooks are off: {inspection.Problem}"); return; }
        if (inspection.Approved)
        {
            if (!onlyWhenPending) Console.WriteLine($"Repository hooks are allowed ({inspection.Hooks.Count}). Edit the file and they need approval again.");
            return;
        }
        if (inspection.Hooks.Count == 0) return;
        Console.WriteLine(SwarmCode.Hosting.Extensions.SwarmCodeExtensions.DescribeApprovalRequest(inspection) + " [y/N] ");
        if (Console.IsInputRedirected) { Console.WriteLine("(not allowed: no interactive answer)"); return; }
        var answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            await approvals.ApproveAsync(_repoRoot, inspection.Sha256!).ConfigureAwait(false);
            Console.WriteLine("Repository hooks allowed for this content. Any change to the file asks again.");
        }
        else Console.WriteLine("Repository hooks stay off. Type /hooks to decide later.");
    }

    /// <summary>
    /// Shows the repository's permission rules (.swarmcode/settings.json, .claude/settings.json). Deny rules always apply;
    /// allow rules apply only after the person approves the current content, asked once per content digest.
    /// </summary>
    private async Task OfferRepositoryPermissionsAsync(bool onlyWhenPending)
    {
        if (Services is null)
        {
            if (!onlyWhenPending) Console.WriteLine("Repository permission rules are unavailable in this host.");
            return;
        }
        var inspection = await SwarmCode.Hosting.SwarmCodePermissions.InspectRepositoryAsync(Services, _repoRoot).ConfigureAwait(false);
        if (!inspection.Exists)
        {
            if (!onlyWhenPending) Console.WriteLine("This repository has no .swarmcode/settings.json or .claude/settings.json with permission rules. Your own rules live in settings.json under Coding:Permissions.");
            return;
        }
        foreach (var problem in inspection.Problems) Console.WriteLine($"Repository permission rules skipped: {problem}");
        if (!onlyWhenPending)
        {
            Console.WriteLine("Repository deny rules (always applied): " + (inspection.Deny.Count == 0 ? "none" : string.Join(", ", inspection.Deny)));
            Console.WriteLine("Repository allow rules: " + (inspection.Allow.Count == 0 ? "none" : string.Join(", ", inspection.Allow))
                + (inspection.Allow.Count == 0 ? "" : inspection.Approved ? " (allowed; edit the files and they need approval again)" : " (not allowed)"));
        }
        if (!inspection.NeedsApproval) return;
        Console.WriteLine(SwarmCode.Hosting.SwarmCodePermissions.DescribeApprovalRequest(inspection) + " [y/N] ");
        if (Console.IsInputRedirected) { Console.WriteLine("(not allowed: no interactive answer)"); return; }
        var answer = Console.ReadLine()?.Trim();
        if (Services.GetService(typeof(SwarmCore.Coding.Permissions.ICodingRepositorySettingsApprovalStore)) is not SwarmCore.Coding.Permissions.ICodingRepositorySettingsApprovalStore approvals)
        {
            Console.WriteLine("This host does not store repository permission approvals, so the allow rules stay off.");
            return;
        }
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            await approvals.ApproveAsync(_repoRoot, inspection.Sha256!).ConfigureAwait(false);
            Console.WriteLine("Repository allow rules apply from the next message. Any change to the files asks again.");
        }
        else Console.WriteLine("Repository allow rules stay off (deny rules still apply). Type /permissions to decide later.");
    }

    private void PrintHelp()
    {
        Console.WriteLine("""
            Commands:
              /help                    show this help
              /lanes                   list lanes (* = current)
              /lane <name>             switch the current lane
              /models                  probe providers and list available models
              /keys                    show which provider keys are set (names only)
              /key <provider> [value]  save an API key to the vault (omit the value to type it hidden; brave = web search)
              /secret <name> [value]   save a password/token/key to the vault (omit the value to type it hidden)
              /secrets                 list stored secret names (never values)
              /forget <name>           delete a stored secret
              /import-passwords <csv>  import a browser or password-manager CSV export into the vault
              /undo, /rewind           undo the file changes of the last turn (asks first)
              /compare [--models a,b] [--judge c] <prompt>
                                       send one prompt to several models (lane or lane:model); with a judge they review
                                       each other and may concede, and the judge settles a disagreement (/review = same)
              /model [id|auto]         run the lane with an exact model id (auto = the lane's configured model)
              /mode [auto|ask-risky|ask] permission mode: ask before risky or every side effect
              /plan                    toggle plan mode (investigate read-only, propose a plan); /approve implements it
              /compact                 compact the working context at the start of the next message
              /cost                    tokens and cost reported in this process, plus tracked API usage
              /status                  repository, session, lane, modes and usage at a glance
              /agents                  the agents this session started
              /mcp                     configured external MCP servers and the native tool bridge
              /export [path]           save this session's transcript as Markdown
              /init                    write or improve the repository's AGENTS.md
              /sessions, /resume [n|id] list saved sessions of this repository, or switch to one
              /new, /clear             start a new conversation
              /hooks                   show the repository's hooks and allow or keep them off
              /permissions             show the repository's permission rules (deny always applies) and allow or keep off its allow rules
              /quit                    exit
            Prefix a message with @<lane> to run just that message on that lane; @path/to/file attaches that file.
            Agents use stored secrets as {{secret:name}} in shell commands, environment values, and HTTP headers;
            the value is injected when the tool runs and never shown to a model.
            Add your own /commands as markdown in .swarmcode/commands/ (also .claude/commands) or commands/ in the Talavine Code data folder.
            """);
        var custom = CustomCommands();
        if (custom.Count > 0)
        {
            Console.WriteLine("Custom commands:");
            foreach (var command in custom)
                Console.WriteLine($"  /{command.Name,-23} {command.Description} ({SwarmCore.Coding.Extensions.ExtensionFiles.Label(command.Source)})");
        }
    }

    private static string NewConversationId() => Guid.NewGuid().ToString("N");
}
