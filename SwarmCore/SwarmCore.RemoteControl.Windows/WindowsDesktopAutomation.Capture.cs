using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

public sealed partial class WindowsDesktopAutomation
{
    private const int BlackSampleGrid = 16;
    private const int RestoreWaitMilliseconds = 300;
    private const int RestorePollIntervalMilliseconds = 50;
    /// <summary>Appended to a window capture's <see cref="Screenshot.Subject"/> when it holds screen pixels, not the window's own.</summary>
    public const string ScreenCopySubjectSuffix = ":screen-copy";

    private volatile string? lastCaptureNote;

    /// <summary>
    /// A note about how the most recent capture differs from what was asked: a window captured from the screen
    /// instead of its own pixels (so whatever covers it is included), or a region clamped to the desktop. Null when
    /// the capture is exactly what was requested.
    /// </summary>
    public string? LastCaptureNote => lastCaptureNote;

    public Screenshot CaptureDisplay(int displayIndex)
    {
        ThrowIfDisposed();
        lastCaptureNote = null;
        using var dpi = new DpiScope();
        var displays = EnumerateDisplays();
        var match = displays.FirstOrDefault(d => d.Info.Index == displayIndex).Info
            ?? throw new DesktopAutomationException(
                $"There is no screen {displayIndex}; this computer has {displays.Count} screen{(displays.Count == 1 ? "" : "s")} (1-{displays.Count}).", FailureCategories.NotFound);
        return CaptureScreenRect(match.Bounds, $"screen:{displayIndex}");
    }

    public Screenshot CaptureAllDisplays()
    {
        ThrowIfDisposed();
        lastCaptureNote = null;
        using var dpi = new DpiScope();
        return CaptureScreenRect(VirtualScreen(), "all-screens");
    }

    public Screenshot CaptureRegion(ScreenRect region)
    {
        ThrowIfDisposed();
        lastCaptureNote = null;
        if (region.IsEmpty) throw new DesktopAutomationException($"The region {region} has no area.", FailureCategories.NotUnderstood);
        using var dpi = new DpiScope();
        return CaptureScreenRect(region, "region");
    }

    /// <summary>
    /// Captures the window's own pixels with PrintWindow(PW_RENDERFULLCONTENT), which works while it is covered or
    /// on another part of the desktop. A minimized window has no pixels, so it is restored without activation and
    /// minimized again afterwards. Trade-off: the window briefly appears (without taking focus) and its restore and
    /// minimize animations play; capturing it any other way would return an empty title-bar stub.
    /// When PrintWindow cannot render the window (it refuses, or UIPI keeps a higher-integrity window from
    /// answering), the visible screen pixels over the window are copied instead; the subject then ends with
    /// <see cref="ScreenCopySubjectSuffix"/> and <see cref="LastCaptureNote"/> explains it.
    /// </summary>
    public Screenshot CaptureWindow(long handle)
    {
        ThrowIfDisposed();
        lastCaptureNote = null;
        using var dpi = new DpiScope();
        var hwnd = (nint)handle;
        RequireWindow(hwnd);
        // PrintWindow and a synchronous restore wait on the window's thread; a hung window would hang us too.
        if (DesktopState.IsHung(hwnd)) throw DesktopState.Busy(hwnd, "capturing it");
        var higher = ProcessIntegrity.IsWindowHigherThanCurrent(hwnd);
        var restored = false;
        if (IsIconic(hwnd))
        {
            EnsureUserIdle(); // restoring changes the screen under the person's pointer
            ShowWindowAsync(hwnd, SwShowNoActivate); // async: never blocks on the target's message queue
            var wait = Stopwatch.StartNew();
            // Condition poll: the restore runs asynchronously on the window's thread. No paint settle is needed after it,
            // because PrintWindow asks the window to render itself synchronously.
            while (IsIconic(hwnd) && wait.ElapsedMilliseconds < RestoreWaitMilliseconds) Thread.Sleep(RestorePollIntervalMilliseconds);
            if (IsIconic(hwnd))
                throw higher
                    ? DesktopState.Elevated(hwnd, "restoring the minimized window to capture it")
                    : new DesktopAutomationException($"Window {handle} stayed minimized when asked to restore, so it has no pixels to capture.", FailureCategories.OsRefused);
            restored = true;
        }
        try
        {
            var bounds = WindowBounds(hwnd);
            if (bounds.IsEmpty) throw new DesktopAutomationException($"Window {handle} has no visible area to capture.", FailureCategories.OsRefused);
            var subject = $"window:{handle}:{WindowTitle(hwnd)}";
            using (var bitmap = higher ? null : PrintWindowBitmap(hwnd, bounds))
            {
                if (bitmap is not null)
                {
                    // PrintWindow succeeded: a dark window is a dark window. Only a cloaked window (on another virtual
                    // desktop or hidden by the shell) that rendered nothing is suspect, and the screen cannot show it.
                    if (IsCloaked(hwnd) && IsUniformlyBlack(bitmap))
                        throw new DesktopAutomationException(
                            $"Window {handle} is cloaked (on another virtual desktop or hidden by the shell) and rendered no pixels; switch to its desktop and try again.",
                            FailureCategories.OsRefused);
                    return Encode(bitmap, bounds, subject);
                }
            }
            var reason = higher
                ? "it runs at a higher integrity level than this program, so Windows does not let it render for us"
                : "it refused to render itself (PrintWindow failed)";
            var shot = CaptureScreenRect(bounds, subject + ScreenCopySubjectSuffix);
            lastCaptureNote = $"Window {handle} was captured from the screen because {reason}; anything covering it is included."
                + (lastCaptureNote is null ? "" : " " + lastCaptureNote);
            return shot;
        }
        finally
        {
            // Put it back only if it is still the way we left it: restored, and not since activated by someone.
            if (restored && IsWindow(hwnd) && !IsIconic(hwnd) && GetForegroundWindow() != hwnd) ShowWindowAsync(hwnd, SwShowMinNoActive);
        }
    }

    /// <summary>PrintWindow renders from the full window rect (including invisible resize borders); crop to the visible frame.</summary>
    private static Bitmap? PrintWindowBitmap(nint hwnd, ScreenRect frame)
    {
        if (!GetWindowRect(hwnd, out var windowRect)) return null;
        var full = windowRect.ToScreenRect();
        if (full.IsEmpty) return null;
        using var whole = AllocateBitmap(full.Width, full.Height);
        using (var graphics = Graphics.FromImage(whole))
        {
            var dc = graphics.GetHdc();
            bool printed;
            try { printed = PrintWindow(hwnd, dc, PwRenderFullContent); }
            finally { graphics.ReleaseHdc(dc); }
            if (!printed) return null;
        }
        var crop = new Rectangle(frame.Left - full.Left, frame.Top - full.Top, frame.Width, frame.Height);
        crop.Intersect(new Rectangle(0, 0, full.Width, full.Height));
        if (crop.Width <= 0 || crop.Height <= 0) return null;
        return whole.Clone(crop, PixelFormat.Format32bppRgb);
    }

    private static bool IsUniformlyBlack(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var gy = 0; gy < BlackSampleGrid; gy++)
            for (var gx = 0; gx < BlackSampleGrid; gx++)
            {
                var x = (int)((gx + 0.5) * bitmap.Width / BlackSampleGrid);
                var y = (int)((gy + 0.5) * bitmap.Height / BlackSampleGrid);
                var pixel = Marshal.ReadInt32(data.Scan0, y * data.Stride + x * sizeof(int));
                if ((pixel & 0x00FFFFFF) != 0) return false;
            }
            return true;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static Bitmap AllocateBitmap(int width, int height)
    {
        try { return new Bitmap(width, height, PixelFormat.Format32bppRgb); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            throw new DesktopAutomationException($"Windows could not allocate a {width}x{height} image for the capture ({ex.Message}).", ex, FailureCategories.OsRefused);
        }
    }

    /// <summary>
    /// BitBlt from the screen DC with CAPTUREBLT so layered windows (menus, tooltips) are included. The rectangle is
    /// clamped to the virtual desktop (and the clamp reported in <see cref="LastCaptureNote"/>); one entirely
    /// outside it is refused.
    /// </summary>
    private Screenshot CaptureScreenRect(ScreenRect requested, string subject)
    {
        var desktop = VirtualScreen();
        var left = Math.Max(requested.Left, desktop.Left);
        var top = Math.Max(requested.Top, desktop.Top);
        var right = Math.Min(requested.Right, desktop.Right);
        var bottom = Math.Min(requested.Bottom, desktop.Bottom);
        if (right <= left || bottom <= top)
            throw new DesktopAutomationException($"The area {requested} is entirely outside the desktop, which spans {desktop}.", FailureCategories.NotFound);
        var rect = ScreenRect.FromEdges(left, top, right, bottom);
        if (rect != requested) lastCaptureNote = $"The area {requested} extends past the desktop ({desktop}); captured the visible part {rect}.";

        using var bitmap = AllocateBitmap(rect.Width, rect.Height);
        var screenDc = GetDC(0);
        if (screenDc == 0)
            throw DesktopState.IsAuthenticationSurfaceActive()
                ? DesktopState.AuthenticationRequired("capturing the screen")
                : new DesktopAutomationException("Windows refused access to the screen device context.", FailureCategories.OsRefused);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            try
            {
                if (!BitBlt(dc, 0, 0, rect.Width, rect.Height, screenDc, rect.Left, rect.Top, RopSrcCopy | RopCaptureBlt))
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw DesktopState.IsAuthenticationSurfaceActive()
                        ? DesktopState.AuthenticationRequired("capturing the screen")
                        : new DesktopAutomationException($"Copying the screen pixels at {rect} failed (Win32 error {error}).", FailureCategories.OsRefused);
                }
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        finally { ReleaseDC(0, screenDc); }
        return Encode(bitmap, rect, subject);
    }

    private static Screenshot Encode(Bitmap bitmap, ScreenRect bounds, string subject)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new Screenshot(stream.ToArray(), bitmap.Width, bitmap.Height, bounds with { Width = bitmap.Width, Height = bitmap.Height }, subject);
    }
}
