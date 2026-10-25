using System.Globalization;
using System.Text;

namespace SwarmCore.RemoteControl;

/// <summary>
/// Executes plain-text remote-control commands against an <see cref="IDesktopAutomation"/>. Keeps the session
/// state COMMANDS.md describes: stable <c>wN</c>/<c>cN</c> aliases, the current window, and a short-lived cache
/// of each window's control list. Never lets an automation exception escape <see cref="Execute(RemoteCommand)"/>;
/// every failure is a <see cref="CommandResult"/> with a <see cref="CommandResult.FailureCategory"/>.
/// </summary>
public sealed partial class RemoteControlSession(IDesktopAutomation automation, RemoteControlSessionOptions options)
{
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly IDesktopAutomation automation = automation ?? throw new ArgumentNullException(nameof(automation));
    private readonly RemoteControlSessionOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Lock gate = new();
    private readonly Dictionary<long, string> windowAliasByHandle = [];
    private readonly Dictionary<string, long> windowHandleByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> lastKnownWindow = [];
    private readonly Dictionary<string, WindowIdentity> windowIdentityByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> controlAliasByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ControlRef> controlByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, (DateTimeOffset At, IReadOnlyList<ControlInfo> Controls)> controlCache = [];
    private int nextWindowAlias = 1;
    private int nextControlAlias = 1;

    private int walkCount;
    private int refreshCount;
    private TimeSpan walkElapsed;

    /// <summary>Walks, walk time and re-reads spent by the most recent <see cref="Execute(RemoteCommand)"/>.</summary>
    public ExecutionStats LastExecutionStats { get; private set; } = new(0, TimeSpan.Zero, 0);

    private IReadOnlyList<ControlInfo> Walk(long windowHandle, ControlQuery query)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return automation.GetControls(windowHandle, query);
        }
        finally
        {
            walkCount++;
            walkElapsed += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
    }

    private ControlInfo? ReadControl(long windowHandle, string runtimeKey)
    {
        refreshCount++;
        return automation.RefreshControl(windowHandle, runtimeKey);
    }

    /// <summary>The window control commands use when they name none.</summary>
    public long? CurrentWindowHandle { get; private set; }

    /// <summary>Clock used for waits and cache ages; tests replace it so nothing sleeps for real.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    internal Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(75);
    /// <summary>How long an action waits for the target to report its new state (accessibility trees lag input by a few ms).</summary>
    internal TimeSpan SettleTimeout { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan SettlePollInterval { get; set; } = TimeSpan.FromMilliseconds(40);
    internal TimeSpan UserIdlePollInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    internal const string UserActiveMessage = "The person is using the keyboard or mouse. Wait a few seconds and retry the same step; do not re-plan.";

    /// <summary>
    /// Holds an action that takes focus or sends input until the person has been idle for
    /// <see cref="RemoteControlSessionOptions.UserIdleRequired"/>, or fails with user-active after
    /// <see cref="RemoteControlSessionOptions.UserIdleWaitLimit"/>. A platform that cannot tell (null) never waits.
    /// </summary>
    private void EnsureUserIdle()
    {
        var required = guardRequired;
        if (required <= TimeSpan.Zero) return;
        // The first command of a batch (or a single command) gets a second wait window before failing.
        var limit = options.UserIdleWaitLimit * (firstStepOfBatch ? 2 : 1);
        var start = Now();
        while (true)
        {
            var idle = automation.GetUserIdleTime();
            if (idle is null || idle >= required) return;
            if (Now() - start >= limit)
                throw new SessionException(
                    $"{UserActiveMessage} (Last input {(long)idle.Value.TotalMilliseconds} ms ago; the guard needs {(long)required.TotalMilliseconds} ms idle.)",
                    FailureCategories.UserActive);
            Sleep(UserIdlePollInterval);
        }
    }

    private TimeSpan guardRequired = options?.UserIdleRequired ?? TimeSpan.FromMilliseconds(350);
    private bool firstStepOfBatch = true;

    /// <summary>The idle time the guard currently requires (zero when off).</summary>
    public TimeSpan UserIdleRequired => guardRequired;

    private sealed record ControlRef(string RuntimeKey, long WindowHandle, string Description);

    /// <summary>What an alias was given to: native handles are reused by the OS, so the process id is part of the identity.</summary>
    private sealed record WindowIdentity(long Handle, int ProcessId, string ProcessName, bool Gone);

    private sealed class SessionException(string message, string category) : Exception(message)
    {
        public string Category { get; } = category;
    }

    public static string HelpText { get; } = """
        Remote control: one command per line. Quoted text is taken literally.
        Ids: screens 1, 2 ...  windows w1, w2 ... (or a native handle)  controls c1, c2 ...
        Aliases never change during the session. Control commands use the current window.

        Session
          help | ?                          this list
          quit | exit                       end the session
          where                             mouse, control under it, focused control, foreground window
          wait 2 | wait 500 ms              pause between steps

        Waiting for state (default timeout 30 s; add "up to 10 seconds")
          wait for window "Save As"         until a window appears
          wait until w5 is gone             until a window closes
          wait for the Save button in w3    until a control appears
          wait until c12 says Done | wait for c12 to change | wait until c12 is enabled

        Screens
          screens                           list monitors
          screenshot                        capture the primary screen
          screenshot screen 2 | screenshot all screens
          screenshot region 100,100 800x600

        Windows
          windows | apps                    list windows with their ids
          find window chrome                find by title or process name
          focus w3 | switch to notepad      bring a window to the front
          screenshot w3 | screenshot chrome
          screenshot w3 with controls       capture and list its controls

        Controls
          controls in w3                    named controls with values
          all controls in w3                include unnamed structural elements
          buttons in chrome | links in w3 | controls in w3 matching save
          inspect c12                       every property of one control
          click c12 | click link Next Page in chrome | click "Save" in w3
          double click c4 | right click c4 | click at 100,200 on screen 2
          invoke c12 | toggle c7 | check c7 | uncheck c7 | select c9 | expand c3 | collapse c3

        Input guard (actions wait while the person types or moves the mouse)
          guard off | guard on | guard 500 ms

        Values and keys
          set c12 to "This is fun!"         set a value (falls back to typing)
          set the Email text box in w3 to bob@example.com
          type "hello" into the Search field in chrome
          clear c12
          type hello world                  type into whatever has focus
          press enter | press ctrl+s | keys alt+f4 | hotkey win+r
          press tab 3 times | press ctrl+c then ctrl+v | select all

        Mouse
          move mouse to 100,200 | hover c12 | hover over the Save button in w3
          shift click c3 | drag c3 to c4 | drag from 10,10 to 50,50
          scroll down 5 | scroll up 3 in chrome | scroll c12 down 2 | scroll left 2
        """;

    public CommandResult Execute(string text) => Execute(RemoteCommandParser.Parse(text));

    /// <summary>
    /// Runs several commands in order: one per line, or separated by <c>" ; "</c> (never inside quotes). Stops at
    /// the first failure unless that step starts with <c>try </c>, and after <c>quit</c>. Returns one result per
    /// step that ran.
    /// </summary>
    public IReadOnlyList<CommandResult> ExecuteAll(string text) => ExecuteAll(text, null);

    /// <summary>
    /// True when a batch did what was asked: every step ran (or the batch ended at <c>quit</c>) and every failed
    /// step was a <c>try</c> step. <paramref name="steps"/> is <see cref="RemoteCommandParser.SplitCommands"/> of the same text.
    /// </summary>
    public static bool BatchSucceeded(IReadOnlyList<string> steps, IReadOnlyList<CommandResult> results)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(results);
        var finished = results.Count == steps.Count || (results.Count > 0 && results[^1].QuitRequested);
        return finished && results.Select((r, i) => r.Succeeded || (i < steps.Count && RemoteCommandParser.IsTryStep(steps[i]))).All(ok => ok);
    }

    /// <summary>As <see cref="ExecuteAll(string)"/>, calling <paramref name="onResult"/> with each step, its result and its duration as it completes.</summary>
    public IReadOnlyList<CommandResult> ExecuteAll(string text, Action<string, CommandResult, TimeSpan>? onResult)
    {
        var results = new List<CommandResult>();
        lock (gate)
        {
            var index = 0;
            foreach (var step in RemoteCommandParser.SplitCommands(text ?? ""))
            {
                firstStepOfBatch = index++ == 0;
                var tolerant = RemoteCommandParser.IsTryStep(step);
                var line = tolerant ? step[4..].TrimStart() : step;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = Execute(line);
                results.Add(result);
                onResult?.Invoke(line, result, System.Diagnostics.Stopwatch.GetElapsedTime(started));
                if (result.QuitRequested || (!result.Succeeded && !tolerant)) break;
            }
            firstStepOfBatch = true;
        }
        return results;
    }

    public CommandResult Execute(RemoteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (gate)
        {
            (walkCount, refreshCount, walkElapsed) = (0, 0, TimeSpan.Zero);
            try
            {
                return Dispatch(command);
            }
            catch (SessionException ex)
            {
                return Failure(command.Kind, ex.Message, ex.Category);
            }
            catch (DesktopAutomationException ex)
            {
                return Failure(command.Kind, ex.Message, ex.Category ?? FailureCategories.OsRefused);
            }
            catch (ArgumentException ex)
            {
                return Failure(command.Kind, $"{ex.Message} Type help for the command list.", FailureCategories.NotUnderstood);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Failure(command.Kind, $"{command.Kind} failed: {ex.Message}", FailureCategories.OsRefused);
            }
            finally
            {
                LastExecutionStats = new ExecutionStats(walkCount, walkElapsed, refreshCount);
            }
        }
    }

    private static CommandResult Failure(CommandKind kind, string message, string category) =>
        CommandResult.Fail(message, category) with { Data = new CommandFailure(kind, message, category) };

    private CommandResult Dispatch(RemoteCommand cmd) => cmd.Kind switch
    {
        CommandKind.Unknown => Failure(cmd.Kind, $"{cmd.Error ?? "I did not understand that."} Type help for the command list.", FailureCategories.NotUnderstood),
        CommandKind.Help => new CommandResult(true, HelpText) { Data = new HelpInfo(HelpText) },
        CommandKind.Quit => new CommandResult(true, "Bye.") { QuitRequested = true, Data = new ActionReceipt("quit", "none", "verified") },
        CommandKind.ListDisplays => ListDisplays(),
        CommandKind.ListWindows => ListWindows(),
        CommandKind.FindWindow => FindWindow(cmd),
        CommandKind.FocusWindow => FocusWindow(cmd),
        CommandKind.Screenshot => Screenshot(cmd),
        CommandKind.ListControls or CommandKind.FindControl => ListControls(cmd),
        CommandKind.Inspect => Inspect(cmd),
        CommandKind.Click => Click(cmd),
        CommandKind.Invoke => Invoke(cmd),
        CommandKind.SetValue => SetValue(cmd),
        CommandKind.TypeText => TypeText(cmd),
        CommandKind.PressKeys => PressKeys(cmd),
        CommandKind.MoveMouse => MoveMouse(cmd),
        CommandKind.Scroll => Scroll(cmd),
        CommandKind.Wait => Wait(cmd),
        CommandKind.WaitFor => WaitFor(cmd),
        CommandKind.Where => Where(),
        CommandKind.Drag => Drag(cmd),
        CommandKind.SetGuard => SetGuard(cmd),
        _ => Failure(cmd.Kind, $"{cmd.Kind} is not supported.", FailureCategories.Unsupported),
    };

    // ------------------------------------------------------------------ aliases and descriptions

    /// <summary>The alias of a handle seen before, or a new one when the handle's process is unknown.</summary>
    private string WindowAlias(long handle)
    {
        if (windowAliasByHandle.TryGetValue(handle, out var alias)) return alias;
        return NewWindowAlias(handle, 0, "");
    }

    /// <summary>
    /// The alias of a window. When the OS has reused a handle for a different process, the old alias is marked gone
    /// and the new window gets a new alias, so "w2" never silently points at someone else's window.
    /// </summary>
    private string WindowAlias(WindowInfo w)
    {
        if (windowAliasByHandle.TryGetValue(w.Handle, out var alias) && windowIdentityByAlias.TryGetValue(alias, out var id))
        {
            if (id.ProcessId == 0 || id.ProcessId == w.ProcessId)
            {
                if (id.ProcessId == 0) windowIdentityByAlias[alias] = id with { ProcessId = w.ProcessId, ProcessName = w.ProcessName };
                return alias;
            }
            windowIdentityByAlias[alias] = id with { Gone = true };
            windowAliasByHandle.Remove(w.Handle);
        }
        return NewWindowAlias(w.Handle, w.ProcessId, w.ProcessName);
    }

    private string NewWindowAlias(long handle, int processId, string processName)
    {
        var alias = "w" + (nextWindowAlias++).ToString(CultureInfo.InvariantCulture);
        windowAliasByHandle[handle] = alias;
        windowHandleByAlias[alias] = handle;
        windowIdentityByAlias[alias] = new WindowIdentity(handle, processId, processName, false);
        return alias;
    }

    /// <summary>True when the live window is still the one the alias was given to.</summary>
    private bool IsSameWindow(string alias, WindowInfo live) =>
        !windowIdentityByAlias.TryGetValue(alias, out var id) || (!id.Gone && (id.ProcessId == 0 || id.ProcessId == live.ProcessId));

    private WindowEntry Entry(WindowInfo w)
    {
        var alias = WindowAlias(w);
        lastKnownWindow[w.Handle] = $"{w.ProcessName}: {w.Title}";
        return new WindowEntry(alias, w);
    }

    /// <summary>
    /// The alias of a control, binding it to its window. An existing binding is never moved to another window, and a
    /// control whose window is unknown (handle 0) gets no new alias.
    /// </summary>
    private string ControlAlias(ControlInfo c, long windowHandle)
    {
        if (!controlAliasByKey.TryGetValue(c.RuntimeKey, out var alias))
        {
            if (windowHandle == 0) return "";
            alias = "c" + (nextControlAlias++).ToString(CultureInfo.InvariantCulture);
            controlAliasByKey[c.RuntimeKey] = alias;
        }
        if (windowHandle != 0 && (!controlByAlias.TryGetValue(alias, out var existing) || existing.WindowHandle == windowHandle))
            controlByAlias[alias] = new ControlRef(c.RuntimeKey, windowHandle, $"{c.ControlType} '{c.Name}'");
        return alias;
    }

    /// <summary>Structured output never carries a password field's value.</summary>
    private ControlEntry Entry(ControlInfo c, long windowHandle) =>
        new(ControlAlias(c, windowHandle), c.IsPassword ? c with { Value = null } : c);

    private string Describe(WindowInfo w) => $"{WindowAlias(w)} ({w.ProcessName}: {w.Title})";

    private string Describe(ControlInfo c, long windowHandle) =>
        $"{ControlAlias(c, windowHandle)} {c.ControlType}{(string.IsNullOrEmpty(c.Name) ? "" : $" '{c.Name}'")}";

    private static string ShowValue(ControlInfo c) => c.IsPassword ? "(hidden)" : c.Value is null ? "(no value)" : $"\"{c.Value}\"";

    private static string StateOf(ControlInfo c)
    {
        var parts = new List<string> { $"value {ShowValue(c)}" };
        if (c.IsToggled is { } on) parts.Add(on ? "on" : "off");
        if (c.IsSelected is true) parts.Add("selected");
        if (!c.IsEnabled) parts.Add("disabled");
        if (c.IsOffscreen) parts.Add("offscreen");
        if (c.HasKeyboardFocus) parts.Add("focused");
        return string.Join(", ", parts);
    }

    private static string Lines(IEnumerable<string> notes) => string.Concat(notes.Select(n => "\n" + n));

    // ------------------------------------------------------------------ window resolution

    private sealed record WindowResolution(WindowInfo Window, IReadOnlyList<WindowInfo> Alternatives, IReadOnlyList<string> Notes);

    private WindowResolution ResolveWindow(WindowSelector? selector, bool acting)
    {
        if (selector is null || selector.IsEmpty)
        {
            if (CurrentWindowHandle is { } current)
            {
                var currentAlias = WindowAlias(current);
                if (automation.GetWindow(current) is { } cw && IsSameWindow(currentAlias, cw))
                    return new WindowResolution(cw, [], []);
                // Never fall back silently: the window the user was working in is gone.
                CurrentWindowHandle = null;
                throw new SessionException(
                    $"{currentAlias} ({lastKnownWindow.GetValueOrDefault(current, "unknown")}) has closed. Name a window, or type windows to list them.",
                    FailureCategories.WindowGone);
            }
            var windows = automation.GetWindows();
            var fg = windows.FirstOrDefault(w => w.IsForeground) ?? windows.FirstOrDefault()
                     ?? throw new SessionException("No windows are open.", FailureCategories.NotFound);
            return new WindowResolution(fg, [], [$"No window named; using the foreground window {Describe(fg)}."]);
        }

        WindowResolution resolution;
        if (selector.Alias is { } alias)
        {
            if (!windowHandleByAlias.TryGetValue(alias, out var handle))
                throw new SessionException($"{alias} is not a known window. Type windows to list them.", FailureCategories.NotFound);
            var w = automation.GetWindow(handle);
            if (w is null || !IsSameWindow(alias, w))
                throw new SessionException(
                    $"{alias} ({lastKnownWindow.GetValueOrDefault(handle, "unknown")}) has closed. Type windows to refresh the list.", FailureCategories.WindowGone);
            resolution = new WindowResolution(w, [], []);
        }
        else if (selector.Handle is { } h)
        {
            var w = automation.GetWindow(h);
            if (w is null && windowHandleByAlias.TryGetValue("w" + h.ToString(CultureInfo.InvariantCulture), out var aliased))
                w = automation.GetWindow(aliased);
            if (w is null)
            {
                var gone = windowAliasByHandle.ContainsKey(h);
                throw new SessionException($"No window has handle {h}.", gone ? FailureCategories.WindowGone : FailureCategories.NotFound);
            }
            resolution = new WindowResolution(w, [], []);
        }
        else
        {
            var windows = automation.GetWindows();
            var notes = new List<string>();
            var matches = MatchWindows(selector.NameText!, windows);
            if (matches.Count == 0 && !string.IsNullOrWhiteSpace(selector.Hint))
            {
                matches = MatchWindows(selector.Hint, windows);
                if (matches.Count > 0) notes.Add($"No window matches '{selector.NameText}'; using '{selector.Hint}' instead.");
            }
            if (matches.Count == 0)
                throw new SessionException($"No window matches '{selector.NameText}'. Type windows to list them.", FailureCategories.NotFound);
            if (matches.Count > 1 && matches[0].Score == matches[1].Score)
            {
                // Tie-break for reads and actions alike: the current window, then the foreground window.
                var tied = matches.TakeWhile(m => m.Score == matches[0].Score).Select(m => m.Window).ToList();
                var current = tied.FirstOrDefault(w => w.Handle == CurrentWindowHandle);
                var foreground = tied.FirstOrDefault(w => w.IsForeground);
                var chosen = current ?? foreground;
                if (chosen is null && acting)
                {
                    throw new SessionException(
                        $"Several windows match '{selector.NameText}' equally well; name one by alias:\n{string.Join("\n", tied.Select(w => "  " + Describe(w)))}",
                        FailureCategories.Ambiguous);
                }
                chosen ??= tied[0];
                var why = chosen == current ? "current window" : chosen == foreground ? "foreground window" : "first in z-order";
                notes.Add($"Using {WindowAlias(chosen.Handle)} ({why}); also matching: {string.Join(", ", tied.Where(w => w != chosen).Select(w => WindowAlias(w.Handle)))}.");
                matches = [.. matches.Where(m => m.Window == chosen), .. matches.Where(m => m.Window != chosen)];
            }
            resolution = new WindowResolution(matches[0].Window, matches.Skip(1).Select(m => m.Window).ToList(), notes);
            return WithHintCheck(resolution, selector);
        }
        return WithHintCheck(resolution, selector);
    }

    private WindowResolution WithHintCheck(WindowResolution r, WindowSelector selector)
    {
        if (string.IsNullOrWhiteSpace(selector.Hint) || selector.NameText is not null && r.Notes.Count > 0) return r;
        var words = selector.Hint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var w = r.Window;
        if (words.Any(word => w.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || w.ProcessName.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return r;
        return r with { Notes = [.. r.Notes, $"Note: '{selector.Hint}' does not match {Describe(w)}."] };
    }

    private static List<(WindowInfo Window, int Score)> MatchWindows(string name, IReadOnlyList<WindowInfo> windows)
    {
        var n = name.Trim();
        static string Proc(WindowInfo w) => w.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? w.ProcessName[..^4] : w.ProcessName;
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Score(WindowInfo w)
        {
            const StringComparison ci = StringComparison.OrdinalIgnoreCase;
            if (Proc(w).Equals(n, ci)) return 6;
            if (w.Title.Equals(n, ci)) return 5;
            if (w.Title.StartsWith(n, ci)) return 4;
            if (Proc(w).Contains(n, ci)) return 3;
            if (w.Title.Contains(n, ci)) return 2;
            // Every word appears somewhere in the title or process name ("Remote Control Smoke chrome").
            var haystack = $"{w.Title} {w.ProcessName}";
            if (words.Length > 1 && words.All(word => haystack.Contains(word, ci))) return 1;
            return 0;
        }
        return windows.Select((w, i) => (Window: w, Score: Score(w), Index: i))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Window.IsForeground).ThenBy(x => x.Index)
            .Select(x => (x.Window, x.Score)).ToList();
    }

    // ------------------------------------------------------------------ control resolution

    private IReadOnlyList<ControlInfo> Controls(long windowHandle, bool fresh = false)
    {
        if (!fresh && TryCachedControls(windowHandle, out var cached)) return cached;
        var list = Walk(windowHandle, new ControlQuery());
        controlCache[windowHandle] = (Now(), list);
        return list;
    }

    private void Invalidate(long windowHandle) => controlCache.Remove(windowHandle);

    private bool TryCachedControls(long windowHandle, out IReadOnlyList<ControlInfo> controls)
    {
        if (controlCache.TryGetValue(windowHandle, out var cached) && Now() - cached.At <= options.ListingFreshness)
        {
            controls = cached.Controls;
            return true;
        }
        controls = [];
        return false;
    }

    /// <summary>A walk filtered to what the selector can match, so adapters can skip everything else.</summary>
    private static ControlQuery LookupQuery(ControlSelector selector, bool listing = false) => new()
    {
        ControlTypes = selector.ControlType is { } t ? (listing ? ControlTypes.ListingFamily(t) : ControlTypes.Family(t)) : null,
        NameContains = SignificantWord(selector.IdText ?? selector.NameText),
    };

    /// <summary>The most selective word of a name (the longest), used as the walk's substring filter.</summary>
    internal static string? SignificantWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 1 ? words[0] : words.OrderByDescending(w => w.Length).First();
    }

    private sealed record ControlResolution(WindowInfo Window, ControlInfo Control, IReadOnlyList<string> Notes);

    private ControlResolution ResolveControl(ControlSelector? selector, WindowSelector? windowSelector, bool acting, bool freshList = false,
        bool exactName = false, ControlQuery? walkQuery = null) =>
        ResolveControlCore(selector, windowSelector, acting, freshList, exactName, walkQuery);

    private ControlResolution ResolveControlCore(ControlSelector? selector, WindowSelector? windowSelector, bool acting, bool freshList, bool exactName,
        ControlQuery? walkQuery)
    {
        if (selector is null || selector.IsEmpty && selector.Ordinal is null)
            throw new SessionException("No control was named. Try: click c12, or click link Next Page in chrome.", FailureCategories.NotUnderstood);

        if (selector.Alias is { } alias) return ResolveAlias(alias, windowSelector);
        if (selector.IdText is { } id && id.Length > 1 && (id[0] is 'c' or 'C') && id[1..].All(char.IsAsciiDigit))
            return ResolveAlias(id.ToLowerInvariant(), windowSelector);

        var win = ResolveWindow(windowSelector, acting);
        var h = win.Window.Handle;
        if (walkQuery is not null) return MatchControl(win, selector, Walk(h, walkQuery), acting, exactName);

        // A fresh listing is the fast path; a miss walks once, filtered to the selector's type and name so the adapter
        // can skip everything else; only a bare name (no type) that still misses gets one unfiltered walk.
        if (!freshList && TryCachedControls(h, out var cached))
        {
            try { return MatchControl(win, selector, cached, acting, exactName); }
            catch (SessionException ex) when (ex.Category == FailureCategories.NotFound) { }
        }
        var bareName = selector.ControlType is null && (selector.NameText ?? selector.IdText) is not null;
        try
        {
            return MatchControl(win, selector, Walk(h, LookupQuery(selector)), acting, exactName);
        }
        catch (SessionException ex) when (ex.Category == FailureCategories.NotFound && bareName)
        {
            return MatchControl(win, selector, Controls(h, fresh: true), acting, exactName);
        }
    }

    private ControlResolution MatchControl(WindowResolution win, ControlSelector selector, IReadOnlyList<ControlInfo> list, bool acting, bool exactName)
    {
        var notes = win.Notes.ToList();

        if (selector.IdText is { } idText)
        {
            var byId = list.Where(c => string.Equals(c.AutomationId, idText, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count == 1) return new ControlResolution(win.Window, byId[0], notes);
            if (byId.Count > 1 && acting) throw Ambiguous(idText, win.Window, byId);
            if (byId.Count > 1) return new ControlResolution(win.Window, byId[0], notes);
            selector = new ControlSelector(NameText: idText);
        }

        var ranked = Rank(list, selector.ControlType, selector.NameText);
        if (exactName && selector.NameText is not null) ranked = ranked.Where(r => r.Score == 4).ToList();
        // The type is a hard filter: a button never stands in for the edit the user named.
        if (ranked.Count == 0)
        {
            var what = (selector.ControlType, selector.NameText) switch
            {
                ({ } type, { } name) => $"No {type} control matching '{name}'",
                ({ } type, null) => $"No {type} control",
                (null, { } name) => $"No control matching '{name}'",
                _ => $"No control matches {selector}",
            };
            var hint = selector.ControlType is { } t ? $"List that type with: find {t} in {WindowAlias(win.Window.Handle)}." : "Type controls to see them.";
            if (automation.LastWalkWasTruncated)
            {
                var reason = automation.LastWalkTruncationReason ?? "the walk stopped early";
                throw new SessionException($"{what} in {Describe(win.Window)}: not reached (walk truncated: {reason}).", FailureCategories.NotFound);
            }
            throw new SessionException($"{what} in {Describe(win.Window)}. {hint}", FailureCategories.NotFound);
        }

        if (selector.Ordinal is { } ordinal)
        {
            var inOrder = ranked.OrderBy(r => r.Order).ToList();
            if (ordinal < 1 || ordinal > inOrder.Count)
                throw new SessionException($"There are only {inOrder.Count} matching controls in {Describe(win.Window)}.", FailureCategories.NotFound);
            return new ControlResolution(win.Window, inOrder[ordinal - 1].Control, notes);
        }

        // Web pages repeat names constantly, so a tie among equally good matches is broken rather than refused:
        // on-screen and enabled first, then keyboard focus, then the largest clickable area, then document order.
        var top = ranked.Where(r => r.Score == ranked[0].Score)
            .OrderBy(r => r.Penalty)
            .ThenByDescending(r => r.Control.HasKeyboardFocus)
            .ThenByDescending(r => r.Control.Bounds.IsEmpty ? 0L : (long)r.Control.Bounds.Width * r.Control.Bounds.Height)
            .ThenBy(r => r.Order)
            .ToList();
        if (top.Count > 1)
        {
            // Refuse only when the tied candidates are genuinely different things: different types, or a
            // non-exact selector that matched different names ("Sav" -> "Save" and "Save As...").
            var differentTypes = top.Select(r => r.Control.ControlType).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            var differentNames = top[0].Score < 4 && top.Select(r => r.Control.Name ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (acting && (differentTypes || differentNames))
                throw Ambiguous(selector.ToString(), win.Window, top.Select(r => r.Control).ToList());
            notes.Add($"Using {ControlAlias(top[0].Control, win.Window.Handle)}; also matching: {string.Join(", ", top.Skip(1).Take(10).Select(r => ControlAlias(r.Control, win.Window.Handle)))}{(top.Count > 11 ? ", ..." : "")}.");
        }
        return new ControlResolution(win.Window, top[0].Control, notes);
    }

    private SessionException Ambiguous(string what, WindowInfo window, IReadOnlyList<ControlInfo> candidates)
    {
        var lines = candidates.Take(20).Select(c => $"  {Describe(c, window.Handle)} at {c.Bounds}{(c.IsEnabled ? "" : " (disabled)")}");
        var more = candidates.Count > 20 ? $"\n  ... and {candidates.Count - 20} more" : "";
        return new SessionException($"Several controls match {what} in {Describe(window)}; name one by alias:\n{string.Join("\n", lines)}{more}", FailureCategories.Ambiguous);
    }

    private ControlResolution ResolveAlias(string alias, WindowSelector? windowSelector = null)
    {
        if (!controlByAlias.TryGetValue(alias, out var r))
            throw new SessionException($"{alias} is not a known control. List controls first (controls in w3).", FailureCategories.NotFound);
        var window = automation.GetWindow(r.WindowHandle) ?? throw new SessionException(
            $"The window holding {alias} ({r.Description}) has closed.", FailureCategories.WindowGone);
        if (windowSelector is { IsEmpty: false })
        {
            // "click c1 in w2" where c1 lives in w1: refuse rather than act in a window the user did not name.
            var named = ResolveWindow(windowSelector, acting: false).Window;
            if (named.Handle != window.Handle)
                throw new SessionException(
                    $"{alias} ({r.Description}) belongs to {Describe(window)}, not {Describe(named)}. List controls in {WindowAlias(named)} to pick one there.",
                    FailureCategories.NotFound);
        }
        var c = ReadControl(r.WindowHandle, r.RuntimeKey) ?? throw new SessionException(
            $"{alias} ({r.Description}) no longer exists. List controls again.", FailureCategories.ControlGone);
        return new ControlResolution(window, c, []);
    }

    private sealed record Ranked(ControlInfo Control, int Score, int Penalty, int Order);

    private static List<Ranked> Rank(IReadOnlyList<ControlInfo> list, string? type, string? name, bool listing = false)
    {
        var words = name?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        int Score(ControlInfo c)
        {
            if (name is null) return 1;
            const StringComparison ci = StringComparison.OrdinalIgnoreCase;
            var n = c.Name ?? "";
            if (n.Equals(name, ci)) return 4;
            if (n.StartsWith(name, ci)) return 3;
            if (n.Contains(name, ci)) return 2;
            if (words.Length > 1 && words.All(w => n.Contains(w, ci))) return 1;
            return 0;
        }
        return list.Select((c, i) => new Ranked(c, Score(c), (c.IsEnabled ? 0 : 1) + (c.IsOffscreen || c.Bounds.IsEmpty ? 1 : 0), i))
            .Where(r => r.Score > 0 && (type is null || (listing ? ControlTypes.MatchesForListing(r.Control.ControlType, type) : ControlTypes.Matches(r.Control.ControlType, type))))
            .OrderByDescending(r => r.Score).ThenBy(r => r.Penalty).ThenBy(r => r.Order)
            .ToList();
    }

    private void FocusOrFail(WindowInfo w)
    {
        if (automation.FocusWindow(w.Handle)) return;
        throw w.IsElevated
            ? new SessionException($"Could not bring {Describe(w)} to the front: it runs elevated. Run the remote control elevated to drive it.", FailureCategories.TargetElevated)
            : new SessionException($"Could not bring {Describe(w)} to the front: the OS refused activation.", FailureCategories.OsRefused);
    }

    private ScreenPoint ToScreen(RemoteCommand cmd, ScreenPoint p)
    {
        if (!cmd.PointIsDisplayRelative || cmd.DisplayIndex is not { } index) return p;
        var display = automation.GetDisplays().FirstOrDefault(d => d.Index == index)
                      ?? throw new SessionException($"There is no screen {index}. Type screens to list them.", FailureCategories.NotFound);
        return new ScreenPoint(p.X + display.Bounds.Left, p.Y + display.Bounds.Top);
    }

    // ------------------------------------------------------------------ listings

    private CommandResult ListDisplays()
    {
        var displays = automation.GetDisplays();
        var table = TextTable.Render(["Screen", "Device", "Primary", "Bounds", "Work area", "Scale"],
            displays.Select(d => (IReadOnlyList<string?>)[d.Index.ToString(CultureInfo.InvariantCulture), d.DeviceName, d.IsPrimary ? "yes" : "",
                d.Bounds.ToString(), d.WorkArea.ToString(), d.ScaleFactor.ToString("0.##", CultureInfo.InvariantCulture)]));
        return new CommandResult(true, $"{displays.Count} screen{(displays.Count == 1 ? "" : "s")}:\n{table}") { Data = new DisplayListing(displays) };
    }

    private string WindowTable(IEnumerable<WindowEntry> entries) =>
        TextTable.Render(["Alias", "Handle", "Process", "Title", "Screen", "Bounds", "State"],
            entries.Select(e => (IReadOnlyList<string?>)[e.Alias, e.Window.Handle.ToString(CultureInfo.InvariantCulture), e.Window.ProcessName, e.Window.Title,
                e.Window.DisplayIndex?.ToString(CultureInfo.InvariantCulture) ?? "", e.Window.Bounds.ToString(), WindowState(e.Window)]));

    private static string WindowState(WindowInfo w) => string.Join(" ", new[]
    {
        w.IsForeground ? "foreground" : null, w.IsMinimized ? "minimized" : null, w.IsElevated ? "elevated" : null,
    }.Where(s => s is not null));

    private CommandResult ListWindows()
    {
        var entries = automation.GetWindows().Select(Entry).ToList();
        return new CommandResult(true, $"{entries.Count} window{(entries.Count == 1 ? "" : "s")}:\n{WindowTable(entries)}") { Data = new WindowListing(entries) };
    }

    private CommandResult FindWindow(RemoteCommand cmd)
    {
        if (cmd.Window is null || cmd.Window.IsEmpty)
            return Failure(cmd.Kind, "Find which window? Try: find window chrome.", FailureCategories.NotUnderstood);
        var r = ResolveWindow(cmd.Window, acting: false);
        var match = Entry(r.Window);
        var alternatives = r.Alternatives.Select(Entry).ToList();
        var text = new StringBuilder($"Best match: {Describe(r.Window)}.");
        text.Append(Lines(r.Notes));
        text.Append('\n').Append(WindowTable([match, .. alternatives]));
        if (alternatives.Count > 0) text.Append($"\n{alternatives.Count} other window{(alternatives.Count == 1 ? "" : "s")} also match.");
        return new CommandResult(true, text.ToString()) { Data = new WindowSearchResult(match, alternatives, r.Notes) };
    }

    private CommandResult ListControls(RemoteCommand cmd)
    {
        var r = ResolveWindow(cmd.Window, acting: false);
        CurrentWindowHandle = r.Window.Handle;
        var listing = BuildListing(r.Window, cmd.Kind == CommandKind.FindControl ? cmd.Control : null, cmd.Verbose, r.Notes);
        return new CommandResult(true, ListingText(listing)) { Data = listing };
    }

    private ControlListing BuildListing(WindowInfo window, ControlSelector? filter, bool verbose, IReadOnlyList<string> notes)
    {
        IReadOnlyList<ControlInfo> list = verbose
            ? Walk(window.Handle, new ControlQuery { IncludeUnnamed = true })
            : filter is not null && filter.Ordinal is null && (filter.ControlType ?? filter.NameText) is not null
                ? Walk(window.Handle, LookupQuery(filter, listing: true))
                : Controls(window.Handle, fresh: true);
        if (filter is not null)
        {
            var wanted = filter.ControlType;
            var matches = Rank(list, wanted, filter.NameText, listing: true)
                .OrderBy(m => wanted is null || ControlTypes.Matches(m.Control.ControlType, wanted) ? 0 : 1)
                .ThenBy(m => m.Order).Select(m => m.Control).ToList();
            if (filter.Ordinal is { } o) matches = o >= 1 && o <= matches.Count ? [matches[o - 1]] : [];
            list = matches;
        }
        return new ControlListing(Entry(window), list.Select(c => Entry(c, window.Handle)).ToList(), verbose) { Filter = filter, Notes = notes };
    }

    private string ListingText(ControlListing listing)
    {
        var w = listing.Window.Window;
        var what = listing.Filter is null ? "Controls" : $"Controls matching {listing.Filter}";
        var sb = new StringBuilder($"{what} in {listing.Window.Alias} ({w.ProcessName}: {w.Title}):");
        sb.Append(Lines(listing.Notes));
        if (listing.Controls.Count == 0)
        {
            sb.Append("\n(none)");
            return sb.ToString();
        }
        var minDepth = listing.Controls.Min(c => c.Control.Depth);
        // Compact rendering for agents: keep interactive rows first when there are too many, cut long names.
        var shown = listing.Controls;
        if (options.ListingMaxRows > 0 && shown.Count > options.ListingMaxRows)
        {
            shown = shown.Select((e, i) => (e, i))
                .OrderBy(x => IsInteractive(x.e.Control.ControlType) ? 0 : 1).ThenBy(x => x.i)
                .Take(options.ListingMaxRows)
                .OrderBy(x => x.i).Select(x => x.e).ToList();
        }
        sb.Append('\n').Append(TextTable.Render(["Alias", "Type", "Name", "Value", "AutomationId", "Bounds", "Flags", "Actions"],
            shown.Select(e =>
            {
                var c = e.Control;
                return (IReadOnlyList<string?>)[e.Alias, c.ControlType, new string('.', Math.Max(0, c.Depth - minDepth) * 2) + Cut(c.Name),
                    c.IsPassword ? "(hidden)" : Cut(c.Value), c.AutomationId, c.Bounds.ToString(), ControlFlags(c),
                    string.Join(",", c.SupportedActions.Select(a => a.ToString().ToLowerInvariant()))];
            })));
        if (shown.Count < listing.Controls.Count)
            sb.Append($"\n{listing.Controls.Count - shown.Count} more controls not shown; narrow with a type word or `matching <text>`.");
        if (options.SummaryThreshold > 0 && listing.Controls.Count > options.SummaryThreshold)
            sb.Append($"\n{listing.Controls.Count} controls.");
        return sb.ToString();
    }

    private string? Cut(string? s) =>
        s is null || options.ListingNameMaxLength <= 0 || s.Length <= options.ListingNameMaxLength
            ? s
            : s[..Math.Max(1, options.ListingNameMaxLength - 1)].TrimEnd() + "…";

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "document", "button", "splitbutton", "menubutton", "hyperlink", "checkbox", "radiobutton", "combobox",
        "listitem", "dataitem", "treeitem", "tabitem", "menuitem", "slider",
    };

    private static bool IsInteractive(string type) => InteractiveTypes.Contains(type);

    private static string ControlFlags(ControlInfo c) => string.Join(" ", new[]
    {
        c.IsEnabled ? null : "disabled", c.IsOffscreen ? "offscreen" : null, c.HasKeyboardFocus ? "focused" : null,
        c.IsReadOnly ? "readonly" : null, c.IsPassword ? "password" : null,
    }.Where(s => s is not null));

    private CommandResult Inspect(RemoteCommand cmd)
    {
        var r = ResolveControl(cmd.Control, cmd.Window, acting: false);
        var c = ReadControl(r.Window.Handle, r.Control.RuntimeKey) ?? throw new SessionException(
            $"{Describe(r.Control, r.Window.Handle)} no longer exists.", FailureCategories.ControlGone);
        CurrentWindowHandle = r.Window.Handle;
        var entry = Entry(c, r.Window.Handle);
        string? parent = c.ParentRuntimeKey is { } pk && controlAliasByKey.TryGetValue(pk, out var pa) ? $"{pa} ({pk})" : c.ParentRuntimeKey;
        var rows = new (string, string?)[]
        {
            ("Value", c.IsPassword ? "(hidden)" : c.Value), ("Alias", entry.Alias), ("Window", Describe(r.Window)), ("Type", c.ControlType),
            ("Name", c.Name), ("AutomationId", c.AutomationId), ("ClassName", c.ClassName),
            ("HelpText", c.HelpText), ("Enabled", Bool(c.IsEnabled)), ("Offscreen", Bool(c.IsOffscreen)),
            ("KeyboardFocus", Bool(c.HasKeyboardFocus)), ("Bounds", c.Bounds.ToString()), ("Center", c.Bounds.Center.ToString()),
            ("Depth", c.Depth.ToString(CultureInfo.InvariantCulture)), ("RuntimeKey", c.RuntimeKey), ("Parent", parent),
            ("Toggled", c.IsToggled is { } tg ? Bool(tg) : null), ("Selected", c.IsSelected is { } s ? Bool(s) : null),
            ("ReadOnly", Bool(c.IsReadOnly)), ("Password", Bool(c.IsPassword)), ("Clickable", Bool(c.IsClickable)),
            ("Actions", string.Join(", ", c.SupportedActions.Select(a => a.ToString().ToLowerInvariant()))),
        };
        var width = rows.Max(x => x.Item1.Length);
        var text = string.Join("\n", rows.Select(x => $"{(x.Item1 + ":").PadRight(width + 2)}{(TextTable.Cell(x.Item2) is { Length: > 0 } v ? v : "(none)")}"));
        return new CommandResult(true, text + Lines(r.Notes)) { Data = new ControlDetail(Entry(r.Window), entry) };

        static string Bool(bool b) => b ? "yes" : "no";
    }

    // ------------------------------------------------------------------ screenshots

    private CommandResult Screenshot(RemoteCommand cmd)
    {
        Screenshot shot;
        string prefix;
        WindowEntry? windowEntry = null;
        var notes = new List<string>();
        switch (cmd.Target)
        {
            case ScreenshotTarget.Display:
            case ScreenshotTarget.Primary:
            {
                var displays = automation.GetDisplays();
                var index = cmd.Target == ScreenshotTarget.Display
                    ? cmd.DisplayIndex ?? 1
                    : displays.FirstOrDefault(d => d.IsPrimary)?.Index ?? 1;
                if (displays.Count > 0 && displays.All(d => d.Index != index))
                    throw new SessionException($"There is no screen {index}. Screens: {string.Join(", ", displays.Select(d => d.Index))}.", FailureCategories.NotFound);
                shot = automation.CaptureDisplay(index);
                prefix = $"screen{index}";
                break;
            }
            case ScreenshotTarget.AllDisplays:
                shot = automation.CaptureAllDisplays();
                prefix = "all-screens";
                break;
            case ScreenshotTarget.Region:
                if (cmd.Region is not { } region)
                    throw new SessionException("No region was given. Try: screenshot region 100,100 800x600.", FailureCategories.NotUnderstood);
                shot = automation.CaptureRegion(region);
                prefix = "region";
                break;
            default:
            {
                var r = ResolveWindow(cmd.Window, acting: false);
                notes.AddRange(r.Notes);
                if (r.Alternatives.Count > 0)
                    notes.Add("Also matching: " + string.Join(", ", r.Alternatives.Take(5).Select(Describe)));
                windowEntry = Entry(r.Window);
                CurrentWindowHandle = r.Window.Handle;
                shot = automation.CaptureWindow(r.Window.Handle);
                prefix = $"{windowEntry.Alias}-{SafeName(r.Window.ProcessName)}";
                break;
            }
        }

        var directory = options.CaptureDirectory;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string full;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{prefix}-{stamp}.png");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(directory, $"{prefix}-{stamp}-{n}.png");
            File.WriteAllBytes(path, shot.Png);
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new SessionException($"The screenshot could not be saved to {directory}: {ex.Message} Choose a writable capture directory.", FailureCategories.Permission);
        }
        PruneCaptures(directory, full);

        var text = new StringBuilder($"Saved screenshot of {shot.Subject} to {full} ({shot.Width}x{shot.Height} px, covering {shot.ScreenBounds}).");
        text.Append(Lines(notes));
        ControlListing? listing = null;
        if (cmd.IncludeControls && windowEntry is not null)
        {
            listing = BuildListing(windowEntry.Window, null, cmd.Verbose, []);
            text.Append("\n\n").Append(ListingText(listing));
        }
        var receipt = new ScreenshotReceipt(full, shot.Width, shot.Height, shot.ScreenBounds, shot.Subject)
        {
            Window = windowEntry,
            PngBase64 = options.EmbedScreenshotBase64 && options.ScreenshotScaler is null ? Convert.ToBase64String(shot.Png) : null,
            Controls = listing,
        };
        if (options.EmbedScreenshotBase64 && options.ScreenshotScaler is { } scaler)
        {
            var scaled = scaler(shot.Png, options.EmbeddedImageMaxSide);
            receipt = receipt with
            {
                PngBase64 = Convert.ToBase64String(scaled.Bytes), ImageMime = scaled.Mime, ImageWidth = scaled.Width,
                ImageHeight = scaled.Height, ImageScale = scaled.Scale,
            };
        }
        return new CommandResult(true, text.ToString()) { Files = [full], Data = receipt };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(screen\d+|w\d+-[a-z0-9-]+|all-screens|region)-\d{8}-\d{6}-\d{3}(-\d+)?\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CaptureNameRegex();

    /// <summary>
    /// Bounds the capture folder to this tool's own files (screenN-, wN-process-, all-screens-, region- plus the
    /// timestamp): keeps the newest <see cref="RemoteControlSessionOptions.MaxRetainedCaptures"/> and anything younger
    /// than <see cref="RemoteControlSessionOptions.CaptureMaxAge"/>. Other files in the folder are never touched.
    /// </summary>
    private void PruneCaptures(string directory, string justWritten)
    {
        try
        {
            var cutoff = DateTime.UtcNow - options.CaptureMaxAge;
            var own = new DirectoryInfo(directory).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && CaptureNameRegex().IsMatch(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var file in own.Skip(Math.Max(0, options.MaxRetainedCaptures)))
            {
                if (file.LastWriteTimeUtc >= cutoff || string.Equals(file.FullName, justWritten, StringComparison.OrdinalIgnoreCase)) continue;
                try { file.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SafeName(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var name = new string(chars).Trim('-');
        return name.Length == 0 ? "window" : name.Length > 40 ? name[..40] : name;
    }

    // ------------------------------------------------------------------ actions

    private CommandResult FocusWindow(RemoteCommand cmd)
    {
        var r = ResolveWindow(cmd.Window, acting: true);
        EnsureUserIdle();
        FocusOrFail(r.Window);
        CurrentWindowHandle = r.Window.Handle;
        var start = Now();
        var after = automation.GetWindow(r.Window.Handle);
        while (after?.IsForeground != true && Now() - start < SettleTimeout)
        {
            Sleep(SettlePollInterval);
            after = automation.GetWindow(r.Window.Handle);
        }
        var verified = after?.IsForeground == true;
        IReadOnlyList<string> notes = [.. r.Notes, SettleNote(Now() - start, verified)];
        var outcome = verified ? "verified" : "issued, not verified";
        var text = verified
            ? $"{Describe(r.Window)} is now in the foreground."
            : $"Asked {Describe(r.Window)} to come to the front (issued, not verified: it is not reported as the foreground window).";
        return new CommandResult(true, text + Lines(r.Notes))
        {
            Data = new ActionReceipt("focus", "focus-window", outcome) { Window = Entry(after ?? r.Window), ObservedState = verified ? "foreground" : "not foreground", Notes = notes },
        };
    }

    private CommandResult Click(RemoteCommand cmd)
    {
        var verb = (cmd.Button, cmd.Clicks) switch
        {
            (MouseButton.Right, _) => "Right-clicked",
            (MouseButton.Middle, _) => "Middle-clicked",
            (_, 2) => "Double-clicked",
            _ => "Clicked",
        };
        if (cmd.Point is { } rawPoint)
        {
            var p = ToScreen(cmd, rawPoint);
            EnsureUserIdle();
            automation.MoveMouse(p);
            automation.Click(p, cmd.Button, cmd.Clicks, cmd.Modifiers);
            var under = automation.GetControlAt(p);
            var state = under is null ? null : $"{under.ControlType} '{under.Name}' is under the mouse";
            var held = cmd.Modifiers is null ? "" : $" holding {cmd.Modifiers}";
            return new CommandResult(true, $"{verb} at {p}{held}{(cmd.PointIsDisplayRelative ? $" (screen {cmd.DisplayIndex} {rawPoint})" : "")} (issued, not verified).{(state is null ? "" : $" Now: {state}.")}")
            {
                Data = new ActionReceipt("click", "mouse-click", "issued, not verified") { Point = p, ObservedState = state },
            };
        }

        var r = ResolveControl(cmd.Control, cmd.Window, acting: true);
        var notes = r.Notes.ToList();
        EnsureUserIdle();
        FocusOrFail(r.Window);
        CurrentWindowHandle = r.Window.Handle;
        var c = Refresh(r.Window.Handle, r.Control);
        if (c.IsOffscreen && c.SupportedActions.Contains(ControlAction.ScrollIntoView))
        {
            automation.PerformAction(r.Window.Handle, c.RuntimeKey, ControlAction.ScrollIntoView);
            c = Refresh(r.Window.Handle, c);
        }

        string method;
        ScreenPoint? point = null;
        // Hit-test first: a popup or overlay covering the control must not receive the click.
        var covering = c.IsClickable ? automation.GetControlAt(c.Bounds.Center) : null;
        var covered = covering is not null && !IsSameOrRelated(covering, c, r.Window.Handle);
        if (covered)
        {
            var cover = $"{covering!.ControlType}{(string.IsNullOrEmpty(covering.Name) ? "" : $" '{covering.Name}'")}";
            var plain = cmd.Button == MouseButton.Left && cmd.Clicks == 1 && cmd.Modifiers is null;
            if (!plain || !c.SupportedActions.Contains(ControlAction.Invoke))
                throw new SessionException($"{Describe(c, r.Window.Handle)} is covered by {cover} at {c.Bounds.Center}; the click was not sent.", FailureCategories.OsRefused);
            var invoked = Perform(r.Window.Handle, c, ControlAction.Invoke);
            method = invoked.Method;
            notes.Add($"{cover} covers the control's center, so it was activated with {invoked.Method} instead of a mouse click.");
        }
        else if (c.IsClickable)
        {
            var center = c.Bounds.Center;
            automation.MoveMouse(center);
            automation.Click(center, cmd.Button, cmd.Clicks, cmd.Modifiers);
            method = "mouse-click";
            point = center;
        }
        else
        {
            var fallback = cmd.Button == MouseButton.Left
                ? new[] { ControlAction.Invoke, ControlAction.Toggle, ControlAction.Select }.Cast<ControlAction?>().FirstOrDefault(a => c.SupportedActions.Contains(a!.Value))
                : null;
            if (fallback is not { } action)
                throw new SessionException($"{Describe(c, r.Window.Handle)} has no on-screen position and supports no invoke, toggle, or select action.", FailureCategories.Unsupported);
            var result = automation.PerformAction(r.Window.Handle, c.RuntimeKey, action);
            if (!result.Succeeded)
                throw new SessionException(result.Message ?? $"{action} failed on {Describe(c, r.Window.Handle)}.", FailureCategories.OsRefused);
            method = result.Method;
            notes.Add($"The control has no on-screen position, so it was activated with {result.Method} instead of a mouse click.");
        }
        Invalidate(r.Window.Handle);
        var holding = cmd.Modifiers is null ? "" : $" holding {cmd.Modifiers}";
        return ActionResult("click", method, r.Window, c, point, $"{verb} {Describe(c, r.Window.Handle)} in {WindowAlias(r.Window.Handle)}{(point is { } pt ? $" at {pt}" : "")}{holding} ({method}).", notes);
    }

    /// <summary>True when the hit-tested element is the target, inside it, or an ancestor of it in the same window.</summary>
    private bool IsSameOrRelated(ControlInfo hit, ControlInfo target, long windowHandle)
    {
        if (hit.RuntimeKey == target.RuntimeKey) return true;
        var known = controlCache.TryGetValue(windowHandle, out var cached) ? cached.Controls : [];
        var parents = known.Concat([target, hit]).GroupBy(x => x.RuntimeKey).ToDictionary(g => g.Key, g => g.First().ParentRuntimeKey);
        var guard = 0;
        for (var k = hit.ParentRuntimeKey; k is not null && guard++ < 200; k = parents.GetValueOrDefault(k))
            if (k == target.RuntimeKey) return true;
        guard = 0;
        for (var k = target.ParentRuntimeKey; k is not null && guard++ < 200; k = parents.GetValueOrDefault(k))
            if (k == hit.RuntimeKey) return true;
        if (!hit.Bounds.IsEmpty && Inside(hit.Bounds, target.Bounds)) return true;
        return known.Any(x => x.RuntimeKey == hit.RuntimeKey) && Inside(target.Bounds, hit.Bounds);
    }

    private static bool Inside(ScreenRect inner, ScreenRect outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private CommandResult SetGuard(RemoteCommand cmd)
    {
        guardRequired = cmd.Value switch
        {
            "status" => guardRequired,
            "on" => options.UserIdleRequired > TimeSpan.Zero ? options.UserIdleRequired : TimeSpan.FromMilliseconds(350),
            _ => cmd.Delay ?? guardRequired,
        };
        var text = guardRequired <= TimeSpan.Zero
            ? "Input guard is off for this session: actions are sent even while the person is typing or moving the mouse."
            : $"Input guard is on: actions wait until there has been no keyboard or mouse input for {(long)guardRequired.TotalMilliseconds} ms (up to {Seconds(options.UserIdleWaitLimit)}, twice for the first step of a batch).";
        return new CommandResult(true, text)
        {
            Data = new ActionReceipt("guard", "none", "verified") { Value = ((long)guardRequired.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), ObservedState = guardRequired <= TimeSpan.Zero ? "off" : "on" },
        };
    }

    private CommandResult Drag(RemoteCommand cmd)
    {
        ScreenPoint End(ScreenPoint? point, ControlSelector? selector, bool relative, out WindowInfo? window, out ControlEntry? entry)
        {
            window = null;
            entry = null;
            if (point is { } p) return relative ? ToScreen(cmd, p) : p;
            var r = ResolveControl(selector, cmd.Window, acting: true);
            var c = Refresh(r.Window.Handle, r.Control);
            if (!c.IsClickable)
                throw new SessionException($"{Describe(c, r.Window.Handle)} has no on-screen position to drag from or to.", FailureCategories.Unsupported);
            window = r.Window;
            entry = Entry(c, r.Window.Handle);
            return c.Bounds.Center;
        }
        var from = End(cmd.Point, cmd.Control, cmd.PointIsDisplayRelative, out var fromWindow, out var fromEntry);
        var to = End(cmd.TargetPoint, cmd.TargetControl, cmd.PointIsDisplayRelative, out var toWindow, out var toEntry);
        EnsureUserIdle();
        if ((fromWindow ?? toWindow) is { } w)
        {
            FocusOrFail(w);
            CurrentWindowHandle = w.Handle;
            Invalidate(w.Handle);
        }
        automation.Drag(from, to, cmd.Button);
        var what = fromEntry is null ? from.ToString() : $"{fromEntry.Alias} {fromEntry.Control.ControlType} '{fromEntry.Control.Name}'";
        var where = toEntry is null ? to.ToString() : $"{toEntry.Alias} {toEntry.Control.ControlType} '{toEntry.Control.Name}'";
        return new CommandResult(true, $"Dragged {what} to {where} ({from} -> {to}; issued, not verified).")
        {
            Data = new ActionReceipt("drag", "mouse-drag", "issued, not verified")
            {
                Window = (fromWindow ?? toWindow) is { } dw ? Entry(dw) : null, Control = fromEntry, Point = from, ObservedState = $"released at {to}",
            },
        };
    }

    /// <summary>Re-reads the target after an action and reports what is observed now.</summary>
    private CommandResult ActionResult(string action, string method, WindowInfo window, ControlInfo before, ScreenPoint? point, string lead,
        IReadOnlyList<string> notes, string? value = null, bool verified = false)
    {
        var after = ReadControl(window.Handle, before.RuntimeKey);
        var outcome = verified && after is not null ? "verified" : "issued, not verified";
        var state = after is null ? "the control is no longer present" : StateOf(after);
        var text = $"{lead} Now: {state} ({outcome})." + Lines(notes);
        return new CommandResult(true, text)
        {
            Data = new ActionReceipt(action, method, outcome)
            {
                Window = Entry(window), Control = Entry(after ?? before, window.Handle), Point = point, Value = value, ObservedState = state, Notes = notes,
            },
        };
    }

    /// <summary>Re-reads the control until <paramref name="expected"/> holds or <see cref="SettleTimeout"/> passes.</summary>
    private (ControlInfo? Control, TimeSpan Elapsed, bool Reached) Settle(long windowHandle, string runtimeKey, Func<ControlInfo?, bool> expected)
    {
        var start = Now();
        var c = ReadControl(windowHandle, runtimeKey);
        while (!expected(c) && Now() - start < SettleTimeout)
        {
            Sleep(SettlePollInterval);
            c = ReadControl(windowHandle, runtimeKey);
        }
        return (c, Now() - start, expected(c));
    }

    private static string SettleNote(TimeSpan elapsed, bool reached)
    {
        var ms = ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return reached ? $"State observed after {ms} ms." : $"Expected state not observed within {ms} ms.";
    }

    private ControlInfo Refresh(long windowHandle, ControlInfo c) =>
        ReadControl(windowHandle, c.RuntimeKey)
        ?? throw new SessionException($"{Describe(c, windowHandle)} no longer exists.", FailureCategories.ControlGone);

    private CommandResult Invoke(RemoteCommand cmd)
    {
        var word = (cmd.Value ?? "invoke").ToLowerInvariant();
        var r = ResolveControl(cmd.Control, cmd.Window, acting: true);
        CurrentWindowHandle = r.Window.Handle;
        var c = Refresh(r.Window.Handle, r.Control);
        var h = r.Window.Handle;

        if (word is "check" or "uncheck")
        {
            var desired = word == "check";
            if (c.IsToggled is null)
                throw new SessionException(
                    $"{Describe(c, h)} does not report whether it is checked, so {word} cannot be ensured (its state cannot be read; use toggle to flip it).",
                    FailureCategories.Unsupported);
            if (c.IsToggled == desired)
            {
                var already = desired ? "already on" : "already off";
                return new CommandResult(true, $"{Describe(c, h)} is {already}; nothing to do (unchanged)." + Lines(r.Notes))
                {
                    Data = new ActionReceipt(word, "none", "unchanged") { Window = Entry(r.Window), Control = Entry(c, h), ObservedState = StateOf(c), Notes = r.Notes },
                };
            }
            Perform(h, c, ControlAction.Toggle);
            var readable = c.IsToggled is not null;
            var (after, elapsed, reached) = Settle(h, c.RuntimeKey, x => !readable || x?.IsToggled == desired);
            Invalidate(h);
            if (readable && after?.IsToggled is { } now)
            {
                if (now != desired)
                    return Failure(cmd.Kind, $"Toggled {Describe(c, h)} but it is still {(now ? "on" : "off")} after {(long)elapsed.TotalMilliseconds} ms.", FailureCategories.OsRefused);
                IReadOnlyList<string> checkNotes = [.. r.Notes, SettleNote(elapsed, reached)];
                return new CommandResult(true, $"{Describe(c, h)} is now {(now ? "on" : "off")} (verified)." + Lines(r.Notes))
                {
                    Data = new ActionReceipt(word, "toggle-pattern", "verified") { Window = Entry(r.Window), Control = Entry(after, h), ObservedState = StateOf(after), Notes = checkNotes },
                };
            }
            return new CommandResult(true, $"Toggled {Describe(c, h)} (issued, not verified: its toggle state cannot be read)." + Lines(r.Notes))
            {
                Data = new ActionReceipt(word, "toggle-pattern", "issued, not verified") { Window = Entry(r.Window), Control = Entry(c, h), Notes = r.Notes },
            };
        }

        var action = word switch
        {
            "toggle" => ControlAction.Toggle,
            "select" => ControlAction.Select,
            "expand" => ControlAction.Expand,
            "collapse" => ControlAction.Collapse,
            "focus" => ControlAction.Focus,
            _ => ControlAction.Invoke,
        };
        var result = Perform(h, c, action);
        Invalidate(h);
        Func<ControlInfo?, bool>? expected = action switch
        {
            ControlAction.Toggle when c.IsToggled is { } before => x => x?.IsToggled is { } t && t != before,
            ControlAction.Select when c.IsSelected is not null => x => x?.IsSelected == true,
            _ => null,
        };
        var notes = r.Notes.ToList();
        var settled = false;
        if (expected is not null)
        {
            var (_, elapsed, reached) = Settle(h, c.RuntimeKey, expected);
            notes.Add(SettleNote(elapsed, reached));
            settled = reached;
        }
        return ActionResult(word, result.Method, r.Window, c, null, $"{Past(word)} {Describe(c, h)} ({result.Method}).", notes, verified: settled);
    }

    private static string Past(string word) => word switch
    {
        "toggle" => "Toggled", "select" => "Selected", "expand" => "Expanded", "collapse" => "Collapsed", "focus" => "Focused", _ => "Invoked",
    };

    private ControlActionResult Perform(long windowHandle, ControlInfo c, ControlAction action, string? value = null)
    {
        var result = automation.PerformAction(windowHandle, c.RuntimeKey, action, value);
        if (result.Succeeded) return result;
        var supported = c.SupportedActions.Contains(action);
        var list = c.SupportedActions.Count == 0 ? "none" : string.Join(", ", c.SupportedActions.Select(a => a.ToString().ToLowerInvariant()));
        throw new SessionException(
            supported
                ? result.Message ?? $"{action} failed on {Describe(c, windowHandle)}."
                : $"{Describe(c, windowHandle)} does not support {action.ToString().ToLowerInvariant()}; it supports: {list}.",
            supported ? FailureCategories.OsRefused : FailureCategories.Unsupported);
    }

    private CommandResult SetValue(RemoteCommand cmd)
    {
        var value = cmd.Value ?? "";
        var r = ResolveControl(cmd.Control, cmd.Window, acting: true);
        var h = r.Window.Handle;
        CurrentWindowHandle = h;
        var c = Refresh(h, r.Control);
        var notes = r.Notes.ToList();
        var shown = c.IsPassword ? "(hidden)" : $"\"{value}\"";

        if (c.IsReadOnly)
            throw new SessionException($"{Describe(c, h)} is read-only; its value cannot be changed.", FailureCategories.Unsupported);
        if (!c.IsPassword && string.Equals(c.Value ?? "", value, StringComparison.Ordinal) && c.Value is not null)
        {
            return new CommandResult(true, $"{Describe(c, h)} already shows {shown}; nothing to do (unchanged)." + Lines(notes))
            {
                Data = new ActionReceipt("set", "none", "unchanged") { Window = Entry(r.Window), Control = Entry(c, h), Value = value, ObservedState = StateOf(c), Notes = notes },
            };
        }

        bool Took(ControlInfo? x) => x is not null && !x.IsPassword && string.Equals(x.Value ?? "", value, StringComparison.Ordinal);
        // The app may reformat what it was given ("5" shown as "5.00"): a changed value counts as taken.
        bool Changed(ControlInfo? x) => x is { IsPassword: false, Value: { } v } && !string.Equals(v, c.Value ?? "", StringComparison.Ordinal);

        if (c.SupportedActions.Contains(ControlAction.SetValue))
        {
            var result = automation.PerformAction(h, c.RuntimeKey, ControlAction.SetValue, value);
            var (after, elapsed, reached) = result.Succeeded
                ? Settle(h, c.RuntimeKey, x => Took(x) || x?.IsPassword == true || Changed(x))
                : (ReadControl(h, c.RuntimeKey), TimeSpan.Zero, false);
            if (result.Succeeded) notes.Add(SettleNote(elapsed, reached));
            if (result.Succeeded && (Took(after) || after?.IsPassword == true))
            {
                Invalidate(h);
                var verified = after!.IsPassword ? "issued, not verified" : "verified";
                return SetResult(r.Window, after, value, result.Method, verified, notes);
            }
            if (result.Succeeded && Changed(after))
            {
                Invalidate(h);
                return Reformatted(r.Window, after!, value, result.Method, notes);
            }
            notes.Add(result.Succeeded
                ? $"The value pattern reported success but the control shows {(after is null ? "nothing" : ShowValue(after))}; typing instead."
                : $"The value pattern failed ({result.Message ?? result.Method}); typing instead.");
        }

        // Keyboard fallback: focus the window, click the control, select all, type. Only for controls that take
        // typed text; clicking a pane or a list and typing would send keystrokes somewhere else.
        var editable = ControlTypes.IsTextInput(c.ControlType) || c.ControlType.Equals("combobox", StringComparison.OrdinalIgnoreCase);
        if (!editable)
        {
            var actions = c.SupportedActions.Count == 0 ? "none" : string.Join(", ", c.SupportedActions.Select(a => a.ToString().ToLowerInvariant()));
            throw new SessionException(
                $"{Describe(c, h)} is a {c.ControlType}, which has no value to set and does not take typed text (supported actions: {actions}). Name the text field inside it instead; list its controls to find it.",
                FailureCategories.Unsupported);
        }
        EnsureUserIdle();
        FocusOrFail(r.Window);
        var cur = Refresh(h, c);
        if (cur.IsClickable)
        {
            automation.MoveMouse(cur.Bounds.Center);
            automation.Click(cur.Bounds.Center);
        }
        else if (cur.SupportedActions.Contains(ControlAction.Focus))
        {
            Perform(h, cur, ControlAction.Focus);
        }
        else
        {
            throw new SessionException($"{Describe(cur, h)} supports no value pattern and has no on-screen position to click and type into.", FailureCategories.Unsupported);
        }
        automation.PressKeys("primary+a");
        if (value.Length == 0) automation.PressKeys("delete");
        else automation.TypeText(value);
        Invalidate(h);
        var (final, typedElapsed, typedReached) = Settle(h, c.RuntimeKey, x => x is null || x.IsPassword || x.Value is null || Took(x));
        notes.Add(SettleNote(typedElapsed, typedReached));
        if (final is null || final.IsPassword || final.Value is null)
            return SetResult(r.Window, final ?? cur, value, "keyboard-replace", "issued, not verified", notes);
        if (!Took(final) && Changed(final))
            return Reformatted(r.Window, final, value, "keyboard-replace", notes);
        if (!Took(final))
            return Failure(cmd.Kind, $"Typed {shown} into {Describe(final, h)} but it still shows {ShowValue(final)} after {(long)typedElapsed.TotalMilliseconds} ms.", FailureCategories.OsRefused);
        return SetResult(r.Window, final, value, "keyboard-replace", "verified", notes);
    }

    /// <summary>The control took the value but shows it in its own format; that is success, not a reason to retype.</summary>
    private CommandResult Reformatted(WindowInfo window, ControlInfo c, string value, string method, IReadOnlyList<string> notes)
    {
        var h = window.Handle;
        var outcome = $"set; control shows \"{c.Value}\"";
        IReadOnlyList<string> allNotes = [.. notes, $"The control reformatted the value: asked for \"{value}\", it shows \"{c.Value}\"."];
        return new CommandResult(true, $"Set {Describe(c, h)} in {WindowAlias(h)} via {method} ({outcome})." + Lines(allNotes))
        {
            Data = new ActionReceipt("set", method, outcome) { Window = Entry(window), Control = Entry(c, h), Value = value, ObservedState = StateOf(c), Notes = allNotes },
        };
    }

    private CommandResult SetResult(WindowInfo window, ControlInfo c, string value, string method, string outcome, IReadOnlyList<string> notes)
    {
        var h = window.Handle;
        var text = outcome == "verified"
            ? $"Set {Describe(c, h)} in {WindowAlias(h)} via {method}; it now shows {ShowValue(c)} (verified)."
            : $"Set {Describe(c, h)} in {WindowAlias(h)} via {method} ({outcome}{(c.IsPassword ? ": password fields cannot be read back" : "")}).";
        return new CommandResult(true, text + Lines(notes))
        {
            Data = new ActionReceipt("set", method, outcome)
            {
                Window = Entry(window), Control = Entry(c, h), Value = c.IsPassword ? null : value, ObservedState = StateOf(c), Notes = notes,
            },
        };
    }

    private CommandResult TypeText(RemoteCommand cmd)
    {
        var value = cmd.Value ?? "";
        WindowEntry? windowEntry = null;
        var target = cmd.Window is { IsEmpty: false } ? ResolveWindow(cmd.Window, acting: true) : null;
        EnsureUserIdle();
        if (target is not null)
        {
            FocusOrFail(target.Window);
            CurrentWindowHandle = target.Window.Handle;
            windowEntry = Entry(target.Window);
        }
        var focusedBefore = automation.GetFocusedControl();
        automation.TypeText(value);
        var focused = automation.GetFocusedControl();
        var secret = focusedBefore?.IsPassword == true || focused?.IsPassword == true;
        var verified = !secret && focused is { Value: { } v } && v.Contains(value, StringComparison.Ordinal);
        var state = focused is null ? null : $"{focused.ControlType} '{focused.Name}' has focus, {StateOf(focused)}";
        var outcome = verified ? "verified" : "issued, not verified";
        return new CommandResult(true, $"Typed {value.Length} character{(value.Length == 1 ? "" : "s")} into the focused {(secret ? "password field" : "control")} ({outcome}).{(state is null ? "" : $" Now: {state}.")}")
        {
            Data = new ActionReceipt("type", "keyboard", outcome) { Window = windowEntry, Value = secret ? null : value, ObservedState = state },
        };
    }

    private CommandResult PressKeys(RemoteCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Keys))
            return Failure(cmd.Kind, "No keys were given. Try: press ctrl+s.", FailureCategories.NotUnderstood);
        var sequence = (cmd.KeySequence.Count > 0 ? cmd.KeySequence : [cmd.Keys]).Select(KeyChord.Normalize).ToList();
        var repeat = Math.Max(1, cmd.Repeat);
        var keys = string.Join(" then ", sequence) + (repeat > 1 ? $" x{repeat}" : "");
        WindowEntry? windowEntry = null;
        var target = cmd.Window is { IsEmpty: false } ? ResolveWindow(cmd.Window, acting: true) : null;
        EnsureUserIdle();
        if (target is not null)
        {
            FocusOrFail(target.Window);
            CurrentWindowHandle = target.Window.Handle;
            windowEntry = Entry(target.Window);
        }
        for (var n = 0; n < repeat; n++)
            foreach (var chord in sequence) automation.PressKeys(chord);
        var fg = automation.GetWindows().FirstOrDefault(w => w.IsForeground);
        var state = fg is null ? null : $"foreground window is {Describe(fg)}";
        return new CommandResult(true, $"Pressed {keys} (issued, not verified).{(state is null ? "" : $" Now: {state}.")}")
        {
            Data = new ActionReceipt("press", "keyboard", "issued, not verified") { Window = windowEntry, Keys = keys, ObservedState = state },
        };
    }

    private CommandResult MoveMouse(RemoteCommand cmd)
    {
        ScreenPoint target;
        string lead;
        WindowEntry? windowEntry = null;
        ControlEntry? controlEntry = null;
        if (cmd.Point is { } raw)
        {
            target = ToScreen(cmd, raw);
            lead = $"Moved the mouse to {target}";
        }
        else
        {
            var r = ResolveControl(cmd.Control, cmd.Window, acting: true);
            FocusOrFail(r.Window);
            CurrentWindowHandle = r.Window.Handle;
            var c = Refresh(r.Window.Handle, r.Control);
            if (!c.IsClickable)
                throw new SessionException($"{Describe(c, r.Window.Handle)} has no on-screen position to hover over.", FailureCategories.Unsupported);
            target = c.Bounds.Center;
            windowEntry = Entry(r.Window);
            controlEntry = Entry(c, r.Window.Handle);
            lead = $"Moved the mouse over {Describe(c, r.Window.Handle)} at {target}";
        }
        EnsureUserIdle();
        automation.MoveMouse(target);
        var now = automation.GetMousePosition();
        var verified = now == target;
        var outcome = verified ? "verified" : "issued, not verified";
        return new CommandResult(true, $"{lead} ({(verified ? "verified" : $"issued, not verified: the mouse reports {now}")}).")
        {
            Data = new ActionReceipt("move", "mouse-move", outcome) { Point = target, Window = windowEntry, Control = controlEntry, ObservedState = now.ToString() },
        };
    }

    private CommandResult Scroll(RemoteCommand cmd)
    {
        ScreenPoint point;
        WindowEntry? windowEntry = null;
        ControlEntry? controlEntry = null;
        if (cmd.Point is { } raw)
        {
            point = ToScreen(cmd, raw);
        }
        else if (cmd.Control is { } sel && (!sel.IsEmpty || sel.Ordinal is not null))
        {
            var r = ResolveControl(sel, cmd.Window, acting: true);
            FocusOrFail(r.Window);
            CurrentWindowHandle = r.Window.Handle;
            var c = Refresh(r.Window.Handle, r.Control);
            if (!c.IsClickable)
                throw new SessionException($"{Describe(c, r.Window.Handle)} has no on-screen position to scroll at.", FailureCategories.Unsupported);
            point = c.Bounds.Center;
            windowEntry = Entry(r.Window);
            controlEntry = Entry(c, r.Window.Handle);
        }
        else if (cmd.Window is { IsEmpty: false })
        {
            var r = ResolveWindow(cmd.Window, acting: true);
            FocusOrFail(r.Window);
            CurrentWindowHandle = r.Window.Handle;
            point = r.Window.Bounds.Center;
            windowEntry = Entry(r.Window);
        }
        else
        {
            point = automation.GetMousePosition();
        }
        EnsureUserIdle();
        automation.Scroll(point, cmd.ScrollX, cmd.ScrollY);
        if (windowEntry is not null) Invalidate(windowEntry.Window.Handle);
        var direction = cmd.ScrollY > 0 ? $"up {cmd.ScrollY}" : cmd.ScrollY < 0 ? $"down {-cmd.ScrollY}" : cmd.ScrollX > 0 ? $"right {cmd.ScrollX}" : $"left {-cmd.ScrollX}";
        var huge = Math.Max(Math.Abs(cmd.ScrollX), Math.Abs(cmd.ScrollY)) >= 1000 ? "\nNote: that is a very large scroll (1000 or more notches)." : "";
        return new CommandResult(true, $"Scrolled {direction} at {point} (issued, not verified).{huge}")
        {
            Data = new ActionReceipt("scroll", "mouse-wheel", "issued, not verified") { Point = point, Window = windowEntry, Control = controlEntry },
        };
    }

    private CommandResult Wait(RemoteCommand cmd)
    {
        var delay = cmd.Delay ?? TimeSpan.FromSeconds(1);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        Sleep(delay);
        var note = delay > LongWait ? "\nNote: that was a long wait; prefer wait for a window or control, which returns as soon as it appears." : "";
        return new CommandResult(true, $"Waited {Seconds(delay)}.{note}") { Data = new WaitReceipt(delay) };
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 1
        ? $"{t.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms"
        : $"{t.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s";

    private CommandResult Where()
    {
        var pos = automation.GetMousePosition();
        var displays = automation.GetDisplays();
        var display = displays.FirstOrDefault(d => d.Bounds.Contains(pos))?.Index;
        var windows = automation.GetWindows();
        var fg = windows.FirstOrDefault(w => w.IsForeground);
        var underWindow = windows.FirstOrDefault(w => !w.IsMinimized && w.Bounds.Contains(pos)) ?? fg;
        var under = automation.GetControlAt(pos);
        var focused = automation.GetFocusedControl();
        var underEntry = under is null ? null : Entry(under, underWindow?.Handle ?? 0);
        var focusedEntry = focused is null ? null : Entry(focused, fg?.Handle ?? 0);
        var fgEntry = fg is null ? null : Entry(fg);

        string Show(ControlEntry? e) => e is null ? "(nothing)" : $"{(e.Alias.Length == 0 ? "(no alias)" : e.Alias)} {e.Control.ControlType} '{e.Control.Name}', {StateOf(e.Control)}";
        var text = string.Join("\n",
            $"Mouse:             {pos}{(display is null ? "" : $" (screen {display})")}",
            $"Under the mouse:   {Show(underEntry)}",
            $"Keyboard focus:    {Show(focusedEntry)}",
            $"Foreground window: {(fg is null ? "(none)" : Describe(fg))}");
        return new CommandResult(true, text) { Data = new WhereReport(pos, display, underEntry, focusedEntry, fgEntry) };
    }

    // ------------------------------------------------------------------ wait-for

    private CommandResult WaitFor(RemoteCommand cmd)
    {
        var target = cmd.Wait?.Target ?? (cmd.Control is null ? WaitTarget.WindowAppears : WaitTarget.ControlAppears);
        var timeout = cmd.Delay is { } d && d > TimeSpan.Zero ? d : DefaultWaitTimeout;
        WindowEntry? windowEntry = null;
        ControlEntry? controlEntry = null;
        Func<(bool Done, string State)> probe;
        string goal;
        var deadline = Now() + timeout;
        // Each poll walks afresh, bounded by the time left and filtered by the name/type, never the cached listing.
        ControlQuery WalkQuery()
        {
            var left = deadline - Now();
            var name = cmd.Control?.IdText is null ? cmd.Control?.NameText : null;
            return new ControlQuery
            {
                Timeout = left < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : left > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : left,
                NameContains = SignificantWord(name),
                ControlTypes = cmd.Control?.ControlType is { } t ? ControlTypes.Family(t) : null,
            };
        }

        static bool Soft(SessionException ex) => ex.Category is FailureCategories.NotFound or FailureCategories.WindowGone or FailureCategories.ControlGone;

        switch (target)
        {
            case WaitTarget.WindowAppears:
                if (cmd.Window is null || cmd.Window.IsEmpty)
                    return Failure(cmd.Kind, "Wait for which window? Try: wait for window \"Save As\".", FailureCategories.NotUnderstood);
                goal = $"a window matching {cmd.Window}";
                probe = () =>
                {
                    try
                    {
                        var r = ResolveWindow(cmd.Window, acting: false);
                        windowEntry = Entry(r.Window);
                        CurrentWindowHandle = r.Window.Handle;
                        return (true, $"{Describe(r.Window)} is open");
                    }
                    catch (SessionException ex) when (Soft(ex)) { return (false, "no matching window is open"); }
                };
                break;

            case WaitTarget.WindowGone:
            {
                WindowInfo w;
                try { w = ResolveWindow(cmd.Window, acting: false).Window; }
                catch (SessionException ex) when (Soft(ex))
                {
                    return WaitSuccess(target, TimeSpan.Zero, $"{cmd.Window} is already gone", null, null);
                }
                windowEntry = Entry(w);
                goal = $"{Describe(w)} to close";
                probe = () => automation.GetWindow(w.Handle) is { } still ? (false, $"{Describe(still)} is still open") : (true, $"{windowEntry.Alias} has closed");
                break;
            }

            case WaitTarget.ControlAppears:
                goal = $"control {cmd.Control} to appear";
                var quotedName = IsQuotedIn(cmd.RawText, cmd.Control?.NameText);
                probe = () =>
                {
                    try
                    {
                        var r = ResolveControl(cmd.Control, cmd.Window, acting: false, freshList: true, exactName: quotedName, walkQuery: WalkQuery());
                        windowEntry = Entry(r.Window);
                        controlEntry = Entry(r.Control, r.Window.Handle);
                        CurrentWindowHandle = r.Window.Handle;
                        return (true, $"{Describe(r.Control, r.Window.Handle)} is present, {StateOf(r.Control)}");
                    }
                    catch (SessionException ex) when (Soft(ex)) { return (false, ex.Message); }
                };
                break;

            case WaitTarget.ControlGone:
            {
                ControlResolution r;
                try { r = ResolveControl(cmd.Control, cmd.Window, acting: false, freshList: true, walkQuery: WalkQuery()); }
                catch (SessionException ex) when (Soft(ex))
                {
                    return WaitSuccess(target, TimeSpan.Zero, $"{cmd.Control} is already gone", null, null);
                }
                var h = r.Window.Handle;
                var key = r.Control.RuntimeKey;
                windowEntry = Entry(r.Window);
                controlEntry = Entry(r.Control, h);
                CurrentWindowHandle = h;
                var label = Describe(r.Control, h);
                goal = $"{label} to disappear";
                probe = () => ReadControl(h, key) is { } c ? (false, $"{label} is still present, {StateOf(c)}") : (true, $"{label} is gone");
                break;
            }

            default:
            {
                // The control may not exist yet (a page still loading): find it with a fresh walk on every poll,
                // then follow it by runtime key.
                ControlResolution? found = null;
                string? initial = null;
                goal = target switch
                {
                    WaitTarget.ControlValueEquals => $"{cmd.Control} to show \"{cmd.Value}\"",
                    WaitTarget.ControlValueChanges => $"{cmd.Control} to change",
                    WaitTarget.ControlEnabled => $"{cmd.Control} to be enabled",
                    _ => throw new SessionException($"Unsupported wait target {target}.", FailureCategories.Unsupported),
                };
                probe = () =>
                {
                    if (found is null)
                    {
                        try { found = ResolveControl(cmd.Control, cmd.Window, acting: false, freshList: true, walkQuery: WalkQuery()); }
                        catch (SessionException ex) when (Soft(ex)) { return (false, ex.Message); }
                        windowEntry = Entry(found.Window);
                        controlEntry = Entry(found.Control, found.Window.Handle);
                        CurrentWindowHandle = found.Window.Handle;
                        initial = found.Control.Value;
                    }
                    var h = found.Window.Handle;
                    var label = Describe(found.Control, h);
                    var c = ReadControl(h, found.Control.RuntimeKey)
                            ?? throw new SessionException($"{label} disappeared while waiting.", FailureCategories.ControlGone);
                    return target switch
                    {
                        WaitTarget.ControlValueEquals => (string.Equals((c.Value ?? "").Trim(), (cmd.Value ?? "").Trim(), StringComparison.OrdinalIgnoreCase), $"{label} shows {ShowValue(c)}"),
                        WaitTarget.ControlValueChanges => (!string.Equals(c.Value, initial, StringComparison.Ordinal), $"{label} shows {ShowValue(c)}"),
                        _ => (c.IsEnabled, $"{label} is {(c.IsEnabled ? "enabled" : "disabled")}"),
                    };
                };
                break;
            }
        }

        var start = Now();
        while (true)
        {
            var pollStarted = Now();
            var (done, state) = probe();
            var pollTook = Now() - pollStarted;
            var elapsed = Now() - start;
            if (done) return WaitSuccess(target, elapsed, state, windowEntry, controlEntry);
            if (elapsed >= timeout)
            {
                var message = $"Timed out after {Seconds(timeout)} waiting for {goal}. Last observed: {state}.";
                return Failure(cmd.Kind, message, FailureCategories.Timeout) with
                {
                    Data = new WaitForReceipt(target, false, elapsed, state) { Window = windowEntry, Control = controlEntry },
                };
            }
            // A slow walk is not repeated back to back: wait at least as long as the last poll took.
            Sleep(pollTook > PollInterval ? pollTook : PollInterval);
        }
    }

    /// <summary>True when the user wrote <paramref name="name"/> in quotes, which asks for an exact name match.</summary>
    private static bool IsQuotedIn(string raw, string? name) =>
        !string.IsNullOrEmpty(name)
        && new[] { $"\"{name}\"", $"“{name}”", $"'{name}'", $"‘{name}’" }.Any(q => raw.Contains(q, StringComparison.Ordinal));

    private static CommandResult WaitSuccess(WaitTarget target, TimeSpan elapsed, string state, WindowEntry? window, ControlEntry? control) =>
        new(true, $"Done after {Seconds(elapsed)}: {state}.")
        {
            Data = new WaitForReceipt(target, true, elapsed, state) { Window = window, Control = control },
        };
}

/// <summary>Platform-neutral control type families from COMMANDS.md.</summary>
internal static class ControlTypes
{
    private static readonly Dictionary<string, string[]> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["listitem"] = ["listitem", "dataitem", "treeitem"],
        ["edit"] = ["edit"],
        ["field"] = ["edit", "combobox"],
        ["document"] = ["document", "edit"],
        ["button"] = ["button", "splitbutton", "menubutton"],
        ["menuitem"] = ["menuitem", "menu"],
        ["pane"] = ["pane", "group"],
        ["group"] = ["group", "pane"],
        ["text"] = ["text", "heading"],
        ["heading"] = ["heading", "text"],
    };

    /// <summary>Listings are broader than lookups: "edit boxes in w1" also lists documents (after the edits).</summary>
    private static readonly Dictionary<string, string[]> ListingFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edit"] = ["edit", "document"],
        ["field"] = ["edit", "combobox", "document"],
    };

    public static bool HasFamily(string type) => Families.ContainsKey(type);

    /// <summary>The platform types a lookup for <paramref name="type"/> accepts.</summary>
    public static IReadOnlyList<string> Family(string type) => Families.TryGetValue(type, out var family) ? family : [type];

    /// <summary>The platform types a listing for <paramref name="type"/> shows.</summary>
    public static IReadOnlyList<string> ListingFamily(string type) =>
        ListingFamilies.TryGetValue(type, out var family) ? family : Family(type);

    /// <summary>The control takes typed text (single-line edit or multi-line document).</summary>
    public static bool IsTextInput(string actual) =>
        actual.Equals("edit", StringComparison.OrdinalIgnoreCase) || actual.Equals("document", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesForListing(string actual, string wanted) =>
        Matches(actual, wanted) || (ListingFamilies.TryGetValue(wanted, out var family) && family.Contains(actual, StringComparer.OrdinalIgnoreCase));

    public static bool Matches(string actual, string wanted) =>
        string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)
        || (Families.TryGetValue(wanted, out var family) && family.Contains(actual, StringComparer.OrdinalIgnoreCase));
}
