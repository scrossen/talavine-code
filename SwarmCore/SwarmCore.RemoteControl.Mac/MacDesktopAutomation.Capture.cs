using SwarmCore.RemoteControl.Mac.Mapping;
using SwarmCore.RemoteControl.Mac.Native;

namespace SwarmCore.RemoteControl.Mac;

// Capture uses CGDisplayCreateImage / CGWindowListCreateImage. Apple soft-deprecated them in macOS 14 in favor of
// ScreenCaptureKit (SCScreenshotManager), which needs Objective-C interop; they still work with Screen Recording
// permission. Moving to ScreenCaptureKit is the future path once these stop working.
public sealed partial class MacDesktopAutomation
{
    public Screenshot CaptureDisplay(int displayIndex)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureScreenCapture();
            var displays = ReadDisplaysWithIds();
            var match = displays.FirstOrDefault(d => d.Info.Index == displayIndex);
            if (match.Info is null)
                throw new DesktopAutomationException(
                    $"Screen {displayIndex} does not exist; this Mac has {displays.Count} screen{(displays.Count == 1 ? "" : "s")}.",
                    FailureCategories.NotFound);
            return Encode(CG.CGDisplayCreateImage(match.Id), match.Info.Bounds, $"screen {displayIndex}");
        }
    }

    public Screenshot CaptureAllDisplays()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureScreenCapture();
            var union = default(RectD);
            foreach (var d in ReadDisplaysWithIds()) union = union.Union(RectD.FromScreenRect(d.Info.Bounds));
            if (union.IsEmpty) throw new DesktopAutomationException("macOS reports no active displays.", FailureCategories.OsRefused);
            var image = CG.CGWindowListCreateImage(CGRect.From(union), CG.kCGWindowListOptionOnScreenOnly, CG.kCGNullWindowID, CG.kCGWindowImageDefault);
            return Encode(image, union.ToScreenRect(), "all screens");
        }
    }

    public Screenshot CaptureWindow(long handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureScreenCapture();
            var entry = RequireWindowEntry(handle);

            // A minimized window has no current pixels: un-minimize it through AX (this does not activate its app).
            if (AX.AXIsProcessTrusted())
            {
                var axWindow = FindAxWindow(entry);
                if (axWindow != 0 && ReadBool(axWindow, "AXMinimized") == true)
                {
                    SetBool(axWindow, "AXMinimized", false);
                    Thread.Sleep(400);
                    entry = RequireWindowEntry(handle);
                }
            }

            var image = CG.CGWindowListCreateImage(CGRect.Null, CG.kCGWindowListOptionIncludingWindow, entry.Id,
                CG.kCGWindowImageBoundsIgnoreFraming | CG.kCGWindowImageBestResolution);
            if (image == 0)
            {
                RequireWindowEntry(handle);
                throw new DesktopAutomationException($"macOS returned no image for window {handle}. {MacMessages.ScreenRecordingHint}", FailureCategories.Permission);
            }
            var title = string.IsNullOrEmpty(entry.Name) ? entry.OwnerName : $"{entry.OwnerName} - {entry.Name}";
            return Encode(image, (entry.Bounds ?? default).ToScreenRect(), $"window {handle} ({title})");
        }
    }

    public Screenshot CaptureRegion(ScreenRect region)
    {
        if (region.IsEmpty) throw new ArgumentException($"The capture region {region} is empty.", nameof(region));
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureScreenCapture();
            var rect = new CGRect(region.Left, region.Top, region.Width, region.Height);
            var image = CG.CGWindowListCreateImage(rect, CG.kCGWindowListOptionOnScreenOnly, CG.kCGNullWindowID, CG.kCGWindowImageDefault);
            return Encode(image, region, $"region {region}");
        }
    }

    private static void EnsureScreenCapture()
    {
        EnsureUnlocked();
        // Without permission macOS 15+ still returns images, but they show only the wallpaper and this process's own
        // windows, so refuse instead of returning a misleading capture.
        if (!CG.CGPreflightScreenCaptureAccess())
            throw new DesktopAutomationException(MacMessages.ScreenRecordingHint, FailureCategories.Permission);
    }

    /// <summary>
    /// True (the default) shrinks a capture taken on a doubled (Retina) display to the screen's point size, so the
    /// image is one pixel per click coordinate and about a quarter the bytes. Set false for full backing resolution.
    /// </summary>
    public bool DownscaleHiDpiCaptures { get; set; } = true;

    /// <summary>Returns a +1 image redrawn at <paramref name="width"/> x <paramref name="height"/>, or 0 on failure. Does not release <paramref name="image"/>.</summary>
    private static nint Resample(nint image, int width, int height)
    {
        var space = CG.CGColorSpaceCreateDeviceRGB();
        if (space == 0) return 0;
        try
        {
            // kCGImageAlphaNoneSkipLast: screens are opaque, so no alpha channel to carry.
            var context = CG.CGBitmapContextCreate(0, (nuint)width, (nuint)height, 8, 0, space, 5);
            if (context == 0) return 0;
            try
            {
                CG.CGContextSetInterpolationQuality(context, 3); // kCGInterpolationHigh
                CG.CGContextDrawImage(context, new CGRect(0, 0, width, height), image);
                return CG.CGBitmapContextCreateImage(context);
            }
            finally { CG.CGContextRelease(context); }
        }
        finally { CG.CGColorSpaceRelease(space); }
    }

    /// <summary>Encodes a +1 CGImage as PNG through ImageIO and releases it.</summary>
    private Screenshot Encode(nint image, ScreenRect bounds, string subject)
    {
        if (image == 0)
            throw new DesktopAutomationException($"macOS returned no image for {subject}. {MacMessages.ScreenRecordingHint}", FailureCategories.Permission);
        try
        {
            var width = (int)CG.CGImageGetWidth(image);
            var height = (int)CG.CGImageGetHeight(image);
            if (width <= 0 || height <= 0)
                throw new DesktopAutomationException($"macOS returned an empty image for {subject}. {MacMessages.ScreenRecordingHint}", FailureCategories.Permission);

            if (DownscaleHiDpiCaptures && bounds.Width > 0 && bounds.Height > 0 && width >= bounds.Width * 1.5 && height >= bounds.Height * 1.5)
            {
                var smaller = Resample(image, bounds.Width, bounds.Height);
                if (smaller != 0)
                {
                    CG.CGImageRelease(image);
                    image = smaller;
                    width = bounds.Width;
                    height = bounds.Height;
                }
            }

            using var data = new CfRef(CF.CFDataCreateMutable(0, 0));
            if (data.IsNull) throw new DesktopAutomationException($"macOS could not allocate a buffer to encode the capture of {subject}.", FailureCategories.OsRefused);
            using var destination = new CfRef(ImageIO.CGImageDestinationCreateWithData(data.Value, _names["public.png"], 1, 0));
            if (destination.IsNull)
                throw new DesktopAutomationException("ImageIO could not create a PNG encoder.", FailureCategories.OsRefused);
            ImageIO.CGImageDestinationAddImage(destination.Value, image, 0);
            if (!ImageIO.CGImageDestinationFinalize(destination.Value))
                throw new DesktopAutomationException($"ImageIO could not encode the capture of {subject} as PNG.", FailureCategories.OsRefused);
            return new Screenshot(Cf.DataToBytes(data.Value), width, height, bounds, subject);
        }
        finally
        {
            CG.CGImageRelease(image);
        }
    }
}
