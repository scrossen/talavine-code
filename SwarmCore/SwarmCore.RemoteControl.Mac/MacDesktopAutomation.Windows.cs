using System.Diagnostics;
using SwarmCore.RemoteControl.Mac.Mapping;
using SwarmCore.RemoteControl.Mac.Native;

namespace SwarmCore.RemoteControl.Mac;

public sealed partial class MacDesktopAutomation
{
    /// <summary>One CGWindowListCopyWindowInfo entry.</summary>
    private sealed record CgWindowEntry(uint Id, int Pid, string? OwnerName, string? Name, RectD? Bounds, bool IsOnScreen, long? Layer, double? Alpha);

    // ---------------------------------------------------------------- displays

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return ReadDisplays();
        }
    }

    private unsafe IReadOnlyList<(uint Id, DisplayInfo Info)> ReadDisplaysWithIds()
    {
        var error = CG.CGGetActiveDisplayList(0, null, out var count);
        if (error != CG.kCGErrorSuccess)
            throw new DesktopAutomationException($"macOS could not list displays (CGError {error}).", FailureCategories.OsRefused);
        if (count == 0) return [];

        var ids = new uint[count];
        fixed (uint* p = ids)
        {
            error = CG.CGGetActiveDisplayList(count, p, out count);
        }
        if (error != CG.kCGErrorSuccess)
            throw new DesktopAutomationException($"macOS could not list displays (CGError {error}).", FailureCategories.OsRefused);

        var main = CG.CGMainDisplayID();
        var ordered = DisplayLayout.Order(ids.Take((int)count).Select(id => (id, CG.CGDisplayBounds(id).ToRectD())), main);
        var result = new List<(uint, DisplayInfo)>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (id, bounds) = ordered[i];
            var rect = bounds.ToScreenRect();
            result.Add((id, new DisplayInfo(i + 1, $"Display {id}", id == main, rect, rect, ScaleFactorOf(id, bounds))));
        }
        return result;
    }

    private IReadOnlyList<DisplayInfo> ReadDisplays() => ReadDisplaysWithIds().Select(d => d.Info).ToList();

    /// <summary>
    /// Backing pixels per point. CGDisplayPixelsWide reports the mode's logical width on HiDPI modes (it equals the
    /// point width on Retina), so the backing width comes from the display mode's pixel width when available.
    /// </summary>
    private static double ScaleFactorOf(uint displayId, RectD bounds)
    {
        if (!(bounds.Width > 0)) return 1;
        double pixels = 0;
        var mode = CG.CGDisplayCopyDisplayMode(displayId);
        try
        {
            if (mode != 0) pixels = CG.CGDisplayModeGetPixelWidth(mode);
        }
        finally
        {
            CG.CGDisplayModeRelease(mode);
        }
        if (!(pixels > 0)) pixels = CG.CGDisplayPixelsWide(displayId);
        return pixels > 0 ? Math.Round(pixels / bounds.Width, 3) : 1;
    }

    // ---------------------------------------------------------------- window list

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureUnlocked();
            var entries = ReadWindowList(CG.kCGWindowListOptionAll | CG.kCGWindowListExcludeDesktopElements, CG.kCGNullWindowID)
                .Where(e => CgWindowFilter.IsCandidate(e.Layer, e.Alpha, e.Bounds, e.OwnerName))
                .ToList();
            var displays = ReadDisplays();
            var focused = ReadFocusedWindow();
            var axByPid = new Dictionary<int, Dictionary<uint, nint>>();

            var result = new List<WindowInfo>(entries.Count);
            foreach (var entry in entries)
            {
                if (!axByPid.TryGetValue(entry.Pid, out var axWindows))
                {
                    axWindows = MapAppWindows(entry.Pid, entries.Where(e => e.Pid == entry.Pid).ToList());
                    axByPid[entry.Pid] = axWindows;
                }
                axWindows.TryGetValue(entry.Id, out var axWindow);
                var info = BuildWindowInfo(entry, axWindow, focused, displays, requireIdentity: true);
                if (info is not null) result.Add(info);
            }

            if (result.Count == 0 && entries.Count > 0 && !AX.AXIsProcessTrusted() && !CG.CGPreflightScreenCaptureAccess())
            {
                throw new DesktopAutomationException(
                    "Window titles are hidden because this process has neither Accessibility nor Screen Recording permission. " +
                    MacMessages.AccessibilityHint + " " + MacMessages.ScreenRecordingHint, FailureCategories.Permission);
            }

            // CGWindowList is front-to-back; a stable sort keeps that z-order after the foreground window.
            return result.OrderBy(w => w.IsForeground ? 0 : 1).ToList();
        }
    }

    public WindowInfo? GetWindow(long handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var entry = ReadWindowEntry(handle);
            if (entry is null) return null;
            var axWindow = FindAxWindow(entry);
            return BuildWindowInfo(entry, axWindow, ReadFocusedWindow(), ReadDisplays(), requireIdentity: false);
        }
    }

    private WindowInfo? BuildWindowInfo(CgWindowEntry entry, nint axWindow, (int Pid, uint WindowId)? focused,
        IReadOnlyList<DisplayInfo> displays, bool requireIdentity)
    {
        var title = entry.Name;
        bool? axMinimized = null;
        if (axWindow != 0)
        {
            if (string.IsNullOrEmpty(title)) title = ReadString(axWindow, "AXTitle");
            axMinimized = ReadBool(axWindow, "AXMinimized");
        }
        if (requireIdentity && string.IsNullOrEmpty(title) && axWindow == 0) return null;

        var bounds = (entry.Bounds ?? default).ToScreenRect();
        // A window on another Space is off-screen but not minimized; trust AX when the window is matched.
        var minimized = axMinimized ?? !entry.IsOnScreen;
        var foreground = focused is { } f && f.Pid == entry.Pid && f.WindowId == entry.Id;
        return new WindowInfo(entry.Id, title ?? string.Empty, entry.OwnerName ?? string.Empty, entry.Pid, bounds,
            minimized, foreground, DisplayLayout.DisplayIndexFor(bounds, displays))
        {
            IsElevated = false,
        };
    }

    private List<CgWindowEntry> ReadWindowList(uint option, uint relativeTo)
    {
        using var list = new CfRef(CG.CGWindowListCopyWindowInfo(option, relativeTo));
        var result = new List<CgWindowEntry>();
        if (!Cf.Is(list.Value, Cf.ArrayType)) return result;

        var kNumber = _names["kCGWindowNumber"];
        var kPid = _names["kCGWindowOwnerPID"];
        var kOwner = _names["kCGWindowOwnerName"];
        var kName = _names["kCGWindowName"];
        var kBounds = _names["kCGWindowBounds"];
        var kLayer = _names["kCGWindowLayer"];
        var kAlpha = _names["kCGWindowAlpha"];
        var kOnScreen = _names["kCGWindowIsOnscreen"];

        var count = CF.CFArrayGetCount(list.Value);
        for (nint i = 0; i < count; i++)
        {
            var dict = CF.CFArrayGetValueAtIndex(list.Value, i);
            if (!Cf.Is(dict, Cf.DictionaryType)) continue;
            var number = Cf.ToInt64(CF.CFDictionaryGetValue(dict, kNumber));
            if (number is not (> 0 and <= uint.MaxValue)) continue;
            var boundsDict = CF.CFDictionaryGetValue(dict, kBounds);
            RectD? bounds = Cf.Is(boundsDict, Cf.DictionaryType) && CG.CGRectMakeWithDictionaryRepresentation(boundsDict, out var r) ? r.ToRectD() : null;
            result.Add(new CgWindowEntry(
                (uint)number.Value,
                (int)(Cf.ToInt64(CF.CFDictionaryGetValue(dict, kPid)) ?? 0),
                Cf.ToStringOrNull(CF.CFDictionaryGetValue(dict, kOwner)),
                Cf.ToStringOrNull(CF.CFDictionaryGetValue(dict, kName)),
                bounds,
                Cf.ToBool(CF.CFDictionaryGetValue(dict, kOnScreen)) == true,
                Cf.ToInt64(CF.CFDictionaryGetValue(dict, kLayer)),
                Cf.ToDouble(CF.CFDictionaryGetValue(dict, kAlpha))));
        }
        return result;
    }

    /// <summary>The CG entry for one window number, or null when it no longer exists.</summary>
    private CgWindowEntry? ReadWindowEntry(long handle)
    {
        if (ToWindowId(handle) is not { } id) return null;
        return ReadWindowList(CG.kCGWindowListOptionIncludingWindow, id).FirstOrDefault(e => e.Id == id);
    }

    private CgWindowEntry RequireWindowEntry(long handle) =>
        ReadWindowEntry(handle) ?? throw new DesktopAutomationException(
            $"Window {handle} no longer exists (it closed or its app quit). List the windows again.", FailureCategories.WindowGone);

    /// <summary>(pid, CGWindowID) of the focused application's focused window, or null.</summary>
    private (int Pid, uint WindowId)? ReadFocusedWindow()
    {
        if (!AX.AXIsProcessTrusted()) return null;
        using var app = new CfRef(CopyElementAttribute(SystemWide, "AXFocusedApplication"));
        if (app.IsNull || AX.AXUIElementGetPid(app.Value, out var pid) != AX.kAXErrorSuccess) return null;
        using var window = new CfRef(CopyElementAttribute(app.Value, "AXFocusedWindow"));
        if (window.IsNull) return (pid, 0);
        if (AX.AXUIElementGetWindow(window.Value, out var wid) == AX.kAXErrorSuccess && wid != 0) return (pid, wid);
        // Fall back to bounds matching against the app's on-screen windows.
        if (ReadFrame(window.Value) is { } frame)
        {
            var candidates = ReadWindowList(CG.kCGWindowListOptionOnScreenOnly | CG.kCGWindowListExcludeDesktopElements, CG.kCGNullWindowID)
                .Where(e => e.Pid == pid && e.Layer == 0).ToList();
            var index = AxWindowMatcher.FindBestByBounds(candidates.Select(c => c.Bounds).ToList(), frame);
            if (index >= 0) return (pid, candidates[index].Id);
        }
        return (pid, 0);
    }

    // ---------------------------------------------------------------- AX window mapping

    /// <summary>
    /// Maps the app's AX windows to CGWindowIDs with <c>_AXUIElementGetWindow</c>, falling back to bounds matching
    /// against <paramref name="cgEntries"/>. Matched windows are cached (+1, released in Dispose); the returned refs are borrowed.
    /// </summary>
    private Dictionary<uint, nint> MapAppWindows(int pid, IReadOnlyList<CgWindowEntry> cgEntries)
    {
        var result = new Dictionary<uint, nint>();
        if (pid <= 0 || !AX.AXIsProcessTrusted()) return result;
        var app = GetAppElement(pid);
        if (app == 0) return result;

        List<nint> windows;
        using (var array = new CfRef(CopyAttribute(app, "AXWindows")))
        {
            windows = Cf.RetainArrayItems(array.Value);
        }

        var unmatched = new List<nint>();
        foreach (var window in windows)
        {
            if (AX.AXUIElementGetWindow(window, out var wid) == AX.kAXErrorSuccess && wid != 0 && !result.ContainsKey(wid))
            {
                CacheAxWindow(wid, pid, window);
                result[wid] = window;
            }
            else
            {
                unmatched.Add(window);
            }
        }

        if (unmatched.Count > 0)
        {
            var frames = unmatched.Select(w => ReadFrame(w)).ToList();
            foreach (var entry in cgEntries)
            {
                if (result.ContainsKey(entry.Id) || entry.Bounds is not { } bounds) continue;
                var index = AxWindowMatcher.FindBestByBounds(frames, bounds);
                if (index < 0) continue;
                CacheAxWindow(entry.Id, pid, unmatched[index]);
                result[entry.Id] = unmatched[index];
                unmatched[index] = 0;
                frames[index] = null;
            }
        }

        foreach (var w in unmatched) CF.Release(w);
        return result;
    }

    /// <summary>Takes ownership of a +1 AX window ref for <paramref name="windowId"/>.</summary>
    private void CacheAxWindow(uint windowId, int pid, nint window)
    {
        if (_axWindows.TryGetValue(windowId, out var old))
        {
            if (old.Element == window) return;
            CF.Release(old.Element);
        }
        _axWindows[windowId] = new AxWindowEntry(pid, window);
    }

    /// <summary>The AX element of a CG window (borrowed from the cache), or 0 when the app exposes none.</summary>
    private nint FindAxWindow(CgWindowEntry entry)
    {
        if (_axWindows.TryGetValue(entry.Id, out var cached))
        {
            if (cached.Pid == entry.Pid && ReadRoleIfAlive(cached.Element) is not null) return cached.Element;
            CF.Release(cached.Element);
            _axWindows.Remove(entry.Id);
        }
        var siblings = ReadWindowList(CG.kCGWindowListOptionAll | CG.kCGWindowListExcludeDesktopElements, CG.kCGNullWindowID)
            .Where(e => e.Pid == entry.Pid && CgWindowFilter.IsCandidate(e.Layer, e.Alpha, e.Bounds, e.OwnerName))
            .ToList();
        if (!siblings.Any(s => s.Id == entry.Id)) siblings.Add(entry);
        return MapAppWindows(entry.Pid, siblings).TryGetValue(entry.Id, out var window) ? window : 0;
    }

    /// <summary>The AX window for a handle; throws window-gone when the CG window is gone and a clear error when the app exposes no AX window.</summary>
    private (CgWindowEntry Entry, nint AxWindow) RequireAxWindow(long handle)
    {
        var entry = RequireWindowEntry(handle);
        var axWindow = FindAxWindow(entry);
        if (axWindow == 0)
        {
            EnsureAccessibility();
            throw new DesktopAutomationException(
                $"Window {handle} ({entry.OwnerName}) exposes no accessibility element. It may be on another Space, be a non-standard window, or belong to an app that does not support Accessibility.",
                FailureCategories.OsRefused);
        }
        return (entry, axWindow);
    }

    // ---------------------------------------------------------------- focus

    public bool FocusWindow(long handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureAccessibility();
            var entry = RequireWindowEntry(handle);
            var app = GetAppElement(entry.Pid);
            if (app == 0)
                throw new DesktopAutomationException($"The app that owns window {handle} has exited.", FailureCategories.WindowGone);
            var axWindow = FindAxWindow(entry);

            var frontError = SetBool(app, "AXFrontmost", true);
            if (frontError == AX.kAXErrorAPIDisabled) throw AxFailure(frontError, "Activating the app");
            if (frontError == AX.kAXErrorInvalidUIElement)
                throw new DesktopAutomationException($"The app that owns window {handle} has exited.", FailureCategories.WindowGone);
            if (axWindow != 0)
            {
                SetBool(axWindow, "AXMinimized", false);
                Perform(axWindow, AxActionMap.Raise);
                SetBool(axWindow, "AXMain", true);
            }

            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (ReadFocusedWindow() is { } f && f.Pid == entry.Pid && (axWindow == 0 || f.WindowId == entry.Id)) return true;
                if (watch.ElapsedMilliseconds >= 500) return false;
                Thread.Sleep(25);
            }
        }
    }
}
