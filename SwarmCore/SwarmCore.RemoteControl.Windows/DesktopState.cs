using System.Diagnostics;
using System.Runtime.Versioning;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>
/// Checks the operating-system conditions that block automation, so failures carry an accurate
/// <see cref="FailureCategories"/> value and a message that says what blocked the step and what unblocks it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DesktopState
{
    private const string ElevateAdvice = "run the remote control elevated (start it with --elevate, or as administrator)";
    private static readonly string[] AuthenticationProcesses = ["consent", "LogonUI", "CredentialUIBroker"];

    /// <summary>True when the window's thread has stopped pumping messages (Windows shows "Not Responding").</summary>
    public static bool IsHung(nint hwnd) => hwnd != 0 && IsHungAppWindow(hwnd);

    /// <summary>The name of the desktop receiving input, or null when we may not even open it (the secure desktop).</summary>
    public static string? InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0) return null;
        try { return ObjectName(desktop); }
        finally { CloseDesktop(desktop); }
    }

    /// <summary>The desktop this process's threads belong to (normally "Default").</summary>
    public static string? OwnDesktopName() => ObjectName(GetThreadDesktop(GetCurrentThreadId()));

    private static string? ObjectName(nint handle)
    {
        if (handle == 0) return null;
        GetUserObjectInformationW(handle, UoiName, null, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = new char[needed / sizeof(char) + 1];
        if (!GetUserObjectInformationW(handle, UoiName, buffer, buffer.Length * sizeof(char), out _)) return null;
        return new string(buffer).TrimEnd('\0');
    }

    /// <summary>
    /// True when a system authentication surface owns the display: input has moved to another desktop (UAC secure
    /// desktop, lock screen, Ctrl+Alt+Del), or there is no foreground window while a consent, logon or credential
    /// broker process runs.
    /// </summary>
    public static bool IsAuthenticationSurfaceActive()
    {
        var input = InputDesktopName();
        var own = OwnDesktopName();
        if (input is null || own is not null && !string.Equals(input, own, StringComparison.OrdinalIgnoreCase)) return true;
        if (GetForegroundWindow() != 0) return false;
        foreach (var name in AuthenticationProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) return true;
        }
        return false;
    }

    public static DesktopAutomationException AuthenticationRequired(string step) => new(
        $"Windows is showing a secure authentication screen (a UAC prompt, the lock screen, Ctrl+Alt+Del or a credential dialog), so {step} was blocked; no program can see or drive that screen. A person must complete or dismiss it at the computer.",
        FailureCategories.AuthenticationRequired);

    public static DesktopAutomationException Elevated(nint hwnd, string step, Exception? inner = null)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = WindowsDesktopAutomation.ProcessName((int)pid);
        var message = WindowsDesktopAutomation.IsProcessElevated
            ? $"Windows blocks {step} because '{process}' (window {(long)hwnd}) runs at system integrity, above even this elevated program (User Interface Privilege Isolation); no desktop program can drive it."
            : $"Windows blocks {step} because '{process}' (window {(long)hwnd}) runs at a higher integrity level (as administrator or as a system process) than this program (User Interface Privilege Isolation). To control an administrator window, {ElevateAdvice}.";
        return inner is null ? new(message, FailureCategories.TargetElevated) : new(message, inner, FailureCategories.TargetElevated);
    }

    public static DesktopAutomationException Busy(nint hwnd, string step, Exception? inner = null)
    {
        var message = $"'{WindowsDesktopAutomation.WindowTitle(hwnd)}' (window {(long)hwnd}) is not responding, so {step} could not be done. Wait for it to recover and try again.";
        return inner is null ? new(message, FailureCategories.AppBusy) : new(message, inner, FailureCategories.AppBusy);
    }

    /// <summary>
    /// Throws before input is sent when the OS would block or silently drop it: SendInput does not report UIPI
    /// blocking, so an elevated target is checked up front.
    /// </summary>
    public static void EnsureInputCanReach(nint target, string step)
    {
        if (IsAuthenticationSurfaceActive()) throw AuthenticationRequired(step);
        if (target == 0) return;
        if (ProcessIntegrity.IsWindowHigherThanCurrent(target)) throw Elevated(target, step);
        if (IsHung(target)) throw Busy(target, step);
    }
}
