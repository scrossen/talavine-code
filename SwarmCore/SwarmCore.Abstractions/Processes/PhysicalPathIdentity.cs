using System.Text;

namespace SwarmCore.Abstractions.Processes;

/// <summary>Resolves physical filesystem aliases before deriving process coordination namespaces.</summary>
public static class PhysicalPathIdentity
{
    public static string Resolve(string profilePath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath));
        var isDirectory = Directory.Exists(fullPath);
        var isFile = File.Exists(fullPath);
        if (!isDirectory && !isFile)
        {
            return NormalizeCanonicalPath(fullPath);
        }

        try
        {
            // A resolved link target can itself sit beneath symlinked parents
            // (for example a link stored as /var/... on macOS, where /var is
            // itself a symlink to /private/var), so re-walk from the resolved
            // spelling until it stabilizes. Stop on an observed cycle instead
            // of imposing a maximum number of valid path transformations.
            var current = fullPath;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(current))
            {
                var root = Path.GetPathRoot(current);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return NormalizeCanonicalPath(current);
                }

                // Drive letters and UNC server/share roots are case-insensitive even
                // when a Windows directory has explicitly enabled case-sensitive
                // child names. Normalize only the root; actual component spelling
                // below preserves distinct case-sensitive directories.
                var walked = OperatingSystem.IsWindows()
                    ? root.ToUpperInvariant()
                    : root;
                var relativePath = current[root.Length..];
                var components = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);

                var traversedLink = false;
                for (var index = 0; index < components.Length; index++)
                {
                    var component = components[index];
                    var actualComponent = FindActualComponentName(walked, component);
                    var candidate = Path.Combine(walked, actualComponent);

                    // Resolve every component, not just the final directory. A
                    // profile beneath a junction/symlinked parent must share the
                    // same namespace as its direct physical spelling too.
                    FileSystemInfo entry = isFile && index == components.Length - 1
                        ? new FileInfo(candidate)
                        : new DirectoryInfo(candidate);
                    var resolvedTarget = entry.ResolveLinkTarget(returnFinalTarget: true);
                    traversedLink |= resolvedTarget is not null;
                    walked = Path.GetFullPath(resolvedTarget?.FullName ?? candidate);
                }

                current = walked;
                if (!traversedLink)
                {
                    break;
                }
            }

            return NormalizeCanonicalPath(current);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or NotSupportedException)
        {
            // A path that exists but cannot be inspected still gets a stable,
            // platform-normalized namespace. Normal profile startup requires
            // directory access, so this is a defensive diagnostic fallback.
            return NormalizeCanonicalPath(fullPath);
        }
    }

    // Only entries whose name matches (in any casing) are materialized; the walk runs for
    // every ancestor on every AtomicJsonFile access, and %TEMP% or AppData can hold thousands
    // of entries. Hidden and system entries (AppData itself) must still be seen.
    private static readonly EnumerationOptions ComponentLookup = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        MatchType = MatchType.Simple,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    private static string FindActualComponentName(string parentPath, string requestedName)
    {
        string? caseInsensitiveMatch = null;
        // A name containing '*' or '?' (legal on Unix) only widens the candidates; the
        // comparisons below still pick the exact entry.
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(parentPath, requestedName, ComponentLookup))
        {
            var entryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(entryPath));
            if (string.Equals(entryName, requestedName, StringComparison.Ordinal))
            {
                return entryName;
            }

            if (caseInsensitiveMatch is null
                && string.Equals(entryName, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitiveMatch = entryName;
            }
        }

        // The complete input path was confirmed to exist. On a case-insensitive
        // filesystem this recovers the on-disk spelling; on a case-sensitive
        // filesystem the exact spelling above wins and distinct directories do
        // not collapse into one key.
        return caseInsensitiveMatch ?? requestedName;
    }

    private static string NormalizeCanonicalPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .Normalize(NormalizationForm.FormC);

        return normalized;
    }

}
