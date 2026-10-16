using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding;

/// <summary>Outcome of <see cref="CodingWorkspacePolicy"/>: the normalized physical root, or why the path is refused.</summary>
public sealed record CodingWorkspacePathCheck(bool IsValid, string? NormalizedPath, string? Error)
{
    public static CodingWorkspacePathCheck Refuse(string error) => new(false, null, error);
}

/// <summary>
/// Which folders a governed host may accept as a coding workspace. A workspace becomes the file root
/// for every coding turn in a conversation, so it must be a real repository the user works in, never
/// a system, data, or home folder:
/// <list type="bullet">
/// <item>an absolute local path with no <c>..</c> segment, not UNC or a device path;</item>
/// <item>an existing directory on a local fixed drive (Windows), after resolving links;</item>
/// <item>not a drive or filesystem root, not the user's home folder itself or one of its ancestors;</item>
/// <item>not inside the platform's system or installed-program folders (Windows, Program Files and the system
/// folder; macOS /System, /Library, /Applications, /usr, /bin, /etc, /var; Linux /usr, /bin, /lib, /etc, /var and
/// the like), except a temporary area under one of them (macOS /private/var/folders, /var/tmp);</item>
/// <item>not equal to, inside, or an ancestor of any host-protected data root (profile data, app data), and not inside the install folder;</item>
/// <item>inside a git work tree (<c>git rev-parse --is-inside-work-tree</c> is <c>true</c>).</item>
/// </list>
/// </summary>
public static class CodingWorkspacePolicy
{
    public static async Task<CodingWorkspacePathCheck> ValidateAsync(string? path, IEnumerable<string> protectedRoots,
        CancellationToken ct = default, IEnumerable<string>? installRoots = null)
    {
        var check = ValidateShape(path, protectedRoots, installRoots);
        if (!check.IsValid) return check;
        var (exit, stdout, stderr) = await GitProcess.RunAsync(check.NormalizedPath!, ["rev-parse", "--is-inside-work-tree"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (exit != 0 || !string.Equals(stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return CodingWorkspacePathCheck.Refuse($"'{check.NormalizedPath}' is not a git work tree" + (exit == -1 ? " (git could not be started: " + GitProcess.ErrorText(exit, stdout, stderr) + ")." : "."));
        return check;
    }

    /// <summary>Every rule except the git check: cheap enough to re-run whenever a bound workspace is used.</summary>
    /// <param name="protectedRoots">Data roots: refused when equal to, inside, or containing the workspace.</param>
    /// <param name="installRoots">Program folders: refused when equal to or inside (a repository may contain a build output).</param>
    public static CodingWorkspacePathCheck ValidateShape(string? path, IEnumerable<string> protectedRoots, IEnumerable<string>? installRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return CodingWorkspacePathCheck.Refuse("A repository folder is required.");
        var raw = path.Trim().Trim('"');
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            return CodingWorkspacePathCheck.Refuse("Network (UNC) and device paths cannot be coding workspaces.");
        if (!Path.IsPathFullyQualified(raw)) return CodingWorkspacePathCheck.Refuse("Use the full path of the repository folder.");
        if (raw.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is ".." or "."))
            return CodingWorkspacePathCheck.Refuse("The folder path cannot contain '.' or '..' segments.");

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (!Directory.Exists(full)) return CodingWorkspacePathCheck.Refuse($"The folder '{full}' does not exist.");
            full = Path.TrimEndingDirectorySeparator(SandboxPathResolver.ResolvePhysicalPath(full));
            // SUBST drives, mapped aliases and links resolve to the path the volume really serves.
            if (WindowsFileIdentity.TryCanonicalize(full) is { } canonical)
            {
                if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
                    return CodingWorkspacePathCheck.Refuse("Network (UNC) and device paths cannot be coding workspaces.");
                full = canonical;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return CodingWorkspacePathCheck.Refuse("The folder path is not valid: " + ex.Message);
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(Path.TrimEndingDirectorySeparator(root), full, Comparison))
            return CodingWorkspacePathCheck.Refuse("A drive or filesystem root cannot be a coding workspace.");
        if (OperatingSystem.IsWindows())
        {
            DriveType type;
            try { type = new DriveInfo(root).DriveType; }
            catch (ArgumentException) { type = DriveType.Unknown; }
            if (type != DriveType.Fixed) return CodingWorkspacePathCheck.Refuse("Coding workspaces must be on a local fixed drive.");
        }

        var home = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (home is not null && (Same(full, home) || IsUnder(home, full)))
            return CodingWorkspacePathCheck.Refuse("Your home folder (or a folder containing it) cannot be a coding workspace; choose the repository folder.");

        if (IsSystemFolder(full))
            return CodingWorkspacePathCheck.Refuse(SystemFolderRefusal);

        // Normalized once: the identity check below must know which normalized root is a data root.
        var dataRoots = protectedRoots.Select(Normalize).OfType<string>().ToList();
        var programRoots = (installRoots ?? []).Select(Normalize).OfType<string>().ToList();
        foreach (var protectedRoot in dataRoots)
            if (Same(full, protectedRoot) || IsUnder(full, protectedRoot) || IsUnder(protectedRoot, full))
                return CodingWorkspacePathCheck.Refuse("Application and profile data folders cannot be coding workspaces.");

        foreach (var installRoot in programRoots)
            if (Same(full, installRoot) || IsUnder(full, installRoot))
                return CodingWorkspacePathCheck.Refuse("The application's own folder cannot be a coding workspace.");

        // Identity check on top of paths: compare by volume serial + file id, so any remaining alias of a
        // protected folder (or of one of its ancestors) is caught even when its spelling differs.
        if (WindowsFileIdentity.IsAvailable)
        {
            var workspaceChain = WindowsFileIdentity.Chain(full);
            foreach (var (protectedRoot, isData) in dataRoots.Select(r => (r, true)).Concat(programRoots.Select(r => (r, false))))
            {
                if (WindowsFileIdentity.Get(protectedRoot) is not { } protectedId) continue;
                // The workspace is the protected folder or inside it.
                if (workspaceChain.Contains(protectedId))
                    return CodingWorkspacePathCheck.Refuse("Application and profile data folders cannot be coding workspaces.");
                // The workspace contains a protected data folder.
                if (isData && workspaceChain.Count > 0
                    && WindowsFileIdentity.Chain(protectedRoot).Skip(1).Contains(workspaceChain[0]))
                    return CodingWorkspacePathCheck.Refuse("Application and profile data folders cannot be coding workspaces.");
            }
        }

        return new(true, full, null);
    }

    /// <summary>The refusal for a folder inside an operating-system or installed-program location on this platform.</summary>
    public static string SystemFolderRefusal { get; } =
        OperatingSystem.IsWindows() ? "System folders (Windows, Program Files) cannot be coding workspaces."
        : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? "System folders (/System, /Library, /Applications, /usr, /bin) cannot be coding workspaces."
            : "System folders (/usr, /bin, /etc, /var) cannot be coding workspaces.";

    private static bool IsSystemFolder(string full)
    {
        foreach (var (root, userWritable) in SystemRoots())
        {
            if (!Same(full, root) && !IsUnder(full, root)) continue;
            // A per-user or shared temporary area under a system root (macOS /private/var/folders and /private/tmp,
            // Linux /var/tmp) holds ordinary user files: a repository there is not a system folder.
            if (userWritable.Any(allowed => Same(full, allowed) || IsUnder(full, allowed))) continue;
            return true;
        }
        return false;
    }

    private static IEnumerable<(string Root, string[] UserWritable)> SystemRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                         Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                     })
                if (Normalize(Environment.GetFolderPath(folder)) is { } path) yield return (path, []);
            yield break;
        }

        // Unix spellings are listed as written and as resolved: on macOS /etc, /var and /tmp are links into /private,
        // and the workspace path compared here is already the resolved one.
        string[] roots = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? ["/System", "/Library", "/Applications", "/usr", "/bin", "/sbin", "/etc", "/var", "/private/etc", "/private/var", "/cores", "/dev"]
            : ["/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/var", "/boot", "/proc", "/sys", "/dev", "/run", "/snap"];
        string[] userWritable = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? ["/var/folders", "/private/var/folders", "/var/tmp", "/private/var/tmp"]
            : ["/var/tmp"];
        var allowed = userWritable.Concat(userWritable.Select(Normalize).OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var root in roots)
        {
            yield return (root, allowed);
            if (Normalize(root) is { } resolved && !Same(resolved, root)) yield return (resolved, allowed);
        }
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            try { full = Path.TrimEndingDirectorySeparator(SandboxPathResolver.ResolvePhysicalPath(full)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (WindowsFileIdentity.TryCanonicalize(full) is { } canonical && !canonical.StartsWith(@"\\", StringComparison.Ordinal))
                full = canonical;
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    private static bool IsUnder(string child, string parent)
        => child.Length > parent.Length + 1
           && child.StartsWith(parent, Comparison)
           && (child[parent.Length] == Path.DirectorySeparatorChar || child[parent.Length] == Path.AltDirectorySeparatorChar
               || parent.EndsWith(Path.DirectorySeparatorChar));
}

/// <summary>
/// Windows file identity: the canonical path a handle really refers to (<c>GetFinalPathNameByHandle</c>, which
/// sees through SUBST drives, mapped aliases, junctions and symlinks) and the volume serial + file index
/// (<c>GetFileInformationByHandle</c>). No-ops on other platforms.
/// </summary>
internal static class WindowsFileIdentity
{
    public readonly record struct FileId(uint VolumeSerial, ulong FileIndex);

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static string? TryCanonicalize(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = Open(path);
        if (handle is null || handle.IsInvalid) return null;
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return null;
        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        return Path.TrimEndingDirectorySeparator(final);
    }

    public static FileId? Get(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = Open(path);
        if (handle is null || handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return new FileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    /// <summary>Identities of the folder and each existing ancestor, the folder first.</summary>
    public static List<FileId> Chain(string path)
    {
        var chain = new List<FileId>();
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (Get(dir.FullName) is { } id) chain.Add(id);
        return chain;
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? Open(string path)
    {
        try
        {
            var handle = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 0x7 /* share read|write|delete */, IntPtr.Zero,
                3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
            return handle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Text.StringBuilder path, uint length, uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);
}
