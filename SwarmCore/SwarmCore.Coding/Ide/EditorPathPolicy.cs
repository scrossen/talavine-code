namespace SwarmCore.Coding.Ide;

/// <summary>
/// Which files a host may hand to the person's editor: an existing file inside the session's repository (relative
/// paths resolve against it and may not climb out of it), or an absolute path to an existing file. Nothing that
/// does not exist is opened, so an editor is never asked to create a file.
/// </summary>
public static class EditorPathPolicy
{
    public static string? Resolve(string? path, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.IndexOfAny(['\0', '\n', '\r']) >= 0) return null;
        var candidate = path.Trim();
        try
        {
            string full;
            if (Path.IsPathFullyQualified(candidate)) full = Path.GetFullPath(candidate);
            else
            {
                if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
                full = Path.GetFullPath(Path.Combine(repositoryRoot, candidate));
                if (!IsUnder(full, repositoryRoot)) return null;
            }
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it (case-insensitive on Windows and macOS).</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (string.Equals(full, parent, comparison)) return true;
            return full.Length > parent.Length
                && full.StartsWith(parent, comparison)
                && (full[parent.Length] == Path.DirectorySeparatorChar || full[parent.Length] == Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The repository-relative path with forward slashes, or the full path when it is outside the root.</summary>
    public static string Display(string fullPath, string? repositoryRoot)
        => !string.IsNullOrWhiteSpace(repositoryRoot) && IsUnder(fullPath, repositoryRoot)
            ? Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/')
            : fullPath;
}
