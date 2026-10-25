using System.Runtime.InteropServices;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

public sealed partial class WindowsDesktopAutomation
{
    /// <summary>Spacing between the presses of a multi-click: long enough for apps to see separate presses, well inside the double-click time.</summary>
    private const int MultiClickIntervalMilliseconds = 40;
    /// <summary>UTF-16 code units per SendInput call while typing; the foreground is re-checked between runs.</summary>
    private const int TypingRunLength = 32;
    private const int DragSteps = 8;
    private const int DragStepMilliseconds = 4;
    private static readonly int InputSize = Marshal.SizeOf<Input>();
    private volatile string? lastInputNote;

    /// <summary>
    /// A note about how the most recent mouse operation was adjusted (for example a point in a gap between monitors
    /// moved to the nearest monitor edge), or null when the point was used exactly as given.
    /// </summary>
    public string? LastInputNote => lastInputNote;

    // ---- Mouse -----------------------------------------------------------------------------------------------

    public ScreenPoint GetMousePosition()
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        if (!GetCursorPos(out var point))
            throw InputFailure("reading the mouse position", Marshal.GetLastPInvokeError(), GetForegroundWindow());
        return new ScreenPoint(point.X, point.Y);
    }

    public void MoveMouse(ScreenPoint point)
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        point = ResolveOnScreen(point);
        if (DesktopState.IsAuthenticationSurfaceActive()) throw DesktopState.AuthenticationRequired("moving the mouse");
        MoveTo(point);
    }

    public void Click(ScreenPoint point, MouseButton button = MouseButton.Left, int clicks = 1, string? modifiers = null)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(clicks, 1);
        var held = ParseModifierChord(modifiers);
        using var dpi = new DpiScope();
        point = ResolveOnScreen(point);
        var target = RootWindowAt(point);
        var step = $"clicking at {point}";
        DesktopState.EnsureInputCanReach(target, step);
        EnsureUserIdle();
        MoveTo(point);
        var (down, up) = ButtonFlags(button);
        // The only delay in a click: presses of a multi-click are spaced so apps register them as separate presses
        // of one N-click, always well inside the user's double-click time.
        var interval = (int)Math.Min(MultiClickIntervalMilliseconds, Math.Max(1, GetDoubleClickTime() / 4));
        WithModifiersHeld(target, step, held, () =>
        {
            for (var i = 0; i < clicks; i++)
            {
                if (i > 0) Thread.Sleep(interval);
                Send(target, step, MouseEvent(down), MouseEvent(up));
            }
        });
    }

    public void Drag(ScreenPoint from, ScreenPoint to, MouseButton button = MouseButton.Left)
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        from = ResolveOnScreen(from);
        var fromNote = lastInputNote;
        to = ResolveOnScreen(to);
        lastInputNote = fromNote is null ? lastInputNote : lastInputNote is null ? fromNote : fromNote + " " + lastInputNote;
        var step = $"dragging from {from} to {to}";
        var source = RootWindowAt(from);
        DesktopState.EnsureInputCanReach(source, step);
        var destination = RootWindowAt(to);
        if (destination != source) DesktopState.EnsureInputCanReach(destination, step);
        EnsureUserIdle();
        var (down, up) = ButtonFlags(button);
        MoveTo(from);
        Send(source, step, MouseEvent(down));
        try
        {
            for (var i = 1; i <= DragSteps; i++)
            {
                // Intermediate moves, a few ms apart, so the source sees motion past the system drag threshold and
                // drop targets get their hover events, as with a real hand-made drag.
                Thread.Sleep(DragStepMilliseconds);
                MoveTo(new ScreenPoint(from.X + (to.X - from.X) * i / DragSteps, from.Y + (to.Y - from.Y) * i / DragSteps));
            }
        }
        finally
        {
            // Always release the button, even if a move failed, so it is never left held down.
            SendInput(1, [MouseEvent(up)], InputSize);
        }
    }

    private static (uint Down, uint Up) ButtonFlags(MouseButton button) => button switch
    {
        MouseButton.Right => (MouseEventRightDown, MouseEventRightUp),
        MouseButton.Middle => (MouseEventMiddleDown, MouseEventMiddleUp),
        _ => (MouseEventLeftDown, MouseEventLeftUp),
    };

    /// <summary>Parses a chord of modifier names ("shift", "ctrl+shift", "primary"); null or empty means none.</summary>
    private static List<VirtualKey> ParseModifierChord(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) return [];
        var keys = new List<VirtualKey>();
        foreach (var name in KeyMap.Split(chord))
        {
            var key = KeyMap.Resolve(name, keys);
            if (key is not { } k || !KeyMap.IsModifier(k.Code))
                throw KeyMap.NotUnderstood($"'{name}' is not a modifier key; a click can hold shift, ctrl, alt, win or primary.");
            keys.Add(k);
        }
        return keys.DistinctBy(k => k.Code).ToList();
    }

    /// <summary>Holds the modifiers down around <paramref name="action"/> and releases every one that went down, in reverse, in finally.</summary>
    private static void WithModifiersHeld(nint target, string step, List<VirtualKey> modifiers, Action action)
    {
        var held = new List<VirtualKey>();
        try
        {
            foreach (var modifier in modifiers)
            {
                Send(target, step, KeyEvent(modifier, keyUp: false));
                held.Add(modifier);
            }
            action();
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--)
                SendInput(1, [KeyEvent(held[i], keyUp: true)], InputSize);
        }
    }

    public void Scroll(ScreenPoint point, int deltaX, int deltaY)
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        point = ResolveOnScreen(point);
        var target = RootWindowAt(point);
        DesktopState.EnsureInputCanReach(target, $"scrolling at {point}");
        EnsureUserIdle();
        MoveTo(point);
        SendWheel(target, point, MouseEventWheel, deltaY);
        SendWheel(target, point, MouseEventHWheel, deltaX);
    }

    private static void SendWheel(nint target, ScreenPoint point, uint flag, int notches)
    {
        // No cap on notches: split totals that overflow one event's 32-bit delta into several events.
        var remaining = (long)notches * WheelDelta;
        var largestStep = int.MaxValue / WheelDelta * WheelDelta;
        while (remaining != 0)
        {
            var step = (int)Math.Clamp(remaining, -largestStep, largestStep);
            Send(target, $"scrolling at {point}", MouseEvent(flag, step));
            remaining -= step;
        }
    }

    /// <summary>SetCursorPos for the pointer itself plus an absolute virtual-desk move so apps that read raw input see it too.</summary>
    private static void MoveTo(ScreenPoint point)
    {
        if (!SetCursorPos(point.X, point.Y))
            throw InputFailure($"moving the mouse to {point}", Marshal.GetLastPInvokeError(), RootWindowAt(point));
        var screen = VirtualScreen();
        var move = MouseEvent(MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk);
        move.Data.Mouse.X = NormalizeAbsolute(point.X - screen.Left, screen.Width);
        move.Data.Mouse.Y = NormalizeAbsolute(point.Y - screen.Top, screen.Height);
        Send(RootWindowAt(point), $"moving the mouse to {point}", move);
    }

    /// <summary>
    /// Windows maps an absolute coordinate back to a pixel with floor(normalized * size / 65536), so the smallest
    /// normalized value that lands exactly on <paramref name="offset"/> is ceil(offset * 65536 / size).
    /// </summary>
    internal static int NormalizeAbsolute(int offset, int size)
        => (int)Math.Min(AbsoluteCoordinateMax, Math.Ceiling(offset * (AbsoluteCoordinateRange / Math.Max(1, size))));

    /// <summary>
    /// The only coordinate limit is the virtual desktop's bounding box. A point inside it but in a gap between
    /// monitors of different sizes is moved to the nearest monitor edge (as Windows itself would move the pointer)
    /// and <see cref="LastInputNote"/> says so.
    /// </summary>
    private ScreenPoint ResolveOnScreen(ScreenPoint point)
    {
        lastInputNote = null;
        var screen = VirtualScreen();
        if (!screen.Contains(point))
            throw new DesktopAutomationException(
                $"The point {point} is outside the desktop, which spans {screen}.", FailureCategories.NotFound);
        if (MonitorFromPoint(new Point(point.X, point.Y), MonitorDefaultToNull) != 0) return point;
        var monitor = MonitorFromPoint(new Point(point.X, point.Y), MonitorDefaultToNearest);
        var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), DeviceName = "" };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return point;
        var bounds = info.Monitor.ToScreenRect();
        var clamped = new ScreenPoint(Math.Clamp(point.X, bounds.Left, bounds.Right - 1), Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1));
        lastInputNote = $"The point {point} falls in a gap between screens; used {clamped}, the nearest point on screen {info.DeviceName}.";
        return clamped;
    }

    private static nint RootWindowAt(ScreenPoint point)
    {
        var hwnd = WindowFromPoint(new Point(point.X, point.Y));
        return hwnd == 0 ? 0 : GetAncestor(hwnd, GaRoot);
    }

    private static Input MouseEvent(uint flags, int data = 0) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = unchecked((uint)data) } },
    };

    // ---- Keyboard --------------------------------------------------------------------------------------------

    public void TypeText(string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;
        using var dpi = new DpiScope();
        var target = GetForegroundWindow();
        DesktopState.EnsureInputCanReach(target, "typing");
        EnsureUserIdle();
        var batch = new List<Input>(TypingRunLength * 2);
        for (var i = 0; i < text.Length;)
        {
            // One SendInput call per run of up to TypingRunLength code units: the run reaches the input queue as one
            // uninterrupted sequence, with no per-character delay.
            batch.Clear();
            var units = 0;
            while (i < text.Length && units < TypingRunLength)
            {
                var c = text[i];
                switch (c)
                {
                    case '\r':
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++; // CRLF is one Enter
                        AddKeyTap(batch, KeyMap.VkReturn);
                        break;
                    case '\n':
                        AddKeyTap(batch, KeyMap.VkReturn);
                        break;
                    case '\t':
                        AddKeyTap(batch, KeyMap.VkTab);
                        break;
                    default:
                        batch.Add(UnicodeEvent(c, keyUp: false));
                        batch.Add(UnicodeEvent(c, keyUp: true));
                        // Never split a surrogate pair across runs: its low half joins this run even past the limit.
                        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            var low = text[++i];
                            batch.Add(UnicodeEvent(low, keyUp: false));
                            batch.Add(UnicodeEvent(low, keyUp: true));
                            units++;
                        }
                        break;
                }
                i++;
                units++;
            }
            Send(target, "typing", [.. batch]);
            if (i >= text.Length) break;
            // Between runs, re-check whatever now has the keyboard (a dialog opened, the user switched windows) so a
            // blocked target fails loudly instead of swallowing keystrokes.
            target = GetForegroundWindow();
            DesktopState.EnsureInputCanReach(target, "typing");
            // The person may have started typing while our previous run went out; stop before mixing keystrokes.
            EnsureUserIdle();
        }
    }

    private static void AddKeyTap(List<Input> batch, ushort vk)
    {
        var key = new VirtualKey(vk, false);
        batch.Add(KeyEvent(key, keyUp: false));
        batch.Add(KeyEvent(key, keyUp: true));
    }

    public void PressKeys(string chord)
    {
        ThrowIfDisposed();
        var names = KeyMap.Split(chord);
        var modifiers = new List<VirtualKey>();
        var keys = new List<VirtualKey>();
        char? unicodeOnly = null;
        foreach (var name in names)
        {
            var key = KeyMap.Resolve(name, modifiers);
            if (key is null)
            {
                // A character not on the current keyboard layout can still be typed, but not combined with modifiers.
                if (names.Count > 1)
                    throw KeyMap.NotUnderstood($"The key '{name}' is not on the current keyboard layout, so it cannot be combined with other keys.");
                unicodeOnly = name[0];
                continue;
            }
            if (KeyMap.IsModifier(key.Value.Code)) modifiers.Add(key.Value);
            else keys.Add(key.Value);
        }
        modifiers = modifiers.DistinctBy(m => m.Code).ToList();
        if (modifiers.Any(m => m.Code == KeyMap.VkControl) && modifiers.Any(m => m.Code == KeyMap.VkMenu) && keys.Any(k => k.Code == KeyMap.VkDelete))
            throw new DesktopAutomationException(
                "Ctrl+Alt+Delete is the secure attention sequence: Windows accepts it only from the physical keyboard (or a system service), never from synthesized input, and the screen it opens is a secure desktop no program can drive. A person must press it at the computer.",
                FailureCategories.AuthenticationRequired);

        using var dpi = new DpiScope();
        var target = GetForegroundWindow();
        var step = $"pressing {chord}";
        DesktopState.EnsureInputCanReach(target, step);
        EnsureUserIdle();
        if (unicodeOnly is { } u)
        {
            Send(target, step, UnicodeEvent(u, keyUp: false), UnicodeEvent(u, keyUp: true));
            return;
        }
        // A chord of only modifiers ("win", "ctrl") taps them; otherwise hold them around the keys.
        if (keys.Count == 0) { keys = modifiers; modifiers = []; }
        var held = new List<VirtualKey>();
        try
        {
            foreach (var modifier in modifiers)
            {
                Send(target, step, KeyEvent(modifier, keyUp: false));
                held.Add(modifier);
            }
            foreach (var key in keys)
            {
                Send(target, step, KeyEvent(key, keyUp: false));
                held.Add(key);
                Send(target, step, KeyEvent(key, keyUp: true));
                held.Remove(key);
            }
        }
        finally
        {
            // Release every key whose down event went out, in reverse, even after a failure, so nothing stays held.
            for (var i = held.Count - 1; i >= 0; i--)
                SendInput(1, [KeyEvent(held[i], keyUp: true)], InputSize);
        }
    }

    private static void SendKeyTap(nint target, string step, VirtualKey key)
        => Send(target, step, KeyEvent(key, keyUp: false), KeyEvent(key, keyUp: true));

    /// <summary>Virtual key plus its scan code (and E0 flag for navigation keys) so scan-code readers (games, terminals, RDP) see it.</summary>
    private static Input KeyEvent(VirtualKey key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key.Code,
                ScanCode = KeyMap.ScanCode(key.Code),
                Flags = (key.Extended ? KeyEventExtendedKey : 0) | (keyUp ? KeyEventKeyUp : 0),
            },
        },
    };

    private static Input UnicodeEvent(char unit, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = unit, Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0) } },
    };

    /// <summary>
    /// Alt down, an unassigned key, Alt up: makes this process the last input source (which Windows requires before
    /// it grants a foreground change) without opening the current window's menu bar.
    /// </summary>
    private static void SendNeutralAltTap()
    {
        var alt = new VirtualKey(KeyMap.VkMenu, false);
        var filler = new VirtualKey(KeyMap.VkUnassigned, false);
        Input[] inputs = [KeyEvent(alt, false), KeyEvent(filler, false), KeyEvent(filler, true), KeyEvent(alt, true)];
        SendInput((uint)inputs.Length, inputs, InputSize);
    }

    private static void Send(nint target, string step, params Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length) throw InputFailure(step, Marshal.GetLastPInvokeError(), target);
    }

    private static DesktopAutomationException InputFailure(string step, int win32Error, nint target)
    {
        if (DesktopState.IsAuthenticationSurfaceActive()) return DesktopState.AuthenticationRequired(step);
        var foreground = GetForegroundWindow();
        foreach (var hwnd in (nint[])[target, foreground])
            if (hwnd != 0 && ProcessIntegrity.IsWindowHigherThanCurrent(hwnd))
                return DesktopState.Elevated(hwnd, step);
        return new DesktopAutomationException($"Windows refused {step} (Win32 error {win32Error}).", FailureCategories.OsRefused);
    }
}
