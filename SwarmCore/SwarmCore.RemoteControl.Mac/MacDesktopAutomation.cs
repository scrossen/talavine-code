using System.Runtime.Versioning;
using SwarmCore.RemoteControl.Mac.Mapping;
using SwarmCore.RemoteControl.Mac.Native;

namespace SwarmCore.RemoteControl.Mac;

/// <summary>
/// macOS implementation of <see cref="IDesktopAutomation"/> built on the Accessibility (AX) API, CoreGraphics window
/// list/events/capture, and ImageIO, through plain P/Invoke.
/// <para><b>Coordinates.</b> On macOS every <see cref="ScreenRect"/> and <see cref="ScreenPoint"/> is in global
/// <i>points</i> (origin at the top-left of the main display, y down), the space shared by CGWindowList, CGEvent,
/// and AX. A display's backing scale (2.0 on Retina) is in <see cref="DisplayInfo.ScaleFactor"/>; screenshots are
/// full pixel resolution, so <see cref="Screenshot.Width"/> is usually <c>ScreenBounds.Width * ScaleFactor</c>.</para>
/// <para><b>Permissions.</b> Reading controls and posting input need Accessibility permission; window titles of other
/// apps (kCGWindowName) and every capture need Screen Recording permission. The constructor never prompts; call
/// <see cref="RequestAccessibility"/> / <see cref="RequestScreenRecordingAccess"/> to show the system prompts. When
/// macOS blocks a call the <see cref="DesktopAutomationException"/> says which permission is missing and where to
/// grant it. No other limits are imposed: no element caps, process lists, or coordinate clamping.</para>
/// <para><b>Elevation.</b> macOS has no per-process integrity levels, so <see cref="WindowInfo.IsElevated"/> is always
/// false; the equivalent barriers are the permissions above and secure input / the login window.</para>
/// <para><b>Mac Catalyst.</b> The same frameworks and exports exist in Catalyst processes, so the adapter also runs there.
/// The host app must not be App Sandboxed (the sandbox blocks AX access to other apps and posting events), and the
/// permissions are granted to the app bundle itself.</para>
/// <para>Thread-safe: public members serialize on one lock. Dispose releases every retained CF/AX reference.</para>
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
public sealed partial class MacDesktopAutomation : IDesktopAutomation, IDisposable
{
    private readonly object _gate = new();
    private readonly CfStringCache _names = new();
    private nint _systemWide;
    private nint _controlAttributeArray;
    private nint _walkAttributeArray;
    /// <summary>AX IPC calls made by this object (each one a round trip to the target app).</summary>
    private long _axCalls;
    private MacWalkStats? _lastWalkStats;
    private readonly Dictionary<int, nint> _appElements = new();
    private readonly Dictionary<long, AxWindowEntry> _axWindows = new();
    private readonly Dictionary<long, Dictionary<string, RetainedElement>> _controlMaps = new();
    private readonly Dictionary<string, RetainedElement> _referenceElements = new(StringComparer.Ordinal);
    private long _referenceSerial;
    private string? _lastWalkTruncationReason;

    /// <summary>A hung app costs this long per AX call instead of the 6 s system default.</summary>
    private const float MessagingTimeoutSeconds = 2.0f;
    private bool _disposed;

    /// <summary>A +1 AX element this object owns, with the role it had when observed.</summary>
    private sealed class RetainedElement(nint element, string? role, string? fingerprint)
    {
        public nint Element { get; set; } = element;
        public string? Role { get; set; } = role;
        /// <summary>Identity recorded when the key was issued (<see cref="AxFingerprint"/>).</summary>
        public string? Fingerprint { get; } = fingerprint;
    }

    private readonly record struct AxWindowEntry(int Pid, nint Element);

    /// <summary>Creates the adapter. Does not prompt for any permission.</summary>
    /// <exception cref="PlatformNotSupportedException">Not running on macOS or Mac Catalyst.</exception>
    public MacDesktopAutomation()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("MacDesktopAutomation runs only on macOS (including Mac Catalyst processes).");
    }

    /// <summary>True when this process has Accessibility permission (AXIsProcessTrusted).</summary>
    public static bool IsAccessibilityTrusted => AX.AXIsProcessTrusted();

    /// <summary>Shows the system Accessibility prompt when permission is missing (AXIsProcessTrustedWithOptions with kAXTrustedCheckOptionPrompt). Returns whether the process is trusted now.</summary>
    public static bool RequestAccessibility()
    {
        // kAXTrustedCheckOptionPrompt is CFSTR("AXTrustedCheckOptionPrompt").
        using var key = new CfRef(Cf.CreateString("AXTrustedCheckOptionPrompt"));
        using var options = new CfRef(Cf.CreateDictionary(key.Value, CF.True));
        return AX.AXIsProcessTrustedWithOptions(options.Value);
    }

    /// <summary>True when this process may capture the screen (CGPreflightScreenCaptureAccess).</summary>
    public static bool HasScreenRecordingAccess => CG.CGPreflightScreenCaptureAccess();

    /// <summary>Shows the system Screen Recording prompt when access is missing (CGRequestScreenCaptureAccess). Returns whether access is granted now; macOS usually requires a relaunch after granting.</summary>
    public static bool RequestScreenRecordingAccess() => CG.CGRequestScreenCaptureAccess();

    /// <summary>True when the login window or screen saver owns the display (CGSessionCopyCurrentDictionary "CGSSessionScreenIsLocked").</summary>
    public static bool IsScreenLocked
    {
        get
        {
            using var session = new CfRef(CG.CGSessionCopyCurrentDictionary());
            if (session.IsNull) return false;
            using var key = new CfRef(Cf.CreateString("CGSSessionScreenIsLocked"));
            return Cf.ToBool(CF.CFDictionaryGetValue(session.Value, key.Value)) == true;
        }
    }

    // ---------------------------------------------------------------- shared plumbing

    /// <summary>The system-wide element; its messaging timeout is the process-wide AX default.</summary>
    private nint SystemWide
    {
        get
        {
            if (_systemWide != 0) return _systemWide;
            _systemWide = AX.AXUIElementCreateSystemWide();
            if (_systemWide != 0) AX.AXUIElementSetMessagingTimeout(_systemWide, MessagingTimeoutSeconds);
            return _systemWide;
        }
    }

    /// <summary>True when the most recent <see cref="GetControls"/> stopped early (MaxCount or Timeout).</summary>
    public bool LastWalkWasTruncated => _lastWalkTruncationReason is not null;

    /// <summary>Why the most recent walk stopped early; null when it completed.</summary>
    public string? LastWalkTruncationReason => _lastWalkTruncationReason;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void EnsureUnlocked()
    {
        if (IsScreenLocked) throw new DesktopAutomationException(MacMessages.LockedHint, FailureCategories.AuthenticationRequired);
    }

    /// <summary>Throws the Accessibility hint (or the locked-screen message) when AX calls cannot work.</summary>
    private static void EnsureAccessibility()
    {
        EnsureUnlocked();
        if (!AX.AXIsProcessTrusted()) throw new DesktopAutomationException(MacMessages.AccessibilityHint, FailureCategories.Permission);
    }

    /// <summary>Maps an AXError to a categorized, user-facing exception.</summary>
    private static DesktopAutomationException AxFailure(int error, string what) => error switch
    {
        AX.kAXErrorAPIDisabled => new DesktopAutomationException($"{what} failed: {MacMessages.AccessibilityHint}", FailureCategories.Permission),
        AX.kAXErrorInvalidUIElement => new DesktopAutomationException($"{what} failed: the element no longer exists. List the controls again.", FailureCategories.ControlGone),
        AX.kAXErrorCannotComplete => new DesktopAutomationException($"{what} failed: the application did not respond (it may be busy, showing a modal dialog, or quitting). Wait and check its state before retrying.", FailureCategories.AppBusy),
        AX.kAXErrorAttributeUnsupported => new DesktopAutomationException($"{what} failed: the element does not support that attribute.", FailureCategories.Unsupported),
        AX.kAXErrorActionUnsupported => new DesktopAutomationException($"{what} failed: the element does not support that action.", FailureCategories.Unsupported),
        AX.kAXErrorNotImplemented => new DesktopAutomationException($"{what} failed: the application does not implement that accessibility call.", FailureCategories.Unsupported),
        AX.kAXErrorIllegalArgument => new DesktopAutomationException($"{what} failed: the application rejected the value.", FailureCategories.OsRefused),
        _ => new DesktopAutomationException($"{what} failed with AXError {error}.", FailureCategories.OsRefused),
    };

    private static bool IsUnsupported(int error) =>
        error is AX.kAXErrorAttributeUnsupported or AX.kAXErrorActionUnsupported or AX.kAXErrorNotImplemented or AX.kAXErrorNoValue;

    /// <summary>The cached application element for a pid (borrowed; released in Dispose).</summary>
    private nint GetAppElement(int pid)
    {
        if (_appElements.TryGetValue(pid, out var app)) return app;
        _ = SystemWide; // sets the global timeout before the first call to this app
        app = AX.AXUIElementCreateApplication(pid);
        if (app != 0)
        {
            AX.AXUIElementSetMessagingTimeout(app, MessagingTimeoutSeconds);
            _appElements[pid] = app;
        }
        return app;
    }

    /// <summary>+1 attribute value or 0.</summary>
    private nint CopyAttribute(nint element, string name, out int error)
    {
        _axCalls++;
        error = AX.AXUIElementCopyAttributeValue(element, _names[name], out var value);
        if (error != AX.kAXErrorSuccess)
        {
            CF.Release(value);
            return 0;
        }
        return value;
    }

    private nint CopyAttribute(nint element, string name) => CopyAttribute(element, name, out _);

    private string? ReadString(nint element, string name)
    {
        using var v = new CfRef(CopyAttribute(element, name));
        return Cf.ToStringOrNull(v.Value);
    }

    private bool? ReadBool(nint element, string name)
    {
        using var v = new CfRef(CopyAttribute(element, name));
        return Cf.ToBool(v.Value);
    }

    /// <summary>kAXPositionAttribute + kAXSizeAttribute as a rect in points.</summary>
    private RectD? ReadFrame(nint element)
    {
        using var pos = new CfRef(CopyAttribute(element, "AXPosition"));
        using var size = new CfRef(CopyAttribute(element, "AXSize"));
        var p = Cf.ToPoint(pos.Value);
        var s = Cf.ToSize(size.Value);
        return p is { } pp && s is { } ss ? new RectD(pp.X, pp.Y, ss.Width, ss.Height) : null;
    }

    /// <summary>+1 AX element attribute value (only when it really is an AXUIElement) or 0.</summary>
    private nint CopyElementAttribute(nint element, string name)
    {
        var v = CopyAttribute(element, name);
        if (v != 0 && !Cf.Is(v, Cf.AxElementType))
        {
            CF.Release(v);
            return 0;
        }
        return v;
    }

    /// <summary>Children (kAXChildrenAttribute, falling back to kAXVisibleChildrenAttribute), each +1.</summary>
    private List<nint> CopyChildren(nint element)
    {
        using (var children = new CfRef(CopyAttribute(element, "AXChildren")))
        {
            if (Cf.Is(children.Value, Cf.ArrayType)) return Cf.RetainArrayItems(children.Value);
        }
        using var visible = new CfRef(CopyAttribute(element, "AXVisibleChildren"));
        return Cf.RetainArrayItems(visible.Value);
    }

    private bool IsSettable(nint element, string name)
    {
        _axCalls++;
        return AX.AXUIElementIsAttributeSettable(element, _names[name], out var settable) == AX.kAXErrorSuccess && settable;
    }

    private List<string> ReadActionNames(nint element)
    {
        _axCalls++;
        if (AX.AXUIElementCopyActionNames(element, out var names) != AX.kAXErrorSuccess)
        {
            CF.Release(names);
            return [];
        }
        using var owned = new CfRef(names);
        return Cf.ToStringList(owned.Value);
    }

    private int SetBool(nint element, string name, bool value)
    {
        _axCalls++;
        return AX.AXUIElementSetAttributeValue(element, _names[name], value ? CF.True : CF.False);
    }

    private int Perform(nint element, string action)
    {
        _axCalls++;
        return AX.AXUIElementPerformAction(element, _names[action]);
    }

    /// <summary>The element's live role, or null when it is gone.</summary>
    private string? ReadRoleIfAlive(nint element)
    {
        var role = CopyAttribute(element, "AXRole", out var error);
        using var owned = new CfRef(role);
        if (error == AX.kAXErrorAPIDisabled) throw AxFailure(error, "Reading the element");
        return error == AX.kAXErrorSuccess ? Cf.ToStringOrNull(role) ?? string.Empty : null;
    }

    private static uint? ToWindowId(long handle) => handle is > 0 and <= uint.MaxValue ? (uint)handle : null;

    // ---------------------------------------------------------------- dispose

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var map in _controlMaps.Values) ReleaseMap(map);
            _controlMaps.Clear();
            ReleaseMap(_referenceElements);
            foreach (var w in _axWindows.Values) CF.Release(w.Element);
            _axWindows.Clear();
            foreach (var app in _appElements.Values) CF.Release(app);
            _appElements.Clear();
            CF.Release(_controlAttributeArray);
            _controlAttributeArray = 0;
            CF.Release(_walkAttributeArray);
            _walkAttributeArray = 0;
            CF.Release(_systemWide);
            _systemWide = 0;
            _names.Dispose();
        }
    }

    private static void ReleaseMap(Dictionary<string, RetainedElement> map)
    {
        foreach (var e in map.Values) CF.Release(e.Element);
        map.Clear();
    }
}
