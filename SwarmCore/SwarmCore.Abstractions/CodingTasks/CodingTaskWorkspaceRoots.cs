using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.CodingTasks;

/// <summary>
/// Where isolated coding-task worktrees live. A Windows app package's virtualized storage
/// (<c>…\Packages\&lt;package&gt;\LocalCache\…</c>) is never a usable root: when the host runs inside another app's
/// package context (for example started from a packaged desktop app), writes under AppData are redirected there,
/// so Git and the host see different paths and every run fails its workspace identity check.
/// </summary>
public static partial class CodingTaskWorkspaceRoots
{
    /// <summary>The default root: the user's real local application data folder (not a package redirection), then Talavine\coding-workspaces.</summary>
    public static string DefaultRoot() => Path.Combine(RealLocalApplicationData(), "Talavine", "coding-workspaces");

    /// <summary>True when the path lies in a Windows app package's virtualized storage.</summary>
    public static bool IsUnderPackageCache(string? path)
        => !string.IsNullOrWhiteSpace(path) && PackageCache().IsMatch(path);

    /// <summary>The refusal shown when a root resolves into package storage.</summary>
    public static string PackageCacheRefusal(string root) =>
        $"The coding workspace root resolves into an app package's virtualized storage ({root}), where Git and Talavine see different files. "
        + "Set CodingTasks:WorkspaceRoot to a regular folder (for example under your user profile) and retry.";

    private static string RealLocalApplicationData()
    {
        string? path = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // KF_FLAG_NO_PACKAGE_REDIRECTION: the folder itself, never a packaged process's redirected copy.
                if (SHGetKnownFolderPath(LocalAppDataFolder, 0x00010000, IntPtr.Zero, out var buffer) == 0)
                {
                    try { path = Marshal.PtrToStringUni(buffer); }
                    finally { Marshal.FreeCoTaskMem(buffer); }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                path = null;
            }
        }
        if (string.IsNullOrWhiteSpace(path) || IsUnderPackageCache(path))
            path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if ((string.IsNullOrWhiteSpace(path) || IsUnderPackageCache(path))
            && Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile)
            path = OperatingSystem.IsWindows() ? Path.Combine(profile, "AppData", "Local") : Path.Combine(profile, ".local", "share");
        return string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path;
    }

    private static readonly Guid LocalAppDataFolder = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [GeneratedRegex(@"[\\/]Packages[\\/][^\\/]+[\\/]LocalCache(?:[\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageCache();
}
