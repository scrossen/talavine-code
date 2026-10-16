using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Existing hard links must not turn a writable workspace path into writes
/// to another resource. Unix sockets/devices likewise cannot become IPC escape routes.</summary>
internal static class MissionFileAliases
{
    internal static MissionAuthority Protect(MissionAuthority authority)
    {
        var exclusions = authority.Exclusions.ToList();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var grant in authority.Grants.Where(g => (g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory) && (g.Effects & (MissionEffect.Modify | MissionEffect.Remove)) != 0))
        {
            var path = grant.Resource.Identity;
            var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                : File.Exists(path) ? [path] : Array.Empty<string>();
            foreach (var file in files)
            {
                if (!seen.Add(file)) continue;
                var (links, regular) = Inspect(file);
                if (links > 1 || !regular)
                    exclusions.Add(new(new(MissionResourceKind.File, file), MissionEffect.Inspect | MissionEffect.Modify | MissionEffect.Remove, "kernel-file-identity", true));
            }
        }
        return authority with { Exclusions = exclusions };
    }
    private static (uint Links, bool Regular) Inspect(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (statx(-100, path, 0x100, 5, buffer) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((Marshal.ReadInt32(buffer) & 5) != 5) throw new IOException("Filesystem does not expose link/type identity.");
                return ((uint)Marshal.ReadInt32(buffer, 16), (Marshal.ReadInt16(buffer, 28) & 0xf000) == 0x8000);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (OperatingSystem.IsWindows())
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = Marshal.AllocHGlobal(52);
            try
            {
                if (!GetFileInformationByHandle(handle, buffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return ((uint)Marshal.ReadInt32(buffer, 40), true);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return (1, true);
    }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, nint buffer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, nint buffer);
}
