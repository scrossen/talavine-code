namespace SwarmCore.RemoteControl.Agent.Tests;

/// <summary>
/// In-memory desktop for procedure tests (adapted from SwarmCore.RemoteControl.Tests' FakeDesktopAutomation, kept
/// separate so this lane does not break while that fake follows interface changes). Activating a control runs
/// <see cref="OnActivate"/> so a test can change the page the way a site would.
/// </summary>
public sealed class ShopDesktop : IDesktopAutomation
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public List<WindowInfo> Windows { get; } = [];
    public Dictionary<long, List<ControlInfo>> Trees { get; } = [];
    public List<string> Typed { get; } = [];
    public List<(string Key, ControlAction Action, string? Value)> Actions { get; } = [];
    public List<ScreenPoint> Clicks { get; } = [];
    public string? FocusedKey { get; set; }
    public Action<long, ControlInfo>? OnActivate { get; set; }
    private int _next = 1;

    public static readonly ControlAction[] ButtonActions = [ControlAction.Invoke, ControlAction.Focus];
    public static readonly ControlAction[] EditActions = [ControlAction.SetValue, ControlAction.Focus];

    public WindowInfo AddWindow(long handle, string process, string title, bool foreground = false)
    {
        var w = new WindowInfo(handle, title, process, (int)handle, new ScreenRect(0, 0, 1200, 800), false, foreground, 1);
        Windows.Add(w);
        Trees[handle] = [];
        return w;
    }

    public ControlInfo Add(long window, string type, string name, string? value = null, bool password = false)
    {
        var n = _next++;
        var c = new ControlInfo($"k{n}", type, name, null, null, value, true, false, false,
            new ScreenRect(10, 10 + n * 30, 200, 24), 1, type == "edit" ? EditActions : ButtonActions)
        { IsPassword = password };
        Trees[window].Add(c);
        return c;
    }

    public void Rename(long window, string from, string to)
    {
        var list = Trees[window];
        var i = list.FindIndex(c => c.Name == from);
        if (i >= 0) list[i] = list[i] with { Name = to };
    }

    public void SetValue(long window, string name, string? value)
    {
        var list = Trees[window];
        var i = list.FindIndex(c => c.Name == name);
        if (i >= 0) list[i] = list[i] with { Value = value };
    }

    public ControlInfo? Find(long window, string name) => Trees[window].FirstOrDefault(c => c.Name == name);

    public IReadOnlyList<DisplayInfo> GetDisplays() =>
        [new(1, @"\\.\DISPLAY1", true, new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 1.0)];
    public IReadOnlyList<WindowInfo> GetWindows() => Windows.OrderByDescending(w => w.IsForeground).ToList();
    public WindowInfo? GetWindow(long handle) => Windows.FirstOrDefault(w => w.Handle == handle);

    public bool FocusWindow(long handle)
    {
        for (var i = 0; i < Windows.Count; i++) Windows[i] = Windows[i] with { IsForeground = Windows[i].Handle == handle };
        return true;
    }

    public Screenshot CaptureDisplay(int displayIndex) => new(TinyPng, 1920, 1080, new ScreenRect(0, 0, 1920, 1080), "screen");
    public Screenshot CaptureAllDisplays() => CaptureDisplay(1);
    public Screenshot CaptureWindow(long handle) => new(TinyPng, 1200, 800, new ScreenRect(0, 0, 1200, 800), "window");
    public Screenshot CaptureRegion(ScreenRect region) => new(TinyPng, region.Width, region.Height, region, "region");

    public IReadOnlyList<ControlInfo> GetControls(long windowHandle, ControlQuery query)
    {
        if (!Trees.TryGetValue(windowHandle, out var list)) throw new DesktopAutomationException("Window is gone.", FailureCategories.WindowGone);
        return list
            .Where(c => query.NameContains is null || c.Name.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase))
            .Where(c => query.ControlType is null || c.ControlType == query.ControlType)
            .Take(query.MaxCount).ToList();
    }

    public ControlInfo? RefreshControl(long windowHandle, string runtimeKey) =>
        Trees.TryGetValue(windowHandle, out var list) ? list.FirstOrDefault(c => c.RuntimeKey == runtimeKey) : null;

    public ControlInfo? GetControlAt(ScreenPoint point) => Trees.Values.SelectMany(x => x).LastOrDefault(c => c.Bounds.Contains(point));
    public ControlInfo? GetFocusedControl() => FocusedKey is null ? null : Trees.Values.SelectMany(x => x).FirstOrDefault(c => c.RuntimeKey == FocusedKey);

    public ControlActionResult PerformAction(long windowHandle, string runtimeKey, ControlAction action, string? value = null)
    {
        Actions.Add((runtimeKey, action, value));
        var c = RefreshControl(windowHandle, runtimeKey) ?? throw new DesktopAutomationException("Control is gone.", FailureCategories.ControlGone);
        if (!c.SupportedActions.Contains(action)) return new ControlActionResult(false, "not-supported", $"{action} is not supported.");
        var list = Trees[windowHandle];
        switch (action)
        {
            case ControlAction.SetValue:
                list[list.FindIndex(x => x.RuntimeKey == runtimeKey)] = c with { Value = value };
                break;
            case ControlAction.Focus:
                FocusedKey = runtimeKey;
                break;
            case ControlAction.Invoke:
                OnActivate?.Invoke(windowHandle, c);
                break;
        }
        return new ControlActionResult(true, action == ControlAction.SetValue ? "value-pattern" : action.ToString().ToLowerInvariant() + "-pattern");
    }

    public TimeSpan? GetUserIdleTime() => null;
    public ScreenPoint GetMousePosition() => new(0, 0);
    public void MoveMouse(ScreenPoint point) { }

    public void Click(ScreenPoint point, MouseButton button = MouseButton.Left, int clicks = 1, string? modifiers = null)
    {
        Clicks.Add(point);
        foreach (var (window, list) in Trees)
            if (list.LastOrDefault(c => c.Bounds.Contains(point)) is { } target)
            {
                FocusedKey = target.RuntimeKey;
                OnActivate?.Invoke(window, target);
                return;
            }
    }

    public void Drag(ScreenPoint from, ScreenPoint to, MouseButton button = MouseButton.Left) { }
    public void Scroll(ScreenPoint point, int deltaX, int deltaY) { }

    public void TypeText(string text)
    {
        Typed.Add(text);
        if (FocusedKey is null) return;
        foreach (var list in Trees.Values)
        {
            var i = list.FindIndex(c => c.RuntimeKey == FocusedKey);
            if (i >= 0) { list[i] = list[i] with { Value = (list[i].Value ?? "") + text }; return; }
        }
    }

    public void PressKeys(string chord) => Typed.Add("{" + chord + "}");
}
