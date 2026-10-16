namespace SwarmCore.Skills.FileSkills;

/// <param name="Success">Whether content was returned.</param>
/// <param name="RelativePath">The resource that was requested, normalized.</param>
/// <param name="Content">File text, possibly truncated.</param>
/// <param name="Truncated">True when the file was longer than the read budget.</param>
/// <param name="ErrorMessage">Why the read was refused, when it was.</param>
public sealed record SkillResourceReadResult(
    bool Success,
    string RelativePath,
    string Content,
    bool Truncated,
    string? ErrorMessage)
{
    public static SkillResourceReadResult Fail(string relativePath, string error) =>
        new(false, relativePath, "", false, error);
}

/// <summary>
/// Reads the supporting files bundled with a file-based skill — the third level of the
/// Agent Skills specification's progressive disclosure model, where <c>references/</c> and
/// <c>assets/</c> load only when a task actually needs them.
///
/// Two rules make this safe to expose to an agent: a resource must have been discovered by
/// the loader (so a request cannot name a path the package never shipped), and the resolved
/// path must stay inside the skill's own directory (so <c>../</c> and reparse points cannot
/// walk out of it). <c>scripts/</c> is readable as text but never executed — Talavine
/// maps script behavior to reviewed C# rather than running an imported package's code.
/// </summary>
public static class SkillResourceReader
{
    /// <summary>Largest single resource returned to a caller, in characters.</summary>
    public const int MaxReadChars = 40_000;

    public static async Task<SkillResourceReadResult> ReadAsync(
        SkillManifest manifest,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var requested = (relativePath ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(requested))
            return SkillResourceReadResult.Fail(requested, "No resource path was supplied.");

        var skillDirectory = manifest.SourceDirectory;
        if (string.IsNullOrEmpty(skillDirectory) || !Directory.Exists(skillDirectory))
            return SkillResourceReadResult.Fail(requested, "This skill has no bundled files on disk.");

        // The loader's inventory is the allow-list. Anything not in it was never part of
        // the package the user reviewed, so there is no reason to go looking for it.
        var known = manifest.Resources.FirstOrDefault(r =>
            r.RelativePath.Equals(requested, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            var available = manifest.Resources.Count == 0
                ? "This skill bundles no supporting files."
                : "Available: " + string.Join(", ", manifest.Resources.Select(r => r.RelativePath));
            return SkillResourceReadResult.Fail(requested, $"'{requested}' is not part of this skill. {available}");
        }

        var root = Path.GetFullPath(skillDirectory);
        var resolved = Path.GetFullPath(Path.Combine(root, known.RelativePath));
        if (!IsInside(resolved, root))
            return SkillResourceReadResult.Fail(requested, "The resource resolves outside the skill directory.");

        if (!File.Exists(resolved))
            return SkillResourceReadResult.Fail(requested, "The resource is listed by the skill but missing on disk.");

        try
        {
            var info = new FileInfo(resolved);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                return SkillResourceReadResult.Fail(requested, "The resource is a link and was not read.");

            // The leaf check alone is not enough: a directory between the skill root and the
            // file can be swapped for a junction or symlink after the inventory was taken, and
            // the file reached through it is a perfectly ordinary file with an in-package path.
            // Path.GetFullPath does not resolve links on Windows, so walk the ancestors.
            if (HasLinkedAncestor(resolved, root))
                return SkillResourceReadResult.Fail(requested, "The resource is reached through a link and was not read.");

            // Read at most the budget, streaming. ReadAllTextAsync would materialize the WHOLE
            // file before truncating it, so a very large text file bundled in an imported package
            // could exhaust memory before the cap was ever applied — the cap has to bound the
            // read itself, not the result of it.
            using var reader = new StreamReader(resolved);
            var buffer = new char[MaxReadChars];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(0, MaxReadChars), ct);
            var text = new string(buffer, 0, read);

            // One more character decides truncated-vs-complete without holding the remainder.
            var truncated = reader.Peek() >= 0;

            return new SkillResourceReadResult(true, known.RelativePath, text, truncated, null);
        }
        catch (Exception ex)
        {
            return SkillResourceReadResult.Fail(requested, $"The resource could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// True when any directory strictly between the skill root and the file is a reparse
    /// point. The root itself is the loader's responsibility (it refuses symlinked skill
    /// directories before a manifest is ever parsed).
    /// </summary>
    private static bool HasLinkedAncestor(string filePath, string rootFullPath)
    {
        var root = Path.TrimEndingDirectorySeparator(rootFullPath);
        var directory = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(directory)
               && !string.Equals(Path.TrimEndingDirectorySeparator(directory), root, StringComparison.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static bool IsInside(string candidate, string rootFullPath)
    {
        var rootWithSep = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
