namespace SwarmCore.Abstractions.Documents;

/// <summary>Read-only local document grants. A file grant never admits its siblings.</summary>
public static class LocalDocumentAccess
{
    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose an absolute file or folder path.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string? ResolveFile(string sourcePath, string relativePath, bool singleFile)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)) return null;
        var root = NormalizePath(sourcePath);
        if (singleFile)
            return PathComparer.Equals(relativePath, Path.GetFileName(root)) ? root : null;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, full);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative) ? null : full;
    }

    public static IEnumerable<string> EnumerateFiles(string sourcePath, bool singleFile, bool includeSubfolders)
    {
        var root = NormalizePath(sourcePath);
        if (singleFile)
        {
            if (File.Exists(root)) yield return root;
            yield break;
        }
        // Cloud file-provider entries may also carry ReparsePoint. Skip actual
        // symbolic links/junctions, not hydrated OneDrive files or directories.
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null) continue;
                if (entry is DirectoryInfo directory)
                {
                    if (includeSubfolders) pending.Push(directory);
                }
                else yield return entry.FullName;
            }
        }
    }
}
