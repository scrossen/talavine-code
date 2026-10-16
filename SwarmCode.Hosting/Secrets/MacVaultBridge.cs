using System.Net.Sockets;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Secrets;

/// <summary>SwarmCode's host-owned paths and peer policy for its bundled Mac vault helper.</summary>
public static class MacVaultBridge
{
    public const string RelativeHelperPath = "Resources/SwarmCodeBridge/swarmcode";
    public static string? ContentsDirectory(string executableOrDirectory)
    {
        var directory = File.Exists(executableOrDirectory) ? Path.GetDirectoryName(executableOrDirectory) : executableOrDirectory;
        for (var current = new DirectoryInfo(Path.GetFullPath(directory!)); current is not null; current = current.Parent)
            if (current.Name == "Contents" && current.Parent?.Name.EndsWith(".app", StringComparison.Ordinal) == true)
                return current.FullName;
        return null;
    }
    public static string? BundledHelper(string executableOrDirectory)
        => ContentsDirectory(executableOrDirectory) is { } contents ? Path.Combine(contents, RelativeHelperPath) : null;
    public static bool IsOwnedAppExecutable(string contents, string executable)
    {
        var plist = Path.Combine(contents, "Info.plist");
        if (!IsUnlinkedOwnedFile(contents, plist)) return false;
        var primary = AppleBundleExecutable.Read(plist);
        if (string.IsNullOrWhiteSpace(primary) || primary is "." or ".." || Path.IsPathRooted(primary)
            || primary.IndexOfAny(['/', '\\']) >= 0) return false;
        return string.Equals(Path.GetFullPath(executable), Path.Combine(contents, "MacOS", primary), StringComparison.Ordinal)
            && IsUnlinkedOwnedFile(contents, executable);
    }
    internal static bool IsUnlinkedOwnedFile(string contents, string file)
    {
        try
        {
            var root = Path.GetFullPath(contents);
            var relative = Path.GetRelativePath(root, Path.GetFullPath(file));
            if (relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(file)) return false;
            // Include the app and Contents boundary, not just the leaf. Link parents must not escape the owned bundle.
            if ((File.GetAttributes(Path.GetDirectoryName(root)!) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            var current = root;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static IOsKeyStore CreateHelperKeyStore(SwarmCodeDataPaths paths)
    {
        var contents = ContentsDirectory(Environment.ProcessPath ?? AppContext.BaseDirectory);
        if (contents is null) return new NoOpKeyStore();
        return new BrokeredKeyStore(paths.RootDirectory, (socket, endpoint) => IsOwnedAppExecutable(contents, endpoint.ExecutablePath)
            && KeyStoreBrokerProtocol.IsPeerExecutable(socket, endpoint.ExecutablePath, endpoint.ProcessId));
    }
    public static Func<Socket, bool> HelperPeerPolicy(string helper)
        => socket => ContentsDirectory(helper) is { } contents && IsUnlinkedOwnedFile(contents, helper)
            && string.Equals(Path.GetFullPath(helper), Path.Combine(contents, RelativeHelperPath.Replace('/', Path.DirectorySeparatorChar)), StringComparison.Ordinal)
            && KeyStoreBrokerProtocol.IsPeerExecutable(socket, helper);
}
