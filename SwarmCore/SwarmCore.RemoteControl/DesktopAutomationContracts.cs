namespace SwarmCore.RemoteControl;

/// <summary>A point in physical screen pixels on the virtual desktop (all monitors share one coordinate space).</summary>
public readonly record struct ScreenPoint(int X, int Y)
{
    public override string ToString() => $"{X},{Y}";
}

/// <summary>A rectangle in physical screen pixels on the virtual desktop.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public ScreenPoint Center => new(Left + Width / 2, Top + Height / 2);
    public bool Contains(ScreenPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    public static ScreenRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
    public override string ToString() => $"{Left},{Top} {Width}x{Height}";
}

/// <summary>One monitor. <see cref="Index"/> is 1-based, primary first, then left-to-right/top-to-bottom; it is what users mean by "screen 2".</summary>
public sealed record DisplayInfo(int Index, string DeviceName, bool IsPrimary, ScreenRect Bounds, ScreenRect WorkArea, double ScaleFactor);

/// <summary>A visible top-level window. <see cref="Handle"/> is the native window handle (HWND on Windows) and is stable while the window lives.</summary>
public sealed record WindowInfo(long Handle, string Title, string ProcessName, int ProcessId, ScreenRect Bounds,
    bool IsMinimized, bool IsForeground, int? DisplayIndex)
{
    public string? ClassName { get; init; }
    /// <summary>True when the window belongs to a process running at a higher integrity level than ours (input to it will be blocked unless we are elevated).</summary>
    public bool IsElevated { get; init; }
}

/// <summary>Actions a control may support through its accessibility patterns (not physical mouse/keyboard).</summary>
public enum ControlAction { Invoke, Toggle, Select, Expand, Collapse, SetValue, Focus, ScrollIntoView }

/// <summary>
/// One accessible UI element inside a window. <see cref="RuntimeKey"/> is an opaque identifier that is stable
/// while the element exists (UI Automation RuntimeId on Windows); callers pass it back to act on the element.
/// <see cref="ControlType"/> is a lowercase platform-neutral type name such as button, edit, hyperlink, checkbox,
/// radiobutton, combobox, listitem, menuitem, tabitem, text, document, pane, window.
/// <see cref="Value"/> is whichever best describes the control's current state: the ValuePattern value for edits
/// and combos, the toggle state ("on"/"off"/"indeterminate") for check boxes, "selected" for selected items,
/// the range value for sliders, or the text content for short text elements. Null when nothing applies.
/// </summary>
public sealed record ControlInfo(
    string RuntimeKey,
    string ControlType,
    string Name,
    string? AutomationId,
    string? ClassName,
    string? Value,
    bool IsEnabled,
    bool IsOffscreen,
    bool HasKeyboardFocus,
    ScreenRect Bounds,
    int Depth,
    IReadOnlyList<ControlAction> SupportedActions)
{
    public string? HelpText { get; init; }
    public string? ParentRuntimeKey { get; init; }
    public bool? IsToggled { get; init; }
    public bool? IsSelected { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsPassword { get; init; }
    public bool IsClickable => !IsOffscreen && !Bounds.IsEmpty;
}

/// <summary>
/// Limits for walking a window's control tree. The defaults are unlimited: the user decides what to read.
/// Hosts may pass smaller values for a quick overview, and the session applies the user's "all" / "everything"
/// words to turn the include flags on.
/// </summary>
public sealed record ControlQuery
{
    /// <summary>Maximum tree depth to descend; <see cref="int.MaxValue"/> means unlimited.</summary>
    public int MaxDepth { get; init; } = int.MaxValue;
    /// <summary>Maximum elements to return; <see cref="int.MaxValue"/> means unlimited.</summary>
    public int MaxCount { get; init; } = int.MaxValue;
    /// <summary>Include elements the platform reports as scrolled out of view.</summary>
    public bool IncludeOffscreen { get; init; } = true;
    /// <summary>Include nameless, valueless structural elements (panes, groups, generic containers).</summary>
    public bool IncludeUnnamed { get; init; }
    /// <summary>Case-insensitive substring filter on Name (and AutomationId); null returns everything.</summary>
    public string? NameContains { get; init; }
    /// <summary>Platform-neutral control type filter (see <see cref="ControlInfo.ControlType"/>); null returns every type.</summary>
    public string? ControlType { get; init; }
    /// <summary>
    /// Platform-neutral control types to return (a type and its family, e.g. edit + combobox); null returns every type.
    /// Adapters should skip fetching attributes for elements outside these types and outside <see cref="NameContains"/>,
    /// which is what makes a targeted lookup fast on large trees.
    /// </summary>
    public IReadOnlyList<string>? ControlTypes { get; init; }
    /// <summary>Upper bound on wall time for the walk; the walk returns what it has so far when it expires. <see cref="Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>A PNG capture. <see cref="ScreenBounds"/> is where the image sits on the virtual desktop so pixel coordinates can be mapped back to screen points.</summary>
public sealed record Screenshot(byte[] Png, int Width, int Height, ScreenRect ScreenBounds, string Subject);

public enum MouseButton { Left, Right, Middle }

/// <summary><see cref="Method"/> names what was actually done ("value-pattern", "invoke-pattern", "mouse-click", "keyboard-replace") so the user sees how the action reached the control.</summary>
public sealed record ControlActionResult(bool Succeeded, string Method, string? Message = null, ControlInfo? Control = null);

/// <summary>
/// Why an operation failed, so callers (people and agents) can choose the right recovery instead of treating
/// everything as "permission denied". Values are stable strings carried on <see cref="DesktopAutomationException.Category"/>
/// and <c>CommandResult.FailureCategory</c>.
/// </summary>
public static class FailureCategories
{
    /// <summary>The control existed when observed but is gone now; observe again.</summary>
    public const string ControlGone = "control-gone";
    /// <summary>The window closed or its process ended; list windows again.</summary>
    public const string WindowGone = "window-gone";
    /// <summary>The target runs at a higher integrity level (elevated) than this process, so input is blocked; run elevated.</summary>
    public const string TargetElevated = "target-elevated";
    /// <summary>The target application is not responding to messages; wait and retry.</summary>
    public const string AppBusy = "app-busy";
    /// <summary>A system authentication surface (UAC secure desktop, lock screen, credential dialog) owns the display; a person must complete it.</summary>
    public const string AuthenticationRequired = "authentication-required";
    /// <summary>An OS permission is missing (macOS Accessibility or Screen Recording); grant it in system settings.</summary>
    public const string Permission = "permission";
    /// <summary>The OS refused an action for another reason (foreground lock, input blocked).</summary>
    public const string OsRefused = "os-refused";
    /// <summary>The control does not support the requested action; the message lists what it does support.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>More than one window or control matched equally well; the message lists the candidates.</summary>
    public const string Ambiguous = "ambiguous";
    /// <summary>Nothing matched the selector.</summary>
    public const string NotFound = "not-found";
    /// <summary>The command text could not be parsed.</summary>
    public const string NotUnderstood = "not-understood";
    /// <summary>A wait-for condition did not become true before the timeout.</summary>
    public const string Timeout = "timeout";
    /// <summary>The person is using the keyboard or mouse right now, so the action was not sent; retry when they are idle.</summary>
    public const string UserActive = "user-active";
}

/// <summary>A platform refused or failed an automation step. The message is user-facing; <see cref="Category"/> is one of <see cref="FailureCategories"/>.</summary>
public sealed class DesktopAutomationException : Exception
{
    public DesktopAutomationException(string message, string? category = null) : base(message) { Category = category; }
    public DesktopAutomationException(string message, Exception inner, string? category = null) : base(message, inner) { Category = category; }
    public string? Category { get; }
}

/// <summary>
/// Platform contract for looking at and driving the local desktop. All coordinates are physical pixels on the
/// virtual desktop. Implementations are synchronous and may block for the duration of a tree walk; the session
/// layer owns threading. Implementations must throw <see cref="DesktopAutomationException"/> with a user-facing
/// message when a target is gone, inaccessible, or refuses focus, and must never silently act on a different
/// window or control than the one identified. Implementations impose no limits of their own beyond what the
/// operating system enforces; when the OS blocks something (an elevated window, the secure desktop) the
/// exception message says so and what would unblock it (for example running elevated).
/// </summary>
public interface IDesktopAutomation
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>Visible, titled, top-level windows (what a user calls "running apps"), foreground first then z-order.</summary>
    IReadOnlyList<WindowInfo> GetWindows();
    WindowInfo? GetWindow(long handle);
    /// <summary>Restore if minimized and bring to the foreground. Returns false when the OS refused activation.</summary>
    bool FocusWindow(long handle);

    Screenshot CaptureDisplay(int displayIndex);
    Screenshot CaptureAllDisplays();
    /// <summary>Capture the window's own pixels (including when partly covered); restores a minimized window without activating it.</summary>
    Screenshot CaptureWindow(long handle);
    Screenshot CaptureRegion(ScreenRect region);

    /// <summary>Walk the window's accessibility tree depth-first in document order, honoring the query limits.</summary>
    IReadOnlyList<ControlInfo> GetControls(long windowHandle, ControlQuery query);
    /// <summary>True when the most recent <see cref="GetControls"/> stopped early (timeout or MaxCount), so "not found" may mean "not reached".</summary>
    bool LastWalkWasTruncated => false;
    /// <summary>Why the most recent walk stopped early, in user-facing words; null when it completed.</summary>
    string? LastWalkTruncationReason => null;
    /// <summary>Re-read one control by its runtime key; null when it no longer exists.</summary>
    ControlInfo? RefreshControl(long windowHandle, string runtimeKey);
    ControlInfo? GetControlAt(ScreenPoint point);
    ControlInfo? GetFocusedControl();
    /// <summary>Perform an accessibility-pattern action. <paramref name="value"/> is required for <see cref="ControlAction.SetValue"/>.</summary>
    ControlActionResult PerformAction(long windowHandle, string runtimeKey, ControlAction action, string? value = null);

    /// <summary>
    /// How long ago the person last touched the keyboard or mouse themselves (input this process injected does not
    /// count). Null when the platform cannot tell. The session uses it to hold an action while the person is typing.
    /// </summary>
    TimeSpan? GetUserIdleTime();

    ScreenPoint GetMousePosition();
    void MoveMouse(ScreenPoint point);
    /// <summary>
    /// Click at a point. <paramref name="modifiers"/> is a chord of modifier names held down for the click ("shift",
    /// "ctrl+shift", "primary"); null or empty means none.
    /// </summary>
    void Click(ScreenPoint point, MouseButton button = MouseButton.Left, int clicks = 1, string? modifiers = null);
    /// <summary>Press at <paramref name="from"/>, move to <paramref name="to"/>, release.</summary>
    void Drag(ScreenPoint from, ScreenPoint to, MouseButton button = MouseButton.Left);
    /// <summary>Wheel notches; positive deltaY scrolls up (away from the user), negative scrolls down.</summary>
    void Scroll(ScreenPoint point, int deltaX, int deltaY);
    /// <summary>Type literal text (Unicode) into whatever has keyboard focus.</summary>
    void TypeText(string text);
    /// <summary>
    /// Press a key chord such as "enter", "ctrl+s", "alt+f4", "ctrl+shift+t", "f5", "tab", "escape", "win+r".
    /// The modifier name "primary" means the platform's primary command modifier: Control on Windows, Command on macOS,
    /// so "primary+a" selects all and "primary+c" copies on both.
    /// </summary>
    void PressKeys(string chord);
}
