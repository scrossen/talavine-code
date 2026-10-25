using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Native;

// Struct layouts match the 64-bit Apple ABI: CGFloat is double, CFIndex is long (nint),
// CFTypeID is unsigned long (nuint), Boolean/bool is one byte.

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;
    public double Y;
    public CGPoint(double x, double y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public CGPoint Origin;
    public CGSize Size;

    public CGRect(double x, double y, double width, double height)
    {
        Origin = new CGPoint(x, y);
        Size = new CGSize { Width = width, Height = height };
    }

    /// <summary>CGRectNull: origin at +infinity, zero size.</summary>
    public static CGRect Null => new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);

    public RectD ToRectD() => new(Origin.X, Origin.Y, Size.Width, Size.Height);
    public static CGRect From(RectD r) => new(r.X, r.Y, r.Width, r.Height);
}

[StructLayout(LayoutKind.Sequential)]
internal struct CFRange
{
    public nint Location;
    public nint Length;
    public CFRange(nint location, nint length) { Location = location; Length = length; }
}

internal static class MacLibraries
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    public const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
}

/// <summary>CoreFoundation. Create/Copy functions return +1 references the caller must CFRelease; Get functions do not.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal static unsafe partial class CF
{
    // CFNumberType (CFIndex)
    public const nint kCFNumberSInt64Type = 4;
    public const nint kCFNumberDoubleType = 13;

    /// <summary>CFRelease crashes on NULL, so this wrapper ignores zero.</summary>
    public static void Release(nint cf)
    {
        if (cf != 0) CFRelease(cf);
    }

    [LibraryImport(MacLibraries.CoreFoundation)] public static partial void CFRelease(nint cf);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFRetain(nint cf);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nuint CFGetTypeID(nint cf);
    [LibraryImport(MacLibraries.CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CFEqual(nint cf1, nint cf2);

    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nuint CFStringGetTypeID();
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nuint CFNumberGetTypeID();
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nuint CFBooleanGetTypeID();
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nuint CFArrayGetTypeID();
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFStringCreateWithCharacters(nint alloc, char* chars, nint numChars);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFStringGetLength(nint str);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial void CFStringGetCharacters(nint str, CFRange range, char* buffer);

    [LibraryImport(MacLibraries.CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CFNumberGetValue(nint number, nint theType, void* valuePtr);
    [LibraryImport(MacLibraries.CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CFNumberIsFloatType(nint number);
    [LibraryImport(MacLibraries.CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CFBooleanGetValue(nint boolean);

    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFArrayCreate(nint alloc, nint* values, nint numValues, nint callBacks);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFArrayGetCount(nint array);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFArrayGetValueAtIndex(nint array, nint idx);

    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFDictionaryCreate(nint alloc, nint* keys, nint* values, nint numValues, nint keyCallBacks, nint valueCallBacks);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFDictionaryGetValue(nint dict, nint key);

    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFDataCreateMutable(nint alloc, nint capacity);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial byte* CFDataGetBytePtr(nint data);
    [LibraryImport(MacLibraries.CoreFoundation)] public static partial nint CFDataGetLength(nint data);

    private static nint s_library;
    private static nint s_true;
    private static nint s_false;
    private static nint s_dictKeyCallBacks;
    private static nint s_dictValueCallBacks;
    private static nint s_arrayCallBacks;

    private static nint Library => s_library != 0 ? s_library : (s_library = NativeLibrary.Load(MacLibraries.CoreFoundation));

    /// <summary>kCFBooleanTrue: the export is a <c>const CFBooleanRef</c> variable, so read the pointer it holds. Never released.</summary>
    public static nint True => s_true != 0 ? s_true : (s_true = Marshal.ReadIntPtr(NativeLibrary.GetExport(Library, "kCFBooleanTrue")));
    /// <summary>kCFBooleanFalse (see <see cref="True"/>).</summary>
    public static nint False => s_false != 0 ? s_false : (s_false = Marshal.ReadIntPtr(NativeLibrary.GetExport(Library, "kCFBooleanFalse")));
    /// <summary>&amp;kCFTypeDictionaryKeyCallBacks: the export is the struct itself, so its address is the pointer CF expects.</summary>
    public static nint TypeDictionaryKeyCallBacks => s_dictKeyCallBacks != 0 ? s_dictKeyCallBacks : (s_dictKeyCallBacks = NativeLibrary.GetExport(Library, "kCFTypeDictionaryKeyCallBacks"));
    /// <summary>&amp;kCFTypeDictionaryValueCallBacks.</summary>
    public static nint TypeDictionaryValueCallBacks => s_dictValueCallBacks != 0 ? s_dictValueCallBacks : (s_dictValueCallBacks = NativeLibrary.GetExport(Library, "kCFTypeDictionaryValueCallBacks"));
    /// <summary>&amp;kCFTypeArrayCallBacks.</summary>
    public static nint TypeArrayCallBacks => s_arrayCallBacks != 0 ? s_arrayCallBacks : (s_arrayCallBacks = NativeLibrary.GetExport(Library, "kCFTypeArrayCallBacks"));
}

/// <summary>CoreGraphics: displays, window list, capture, session state, and synthetic events.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal static unsafe partial class CG
{
    // CGWindowListOption (uint32_t)
    public const uint kCGWindowListOptionAll = 0;
    public const uint kCGWindowListOptionOnScreenOnly = 1u << 0;
    public const uint kCGWindowListOptionOnScreenAboveWindow = 1u << 1;
    public const uint kCGWindowListOptionOnScreenBelowWindow = 1u << 2;
    public const uint kCGWindowListOptionIncludingWindow = 1u << 3;
    public const uint kCGWindowListExcludeDesktopElements = 1u << 4;
    // CGWindowID (uint32_t)
    public const uint kCGNullWindowID = 0;

    // CGWindowImageOption (uint32_t)
    public const uint kCGWindowImageDefault = 0;
    public const uint kCGWindowImageBoundsIgnoreFraming = 1u << 0;
    public const uint kCGWindowImageShouldBeOpaque = 1u << 1;
    public const uint kCGWindowImageOnlyShadows = 1u << 2;
    public const uint kCGWindowImageBestResolution = 1u << 3;
    public const uint kCGWindowImageNominalResolution = 1u << 4;

    // CGEventType (uint32_t)
    public const uint kCGEventLeftMouseDown = 1;
    public const uint kCGEventLeftMouseUp = 2;
    public const uint kCGEventRightMouseDown = 3;
    public const uint kCGEventRightMouseUp = 4;
    public const uint kCGEventMouseMoved = 5;
    public const uint kCGEventLeftMouseDragged = 6;
    public const uint kCGEventRightMouseDragged = 7;
    public const uint kCGEventKeyDown = 10;
    public const uint kCGEventKeyUp = 11;
    public const uint kCGEventFlagsChanged = 12;
    public const uint kCGEventOtherMouseDown = 25;
    public const uint kCGEventOtherMouseUp = 26;
    public const uint kCGEventOtherMouseDragged = 27;

    // CGMouseButton (uint32_t)
    public const uint kCGMouseButtonLeft = 0;
    public const uint kCGMouseButtonRight = 1;
    public const uint kCGMouseButtonCenter = 2;

    // CGEventTapLocation (uint32_t)
    public const uint kCGHIDEventTap = 0;

    // CGEventField (uint32_t)
    public const uint kCGMouseEventClickState = 1;

    // CGScrollEventUnit (uint32_t)
    public const uint kCGScrollEventUnitPixel = 0;
    public const uint kCGScrollEventUnitLine = 1;

    // CGError (int32_t)
    public const int kCGErrorSuccess = 0;
    public const int kCGErrorFailure = 1000;

    [LibraryImport(MacLibraries.CoreGraphics)] public static partial int CGGetActiveDisplayList(uint maxDisplays, uint* activeDisplays, out uint displayCount);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial uint CGMainDisplayID();
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial CGRect CGDisplayBounds(uint display);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nuint CGDisplayPixelsWide(uint display);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGDisplayCopyDisplayMode(uint display);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nuint CGDisplayModeGetPixelWidth(nint mode);
    /// <summary>NULL-safe release of a CGDisplayModeRef.</summary>
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGDisplayModeRelease(nint mode);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGDisplayCreateImage(uint displayId);

    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGWindowListCreateImage(CGRect screenBounds, uint listOption, uint windowId, uint imageOption);
    [LibraryImport(MacLibraries.CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRect rect);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGSessionCopyCurrentDictionary();

    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nuint CGImageGetWidth(nint image);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nuint CGImageGetHeight(nint image);
    /// <summary>NULL-safe release of a CGImageRef.</summary>
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGImageRelease(nint image);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGColorSpaceCreateDeviceRGB();
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGColorSpaceRelease(nint space);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGContextSetInterpolationQuality(nint context, int quality);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGContextDrawImage(nint context, CGRect rect, nint image);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGBitmapContextCreateImage(nint context);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGContextRelease(nint context);

    [LibraryImport(MacLibraries.CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGPreflightScreenCaptureAccess();
    [LibraryImport(MacLibraries.CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGRequestScreenCaptureAccess();

    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGEventCreate(nint source);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial CGPoint CGEventGetLocation(nint ev);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGEventCreateMouseEvent(nint source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);
    /// <summary>The non-variadic scroll constructor (the variadic CGEventCreateScrollWheelEvent cannot be called safely through P/Invoke on arm64).</summary>
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGEventKeyboardSetUnicodeString(nint ev, nuint stringLength, char* unicodeString);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGEventSetFlags(nint ev, ulong flags);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGEventSetIntegerValueField(nint ev, uint field, long value);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial void CGEventPost(uint tap, nint ev);
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    // CGEventSourceStateID (int32_t)
    public const int kCGEventSourceStateHIDSystemState = 1;
    // CGEventType kCGAnyInputEventType = ~0
    public const uint kCGAnyInputEventType = ~0u;

    /// <summary>Seconds since the last event of a type in a source state. The HID system state includes events this process posts at kCGHIDEventTap.</summary>
    [LibraryImport(MacLibraries.CoreGraphics)] public static partial double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
}

/// <summary>HIServices Accessibility API (exported through the ApplicationServices umbrella).</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal static unsafe partial class AX
{
    // AXError (int32_t)
    public const int kAXErrorSuccess = 0;
    public const int kAXErrorFailure = -25200;
    public const int kAXErrorIllegalArgument = -25201;
    public const int kAXErrorInvalidUIElement = -25202;
    public const int kAXErrorInvalidUIElementObserver = -25203;
    public const int kAXErrorCannotComplete = -25204;
    public const int kAXErrorAttributeUnsupported = -25205;
    public const int kAXErrorActionUnsupported = -25206;
    public const int kAXErrorNotificationUnsupported = -25207;
    public const int kAXErrorNotImplemented = -25208;
    public const int kAXErrorNotificationAlreadyRegistered = -25209;
    public const int kAXErrorNotificationNotRegistered = -25210;
    public const int kAXErrorAPIDisabled = -25211;
    public const int kAXErrorNoValue = -25212;
    public const int kAXErrorParameterizedAttributeUnsupported = -25213;
    public const int kAXErrorNotEnoughPrecision = -25214;

    // AXValueType (uint32_t)
    public const uint kAXValueTypeIllegal = 0;
    public const uint kAXValueTypeCGPoint = 1;
    public const uint kAXValueTypeCGSize = 2;
    public const uint kAXValueTypeCGRect = 3;
    public const uint kAXValueTypeCFRange = 4;
    public const uint kAXValueTypeAXError = 5;

    [LibraryImport(MacLibraries.ApplicationServices)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool AXIsProcessTrusted();
    [LibraryImport(MacLibraries.ApplicationServices)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool AXIsProcessTrustedWithOptions(nint options);

    [LibraryImport(MacLibraries.ApplicationServices)] public static partial nint AXUIElementCreateSystemWide();
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial nint AXUIElementCreateApplication(int pid);
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    /// <summary>options is AXCopyMultipleAttributeOptions (uint32_t); 0 = keep going and put an AXValue(kAXValueTypeAXError) in failed slots.</summary>
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementCopyMultipleAttributeValues(nint element, nint attributes, uint options, out nint values);
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementIsAttributeSettable(nint element, nint attribute, [MarshalAs(UnmanagedType.U1)] out bool settable);
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementCopyActionNames(nint element, out nint names);
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementPerformAction(nint element, nint action);
    /// <remarks>x and y are C <c>float</c>, not CGFloat.</remarks>
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementCopyElementAtPosition(nint application, float x, float y, out nint element);
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementGetPid(nint element, out int pid);
    /// <summary>timeoutInSeconds is C <c>float</c>; on the system-wide element it sets the default for every element.</summary>
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial int AXUIElementSetMessagingTimeout(nint element, float timeoutInSeconds);

    /// <summary>Private but stable and universally used HIServices export mapping an AX window element to its CGWindowID.</summary>
    [LibraryImport(MacLibraries.ApplicationServices, EntryPoint = "_AXUIElementGetWindow")] public static partial int AXUIElementGetWindow(nint element, out uint windowId);

    [LibraryImport(MacLibraries.ApplicationServices)] public static partial nuint AXUIElementGetTypeID();
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial nuint AXValueGetTypeID();
    [LibraryImport(MacLibraries.ApplicationServices)] public static partial uint AXValueGetType(nint value);
    [LibraryImport(MacLibraries.ApplicationServices)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool AXValueGetValue(nint value, uint theType, void* valuePtr);
}

/// <summary>ImageIO PNG encoding.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal static partial class ImageIO
{
    [LibraryImport(MacLibraries.ImageIO)] public static partial nint CGImageDestinationCreateWithData(nint data, nint type, nuint count, nint options);
    [LibraryImport(MacLibraries.ImageIO)] public static partial void CGImageDestinationAddImage(nint dest, nint image, nint properties);
    [LibraryImport(MacLibraries.ImageIO)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool CGImageDestinationFinalize(nint dest);
}
