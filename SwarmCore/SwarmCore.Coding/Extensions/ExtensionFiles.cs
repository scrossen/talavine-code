using SwarmCore.Skills.FileSkills;

namespace SwarmCore.Coding.Extensions;

/// <summary>Where an extension (command, skill, hook) was found. Repository content is untrusted input.</summary>
public enum ExtensionSource
{
    /// <summary>The person's own SwarmCode data directory (<see cref="CodingOptions.UserExtensionsDirectory"/>).</summary>
    User = 0,

    /// <summary><c>&lt;repo&gt;/.swarmcode/...</c>, written by whoever controls the repository.</summary>
    Repository = 1,

    /// <summary><c>&lt;repo&gt;/.claude/...</c>, read for compatibility; same trust as <see cref="Repository"/>.</summary>
    RepositoryClaude = 2,
}

/// <summary>Shared discovery helpers for file-based extensions: bounded reads, folder roots, frontmatter.</summary>
public static class ExtensionFiles
{
    /// <summary>Repository folder for SwarmCode extensions.</summary>
    public const string RepositoryFolder = ".swarmcode";

    /// <summary>Claude Code's repository folder, read for compatibility.</summary>
    public const string ClaudeFolder = ".claude";

    public static bool IsRepository(this ExtensionSource source) => source != ExtensionSource.User;

    public static string Label(this ExtensionSource source) => source switch
    {
        ExtensionSource.User => "user",
        ExtensionSource.Repository => "repository",
        _ => "repository (.claude)",
    };

    /// <summary>
    /// The directories that hold <paramref name="kind"/> (<c>commands</c> or <c>skills</c>) in precedence order: the
    /// person's own folder first (a repository cannot shadow a person's definition), then <c>.swarmcode</c>, then
    /// <c>.claude</c>. Missing roots are skipped.
    /// </summary>
    public static IReadOnlyList<(string Directory, ExtensionSource Source)> Roots(string? repositoryRoot, string? userDirectory, string kind)
    {
        var roots = new List<(string, ExtensionSource)>();
        if (!string.IsNullOrWhiteSpace(userDirectory)) roots.Add((Path.Combine(Path.GetFullPath(userDirectory), kind), ExtensionSource.User));
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var repo = Path.GetFullPath(repositoryRoot);
            roots.Add((Path.Combine(repo, RepositoryFolder, kind), ExtensionSource.Repository));
            roots.Add((Path.Combine(repo, ClaudeFolder, kind), ExtensionSource.RepositoryClaude));
        }
        return roots;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes of a text file; null when unreadable or larger than the cap.</summary>
    public static string? ReadBounded(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes) return null;
            return File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Splits frontmatter and body with SwarmCore.Skills' SKILL.md parser (Agent Skills vocabulary: name,
    /// description, ...). Unknown keys are ignored. Never throws: unparseable frontmatter yields the raw text as body.
    /// </summary>
    public static (string? Name, string? Description, string Body) ParseFrontmatter(string content)
    {
        try
        {
            var manifest = SkillManifestParser.Parse(content);
            return (Blank(manifest.Name), Blank(manifest.Description), manifest.Instructions ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, null, content.Trim());
        }
    }

    /// <summary>Lower-case name made of letters, digits, '-', '_', '.', ':' (1-64 characters); null otherwise.</summary>
    public static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = raw.Trim().ToLowerInvariant();
        if (name.Length > 64) return null;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return null;
        return name[0] == '.' ? null : name;
    }

    public static string Clip(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
