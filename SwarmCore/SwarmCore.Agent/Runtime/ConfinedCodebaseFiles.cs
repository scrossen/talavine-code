using System.Security;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Agent.Runtime;

public sealed record CodebaseFileText(string RelativePath, string Text);

/// <summary>
/// Best-effort source inspection within a host-selected directory. Symbolic/reparse links and
/// entries whose metadata cannot be inspected are omitted. Metadata is checked
/// again at read time; this is not an atomic filesystem snapshot or OS sandbox.
/// </summary>
public static class ConfinedCodebaseFiles
{
    public static IEnumerable<CodebaseFileText> ReadFiles(string root, Func<string, bool> allowedFile,
        Func<string, bool> ignoredDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allowedFile);
        ArgumentNullException.ThrowIfNull(ignoredDirectory);
        ct.ThrowIfCancellationRequested();
        var physicalRoot = NormalizeRoot(root);
        if (physicalRoot is null) yield break;
        foreach (var path in EnumerateCore(physicalRoot, allowedFile, ignoredDirectory, ct))
            if (TryReadCore(physicalRoot, path, out var result, ct)) yield return result!;
    }

    public static IEnumerable<string> EnumerateFiles(string root, Func<string, bool> allowedFile,
        Func<string, bool> ignoredDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allowedFile);
        ArgumentNullException.ThrowIfNull(ignoredDirectory);
        ct.ThrowIfCancellationRequested();
        var physicalRoot = NormalizeRoot(root);
        if (physicalRoot is null) yield break;
        foreach (var path in EnumerateCore(physicalRoot, allowedFile, ignoredDirectory, ct)) yield return path;
    }

    public static bool TryReadFile(string root, string path, out CodebaseFileText? result, CancellationToken ct = default)
    {
        result = null;
        ct.ThrowIfCancellationRequested();
        var physicalRoot = NormalizeRoot(root);
        return physicalRoot is not null && TryReadCore(physicalRoot, path, out result, ct);
    }

    private static string? NormalizeRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            // An alias in an ancestor may identify the same authorized root;
            // a link used as the selected root itself is not admitted.
            if (!OrdinaryEntry(lexical, directory: true)) return null;
            var physical = PhysicalPathIdentity.Resolve(lexical);
            // Resolve can defensively fall back to a lexical path. Independently
            // inspecting every ancestor prevents that fallback following a link.
            return OrdinaryAncestorChain(physical) ? physical : null;
        }
        catch (Exception ex) when (Unavailable(ex)) { return null; }
    }

    private static IEnumerable<string> EnumerateCore(string root, Func<string, bool> allowedFile,
        Func<string, bool> ignoredDirectory, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if (!OrdinaryPath(root, directory, directory: true)) continue;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (Unavailable(ex)) { continue; }
            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (OrdinaryPath(root, path, directory: true))
                {
                    if (!ignoredDirectory(path)) pending.Push(path);
                }
                else if (OrdinaryPath(root, path, directory: false) && allowedFile(path)) yield return path;
            }
        }
    }

    private static bool TryReadCore(string root, string path, out CodebaseFileText? result, CancellationToken ct)
    {
        result = null;
        ct.ThrowIfCancellationRequested();
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!OrdinaryPath(root, fullPath, directory: false)) return false;
            var text = File.ReadAllText(fullPath);
            ct.ThrowIfCancellationRequested();
            if (!OrdinaryPath(root, fullPath, directory: false)) return false;
            result = new CodebaseFileText(Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/'), text);
            return true;
        }
        catch (Exception ex) when (Unavailable(ex)) { return false; }
    }

    private static bool OrdinaryPath(string root, string path, bool directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            // Windows RelativePath folds case even for explicitly case-sensitive
            // directories. Validate the supplied spelling before checking root-relative
            // metadata so an external case-differing sibling cannot borrow those checks.
            if (!fullPath.Equals(root, StringComparison.Ordinal)
                && !fullPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var relative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return false;
            // NormalizeRoot validated the root's whole ancestor chain once for this call; per entry,
            // only the root itself and the pieces below it are re-inspected.
            if (!OrdinaryEntry(root, directory: true)) return false;
            if (relative == ".") return directory;
            var pieces = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < pieces.Length; index++)
            {
                current = Path.Combine(current, pieces[index]);
                if (!OrdinaryEntry(current, index != pieces.Length - 1 || directory)) return false;
            }
            return true;
        }
        catch (Exception ex) when (Unavailable(ex)) { return false; }
    }

    private static bool OrdinaryAncestorChain(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (!OrdinaryEntry(current, directory: true)) return false;
            current = Path.GetDirectoryName(current);
        }
        return true;
    }

    private static bool OrdinaryEntry(string path, bool directory)
    {
        try
        {
            FileSystemInfo entry = directory ? new DirectoryInfo(path) : new FileInfo(path);
            entry.Refresh();
            return entry.Exists && entry.LinkTarget is null
                && (entry.Attributes & FileAttributes.ReparsePoint) == 0
                && ((entry.Attributes & FileAttributes.Directory) != 0) == directory;
        }
        catch (Exception ex) when (Unavailable(ex)) { return false; }
    }

    private static bool Unavailable(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or NotSupportedException or ArgumentException;
}
