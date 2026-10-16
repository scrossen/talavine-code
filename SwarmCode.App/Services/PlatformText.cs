namespace SwarmCode.App.Services;

/// <summary>Wording that differs by platform, so a Mac user is not told to press Ctrl or use a Windows desktop.</summary>
public static class PlatformText
{
    public static string SettingsShortcut => OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() ? "\u2318," : "Ctrl+,";

    public static string DesktopControl => OperatingSystem.IsWindows()
        ? "Desktop control uses the current Windows desktop."
        : "Desktop window control is available on Windows; on this computer the agent uses Chrome only.";
}
