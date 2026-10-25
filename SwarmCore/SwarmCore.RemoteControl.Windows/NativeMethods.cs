using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>Win32 declarations used by the Windows desktop automation. All coordinates are physical pixels.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";
    private const string Dwmapi = "dwmapi.dll";
    private const string Shcore = "shcore.dll";

    // DPI awareness
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    // Monitors
    public const uint MonitorDefaultToNull = 0;
    public const uint MonitorDefaultToNearest = 2;
    public const uint MonitorInfoFlagPrimary = 1;
    public const int MonitorDpiTypeEffective = 0;
    public const double BaselineDpi = 96.0;

    // System metrics
    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    // Window state / ownership
    public const int SwShowMinNoActive = 7;
    public const int SwShowNoActivate = 4;
    public const int SwRestore = 9;
    public const uint GwOwner = 4;
    public const uint GaRoot = 2;
    public const uint AsfwAny = unchecked((uint)-1);

    // DWM
    public const int DwmwaExtendedFrameBounds = 9;
    public const int DwmwaCloaked = 14;

    // Capture
    public const uint PwRenderFullContent = 2;
    public const uint RopSrcCopy = 0x00CC0020;
    public const uint RopCaptureBlt = 0x40000000;

    // Input
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
    public const uint MouseEventMove = 0x0001;
    public const uint MouseEventLeftDown = 0x0002;
    public const uint MouseEventLeftUp = 0x0004;
    public const uint MouseEventRightDown = 0x0008;
    public const uint MouseEventRightUp = 0x0010;
    public const uint MouseEventMiddleDown = 0x0020;
    public const uint MouseEventMiddleUp = 0x0040;
    public const uint MouseEventWheel = 0x0800;
    public const uint MouseEventHWheel = 0x1000;
    public const uint MouseEventVirtualDesk = 0x4000;
    public const uint MouseEventAbsolute = 0x8000;
    public const int WheelDelta = 120;
    public const int AbsoluteCoordinateMax = 65535;
    public const double AbsoluteCoordinateRange = 65536.0;
    public const uint KeyEventExtendedKey = 0x0001;
    public const uint KeyEventKeyUp = 0x0002;
    public const uint KeyEventUnicode = 0x0004;
    public const uint MapVkVkToVsc = 0;

    // Process / token
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const int TokenInformationElevation = 20;
    public const int TokenInformationIntegrityLevel = 25;
    public const int ErrorAccessDenied = 5;

    // Desktops
    public const uint DesktopReadObjects = 0x0001;
    public const int UoiName = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly ScreenRect ToScreenRect() => ScreenRect.FromEdges(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
        public Point(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    public delegate bool EnumWindowsProc(nint hwnd, nint state);
    public delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint state);

    [DllImport(User32, SetLastError = true)] public static extern bool EnumWindows(EnumWindowsProc callback, nint state);
    [DllImport(User32, SetLastError = true)] public static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint state);
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
    [DllImport(User32)] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport(User32)] public static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport(User32)] public static extern bool IsWindow(nint hwnd);
    [DllImport(User32)] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport(User32)] public static extern bool IsIconic(nint hwnd);
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int maxCount);
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetClassNameW(nint hwnd, [Out] char[] className, int maxCount);
    [DllImport(User32, SetLastError = true)] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport(User32)] public static extern nint GetForegroundWindow();
    [DllImport(User32, SetLastError = true)] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport(User32)] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport(User32)] public static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport(User32, SetLastError = true)] public static extern bool BringWindowToTop(nint hwnd);
    [DllImport(User32)] public static extern void SwitchToThisWindow(nint hwnd, bool altTab);
    [DllImport(User32, SetLastError = true)] public static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport(User32, SetLastError = true)] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport(User32)] public static extern nint GetWindow(nint hwnd, uint command);
    [DllImport(User32)] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport(User32)] public static extern nint WindowFromPoint(Point point);
    [DllImport(User32, SetLastError = true)] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport(User32, SetLastError = true)] public static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport(User32)] public static extern nint GetDC(nint hwnd);
    [DllImport(User32)] public static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport(User32)] public static extern int GetSystemMetrics(int index);
    [DllImport(User32, EntryPoint = "SetCursorPos", SetLastError = true)] private static extern bool SetCursorPosNative(int x, int y);

    /// <summary>SetCursorPos, recorded as our own input so it is not mistaken for the person's.</summary>
    public static bool SetCursorPos(int x, int y)
    {
        UserInputMonitor.NoteWarp(x, y);
        UserActivity.BeginInjection();
        try { return SetCursorPosNative(x, y); }
        finally { UserActivity.EndInjection(); }
    }
    [DllImport(User32, SetLastError = true)] public static extern bool GetCursorPos(out Point point);
    [DllImport(User32, EntryPoint = "SendInput", SetLastError = true)] private static extern uint SendInputNative(uint count, Input[] inputs, int size);

    /// <summary>SendInput, recorded as our own input so it is not mistaken for the person's.</summary>
    public static uint SendInput(uint count, Input[] inputs, int size)
    {
        UserActivity.BeginInjection();
        try { return SendInputNative(count, inputs, size); }
        finally { UserActivity.EndInjection(); }
    }
    [DllImport(User32)] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport(User32, CharSet = CharSet.Unicode)] public static extern short VkKeyScanW(char character);
    [DllImport(User32)] public static extern uint GetDoubleClickTime();
    [DllImport(User32)] public static extern bool IsHungAppWindow(nint hwnd);
    [DllImport(User32, SetLastError = true)] public static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport(User32, SetLastError = true)] public static extern nint GetThreadDesktop(uint threadId);
    [DllImport(User32, SetLastError = true)] public static extern bool CloseDesktop(nint desktop);
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetUserObjectInformationW(nint handle, int index, [Out] char[]? buffer, int length, out int needed);
    [DllImport(User32, SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport(User32)] public static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern bool BitBlt(nint destDc, int x, int y, int width, int height, nint sourceDc, int sourceX, int sourceY, uint rop);

    [DllImport(Dwmapi)] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport(Dwmapi)] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport(Shcore)] public static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport(Kernel32)] public static extern uint GetCurrentThreadId();
    [DllImport(Kernel32)] public static extern nint GetCurrentProcess();
    [DllImport(Kernel32, SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport(Kernel32, SetLastError = true)] public static extern bool CloseHandle(nint handle);

    [DllImport(Advapi32, SetLastError = true)] public static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport(Advapi32, SetLastError = true)] public static extern bool GetTokenInformation(nint token, int infoClass, nint buffer, int length, out int returnLength);
    [DllImport(Advapi32)] public static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport(Advapi32)] public static extern nint GetSidSubAuthority(nint sid, uint index);

    /// <summary>Sets per-monitor-v2 awareness on the calling thread for the scope, so every API in it speaks physical pixels.</summary>
    public readonly struct DpiScope : IDisposable
    {
        private readonly nint previous;
        public DpiScope() => previous = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
}
