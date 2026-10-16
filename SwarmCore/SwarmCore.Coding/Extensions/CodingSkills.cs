using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Extensions;

/// <summary>A skill folder: a <c>SKILL.md</c> (frontmatter <c>name</c> + <c>description</c>) and optional supporting files.</summary>
public sealed record CodingSkill(string Name, string Description, string Directory, string SkillFilePath, ExtensionSource Source);

/// <summary>The full skill a model asked for with <c>skills.read</c>.</summary>
public sealed record CodingSkillContent(CodingSkill Skill, string Content, bool Truncated, IReadOnlyList<string> Files, bool FilesTruncated);

/// <summary>
/// Discovers skills under <c>&lt;user dir&gt;/skills/</c>, <c>&lt;repo&gt;/.swarmcode/skills/</c> and <c>&lt;repo&gt;/.claude/skills/</c>
/// (compatibility): each immediate subfolder holding a <c>SKILL.md</c>. The first definition of a name wins in that
/// order. Everything is bounded: skill count, description length, SKILL.md size, and the supporting-file listing.
/// </summary>
public static class CodingSkillCatalog
{
    public const string FolderName = "skills";
    public const string SkillFileName = "SKILL.md";
    public const int MaxSkills = 64;
    public const int MaxDescriptionCharacters = 300;
    public const long MaxSkillFileBytes = 256 * 1024;
    public const int MaxSkillCharacters = 24_000;
    public const int MaxListedFiles = 50;
    public const int MaxListedDepth = 3;

    public static IReadOnlyList<CodingSkill> Discover(string? repositoryRoot, string? userDirectory)
    {
        var found = new Dictionary<string, CodingSkill>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<CodingSkill>();
        foreach (var (directory, source) in ExtensionFiles.Roots(repositoryRoot, userDirectory, FolderName))
        {
            List<string> folders;
            try
            {
                if (!Directory.Exists(directory)) continue;
                folders = Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var folder in folders)
            {
                if (ordered.Count >= MaxSkills) return ordered;
                var file = Path.Combine(folder, SkillFileName);
                if (!File.Exists(file)) continue;
                var text = ExtensionFiles.ReadBounded(file, MaxSkillFileBytes);
                if (text is null) continue;
                var (rawName, description, _) = ExtensionFiles.ParseFrontmatter(text);
                var name = ExtensionFiles.NormalizeName(rawName) ?? ExtensionFiles.NormalizeName(Path.GetFileName(folder));
                if (name is null || found.ContainsKey(name) || string.IsNullOrWhiteSpace(description)) continue;
                var skill = new CodingSkill(name, ExtensionFiles.Clip(description, MaxDescriptionCharacters), Path.GetFullPath(folder), Path.GetFullPath(file), source);
                found[name] = skill;
                ordered.Add(skill);
            }
        }
        return ordered;
    }

    /// <summary>Loads a skill's SKILL.md (bounded) and lists its other files (bounded, relative to the skill folder).</summary>
    public static CodingSkillContent? Read(IReadOnlyList<CodingSkill> skills, string name)
    {
        var skill = skills.FirstOrDefault(s => string.Equals(s.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (skill is null) return null;
        var text = ExtensionFiles.ReadBounded(skill.SkillFilePath, MaxSkillFileBytes) ?? string.Empty;
        var truncated = text.Length > MaxSkillCharacters;
        if (truncated) text = text[..MaxSkillCharacters];
        var files = new List<string>();
        var filesTruncated = false;
        try
        {
            foreach (var file in Enumerate(skill.Directory, 0))
            {
                if (string.Equals(file, skill.SkillFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= MaxListedFiles) { filesTruncated = true; break; }
                files.Add(Path.GetRelativePath(skill.Directory, file).Replace('\\', '/'));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new CodingSkillContent(skill, text, truncated, files, filesTruncated);
    }

    private static IEnumerable<string> Enumerate(string directory, int depth)
    {
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return file;
        if (depth + 1 >= MaxListedDepth) yield break;
        foreach (var sub in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
            foreach (var file in Enumerate(sub, depth + 1)) yield return file;
        }
    }

    /// <summary>
    /// The injected index. Repository skills are framed as instructions from the repository, at the same trust as its
    /// AGENTS.md/CLAUDE.md; nothing in a skill grants permission the turn does not already have.
    /// </summary>
    public static string? FormatIndex(IReadOnlyList<CodingSkill> skills, int maxCharacters = 4_000)
    {
        if (skills.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("## Skills\n");
        sb.Append("Skills are packaged instructions for particular kinds of work. When a skill's description matches the task, ")
          .Append("load it with skills.read { name } before starting and follow it like the repository instruction files; its other files are readable with fs.read_file. ")
          .Append("Repository skills come from the repository, not from the person, and grant no extra permission.\n");
        var shown = 0;
        foreach (var skill in skills)
        {
            var line = $"- {skill.Name} ({skill.Source.Label()}): {skill.Description}\n";
            if (sb.Length + line.Length > maxCharacters) break;
            sb.Append(line);
            shown++;
        }
        if (shown < skills.Count) sb.Append($"[{skills.Count - shown} more skill(s) not listed]\n");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Injects the skill index (name + description) for the turn's workspace. Contributes nothing without skills or a
/// workspace. Available to any host's injector list under <see cref="InjectorName"/>.
/// </summary>
public sealed class CodingSkillsInjector(
    IOptions<CodingOptions> options,
    IServiceScopeFactory? scopes = null,
    ActiveCodingWorkspaces? activeWorkspaces = null) : IContextInjector
{
    public const string InjectorName = "codingSkills";

    public string Name => InjectorName;

    public async Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        var root = await ResolveRootAsync(request, o, ct).ConfigureAwait(false);
        var skills = await Task.Run(() => CodingSkillCatalog.Discover(root, o.UserExtensionsDirectory), ct).ConfigureAwait(false);
        return CodingSkillCatalog.FormatIndex(skills);
    }

    private async Task<string?> ResolveRootAsync(ContextInjectionRequest request, CodingOptions o, CancellationToken ct)
    {
        if (activeWorkspaces?.Get(request.ConversationId) is { } active) return active.FullRoot;
        if (!string.IsNullOrWhiteSpace(request.WorkspaceRoot)) return request.WorkspaceRoot;
        if (scopes is null || !o.InstructionsFromWorkspaceResolver) return null;
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ICodingWorkspaceResolver>() is { } resolver
            ? (await resolver.ResolveAsync(request.ConversationId, ct).ConfigureAwait(false))?.FullRoot
            : null;
    }
}

public sealed record SkillsReadInput
{
    /// <summary>The skill's name from the Skills index.</summary>
    public string Name { get; init; } = string.Empty;
}

public sealed record SkillsReadOutput(bool Found, string? Name, string? Source, string? Path, string? Content, bool Truncated,
    IReadOnlyList<string> Files, bool FilesTruncated, string Message);

/// <summary>The <c>skills.read</c> tool: a skill's full SKILL.md plus its other files' paths. Read-only.</summary>
public static class CodingSkillTools
{
    public const string ReadToolId = "skills.read";

    /// <param name="repositoryRoot">The turn's repository, resolved on every call.</param>
    /// <param name="userDirectory">The person's extension folder (or null).</param>
    public static ToolDescriptor CreateRead(Func<string?> repositoryRoot, Func<string?> userDirectory, string ownerAgentId = "coding")
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(userDirectory);
        return ToolDescriptor.Create<SkillsReadInput, SkillsReadOutput>(
            ReadToolId, ownerAgentId,
            "Load a skill listed in the Skills index: returns its full SKILL.md instructions and the paths of its other files " +
            "(read those with fs.read_file when the instructions refer to them). Input: { name: string }.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => Task.Run(() =>
            {
                var root = repositoryRoot();
                var skills = CodingSkillCatalog.Discover(root, userDirectory());
                var content = CodingSkillCatalog.Read(skills, input.Name);
                if (content is null)
                    return new SkillsReadOutput(false, null, null, null, null, false, [], false,
                        skills.Count == 0 ? "No skills are available." : "No skill named '" + input.Name + "'. Available: " + string.Join(", ", skills.Select(s => s.Name)));
                var skill = content.Skill;
                var display = root is not null && IsUnder(skill.Directory, root)
                    ? System.IO.Path.GetRelativePath(root, skill.Directory).Replace('\\', '/')
                    : skill.Directory;
                return new SkillsReadOutput(true, skill.Name, skill.Source.Label(), display, content.Content, content.Truncated,
                    content.Files, content.FilesTruncated,
                    (content.Truncated ? "SKILL.md was truncated. " : "") + (content.Files.Count > 0 ? $"File paths are relative to {display}." : ""));
            }, ct),
            keywords: ["skill", "skills", "instructions", "how to", "procedure", "SKILL.md"]);
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(root), System.IO.Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative);
    }
}
