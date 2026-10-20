namespace SwarmCore.Coding.Projects;

public sealed record CodingProjectFolder(string Name, string Root, string? LibraryId = null);

/// <summary>Combines registered folders and chat history without registering or moving files.</summary>
public static class CodingProjectFolders
{
    public static IReadOnlyList<CodingProjectFolder> List(CodingAppLibrary library, IEnumerable<string> recentRoots,
        IEnumerable<string>? excludedRecentRoots = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = library.Apps.Select(a => CodingAppPaths.Normalize(a.Root)).ToHashSet(comparer);
        var excluded = (excludedRecentRoots ?? []).Select(CodingAppPaths.Normalize).ToArray();
        var projects = library.Apps.Where(a => a.Kind == CodingProjectKind.Project)
            .Select(a => new CodingProjectFolder(a.Name, a.Root, a.Id)).ToList();
        foreach (var root in recentRoots)
        {
            var normalized = CodingAppPaths.Normalize(root);
            if (excluded.Any(parent => comparer.Equals(normalized, parent)
                || normalized.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, CodingAppPaths.Comparison))) continue;
            if (!known.Add(normalized)) continue;
            projects.Add(new(Path.GetFileName(normalized) is { Length: > 0 } name ? name : normalized, normalized));
        }
        return projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
