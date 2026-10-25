using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>
/// Watches the person's own keyboard and mouse input with low-level hooks (WH_KEYBOARD_LL, WH_MOUSE_LL) on a dedicated
/// message-loop thread. Windows marks every synthesized event with LLKHF_INJECTED / LLMHF_INJECTED, so unlike
/// GetLastInputInfo this tells physical input from ours with certainty, however late the event is processed.
/// Process-wide (the hooks see the whole session); reference-counted by the automation instances using it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserInputMonitor
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint WmQuit = 0x0012;
    private const nint WmMouseMove = 0x0200;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;
    /// <summary>A non-injected pointer move counts as the person's only beyond this distance (filters our own warps).</summary>
    private const int MoveThresholdPixels = 2;
    private const int StartTimeoutMilliseconds = 2000;
    // KBDLLHOOKSTRUCT { DWORD vkCode; DWORD scanCode; DWORD flags; ... }
    private const int KeyboardFlagsOffset = 8;
    // MSLLHOOKSTRUCT { POINT pt; DWORD mouseData; DWORD flags; ... }
    private const int MouseFlagsOffset = 12;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int idHook, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(nint name);

    // Kept in static fields so the delegates outlive the hooks.
    private static readonly HookProc KeyboardProc = OnKeyboard;
    private static readonly HookProc MouseProc = OnMouse;
    private static readonly Lock Gate = new();
    private static Thread? thread;
    private static uint threadId;
    private static int users;
    private static volatile bool running;
    private static long lastUserInput;
    // Last pointer position attributed to anyone (person or one of our warps), packed x/y.
    private static long lastPointer = long.MinValue;

    public static bool IsRunning => running;

    /// <summary>Time since the person's last own input; null when the hooks are not running.</summary>
    public static TimeSpan? UserIdleTime()
        => running ? TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref lastUserInput))) : null;

    /// <summary>Called for every SetCursorPos we make, so the resulting non-injected move is not mistaken for the person's.</summary>
    public static void NoteWarp(int x, int y) => Interlocked.Exchange(ref lastPointer, Pack(x, y));

    /// <summary>Starts the hooks if this is the first user. Returns false when Windows refused the hooks.</summary>
    public static bool Acquire()
    {
        lock (Gate)
        {
            if (running) { users++; return true; }
            // Baseline: the best attribution available before the hooks existed.
            var baseline = UserActivity.UserIdleTime() ?? TimeSpan.Zero;
            Interlocked.Exchange(ref lastUserInput, Environment.TickCount64 - (long)baseline.TotalMilliseconds);
            using var started = new ManualResetEventSlim();
            var ok = false;
            var worker = new Thread(() => Run(started, success => ok = success))
            {
                IsBackground = true,
                Name = "SwarmCore.RemoteControl input monitor",
            };
            worker.Start();
            started.Wait(StartTimeoutMilliseconds);
            if (!ok) return false;
            thread = worker;
            running = true;
            users++;
            return true;
        }
    }

    /// <summary>Stops the hooks when the last user releases them.</summary>
    public static void Release()
    {
        Thread? stopping;
        lock (Gate)
        {
            if (!running || --users > 0) return;
            running = false;
            stopping = thread;
            thread = null;
            PostThreadMessageW(threadId, WmQuit, 0, 0);
        }
        stopping?.Join(StartTimeoutMilliseconds);
    }

    private static void Run(ManualResetEventSlim started, Action<bool> report)
    {
        threadId = NativeMethods.GetCurrentThreadId();
        var module = GetModuleHandleW(0);
        var keyboard = SetWindowsHookExW(WhKeyboardLl, KeyboardProc, module, 0);
        var mouse = SetWindowsHookExW(WhMouseLl, MouseProc, module, 0);
        if (keyboard == 0 || mouse == 0)
        {
            if (keyboard != 0) UnhookWindowsHookEx(keyboard);
            if (mouse != 0) UnhookWindowsHookEx(mouse);
            report(false);
            started.Set();
            return;
        }
        report(true);
        started.Set();
        try
        {
            // Low-level hooks are delivered through this thread's message queue; pump until WM_QUIT (0) or error (-1).
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
        }
        finally
        {
            UnhookWindowsHookEx(keyboard);
            UnhookWindowsHookEx(mouse);
        }
    }

    // The callbacks run for every input event in the session: constant work, no allocation, never throw.
    private static nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == HcAction && ((uint)Marshal.ReadInt32(lParam, KeyboardFlagsOffset) & LlkhfInjected) == 0)
                Interlocked.Exchange(ref lastUserInput, Environment.TickCount64);
        }
        catch { /* never let an exception cross into the input pipeline */ }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static nint OnMouse(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == HcAction && ((uint)Marshal.ReadInt32(lParam, MouseFlagsOffset) & LlmhfInjected) == 0)
            {
                if (wParam != WmMouseMove)
                {
                    Interlocked.Exchange(ref lastUserInput, Environment.TickCount64); // a button or wheel
                }
                else
                {
                    var x = Marshal.ReadInt32(lParam);
                    var y = Marshal.ReadInt32(lParam, sizeof(int));
                    var previous = Interlocked.Read(ref lastPointer);
                    if (previous == long.MinValue || Math.Abs(x - (int)(previous >> 32)) > MoveThresholdPixels || Math.Abs(y - (int)previous) > MoveThresholdPixels)
                    {
                        if (previous != long.MinValue) Interlocked.Exchange(ref lastUserInput, Environment.TickCount64);
                        Interlocked.Exchange(ref lastPointer, Pack(x, y));
                    }
                }
            }
        }
        catch { /* never let an exception cross into the input pipeline */ }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;
}
