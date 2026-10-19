using System.Diagnostics;

namespace SwarmCore.Coding.Computer;

/// <summary>Discovers Chrome's consent-based connection without copying cookies or relaunching the profile.</summary>
public static class PersonalChromeConnection
{
    public const string SetupUrl = "chrome://inspect/#remote-debugging";

    public static string DefaultUserDataDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data")
        : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Google", "Chrome")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "google-chrome");

    public static string GetEndpoint(string? userDataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(userDataDirectory) ? DefaultUserDataDirectory : Path.GetFullPath(userDataDirectory);
        try { return ParseActivePort(File.ReadAllText(Path.Combine(root, "DevToolsActivePort"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, unreadable or invalid discovery data is an environmental prerequisite, not a code defect.
            // Keep that distinction through the API/native tool error text and self-improvement capture.
            throw new IOException("Open your regular Chrome (version 144 or newer), go to " + SetupUrl +
                ", enable remote debugging, then connect again and allow Chrome's connection prompt. Your existing tabs and sign-ins stay in Chrome.", ex);
        }
    }

    public static string ParseActivePort(string content)
    {
        var lines = content.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length != 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535
            || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)
            || lines[1]["/devtools/browser/".Length..] is not { Length: > 0 } id
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid Chrome connection file.");
        return $"ws://127.0.0.1:{port}{lines[1]}";
    }

    public static void OpenSetup()
    {
        var executable = SharedChromeProcess.FindChrome();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(SetupUrl);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Chrome could not open.");
    }
}
