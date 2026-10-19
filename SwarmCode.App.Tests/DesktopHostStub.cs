namespace SwarmCode.App;

// Portable AppState tests do not construct either native desktop host.
internal static class DesktopHost
{
    public static void LogToFile(string message) { }
}
