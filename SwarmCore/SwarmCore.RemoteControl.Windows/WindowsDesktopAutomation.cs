using System.Diagnostics;
using System.Runtime.Versioning;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>
/// Windows implementation of <see cref="IDesktopAutomation"/>: Win32 for displays, windows, capture and input,
/// UI Automation 3 (through FlaUI's automation object and the raw UIA COM interfaces) for control trees.
/// It imposes no limits of its own: every window, control and point on the virtual desktop is reachable, and only
/// the operating system restricts it (UIPI blocks a non-elevated process from driving elevated windows; nothing
/// reaches the secure desktop). Safe for one caller at a time plus <see cref="Dispose"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDesktopAutomation : IDesktopAutomation, IDisposable
{
    private const int FocusPollMilliseconds = 300;
    private const int FocusPollIntervalMilliseconds = 10;
    private const int MaxClassNameLength = 256;
    private const string UnknownProcessName = "unknown";

    static WindowsDesktopAutomation()
    {
        // Physical pixels everywhere. Fails harmlessly when the host manifest or runtime already set awareness;
        // every entry point also sets per-monitor-v2 on its thread (DpiScope) so mixed hosts still agree.
        if (OperatingSystem.IsWindows()) SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
    }

    public WindowsDesktopAutomation()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsDesktopAutomation runs only on Windows.");
    }

    /// <summary>
    /// How recently the person may have used the keyboard or mouse before an input action is held back with
    /// <see cref="FailureCategories.UserActive"/> (so their typing cannot land in a field we just focused, or ours in
    /// theirs). Applies to FocusWindow, Click, Scroll, TypeText, PressKeys and restoring a minimized window for capture.
    /// Default 700 ms; <see cref="TimeSpan.Zero"/> disables the guard.
    /// </summary>
    public TimeSpan UserActivityGuard { get; set; } = TimeSpan.FromMilliseconds(700);

    private bool inputMonitorAcquired;

    /// <summary>
    /// Time since the person's own last keyboard or mouse input. Uses the low-level hook monitor (certain: Windows
    /// flags injected events) when it runs, else the GetLastInputInfo attribution of <see cref="UserActivity"/>.
    /// </summary>
    public TimeSpan? GetUserIdleTime()
    {
        ThrowIfDisposed();
        EnsureInputMonitor();
        return UserInputMonitor.UserIdleTime() ?? UserActivity.UserIdleTime();
    }

    /// <summary>Starts the hook monitor on first use (once per instance); released in Dispose.</summary>
    private void EnsureInputMonitor()
    {
        lock (sync)
        {
            if (inputMonitorAcquired || disposed) return;
            inputMonitorAcquired = UserInputMonitor.Acquire();
        }
    }

    /// <summary>Throws user-active when the person touched the keyboard or mouse within <see cref="UserActivityGuard"/>.</summary>
    private void EnsureUserIdle()
    {
        var guard = UserActivityGuard;
        if (guard <= TimeSpan.Zero) return;
        EnsureInputMonitor();
        if ((UserInputMonitor.UserIdleTime() ?? UserActivity.UserIdleTime()) is { } idle && idle < guard)
            throw new DesktopAutomationException(
                $"You were using the keyboard or mouse {(long)idle.TotalMilliseconds} ms ago; the action was not sent. Retry when idle.",
                FailureCategories.UserActive);
    }

    /// <summary>True when this process runs elevated (as administrator), which lets it drive elevated windows too.</summary>
    public static bool IsProcessElevated => ProcessIntegrity.IsCurrentProcessElevated;

    // ---- Displays --------------------------------------------------------------------------------------------

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        return EnumerateDisplays().Select(d => d.Info).ToList();
    }

    private static List<(nint Monitor, DisplayInfo Info)> EnumerateDisplays()
    {
        var raw = new List<(nint Monitor, string Device, bool Primary, ScreenRect Bounds, ScreenRect Work, double Scale)>();
        MonitorEnumProc callback = (nint monitor, nint _, ref Rect _, nint _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>(), DeviceName = "" };
            if (!GetMonitorInfoW(monitor, ref info)) return true;
            var scale = GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / BaselineDpi : 1.0;
            raw.Add((monitor, info.DeviceName, (info.Flags & MonitorInfoFlagPrimary) != 0,
                info.Monitor.ToScreenRect(), info.Work.ToScreenRect(), scale));
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return raw
            .OrderByDescending(d => d.Primary)
            .ThenBy(d => d.Bounds.Left)
            .ThenBy(d => d.Bounds.Top)
            .Select((d, i) => (d.Monitor, new DisplayInfo(i + 1, d.Device, d.Primary, d.Bounds, d.Work, d.Scale)))
            .ToList();
    }

    private static ScreenRect VirtualScreen() => new(
        GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    // ---- Windows ---------------------------------------------------------------------------------------------

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        var displays = EnumerateDisplays();
        var foreground = GetForegroundWindow();
        var handles = new List<nint>();
        EnumWindowsProc callback = (hwnd, _) => { handles.Add(hwnd); return true; };
        EnumWindows(callback, 0);
        GC.KeepAlive(callback);

        var processNames = new Dictionary<int, string>();
        var elevation = new Dictionary<int, bool>();
        var result = new List<WindowInfo>();
        foreach (var hwnd in handles)
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) continue;
            var title = WindowTitle(hwnd);
            if (title.Length == 0) continue;
            var bounds = WindowBounds(hwnd);
            if (bounds.IsEmpty) continue;
            result.Add(Describe(hwnd, title, bounds, foreground, displays, processNames, elevation));
        }
        // Foreground first, then z-order (EnumWindows already enumerates top to bottom).
        return result.OrderByDescending(w => w.IsForeground).ToList();
    }

    public WindowInfo? GetWindow(long handle)
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        var hwnd = (nint)handle;
        if (hwnd == 0 || !IsWindow(hwnd)) return null;
        return Describe(hwnd, WindowTitle(hwnd), WindowBounds(hwnd), GetForegroundWindow(), EnumerateDisplays(), [], []);
    }

    private static WindowInfo Describe(nint hwnd, string title, ScreenRect bounds, nint foreground,
        List<(nint Monitor, DisplayInfo Info)> displays, Dictionary<int, string> processNames, Dictionary<int, bool> elevation)
    {
        GetWindowThreadProcessId(hwnd, out var rawPid);
        var pid = (int)rawPid;
        if (!processNames.TryGetValue(pid, out var processName)) processNames[pid] = processName = ProcessName(pid);
        if (!elevation.TryGetValue(pid, out var elevated)) elevation[pid] = elevated = ProcessIntegrity.IsHigherThanCurrent(pid);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        int? displayIndex = null;
        foreach (var (m, info) in displays)
            if (m == monitor) { displayIndex = info.Index; break; }
        return new WindowInfo(hwnd, title, processName, pid, bounds, IsIconic(hwnd), hwnd == foreground, displayIndex)
        {
            ClassName = ClassName(hwnd),
            IsElevated = elevated,
        };
    }

    internal static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return UnknownProcessName;
        }
    }

    internal static string WindowTitle(nint hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        var copied = GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, copied));
    }

    private static string? ClassName(nint hwnd)
    {
        var buffer = new char[MaxClassNameLength];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private static bool IsCloaked(nint hwnd)
        => DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The visible frame (DWM extended frame bounds, without the invisible resize borders), else GetWindowRect.</summary>
    internal static ScreenRect WindowBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect frame, System.Runtime.InteropServices.Marshal.SizeOf<Rect>()) == 0)
        {
            var rect = frame.ToScreenRect();
            if (!rect.IsEmpty) return rect;
        }
        return GetWindowRect(hwnd, out var windowRect) ? windowRect.ToScreenRect() : default;
    }

    private static void RequireWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd))
            throw new DesktopAutomationException($"Window {(long)hwnd} is no longer open; list the windows again.", FailureCategories.WindowGone);
    }

    // ---- Focus -----------------------------------------------------------------------------------------------

    public bool FocusWindow(long handle)
    {
        ThrowIfDisposed();
        using var dpi = new DpiScope();
        var hwnd = (nint)handle;
        RequireWindow(hwnd);
        if (IsForegroundOrOwned(hwnd) && !IsIconic(hwnd)) return true;
        EnsureUserIdle();
        if (DesktopState.IsAuthenticationSurfaceActive()) throw DesktopState.AuthenticationRequired("bringing the window to the front");
        if (DesktopState.IsHung(hwnd)) throw DesktopState.Busy(hwnd, "bringing it to the front");
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        if (SetForegroundWindow(hwnd) && WaitForForeground(hwnd)) return true;
        if (TryForceForeground(hwnd)) return true;

        RequireWindow(hwnd);
        if (DesktopState.IsAuthenticationSurfaceActive()) throw DesktopState.AuthenticationRequired("bringing the window to the front");
        if (ProcessIntegrity.IsWindowHigherThanCurrent(hwnd)) throw DesktopState.Elevated(hwnd, "bringing the window to the front");
        if (DesktopState.IsHung(hwnd)) throw DesktopState.Busy(hwnd, "bringing it to the front");
        return false; // the foreground lock refused us (for example the user is typing in another window)
    }

    private static bool TryForceForeground(nint hwnd)
    {
        // Windows grants foreground changes only to the process that received the last input (or the current
        // foreground process). Make ourselves the last input source, lift the lock for everyone, and share the
        // foreground thread's input state while asking again.
        SendNeutralAltTap();
        AllowSetForegroundWindow(AsfwAny);
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (WaitForForeground(hwnd)) return true;
            SwitchToThisWindow(hwnd, true);
            return WaitForForeground(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static bool WaitForForeground(nint hwnd)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (IsForegroundOrOwned(hwnd)) return true;
            if (deadline.ElapsedMilliseconds >= FocusPollMilliseconds) return false;
            Thread.Sleep(FocusPollIntervalMilliseconds); // condition poll: activation completes asynchronously in the target's thread
        }
    }

    /// <summary>
    /// The foreground is the window itself or a window it owns, directly or through a chain (a modal dialog on
    /// its thread). Another unrelated window of the same process or thread does not count, so we never report
    /// success while a different window holds the keyboard.
    /// </summary>
    private static bool IsForegroundOrOwned(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        if (foreground == hwnd) return true;
        for (var owner = NativeMethods.GetWindow(foreground, GwOwner); owner != 0; owner = NativeMethods.GetWindow(owner, GwOwner))
            if (owner == hwnd) return true;
        return false;
    }
}
