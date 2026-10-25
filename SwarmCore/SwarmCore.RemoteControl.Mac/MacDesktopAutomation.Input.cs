using SwarmCore.RemoteControl.Mac.Mapping;
using SwarmCore.RemoteControl.Mac.Native;

namespace SwarmCore.RemoteControl.Mac;

// Synthetic input is posted at kCGHIDEventTap. macOS silently drops posted events from processes without
// Accessibility permission, so every input method checks it first and says how to grant it.
public sealed partial class MacDesktopAutomation
{
    private const int ClickIntervalMs = 60;
    private const int KeyIntervalMs = 8;

    // Process-wide: injected input is per process, not per adapter instance.
    private static readonly UserIdleTracker s_idle = new();

    private static double MonotonicSeconds() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Reads the HID idle counter and folds it into the tracker.</summary>
    private static TimeSpan? SampleIdle() =>
        s_idle.Observe(MonotonicSeconds(), CG.CGEventSourceSecondsSinceLastEventType(CG.kCGEventSourceStateHIDSystemState, CG.kCGAnyInputEventType));

    /// <summary>
    /// Time since the person last used the keyboard or mouse, from the HID system idle counter with this process's
    /// own injected events discounted. Null when only our own input has been seen since the first injection.
    /// </summary>
    public TimeSpan? GetUserIdleTime()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return SampleIdle();
        }
    }

    public ScreenPoint GetMousePosition()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var p = ReadCursor();
            return new ScreenPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
        }
    }

    public void MoveMouse(ScreenPoint point)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureInput();
            MoveTo(point);
        }
    }

    public void Click(ScreenPoint point, MouseButton button = MouseButton.Left, int clicks = 1, string? modifiers = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clicks, 1);
        var held = MacKeyMap.ParseModifiers(modifiers);
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureInput();
            var (down, up, _, cgButton) = ButtonEvents(button);
            MoveTo(point);
            Thread.Sleep(15);
            var location = new CGPoint(point.X, point.Y);
            var flags = PressModifiers(held);
            try
            {
                for (var i = 1; i <= clicks; i++)
                {
                    if (i > 1) Thread.Sleep(ClickIntervalMs);
                    PostMouse(down, location, cgButton, i, flags);
                    Thread.Sleep(10);
                    PostMouse(up, location, cgButton, i, flags);
                }
            }
            finally
            {
                ReleaseModifiers(held);
            }
        }
    }

    /// <summary>Mouse down at <paramref name="from"/>, a series of dragged events along the line, mouse up at <paramref name="to"/>.</summary>
    public void Drag(ScreenPoint from, ScreenPoint to, MouseButton button = MouseButton.Left)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureInput();
            var (down, up, dragged, cgButton) = ButtonEvents(button);
            MoveTo(from);
            Thread.Sleep(15);
            var start = new CGPoint(from.X, from.Y);
            var end = new CGPoint(to.X, to.Y);
            PostMouse(down, start, cgButton, 1, 0);
            try
            {
                const int steps = 12;
                for (var i = 1; i <= steps; i++)
                {
                    Thread.Sleep(15);
                    var t = i / (double)steps;
                    var p = new CGPoint(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
                    Post(CG.CGEventCreateMouseEvent(0, dragged, p, cgButton), "mouse drag");
                }
                Thread.Sleep(30);
            }
            finally
            {
                // Always release the button, even if a move failed, so the system is not left with a held button.
                PostMouse(up, end, cgButton, 1, 0);
            }
        }
    }

    private static (uint Down, uint Up, uint Dragged, uint Button) ButtonEvents(MouseButton button) => button switch
    {
        MouseButton.Left => (CG.kCGEventLeftMouseDown, CG.kCGEventLeftMouseUp, CG.kCGEventLeftMouseDragged, CG.kCGMouseButtonLeft),
        MouseButton.Right => (CG.kCGEventRightMouseDown, CG.kCGEventRightMouseUp, CG.kCGEventRightMouseDragged, CG.kCGMouseButtonRight),
        MouseButton.Middle => (CG.kCGEventOtherMouseDown, CG.kCGEventOtherMouseUp, CG.kCGEventOtherMouseDragged, CG.kCGMouseButtonCenter),
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
    };

    /// <summary>Posts modifier key-downs (accumulating flags) and returns the combined flags.</summary>
    private static ulong PressModifiers(MacModifiers modifiers)
    {
        ulong flags = 0;
        foreach (var (_, keyCode, flag) in MacKeyMap.ModifierKeys(modifiers))
        {
            flags |= flag;
            PostKey(keyCode, true, flags);
        }
        return flags;
    }

    /// <summary>Posts modifier key-ups in reverse order with decreasing flags.</summary>
    private static void ReleaseModifiers(MacModifiers modifiers)
    {
        var keys = MacKeyMap.ModifierKeys(modifiers);
        var flags = MacKeyMap.ToEventFlags(modifiers);
        for (var i = keys.Count - 1; i >= 0; i--)
        {
            flags &= ~keys[i].Flag;
            PostKey(keys[i].KeyCode, false, flags);
        }
    }

    public void Scroll(ScreenPoint point, int deltaX, int deltaY)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureInput();
            MoveTo(point);
            // Line units, 3 lines per notch. wheel1 > 0 scrolls up (contract: positive deltaY = up). Quartz treats a
            // positive wheel2 as scrolling left, so deltaX is negated to keep positive deltaX = right as on Windows.
            var ev = CG.CGEventCreateScrollWheelEvent2(0, CG.kCGScrollEventUnitLine, 2, deltaY * 3, -deltaX * 3, 0);
            Post(ev, "scroll");
        }
    }

    public void TypeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureInput();
            foreach (var segment in MacTextChunker.Split(text))
            {
                if (segment.KeyCode is { } code)
                {
                    PostKey(code, true, 0);
                    PostKey(code, false, 0);
                }
                else
                {
                    PostUnicode(segment.Text!, true);
                    PostUnicode(segment.Text!, false);
                }
                Thread.Sleep(KeyIntervalMs);
            }
        }
    }

    public void PressKeys(string chord)
    {
        var parsed = MacKeyMap.Parse(chord);
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureInput();
            var flags = PressModifiers(parsed.Modifiers);
            try
            {
                if (parsed.KeyCode is { } key)
                {
                    PostKey(key, true, flags);
                    Thread.Sleep(KeyIntervalMs);
                    PostKey(key, false, flags);
                }
            }
            finally
            {
                ReleaseModifiers(parsed.Modifiers);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void EnsureInput()
    {
        EnsureUnlocked();
        if (!AX.AXIsProcessTrusted())
            throw new DesktopAutomationException($"macOS blocks synthetic mouse and keyboard input. {MacMessages.AccessibilityHint}", FailureCategories.Permission);
    }

    private static CGPoint ReadCursor()
    {
        var ev = CG.CGEventCreate(0);
        if (ev == 0)
            throw new DesktopAutomationException($"macOS refused to read the mouse position. {MacMessages.AccessibilityHint}", FailureCategories.Permission);
        try
        {
            return CG.CGEventGetLocation(ev);
        }
        finally
        {
            CF.CFRelease(ev);
        }
    }

    private static void MoveTo(ScreenPoint point)
    {
        var target = new CGPoint(point.X, point.Y);
        Post(CG.CGEventCreateMouseEvent(0, CG.kCGEventMouseMoved, target, CG.kCGMouseButtonLeft), "mouse move");
        var now = ReadCursor();
        if (Math.Abs(now.X - target.X) > 1 || Math.Abs(now.Y - target.Y) > 1)
        {
            var error = CG.CGWarpMouseCursorPosition(target);
            s_idle.RecordInjection(MonotonicSeconds());
            if (error != CG.kCGErrorSuccess)
                throw new DesktopAutomationException($"macOS refused to move the mouse (CGError {error}). {MacMessages.AccessibilityHint}",
                    error == CG.kCGErrorFailure ? FailureCategories.Permission : FailureCategories.OsRefused);
        }
    }

    private static void PostMouse(uint type, CGPoint location, uint button, int clickState, ulong flags)
    {
        var ev = CG.CGEventCreateMouseEvent(0, type, location, button);
        if (ev != 0)
        {
            CG.CGEventSetIntegerValueField(ev, CG.kCGMouseEventClickState, clickState);
            CG.CGEventSetFlags(ev, flags);
        }
        Post(ev, "mouse click");
    }

    private static void PostKey(ushort keyCode, bool down, ulong flags)
    {
        var ev = CG.CGEventCreateKeyboardEvent(0, keyCode, down);
        if (ev != 0) CG.CGEventSetFlags(ev, flags);
        Post(ev, "key press");
    }

    private static unsafe void PostUnicode(string chunk, bool down)
    {
        var ev = CG.CGEventCreateKeyboardEvent(0, 0, down);
        if (ev != 0)
        {
            // Clear modifiers so a physically held key cannot turn typed text into shortcuts.
            CG.CGEventSetFlags(ev, 0);
            fixed (char* p = chunk)
            {
                CG.CGEventKeyboardSetUnicodeString(ev, (nuint)chunk.Length, p);
            }
        }
        Post(ev, "typing");
    }

    /// <summary>Posts and releases a +1 event; a NULL event means macOS refused to create it.</summary>
    private static void Post(nint ev, string what)
    {
        if (ev == 0)
            throw new DesktopAutomationException($"macOS refused to create the {what} event. {MacMessages.AccessibilityHint}", FailureCategories.Permission);
        try
        {
            // Sample first so the person's latest input is captured before our event becomes the newest one.
            SampleIdle();
            CG.CGEventPost(CG.kCGHIDEventTap, ev);
            s_idle.RecordInjection(MonotonicSeconds());
        }
        finally
        {
            CF.CFRelease(ev);
        }
    }
}
