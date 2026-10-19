using System.Diagnostics;
using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using ImageIO;
using Microsoft.Extensions.Logging;
using ObjCRuntime;
using ScreenCaptureKit;
using SwarmCode.App.Services;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Coding.Computer;

namespace SwarmCode.App.Platforms.macOS.Services;

/// <summary>Public Apple APIs only. ScreenCaptureKit captures exactly one window, never the whole desktop.</summary>
public sealed class MacDesktopDriver(ILogger<MacDesktopDriver> logger) : IMacDesktopDriver
{
    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string Accessibility = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private bool requestedScreen, requestedAccessibility;

    private void RequirePermissions(bool input)
    {
        if (!CGPreflightScreenCaptureAccess())
        {
            if (!requestedScreen) { requestedScreen = true; CGRequestScreenCaptureAccess(); }
            throw new ComputerOperationException("Allow Talavine Code in System Settings > Privacy & Security > Screen & System Audio Recording, then reopen the app if macOS requests it.");
        }
        if (input && !AXIsProcessTrusted())
        {
            if (!requestedAccessibility)
            {
                requestedAccessibility = true;
                using var key = new NSString("AXTrustedCheckOptionPrompt");
                using var prompt = NSDictionary.FromObjectAndKey(NSNumber.FromBoolean(true), key);
                AXIsProcessTrustedWithOptions(prompt.Handle);
            }
            throw new ComputerOperationException("Allow Talavine Code in System Settings > Privacy & Security > Accessibility, then observe the window again.");
        }
    }

    public async Task<IReadOnlyList<MacDesktopWindow>> ListAsync(CancellationToken ct)
    {
        RequirePermissions(false);
        using var content = await SCShareableContent.GetShareableContentAsync(false, true).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var result = new List<MacDesktopWindow>();
        foreach (var window in content.Windows)
        {
            ct.ThrowIfCancellationRequested();
            if (window.WindowLayer != 0 || !window.OnScreen || window.OwningApplication is not { } app
                || window.Frame.Width < 1 || window.Frame.Height < 1) continue;
            try
            {
                using var process = Process.GetProcessById(app.ProcessId);
                result.Add(new(window.WindowId.ToString(), window.Title ?? app.ApplicationName ?? "Window", (uint)app.ProcessId,
                    process.StartTime.ToUniversalTime().Ticks, window.Frame.X, window.Frame.Y,
                    (int)Math.Ceiling(window.Frame.Width), (int)Math.Ceiling(window.Frame.Height)));
            }
            catch (ArgumentException) { } // Window closed during enumeration.
        }
        return result;
    }

    public async Task ActivateAsync(MacDesktopWindow window, CancellationToken ct)
    {
        RequirePermissions(true);
        if (!await IsCurrentAsync(window, false, ct)) throw new ComputerOperationException("Window changed; list windows again.");
        await DesktopPlatform.InvokeAsync(() =>
        {
            using var app = NSRunningApplication.GetRunningApplication((int)window.ProcessId);
            if (app is null) throw new ComputerOperationException("The selected application closed.");
            // macOS 14+ uses cooperative activation; the old ignore-other-apps flag has no effect.
            NSApplication.SharedApplication.YieldActivation(app);
            app.Activate(NSRunningApplication.CurrentApplication, (NSApplicationActivationOptions)0);
        }).ConfigureAwait(false);
        if (await IsCurrentAsync(window, true, ct)) return;
        Raise(window);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsCurrentAsync(window, true, ct)) return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        var visible = WindowInfo();
        var foreground = await DesktopPlatform.InvokeAsync(() => NSWorkspace.SharedWorkspace.FrontmostApplication?.ProcessIdentifier == window.ProcessId).ConfigureAwait(false);
        logger.LogWarning("Mac window activation did not settle: foreground app matches={Foreground}, unique window geometry matches={UniqueWindow}, AX focus matches={AXFocus}.",
            foreground, visible.Count(w => MatchesBounds(w, window)) == 1, FocusedWindowMatches(window));
        throw new ComputerOperationException("The selected window could not be focused. Unlock the Mac if needed, bring the window to the front, then observe again.");
    }

    public async Task<byte[]> CaptureAsync(MacDesktopWindow window, CancellationToken ct)
    {
        RequirePermissions(false);
        using var content = await SCShareableContent.GetShareableContentAsync(false, true).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var target = content.Windows.SingleOrDefault(w => w.WindowId.ToString() == window.Id && w.OwningApplication?.ProcessId == window.ProcessId)
            ?? throw new ComputerOperationException("Window closed before capture.");
        using var filter = new SCContentFilter(target);
        using var config = new SCStreamConfiguration { Width = (nuint)window.Width, Height = (nuint)window.Height,
            ShowsCursor = false, IgnoreShadowsSingleWindow = true };
        using var image = await SCScreenshotManager.CaptureImageAsync(filter, config).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if ((int)image.Width != window.Width || (int)image.Height != window.Height)
            throw new ComputerOperationException("Screenshot dimensions changed; observe again.");
        // Encode on the worker through ImageIO; AppKit image conversion requires the dispatcher.
        using var png = new NSMutableData();
        using var encoder = CGImageDestination.Create(png, "public.png", 1)
            ?? throw new ComputerOperationException("Window screenshot encoder is unavailable.");
        encoder.AddImage(image, (CGImageDestinationOptions?)null);
        if (!encoder.Close()) throw new ComputerOperationException("Window screenshot failed.");
        return png.ToArray();
    }

    public async Task<bool> IsCurrentAsync(MacDesktopWindow window, bool requireFocus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequirePermissions(requireFocus);
        try
        {
            using var process = Process.GetProcessById((int)window.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != window.ProcessStartTicks) return false;
        }
        catch (ArgumentException) { return false; }
        var windows = WindowInfo();
        var selected = windows.FirstOrDefault(w => w.Id == window.Id && w.Pid == window.ProcessId);
        var same = selected is not null && MatchesBounds(selected, window);
        if (same && requireFocus)
            same = await DesktopPlatform.InvokeAsync(() => NSWorkspace.SharedWorkspace.FrontmostApplication?.ProcessIdentifier == window.ProcessId).ConfigureAwait(false)
                // Window-server ordering spans displays/Spaces and does not identify the keyboard target.
                // AX focus is authoritative; require unique native geometry so equal-sized overlapping windows
                // cannot impersonate the selected window. Pointer input separately checks hit ownership.
                && windows.Count(w => MatchesBounds(w, window)) == 1 && FocusedWindowMatches(window);
        return same;
    }

    public async Task ExecuteAsync(MacDesktopWindow window, ComputerAction action, CancellationToken ct)
    {
        async Task Guard(ComputerPoint? point = null)
        {
            if (!await IsCurrentAsync(window, true, ct)) throw new ComputerOperationException("Window focus or bounds changed; observe again.");
            if (point is not null && !HitBelongsToWindow(window, point))
                throw new ComputerOperationException("Another window covers the input location; observe again.");
        }
        void Post(CGEvent ev) { CGEvent.Post(ev, CGEventTapLocation.HID); }
        async Task Move(ComputerPoint point, CGEventType kind = CGEventType.MouseMoved, CGMouseButton button = CGMouseButton.Left)
        {
            await Guard(point);
            using var ev = new CGEvent(null, kind, new CGPoint(window.X + point.X, window.Y + point.Y), button);
            Post(ev);
        }
        var button = action.Button switch { "right" => CGMouseButton.Right, "middle" => CGMouseButton.Center, _ => CGMouseButton.Left };
        var (down, up, drag) = button switch
        {
            CGMouseButton.Right => (CGEventType.RightMouseDown, CGEventType.RightMouseUp, CGEventType.RightMouseDragged),
            CGMouseButton.Center => (CGEventType.OtherMouseDown, CGEventType.OtherMouseUp, CGEventType.OtherMouseDragged),
            _ => (CGEventType.LeftMouseDown, CGEventType.LeftMouseUp, CGEventType.LeftMouseDragged),
        };
        void ReleaseMouse(ComputerPoint point, int clickCount = 1)
        {
            using var release = new CGEvent(null, up, new CGPoint(window.X + point.X, window.Y + point.Y), button);
            release.SetValueField(CGEventField.MouseEventClickState, (long)clickCount);
            Post(release);
        }
        await Guard();
        switch (action.Type)
        {
            case "click": case "double_click":
                var point = new ComputerPoint(action.X!.Value, action.Y!.Value);
                await Move(point);
                for (var click = 1; click <= (action.Type == "double_click" ? 2 : 1); click++)
                {
                    await Guard(point);
                    using var press = new CGEvent(null, down, new CGPoint(window.X + point.X, window.Y + point.Y), button);
                    press.SetValueField(CGEventField.MouseEventClickState, (long)click);
                    try { Post(press); }
                    finally { ReleaseMouse(point, click); }
                }
                break;
            case "move": await Move(new(action.X!.Value, action.Y!.Value)); break;
            case "drag":
            {
                var last = action.Path[0];
                await Move(last);
                await Guard(last);
                using var dragPress = new CGEvent(null, down, new CGPoint(window.X + last.X, window.Y + last.Y), button);
                try
                {
                    Post(dragPress);
                    foreach (var next in action.Path.Skip(1)) { await Move(next, drag, button); last = next; }
                }
                finally { ReleaseMouse(last); }
                break;
            }
            case "scroll":
                await Move(new(action.X!.Value, action.Y!.Value));
                await Guard(new(action.X.Value, action.Y.Value));
                using (var scroll = new CGEvent(null, CGScrollEventUnit.Pixel, [(int)-action.ScrollY, (int)-action.ScrollX])) Post(scroll);
                break;
            case "type":
                foreach (var chunk in (action.Text ?? "").EnumerateRunes())
                {
                    await Guard();
                    using var press = new CGEvent(null, (ushort)0, true);
                    using var release = new CGEvent(null, (ushort)0, false);
                    press.SetUnicodeString(chunk.ToString()); release.SetUnicodeString(chunk.ToString());
                    try { Post(press); } finally { Post(release); }
                }
                break;
            case "keypress":
                var held = new List<ushort>();
                var flags = (CGEventFlags)0;
                try
                {
                    foreach (var key in action.Keys)
                    {
                        await Guard();
                        var code = MacDesktopKeys.Code(key);
                        flags |= Modifier(code);
                        using var press = new CGEvent(null, code, true) { Flags = flags };
                        held.Add(code); Post(press);
                    }
                }
                finally
                {
                    foreach (var code in held.AsEnumerable().Reverse())
                    {
                        flags &= ~Modifier(code);
                        using var release = new CGEvent(null, code, false) { Flags = flags }; Post(release);
                    }
                }
                break;
            case "wait": await Task.Delay(action.WaitMilliseconds, ct); break;
        }
    }

    private static CGEventFlags Modifier(ushort code) => code switch
    { 55 => CGEventFlags.Command, 56 => CGEventFlags.Shift, 58 => CGEventFlags.Alternate, 59 => CGEventFlags.Control, _ => 0 };

    private sealed record Window(string Id, uint Pid, int Layer, CGRect Bounds);
    private static bool MatchesBounds(Window candidate, MacDesktopWindow window)
        => candidate.Layer == 0 && candidate.Pid == window.ProcessId && candidate.Bounds.X == window.X && candidate.Bounds.Y == window.Y
            && Math.Ceiling(candidate.Bounds.Width) == window.Width && Math.Ceiling(candidate.Bounds.Height) == window.Height;
    private static List<Window> WindowInfo()
    {
        var pointer = CGWindowListCopyWindowInfo(1 | 16, 0);
        if (pointer == 0) throw new ComputerOperationException("The macOS window server is unavailable.");
        try
        {
            var array = Runtime.GetNSObject<NSArray>(pointer)!;
            var result = new List<Window>();
            for (nuint i = 0; i < array.Count; i++)
            {
                var info = array.GetItem<NSDictionary>(i);
                if (info["kCGWindowNumber"] is not NSNumber id || info["kCGWindowOwnerPID"] is not NSNumber pid
                    || info["kCGWindowLayer"] is not NSNumber layer || info["kCGWindowBounds"] is not NSDictionary bounds
                    || info["kCGWindowAlpha"] is NSNumber alpha && alpha.DoubleValue <= 0) continue;
                var rect = new CGRect(((NSNumber)bounds["X"]).DoubleValue, ((NSNumber)bounds["Y"]).DoubleValue,
                    ((NSNumber)bounds["Width"]).DoubleValue, ((NSNumber)bounds["Height"]).DoubleValue);
                result.Add(new(id.UInt32Value.ToString(), pid.UInt32Value, layer.Int32Value, rect));
            }
            return result;
        }
        finally { CFRelease(pointer); }
    }

    private static void Raise(MacDesktopWindow window)
    {
        var application = AXUIElementCreateApplication((int)window.ProcessId);
        try
        {
            AXUIElementSetMessagingTimeout(application, 0.5f);
            // Accessibility permission permits selecting the application's foreground state even when the
            // current foreground app did not participate in cooperative activation. Input still needs all guards.
            using var frontmost = new NSString("AXFrontmost");
            using var yes = NSNumber.FromBoolean(true);
            AXUIElementSetAttributeValue(application, frontmost.Handle, yes.Handle);
            using var windowsKey = new NSString("AXWindows");
            if (AXUIElementCopyAttributeValue(application, windowsKey.Handle, out var array) != 0) return;
            try
            {
                var matches = new List<nint>();
                for (nint i = 0; i < CFArrayGetCount(array); i++)
                {
                    var candidate = CFArrayGetValueAtIndex(array, i);
                    AXUIElementSetMessagingTimeout(candidate, 0.5f);
                    if (Matches(candidate, window)) matches.Add(candidate);
                }
                if (matches.Count != 1) throw new ComputerOperationException("Window identity is ambiguous. Bring the intended window to the front, then observe again.");
                using var raise = new NSString("AXRaise");
                AXUIElementPerformAction(matches[0], raise.Handle);
            }
            finally { CFRelease(array); }
        }
        finally { CFRelease(application); }
    }

    private static bool Matches(nint element, MacDesktopWindow window)
    {
        using var position = new NSString("AXPosition");
        using var size = new NSString("AXSize");
        if (AXUIElementCopyAttributeValue(element, position.Handle, out var p) != 0) return false;
        try
        {
            if (AXUIElementCopyAttributeValue(element, size.Handle, out var s) != 0) return false;
            try
            {
                return AXValueGetPoint(p, 1, out var point) && AXValueGetSize(s, 2, out var dimensions)
                    && point.X == window.X && point.Y == window.Y && Math.Ceiling(dimensions.Width) == window.Width && Math.Ceiling(dimensions.Height) == window.Height;
            }
            finally { CFRelease(s); }
        }
        finally { CFRelease(p); }
    }

    private static bool FocusedWindowMatches(MacDesktopWindow window)
    {
        var app = AXUIElementCreateApplication((int)window.ProcessId);
        try
        {
            AXUIElementSetMessagingTimeout(app, 0.5f);
            using var focused = new NSString("AXFocusedWindow");
            if (AXUIElementCopyAttributeValue(app, focused.Handle, out var value) != 0) return false;
            try { AXUIElementSetMessagingTimeout(value, 0.5f); return Matches(value, window); }
            finally { CFRelease(value); }
        }
        finally { CFRelease(app); }
    }

    private static bool HitBelongsToWindow(MacDesktopWindow window, ComputerPoint point)
    {
        // Rectangle stacking includes transparent/system overlays. Use the native Accessibility hit owner,
        // then match its owning window, rather than guessing input ownership from window-list ordering.
        var system = AXUIElementCreateSystemWide();
        try
        {
            AXUIElementSetMessagingTimeout(system, 0.5f);
            if (AXUIElementCopyElementAtPosition(system, (float)(window.X + point.X), (float)(window.Y + point.Y), out var element) != 0) return false;
            try
            {
                AXUIElementSetMessagingTimeout(element, 0.5f);
                if (AXUIElementGetPid(element, out var pid) != 0 || pid != window.ProcessId) return false;
                using var key = new NSString("AXWindow");
                if (AXUIElementCopyAttributeValue(element, key.Handle, out var owner) != 0) return Matches(element, window);
                try { AXUIElementSetMessagingTimeout(owner, 0.5f); return Matches(owner, window); }
                finally { CFRelease(owner); }
            }
            finally { CFRelease(element); }
        }
        finally { CFRelease(system); }
    }

    [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(Graphics)] private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeWindow);
    [DllImport(Accessibility)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(Accessibility)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrustedWithOptions(nint options);
    [DllImport(Accessibility)] private static extern nint AXUIElementCreateApplication(int pid);
    [DllImport(Accessibility)] private static extern nint AXUIElementCreateSystemWide();
    [DllImport(Accessibility)] private static extern int AXUIElementCopyElementAtPosition(nint element, float x, float y, out nint hit);
    [DllImport(Accessibility)] private static extern int AXUIElementGetPid(nint element, out int pid);
    [DllImport(Accessibility)] private static extern int AXUIElementSetMessagingTimeout(nint element, float timeout);
    [DllImport(Accessibility)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(Accessibility)] private static extern int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);
    [DllImport(Accessibility)] private static extern int AXUIElementPerformAction(nint element, nint action);
    [DllImport(Accessibility, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetPoint(nint value, int type, out CGPoint point);
    [DllImport(Accessibility, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetSize(nint value, int type, out CGSize size);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
}
