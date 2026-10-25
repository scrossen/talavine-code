namespace SwarmCore.RemoteControl.Tests;

/// <summary>In-memory desktop that records every call and mutates control state the way a real desktop would.</summary>
public sealed class FakeDesktopAutomation : IDesktopAutomation
{
    // 1x1 transparent PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public List<DisplayInfo> Displays { get; } =
    [
        new(1, @"\\.\DISPLAY1", true, new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 1.0),
    ];

    public List<WindowInfo> Windows { get; } = [];
    public Dictionary<long, List<ControlInfo>> ControlTrees { get; } = [];

    public List<string> Calls { get; } = [];
    public List<(ScreenPoint Point, MouseButton Button, int Clicks)> Clicks { get; } = [];
    public List<ScreenPoint> MouseMoves { get; } = [];
    public List<string> TypedText { get; } = [];
    public List<string> KeyChords { get; } = [];
    public List<(long Window, string Key, ControlAction Action, string? Value)> Actions { get; } = [];
    public List<(ScreenPoint Point, int DeltaX, int DeltaY)> Scrolls { get; } = [];
    public List<long> FocusRequests { get; } = [];
    public List<int> CapturedDisplays { get; } = [];

    /// <summary>Runtime keys whose SetValue reports success but does not change the value.</summary>
    public HashSet<string> SetValueIgnored { get; } = [];
    /// <summary>Runtime keys whose SetValue stores a reformatted value ("5" shown as "5.00").</summary>
    public Dictionary<string, Func<string?, string?>> ReformatOnSet { get; } = [];
    public bool RefuseFocus { get; set; }
    public ScreenPoint MousePosition { get; set; } = new(10, 10);
    public string? FocusedKey { get; set; }
    public int GetControlsCalls { get; private set; }
    /// <summary>Thrown from the next call to <see cref="PerformAction"/> when set.</summary>
    public DesktopAutomationException? NextActionException { get; set; }

    private bool selectAllPending;

    /// <summary>After an action changes a control, this many RefreshControl reads still return the old state (like a lagging accessibility tree).</summary>
    public int StaleReadsAfterChange { get; set; }
    /// <summary>After FocusWindow, this many GetWindow reads still report the window as not in the foreground.</summary>
    public int StaleForegroundReads { get; set; }
    public int RefreshCalls { get; private set; }
    private readonly Dictionary<string, (ControlInfo Old, int Remaining)> staleControls = [];
    private readonly Dictionary<long, int> staleForeground = [];

    /// <summary>Applies a change made by an action, keeping the old state visible for <see cref="StaleReadsAfterChange"/> reads.</summary>
    private void Mutate(long window, string key, Func<ControlInfo, ControlInfo> change)
    {
        var old = Get(window, key);
        UpdateControl(window, key, change);
        if (StaleReadsAfterChange > 0 && !staleControls.ContainsKey(key)) staleControls[key] = (old, StaleReadsAfterChange);
    }

    public static ControlInfo Control(string key, string type, string name, ScreenRect bounds, string? value = null,
        int depth = 1, params ControlAction[] actions) =>
        new(key, type, name, null, null, value, true, false, false, bounds, depth, actions);

    public WindowInfo AddWindow(long handle, string process, string title, ScreenRect? bounds = null, bool foreground = false, params ControlInfo[] controls)
    {
        var w = new WindowInfo(handle, title, process, (int)handle, bounds ?? new ScreenRect(0, 0, 800, 600), false, foreground, 1);
        Windows.Add(w);
        ControlTrees[handle] = [.. controls];
        return w;
    }

    public void RemoveWindow(long handle)
    {
        Windows.RemoveAll(w => w.Handle == handle);
        ControlTrees.Remove(handle);
    }

    public void UpdateControl(long window, string key, Func<ControlInfo, ControlInfo> change)
    {
        var list = ControlTrees[window];
        var i = list.FindIndex(c => c.RuntimeKey == key);
        list[i] = change(list[i]);
    }

    public ControlInfo Get(long window, string key) => ControlTrees[window].Single(c => c.RuntimeKey == key);

    public IReadOnlyList<DisplayInfo> GetDisplays() { Calls.Add("GetDisplays"); return Displays; }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        Calls.Add("GetWindows");
        return Windows.OrderByDescending(w => w.IsForeground).ToList();
    }

    public WindowInfo? GetWindow(long handle)
    {
        var w = Windows.FirstOrDefault(x => x.Handle == handle);
        if (w is not null && staleForeground.TryGetValue(handle, out var remaining) && remaining > 0)
        {
            staleForeground[handle] = remaining - 1;
            return w with { IsForeground = false };
        }
        return w;
    }

    public bool FocusWindow(long handle)
    {
        Calls.Add($"FocusWindow {handle}");
        FocusRequests.Add(handle);
        if (RefuseFocus) return false;
        if (GetWindow(handle) is null) throw new DesktopAutomationException($"Window {handle} is gone.", FailureCategories.WindowGone);
        for (var i = 0; i < Windows.Count; i++) Windows[i] = Windows[i] with { IsForeground = Windows[i].Handle == handle };
        if (StaleForegroundReads > 0) staleForeground[handle] = StaleForegroundReads;
        return true;
    }

    public Screenshot CaptureDisplay(int displayIndex)
    {
        CapturedDisplays.Add(displayIndex);
        var d = Displays.First(x => x.Index == displayIndex);
        return new Screenshot(TinyPng, d.Bounds.Width, d.Bounds.Height, d.Bounds, $"screen {displayIndex}");
    }

    public Screenshot CaptureAllDisplays() => new(TinyPng, 1920, 1080, new ScreenRect(0, 0, 1920, 1080), "all screens");

    public Screenshot CaptureWindow(long handle)
    {
        var w = GetWindow(handle) ?? throw new DesktopAutomationException("Window is gone.", FailureCategories.WindowGone);
        return new Screenshot(TinyPng, w.Bounds.Width, w.Bounds.Height, w.Bounds, $"window '{w.Title}'");
    }

    public Screenshot CaptureRegion(ScreenRect region) => new(TinyPng, region.Width, region.Height, region, $"region {region}");

    /// <summary>Called with the 1-based walk number before each GetControls, so tests can add controls on the Nth walk.</summary>
    public Action<int>? BeforeWalk { get; set; }
    public ControlQuery? LastQuery { get; private set; }
    public bool LastWalkWasTruncated { get; set; }
    public string? LastWalkTruncationReason { get; set; }

    public IReadOnlyList<ControlInfo> GetControls(long windowHandle, ControlQuery query)
    {
        GetControlsCalls++;
        LastQuery = query;
        BeforeWalk?.Invoke(GetControlsCalls);
        Calls.Add($"GetControls {windowHandle}");
        if (!ControlTrees.TryGetValue(windowHandle, out var list)) throw new DesktopAutomationException("Window is gone.", FailureCategories.WindowGone);
        return list
            .Where(c => query.IncludeUnnamed || !string.IsNullOrEmpty(c.Name) || c.Value is not null)
            .Where(c => query.NameContains is null || c.Name.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase)
                        || (c.AutomationId?.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(c => query.ControlType is null || c.ControlType == query.ControlType)
            .Where(c => query.ControlTypes is null || query.ControlTypes.Contains(c.ControlType, StringComparer.OrdinalIgnoreCase))
            .Where(c => c.Depth <= query.MaxDepth)
            .Take(query.MaxCount)
            .ToList();
    }

    public ControlInfo? RefreshControl(long windowHandle, string runtimeKey)
    {
        RefreshCalls++;
        if (staleControls.TryGetValue(runtimeKey, out var stale))
        {
            if (stale.Remaining > 0)
            {
                staleControls[runtimeKey] = (stale.Old, stale.Remaining - 1);
                return stale.Old;
            }
            staleControls.Remove(runtimeKey);
        }
        return ControlTrees.TryGetValue(windowHandle, out var list) ? list.FirstOrDefault(c => c.RuntimeKey == runtimeKey) : null;
    }

    /// <summary>When set, GetControlAt returns this element (an overlay covering the target).</summary>
    public ControlInfo? HitOverride { get; set; }

    /// <summary>The deepest element at the point, searching the foreground window first (like a real hit test).</summary>
    public ControlInfo? GetControlAt(ScreenPoint point)
    {
        if (HitOverride is not null) return HitOverride;
        var order = Windows.OrderByDescending(w => w.IsForeground).Select(w => w.Handle).ToList();
        foreach (var handle in order)
            if (ControlTrees.TryGetValue(handle, out var list) && list.LastOrDefault(c => c.Bounds.Contains(point)) is { } hit) return hit;
        return null;
    }

    public ControlInfo? GetFocusedControl() =>
        FocusedKey is null ? null : ControlTrees.Values.SelectMany(x => x).FirstOrDefault(c => c.RuntimeKey == FocusedKey);

    public ControlActionResult PerformAction(long windowHandle, string runtimeKey, ControlAction action, string? value = null)
    {
        Actions.Add((windowHandle, runtimeKey, action, value));
        if (NextActionException is { } ex) { NextActionException = null; throw ex; }
        var c = RefreshControl(windowHandle, runtimeKey) ?? throw new DesktopAutomationException("Control is gone.", FailureCategories.ControlGone);
        if (!c.SupportedActions.Contains(action)) return new ControlActionResult(false, "not-supported", $"{action} is not supported.");
        switch (action)
        {
            case ControlAction.SetValue when !SetValueIgnored.Contains(runtimeKey):
                var shown = ReformatOnSet.TryGetValue(runtimeKey, out var reformat) ? reformat(value) : value;
                Mutate(windowHandle, runtimeKey, x => x with { Value = shown });
                break;
            case ControlAction.Toggle:
                Mutate(windowHandle, runtimeKey, x => x with { IsToggled = !(x.IsToggled ?? false), Value = x.IsToggled == true ? "off" : "on" });
                break;
            case ControlAction.Focus:
                FocusedKey = runtimeKey;
                break;
        }
        return new ControlActionResult(true, action switch
        {
            ControlAction.SetValue => "value-pattern",
            ControlAction.Toggle => "toggle-pattern",
            _ => action.ToString().ToLowerInvariant() + "-pattern",
        });
    }

    /// <summary>Idle times returned by successive GetUserIdleTime calls; when empty, <see cref="UserIdleTime"/> is returned.</summary>
    public Queue<TimeSpan?> UserIdleTimes { get; } = new();
    /// <summary>Null means the platform cannot tell (the session then never waits).</summary>
    public TimeSpan? UserIdleTime { get; set; }
    public int IdleChecks { get; private set; }

    public TimeSpan? GetUserIdleTime()
    {
        IdleChecks++;
        return UserIdleTimes.Count > 0 ? UserIdleTimes.Dequeue() : UserIdleTime;
    }

    public ScreenPoint GetMousePosition() => MousePosition;

    public void MoveMouse(ScreenPoint point)
    {
        MouseMoves.Add(point);
        MousePosition = point;
    }

    public List<string?> ClickModifiers { get; } = [];
    public List<(ScreenPoint From, ScreenPoint To, MouseButton Button)> Drags { get; } = [];

    public void Drag(ScreenPoint from, ScreenPoint to, MouseButton button = MouseButton.Left) => Drags.Add((from, to, button));

    public void Click(ScreenPoint point, MouseButton button = MouseButton.Left, int clicks = 1, string? modifiers = null)
    {
        Calls.Add($"Click {point}");
        Clicks.Add((point, button, clicks));
        ClickModifiers.Add(modifiers);
        MousePosition = point;
        var target = GetControlAt(point);
        if (target is not null) FocusedKey = target.RuntimeKey;
    }

    public void Scroll(ScreenPoint point, int deltaX, int deltaY) => Scrolls.Add((point, deltaX, deltaY));

    public void TypeText(string text)
    {
        TypedText.Add(text);
        ReplaceFocused(current => selectAllPending ? text : (current ?? "") + text);
    }

    public void PressKeys(string chord)
    {
        KeyChords.Add(chord);
        if (chord == "primary+a") { selectAllPending = true; return; }
        if (chord == "delete" && selectAllPending) ReplaceFocused(_ => "");
    }

    private void ReplaceFocused(Func<string?, string> change)
    {
        selectAllPending = false;
        if (FocusedKey is null) return;
        foreach (var (window, list) in ControlTrees)
        {
            var i = list.FindIndex(c => c.RuntimeKey == FocusedKey);
            if (i >= 0) { Mutate(window, FocusedKey, c => c with { Value = change(c.Value) }); return; }
        }
    }
}
