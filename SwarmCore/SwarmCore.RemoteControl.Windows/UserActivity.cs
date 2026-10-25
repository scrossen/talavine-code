using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>
/// Tells the person's own keyboard and mouse input apart from input this process injected. Windows reports only the
/// time of the last input of any origin (GetLastInputInfo), so every injection records when it happened and a last
/// input that falls inside an injection window (plus a small tolerance) is attributed to us. Process-wide, because
/// every instance in the process injects into the same session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserActivity
{
    /// <summary>
    /// Slack before an injection starts: GetLastInputInfo and Environment.TickCount64 read the same millisecond counter,
    /// but it advances in ~15.6 ms steps, so a stamp can read up to one step earlier than our start reading.
    /// </summary>
    private const long PreToleranceMilliseconds = 50;
    /// <summary>
    /// Slack after an injection returns: SendInput only queues events; the raw input thread stamps them when it
    /// processes them, after any low-level keyboard/mouse hooks in other processes have run, which can be later.
    /// </summary>
    private const long PostToleranceMilliseconds = 100;
    /// <summary>Recent injection windows kept for attribution (a burst of separate SendInput calls, then a delayed stamp).</summary>
    private const int WindowHistory = 64;

    private static readonly Lock Gate = new();
    private static readonly Queue<(long Start, long End)> Windows = new();
    private static long injectionStart = long.MinValue;
    private static long lastUserInput = long.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Call immediately before an injection.</summary>
    public static void BeginInjection()
    {
        lock (Gate)
        {
            // Anything before this moment that we have not seen yet was the person's; capture it before our input hides it.
            Observe();
            injectionStart = Environment.TickCount64;
        }
    }

    /// <summary>Call immediately after an injection.</summary>
    public static void EndInjection()
    {
        lock (Gate)
        {
            Windows.Enqueue((injectionStart, Environment.TickCount64));
            while (Windows.Count > WindowHistory) Windows.Dequeue();
        }
    }

    /// <summary>
    /// True when the stamp falls inside one of our recent injection windows (or the one in progress), widened by
    /// the tolerances above.
    /// </summary>
    private static bool IsOurs(long stamp, long now)
    {
        if (injectionStart != long.MinValue && stamp >= injectionStart - PreToleranceMilliseconds && stamp <= now + PostToleranceMilliseconds
            && (Windows.Count == 0 || Windows.Last().Start != injectionStart))
            return true; // an injection is in progress right now
        foreach (var (start, end) in Windows)
            if (stamp >= start - PreToleranceMilliseconds && stamp <= end + PostToleranceMilliseconds) return true;
        return false;
    }

    /// <summary>Time since the person's own last keyboard or mouse input; null when Windows cannot say.</summary>
    public static TimeSpan? UserIdleTime()
    {
        lock (Gate)
        {
            if (!Observe()) return null;
            return TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - lastUserInput));
        }
    }

    /// <summary>Reads the last input time and records it as the person's unless it falls inside our injection window.</summary>
    private static bool Observe()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return false;
        var now = Environment.TickCount64;
        // GetLastInputInfo uses the same millisecond counter, truncated to 32 bits; unsigned subtraction handles wrap.
        var age = unchecked((uint)now - info.Time);
        var lastInput = now - age;
        var ours = IsOurs(lastInput, now);
        if (!ours && lastInput > lastUserInput) lastUserInput = lastInput;
        if (lastUserInput == long.MinValue) lastUserInput = lastInput; // first look ever, and it was ours: best bound we have
        return true;
    }
}
