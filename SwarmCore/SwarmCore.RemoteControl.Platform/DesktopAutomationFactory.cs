using System.Runtime.Versioning;
using SwarmCore.RemoteControl.Mac;
using SwarmCore.RemoteControl.Windows;

namespace SwarmCore.RemoteControl.Platform;

/// <summary>
/// Chooses the <see cref="IDesktopAutomation"/> for the operating system this process runs on: Windows (UI Automation
/// plus SendInput) or macOS (Accessibility plus Quartz events). macOS includes Mac Catalyst apps (which must not be App Sandboxed). Anything else, including Android and iOS
/// processes, is unsupported.
/// </summary>
public static class DesktopAutomationFactory
{
    /// <summary>The message an unsupported platform reports.</summary>
    public const string UnsupportedMessage = "Desktop control is available on Windows and macOS.";

    /// <summary>True when <see cref="Create"/> can return an adapter for this process.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();

    /// <summary>
    /// Creates the platform adapter now. On macOS, when Accessibility permission is missing, this shows the system
    /// prompt once (the adapter's operations then report what to grant). The caller owns and disposes the result.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Neither Windows nor macOS.</exception>
    public static IDesktopAutomation Create()
    {
        if (OperatingSystem.IsWindows()) return CreateWindows();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) return CreateMac();
        throw new PlatformNotSupportedException(UnsupportedMessage);
    }

    /// <summary>
    /// An adapter that is created on its first use, not now: hosts can list desktop tools at startup without loading
    /// UI Automation, changing process DPI awareness or prompting for macOS permissions until an agent actually looks
    /// at or drives the desktop. <paramref name="unavailableReason"/> is consulted on every first-use attempt; when it
    /// returns text (for example "Computer access is disabled in Settings."), the operation fails with that message
    /// and nothing is created. Disposing the result disposes the adapter if one was created.
    /// </summary>
    public static IDesktopAutomation CreateLazy(Func<string?>? unavailableReason = null, Func<IDesktopAutomation>? create = null)
        => new LazyDesktopAutomation(unavailableReason, create ?? Create);

    [SupportedOSPlatform("windows")]
    private static IDesktopAutomation CreateWindows() => new WindowsDesktopAutomation();

    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("maccatalyst")]
    private static IDesktopAutomation CreateMac()
    {
        if (!MacDesktopAutomation.IsAccessibilityTrusted) MacDesktopAutomation.RequestAccessibility();
        return new MacDesktopAutomation();
    }
}
