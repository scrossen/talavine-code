using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Computer;

namespace SwarmCore.Coding.Computer;

public sealed record DesktopWindowInfo(string Id, string Title, uint ProcessId, int Width, int Height);

/// <summary>Windows visual input targeted to an observed top-level window, using ordinary user privileges.</summary>
public sealed class WindowsComputerSession(IOptions<CodingComputerOptions> options) : IComputerSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private ComputerObservation? current;
    private nint window;
    private uint processId;
    private Rect bounds;

    private void RequireAvailable()
    {
        if (!options.Value.Enabled) throw new ComputerOperationException("Computer access is disabled in Settings.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop control is available on Windows.");
    }

    public Task<IReadOnlyList<DesktopWindowInfo>> ListAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DesktopWindowInfo>>(() =>
        {
            RequireAvailable();
            using var dpi = new DpiScope();
            var result = new List<DesktopWindowInfo>();
            EnumWindows((handle, _) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0 || !GetWindowRect(handle, out var rect)) return true;
                GetWindowThreadProcessId(handle, out var pid);
                result.Add(new(handle.ToInt64().ToString(), Title(handle), pid, rect.Right - rect.Left, rect.Bottom - rect.Top));
                return true;
            }, 0);
            return result;
        }, ct);

    public async Task<ComputerObservation> ObserveAsync(string? url = null, CancellationToken ct = default)
    {
        RequireAvailable();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (url is not null)
            {
                if (!long.TryParse(url, out var id) || !(await ListAsync(ct).ConfigureAwait(false)).Any(w => w.Id == url))
                    throw new ArgumentException("Select a window ID returned by computer.windows.");
                window = (nint)id;
                GetWindowThreadProcessId(window, out processId);
            }
            if (window == 0) throw new ArgumentException("Select a window ID returned by computer.windows.");
            return await CaptureAsync(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<ComputerActionResult> ExecuteBatchAsync(ComputerActionBatch batch, CancellationToken ct = default)
    {
        RequireAvailable();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var completed = 0;
        var dispatched = false;
        try
        {
            if (current is null || batch.SessionId != sessionId || batch.SurfaceId != window.ToInt64().ToString()
                || batch.ObservationId != current.ObservationId)
                return Failure("stale-or-missing-observation");
            if (batch.Actions.Count is < 1 or > 64) return Failure("invalid-action-batch");
            foreach (var action in batch.Actions) ValidateAction(action, current.Width, current.Height);
            if (!SameWindow() || !PhysicalBounds(window, out var now) || !now.Equals(bounds)) return Failure("window-changed; observe again");
            if (GetForegroundWindow() != window)
            {
                SetForegroundWindow(window);
                if (GetForegroundWindow() != window)
                    return Failure("Windows did not let Talavine Code bring this window to the front, so no input was sent. Ask the person to click the window once, then observe again.");
            }
            current = null; // Observations are consumed even when input/refresh fails; uncertain input must never replay.
            foreach (var action in batch.Actions)
            {
                ct.ThrowIfCancellationRequested();
                if (!SameWindow() || GetForegroundWindow() != window || !PhysicalBounds(window, out var actionBounds) || !actionBounds.Equals(bounds))
                    return Failure("window-focus-or-bounds-changed; observe again", dispatched);
                dispatched = true;
                await Task.Run(() => Execute(action, ct), ct).ConfigureAwait(false);
                completed++;
            }
            await Task.Delay(100, ct).ConfigureAwait(false); // Let the target dispatch queued input before reading its pixels.
            return new(true, completed, await CaptureAsync(ct).ConfigureAwait(false)) { Mode = "desktop", BeforeObservationId = batch.ObservationId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            current = null;
            return Failure(ex.Message, dispatched);
        }
        finally { gate.Release(); }
        ComputerActionResult Failure(string error, bool uncertain = false) => new(false, completed, null, error)
        { Mode = "desktop", BeforeObservationId = batch.ObservationId, OutcomeUncertain = uncertain };
    }

    public static void ValidateAction(ComputerAction a, int width, int height)
    {
        if (a.Type is not ("click" or "double_click" or "move" or "drag" or "scroll" or "keypress" or "type" or "wait" or "screenshot"))
            throw new ArgumentException("Unsupported desktop action.");
        if (a.Type is "click" or "double_click" or "move" or "scroll") Check(a.X, a.Y);
        if (a.Button is not (null or "left" or "right" or "middle")) throw new ArgumentException("Unsupported mouse button.");
        if (a.Text?.Length > 16_000 || a.Keys.Count > 16 || a.WaitMilliseconds is < 0 or > 15_000
            || !double.IsFinite(a.ScrollX) || !double.IsFinite(a.ScrollY) || Math.Abs(a.ScrollX) > 10_000 || Math.Abs(a.ScrollY) > 10_000)
            throw new ArgumentException("Invalid desktop action parameters.");
        if (a.Type == "keypress" && a.Keys.Count == 0) throw new ArgumentException("Keys required.");
        foreach (var key in a.Keys) _ = Key(key);
        if (a.Type == "drag" && a.Path.Count < 2 || a.Path.Count > 256) throw new ArgumentException("Drag requires 2 to 256 points.");
        foreach (var p in a.Path) Check(p.X, p.Y);
        void Check(double? x, double? y)
        {
            if (x is null || y is null || !double.IsFinite(x.Value) || !double.IsFinite(y.Value)
                || x < 0 || y < 0 || x >= width || y >= height) throw new ArgumentException("Coordinate outside observed window.");
        }
    }

    private bool SameWindow() => IsWindow(window) && GetWindowThreadProcessId(window, out var pid) != 0 && pid == processId;
    private async Task<ComputerObservation> CaptureAsync(CancellationToken ct)
    {
        current = null;
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var dpi = new DpiScope();
            if (!SameWindow()) throw new ComputerOperationException("Window closed or process identity changed.");
            // Restore a minimized window so it has pixels, but do not activate it: PrintWindow with
            // PW_RENDERFULLCONTENT captures a background window, so looking never takes focus from the person
            // (and Windows refuses activation to a background process anyway). Input needs focus;
            // ExecuteBatchAsync activates and checks again before sending any.
            if (IsIconic(window)) ShowWindow(window, 9);
            if (!GetWindowRect(window, out bounds)) throw new ComputerOperationException("Could not read window bounds.");
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new ComputerOperationException("Invalid window size.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var png = CapturePng(window, width, height);
            return current = new(sessionId, window.ToInt64().ToString(), Guid.NewGuid().ToString("N"), "", DateTimeOffset.UtcNow, width, height, 1)
            { Title = Title(window), Trust = "untrusted-desktop-evidence", PngBase64 = Convert.ToBase64String(png) };
        }, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CapturePng(nint handle, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(handle, dc, 2)) throw new ComputerOperationException("Window screenshot failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private void Execute(ComputerAction a, CancellationToken ct)
    {
        using var dpi = new DpiScope();
        void Move(double x, double y) { RequireInputTarget(); if (!SetCursorPos(bounds.Left + (int)x, bounds.Top + (int)y)) throw new ComputerOperationException("Pointer move failed."); }
        var (down, up) = a.Button switch { "right" => (8u, 16u), "middle" => (32u, 64u), _ => (2u, 4u) };
        switch (a.Type)
        {
            case "click": case "double_click":
                Move(a.X!.Value, a.Y!.Value);
                Mouse(down); Mouse(up, release: true);
                if (a.Type == "double_click") { Mouse(down); Mouse(up, release: true); }
                break;
            case "move": Move(a.X!.Value, a.Y!.Value); break;
            case "scroll":
                Move(a.X!.Value, a.Y!.Value);
                if (a.ScrollY != 0) Mouse(0x800, -(int)a.ScrollY);
                if (a.ScrollX != 0) Mouse(0x1000, (int)a.ScrollX);
                break;
            case "drag":
                Move(a.Path[0].X, a.Path[0].Y); Mouse(down);
                try { foreach (var p in a.Path.Skip(1)) { ct.ThrowIfCancellationRequested(); Move(p.X, p.Y); } }
                finally { Mouse(up, release: true); }
                break;
            case "type":
                foreach (var c in a.Text ?? "") { ct.ThrowIfCancellationRequested(); Keyboard(0, c, 4); Keyboard(0, c, 6); }
                break;
            case "keypress":
                var keys = a.Keys.Select(Key).ToArray();
                try { foreach (var k in keys) Keyboard(k, 0, 0); }
                finally { foreach (var k in keys.Reverse()) Keyboard(k, 0, 2); }
                break;
            case "wait": ct.WaitHandle.WaitOne(a.WaitMilliseconds); ct.ThrowIfCancellationRequested(); break;
        }
    }

    private static ushort Key(string key) => key.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => 0x11, "SHIFT" => 0x10, "ALT" => 0x12, "WIN" or "META" => 0x5B,
        "ENTER" or "RETURN" => 0x0D, "TAB" => 9, "ESC" or "ESCAPE" => 0x1B, "SPACE" => 0x20,
        "BACKSPACE" => 8, "DELETE" => 0x2E, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
        "LEFT" or "ARROWLEFT" => 0x25, "UP" or "ARROWUP" => 0x26, "RIGHT" or "ARROWRIGHT" => 0x27, "DOWN" or "ARROWDOWN" => 0x28,
        var k when k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]) => k[0],
        var k when k.StartsWith('F') && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24 => (ushort)(0x70 + f - 1),
        _ => throw new ArgumentException("Unsupported key: " + key),
    };

    private static string Title(nint handle) { var text = new StringBuilder(1024); GetWindowText(handle, text, text.Capacity); return text.ToString(); }
    private static bool PhysicalBounds(nint handle, out Rect rect) { using var dpi = new DpiScope(); return GetWindowRect(handle, out rect); }
    private sealed class DpiScope : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2: screenshots and cursor share physical pixels.
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    private void RequireInputTarget()
    {
        if (!SameWindow() || GetForegroundWindow() != window || !PhysicalBounds(window, out var now) || !now.Equals(bounds))
            throw new ComputerOperationException("Selected window focus or bounds changed; observe again before input.");
    }
    private void Mouse(uint flags, int data = 0, bool release = false)
    {
        if (!release) RequireInputTarget();
        Send(new() { Type = 0, Union = new() { Mouse = new() { Flags = flags, Data = unchecked((uint)data) } } });
    }
    private void Keyboard(ushort key, ushort scan, uint flags)
    {
        if ((flags & 2) == 0) RequireInputTarget(); // Always release a held key even if the user switched windows.
        Send(new() { Type = 1, Union = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } });
    }
    private static void Send(Input input) { if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new ComputerOperationException("Windows input was blocked; elevated/secure windows require the user."); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    private delegate bool EnumWindowProc(nint hwnd, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
