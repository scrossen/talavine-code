namespace SwarmCore.Abstractions.IO;

/// <summary>Resolves one named directory within an explicitly owned parent.</summary>
public static class ContainedDirectoryPath
{
    private static readonly char[] UnsafeNameCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Child(string parent, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathRooted(name) || name.IndexOfAny(UnsafeNameCharacters) >= 0
            || name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("A contained directory name must be one portable path component without aliases.", nameof(name));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var result = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(result), root, Comparison))
            throw new ArgumentException("The directory must be a direct child of its owning parent.", nameof(name));
        return result;
    }

    /// <summary>
    /// Fail before destructive work if a resolver points elsewhere, access cannot
    /// be verified, or the owned tree contains links. This is a preflight, not a
    /// filesystem transaction; callers must recheck after awaiting other work.
    /// </summary>
    public static string ValidateForDeletion(string parent, string name, string resolvedPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var expected = Child(parent, name);
        if (!string.Equals(expected, Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedPath)), Comparison))
            throw new ArgumentException("The resolved directory does not match its owning parent and name.", nameof(resolvedPath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!IsUnlinkedDirectory(root) || !IsUnlinkedDirectory(expected)) return expected;

        // Lazy depth-first traversal retains one enumerator per level, not a
        // list of every attachment directory in a large account.
        var pending = new Stack<IEnumerator<string>>();
        pending.Push(Directory.EnumerateFileSystemEntries(expected).GetEnumerator());
        try
        {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var entries = pending.Peek();
                if (!entries.MoveNext()) { pending.Pop().Dispose(); continue; }
                var entry = entries.Current;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Directory removal cannot follow filesystem links.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(Directory.EnumerateFileSystemEntries(entry).GetEnumerator());
            }
        }
        finally { while (pending.Count > 0) pending.Pop().Dispose(); }
        return expected;
    }

    internal static bool IsUnlinkedDirectory(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("Directory removal requires an ordinary owned directory without a filesystem link.");
        return true;
    }
}
