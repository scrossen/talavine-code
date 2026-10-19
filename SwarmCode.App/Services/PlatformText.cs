namespace SwarmCode.App.Services;

/// <summary>Wording that differs by platform, so a Mac user is not told to press Ctrl or use a Windows desktop.</summary>
public static class PlatformText
{
    public static string SettingsShortcut => OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() ? "\u2318," : "Ctrl+,";

    public static string DesktopControl => OperatingSystem.IsWindows()
        ? "Desktop control uses the current Windows desktop."
        : OperatingSystem.IsMacOS()
            ? "Native Mac app control requires Screen & System Audio Recording and Accessibility permission in System Settings > Privacy & Security. The first request opens the macOS permission prompt."
            : "Native desktop control is available in the Windows and macOS desktop apps.";
}
