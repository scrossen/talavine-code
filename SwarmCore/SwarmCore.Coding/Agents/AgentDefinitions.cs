using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Extensions;

namespace SwarmCore.Coding.Agents;

/// <summary>
/// A named agent role defined in a markdown file: <c>&lt;user dir&gt;/agents/*.md</c>, <c>&lt;repo&gt;/.swarmcode/agents/*.md</c>
/// or <c>&lt;repo&gt;/.claude/agents/*.md</c> (compatibility). Frontmatter: <c>name</c>, <c>description</c> (both
/// required), optional <c>lane</c>, <c>model</c>, <c>tools</c> (SwarmCode tool-id globs or Claude Code tool names,
/// comma separated or a list), <c>isolation</c> (<c>worktree</c>). The body is the role's instructions, prepended to
/// the agent's brief. A definition grants nothing: tools listed are an allow list within the session's own profile.
/// </summary>
/// <param name="Tools">Tool-id globs the agent may use (empty: every tool of the profile).</param>
public sealed record AgentDefinition(string Name, string Description, string Instructions, string? Lane, string? Model,
    IReadOnlyList<string> Tools, string? Isolation, string Path, ExtensionSource Source);

/// <summary>Discovers agent definitions. The first definition of a name wins (user, then repository, then .claude).</summary>
public static class AgentDefinitionCatalog
{
    public const string FolderName = "agents";
    public const long MaxFileBytes = 128 * 1024;
    public const int MaxDefinitionsPerFolder = 100;
    public const int MaxDescriptionCharacters = 300;
    public const int MaxInstructionCharacters = 16_000;

    /// <summary>Claude Code tool names accepted in <c>tools:</c> for <c>.claude/agents</c> compatibility.</summary>
    public static readonly IReadOnlyDictionary<string, string> ClaudeToolNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"] = "fs.read_file", ["Write"] = "fs.write_file", ["Edit"] = "code.edit_file", ["MultiEdit"] = "code.edit_file",
        ["Grep"] = "code.grep", ["Glob"] = "code.glob", ["LS"] = "fs.list_files", ["Bash"] = "shell.exec", ["PowerShell"] = "shell.exec",
        ["WebFetch"] = "web.fetch", ["WebSearch"] = "web.search", ["NotebookEdit"] = "notebook.edit", ["NotebookRead"] = "notebook.read",
        ["TodoWrite"] = "plan.update", ["Task"] = "agents.*", ["Agent"] = "agents.*",
    };

    public static IReadOnlyList<AgentDefinition> Discover(string? repositoryRoot, string? userDirectory)
    {
        var found = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AgentDefinition>();
        foreach (var (directory, source) in ExtensionFiles.Roots(repositoryRoot, userDirectory, FolderName))
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(directory)) continue;
                files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxDefinitionsPerFolder).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                var text = ExtensionFiles.ReadBounded(file, MaxFileBytes);
                if (text is null) continue;
                var definition = Parse(text, file, source);
                if (definition is null || found.ContainsKey(definition.Name)) continue;
                found[definition.Name] = definition;
                ordered.Add(definition);
            }
        }
        return ordered.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    public static AgentDefinition? Find(IReadOnlyList<AgentDefinition> definitions, string? name)
        => string.IsNullOrWhiteSpace(name) ? null
            : definitions.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses one definition file; null when it has no usable name or description.</summary>
    public static AgentDefinition? Parse(string text, string path, ExtensionSource source)
    {
        var (fields, body) = Frontmatter.Parse(text);
        var name = ExtensionFiles.NormalizeName(fields.GetValueOrDefault("name")) ?? ExtensionFiles.NormalizeName(System.IO.Path.GetFileNameWithoutExtension(path));
        var description = fields.GetValueOrDefault("description");
        if (name is null || string.IsNullOrWhiteSpace(description)) return null;
        var instructions = body.Trim();
        if (instructions.Length > MaxInstructionCharacters) instructions = instructions[..MaxInstructionCharacters].TrimEnd() + "\n[instructions truncated]";
        var isolation = Blank(fields.GetValueOrDefault("isolation"));
        return new AgentDefinition(name, ExtensionFiles.Clip(description, MaxDescriptionCharacters), instructions,
            Blank(fields.GetValueOrDefault("lane")), Blank(fields.GetValueOrDefault("model")),
            ParseTools(fields.GetValueOrDefault("tools") ?? fields.GetValueOrDefault("allowed-tools")),
            isolation is null ? null : isolation.ToLowerInvariant(), path, source);
    }

    /// <summary>Tool patterns from <c>a, b</c>, <c>[a, b]</c> or line-separated items; Claude Code names are translated.</summary>
    public static IReadOnlyList<string> ParseTools(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var items = raw.Trim().TrimStart('[').TrimEnd(']').Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tools = new List<string>();
        foreach (var item in items)
        {
            var token = item.Trim().Trim('"', '\'');
            if (token.Length == 0) continue;
            // Claude Code's "Bash(git:*)" form: the tool name is what SwarmCode can honour.
            var paren = token.IndexOf('(');
            if (paren > 0) token = token[..paren];
            if (ClaudeToolNames.TryGetValue(token, out var mapped)) token = mapped;
            if (token == "*" || !tools.Contains(token, StringComparer.OrdinalIgnoreCase)) tools.Add(token);
        }
        return tools.Any(t => t == "*") ? [] : tools;
    }

    /// <summary>The injected index: one line per agent so a model can start one by name with <c>agents.spawn { agent }</c>.</summary>
    public static string? FormatIndex(IReadOnlyList<AgentDefinition> definitions, int maxCharacters = 3_000)
    {
        if (definitions.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("## Agent roles\n");
        sb.Append("Named agents you can start with agents.spawn { agent: \"<name>\", task }. Each adds its own instructions, default lane/model and tool limits to the agent; ")
          .Append("repository roles come from the repository, not from the person, and grant no extra permission.\n");
        var shown = 0;
        foreach (var definition in definitions)
        {
            var line = $"- {definition.Name} ({definition.Source.Label()}): {definition.Description}\n";
            if (sb.Length + line.Length > maxCharacters) break;
            sb.Append(line);
            shown++;
        }
        if (shown < definitions.Count) sb.Append($"[{definitions.Count - shown} more role(s) not listed]\n");
        return sb.ToString().TrimEnd();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Minimal frontmatter reader for agent files: <c>---</c> fenced, <c>key: value</c> scalars, inline <c>[a, b]</c>
    /// lists and <c>- item</c> continuation lines. Quotes around a value are removed. Never throws.
    /// </summary>
    internal static class Frontmatter
    {
        public static (Dictionary<string, string> Fields, string Body) Parse(string text)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var normalized = text.Replace("\r\n", "\n");
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (fields, normalized);
            var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return (fields, normalized);
            var header = normalized[4..end];
            var bodyStart = normalized.IndexOf('\n', end + 1);
            var body = bodyStart < 0 ? string.Empty : normalized[(bodyStart + 1)..];
            string? current = null;
            foreach (var rawLine in header.Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
                if (current is not null && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    var item = line.TrimStart()[2..].Trim().Trim('"', '\'');
                    fields[current] = fields[current].Length == 0 ? item : fields[current] + ", " + item;
                    continue;
                }
                var colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) { current = null; continue; }
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
                fields[key] = value;
                current = key;
            }
            return (fields, body);
        }
    }
}

/// <summary>Resolves a named agent role for the conversation that starts the agent (its repository and the person's folder).</summary>
public interface IAgentDefinitionSource
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(string conversationId, CancellationToken ct = default);

    async Task<AgentDefinition?> FindAsync(string conversationId, string name, CancellationToken ct = default)
        => AgentDefinitionCatalog.Find(await ListAsync(conversationId, ct).ConfigureAwait(false), name);
}

/// <summary>
/// The harness default: the running turn's workspace (or the configured repository) plus
/// <see cref="CodingOptions.UserExtensionsDirectory"/>, read on every call so edited files apply at once.
/// </summary>
public sealed class CodingAgentDefinitionSource(IOptions<CodingOptions> options, ActiveCodingWorkspaces? active = null) : IAgentDefinitionSource
{
    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string conversationId, CancellationToken ct = default)
    {
        var o = options.Value;
        var root = active?.Get(conversationId)?.FullRoot ?? o.RepositoryRoot;
        return Task.Run(() => AgentDefinitionCatalog.Discover(root, o.UserExtensionsDirectory), ct);
    }
}

/// <summary>Injects the agent-role index for the turn's workspace; contributes nothing without definitions.</summary>
public sealed class AgentDefinitionsInjector(IOptions<CodingOptions> options, ActiveCodingWorkspaces? activeWorkspaces = null) : IContextInjector
{
    public const string InjectorName = "codingAgents";

    public string Name => InjectorName;

    public async Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        var root = activeWorkspaces?.Get(request.ConversationId)?.FullRoot ?? request.WorkspaceRoot ?? o.RepositoryRoot;
        var definitions = await Task.Run(() => AgentDefinitionCatalog.Discover(root, o.UserExtensionsDirectory), ct).ConfigureAwait(false);
        return AgentDefinitionCatalog.FormatIndex(definitions);
    }
}

/// <summary>
/// An agent role's tool allow list for a turn: host state carried in <see cref="CodingTurnRequest.Metadata"/> under
/// <see cref="MetadataKey"/> (patterns joined by <c>|</c>), applied to engine lanes by <see cref="CodingAgentToolFilter"/>
/// and to native lanes through the host tool bridge (<see cref="ActiveCodingWorkspaces"/>). Never model output.
/// </summary>
public static class CodingAgentTools
{
    public const string MetadataKey = "coding.agent-tools.v1";

    /// <summary>Tools every agent keeps whatever its allow list: the task list and the decline signal.</summary>
    private static readonly string[] AlwaysAllowed = ["plan.update", "plan.read", "harness.decline"];

    public static IReadOnlyDictionary<string, string> Metadata(IReadOnlyDictionary<string, string>? metadata, IReadOnlyList<string> patterns)
    {
        var copy = metadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        if (patterns.Count == 0) copy.Remove(MetadataKey);
        else copy[MetadataKey] = string.Join('|', patterns);
        return copy;
    }

    public static IReadOnlyList<string> Patterns(IReadOnlyDictionary<string, string>? metadata)
        => metadata is not null && metadata.TryGetValue(MetadataKey, out var joined) && !string.IsNullOrWhiteSpace(joined)
            ? joined.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>True when the allow list admits <paramref name="toolId"/> (an empty list admits everything).</summary>
    public static bool Admits(IReadOnlyList<string> patterns, string toolId)
        => patterns.Count == 0
           || AlwaysAllowed.Contains(toolId, StringComparer.OrdinalIgnoreCase)
           || patterns.Any(p => CodingHookMatcher.Matches(p, toolId));
}

/// <summary>Keeps only the tools an agent role allows on an engine turn (see <see cref="CodingAgentTools"/>).</summary>
public sealed class CodingAgentToolFilter : IAgentTurnToolFilter
{
    public ValueTask<IReadOnlyList<ToolDescriptor>> FilterAsync(AgentTurnRequest request, AgentProfileSnapshot profile,
        IReadOnlyList<ToolDescriptor> tools, CancellationToken ct = default)
    {
        var patterns = CodingAgentTools.Patterns(request.Metadata);
        return ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(patterns.Count == 0 ? tools
            : tools.Where(t => CodingAgentTools.Admits(patterns, t.ToolId)).ToList());
    }
}
