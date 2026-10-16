using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Extensions;

/// <summary>A markdown prompt template invoked as <c>/name args</c>.</summary>
/// <param name="Name">Lower-case command name (the file name without <c>.md</c>).</param>
/// <param name="Description">Frontmatter <c>description</c>, or the body's first line.</param>
/// <param name="Body">Template text after the frontmatter.</param>
/// <param name="Path">Full path of the markdown file.</param>
/// <param name="Source">Where it was found.</param>
public sealed record CustomCommand(string Name, string Description, string Body, string Path, ExtensionSource Source);

/// <summary>
/// Discovers custom slash commands: <c>&lt;user dir&gt;/commands/*.md</c>, <c>&lt;repo&gt;/.swarmcode/commands/*.md</c> and
/// <c>&lt;repo&gt;/.claude/commands/*.md</c> (compatibility). The first definition of a name wins, in that order, so a
/// repository cannot shadow the person's own command. Built-in host commands always win; hosts pass their names.
/// Expanding a command only produces the message text the person chose to send; nothing runs.
/// </summary>
public static class CustomCommandCatalog
{
    public const string FolderName = "commands";
    public const long MaxFileBytes = 64 * 1024;
    public const int MaxCommandsPerFolder = 200;
    public const int MaxDescriptionCharacters = 200;

    public static IReadOnlyList<CustomCommand> Discover(string? repositoryRoot, string? userDirectory, IEnumerable<string>? builtInNames = null)
    {
        var reserved = new HashSet<string>(builtInNames ?? [], StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, CustomCommand>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<CustomCommand>();
        foreach (var (directory, source) in ExtensionFiles.Roots(repositoryRoot, userDirectory, FolderName))
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(directory)) continue;
                files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxCommandsPerFolder).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                var name = ExtensionFiles.NormalizeName(System.IO.Path.GetFileNameWithoutExtension(file));
                if (name is null || reserved.Contains(name) || found.ContainsKey(name)) continue;
                var text = ExtensionFiles.ReadBounded(file, MaxFileBytes);
                if (text is null) continue;
                var (_, description, body) = ExtensionFiles.ParseFrontmatter(text);
                if (string.IsNullOrWhiteSpace(body)) continue;
                var command = new CustomCommand(name,
                    ExtensionFiles.Clip(description ?? FirstLine(body), MaxDescriptionCharacters), body, file, source);
                found[name] = command;
                ordered.Add(command);
            }
        }
        return ordered.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The command a typed <c>/name ...</c> line names, or null (also null for a built-in name).</summary>
    public static CustomCommand? Find(IReadOnlyList<CustomCommand> commands, string name)
        => commands.FirstOrDefault(c => string.Equals(c.Name, name.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Splits <c>/name rest</c> into the command name and its argument text.</summary>
    public static bool TryParseInvocation(string text, out string name, out string arguments)
    {
        name = arguments = string.Empty;
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
        name = (split < 0 ? text[1..] : text[1..split]).ToLowerInvariant();
        arguments = split < 0 ? string.Empty : text[(split + 1)..].Trim();
        return name.Length > 0;
    }

    private static string FirstLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return string.Empty;
    }
}

/// <summary>
/// Expands a command template: <c>$ARGUMENTS</c> is the whole argument text, <c>$1</c>..<c>$9</c> are positional
/// arguments (whitespace separated, double quotes group). A template without any placeholder gets the arguments
/// appended on their own line so nothing the person typed is dropped.
/// </summary>
public static partial class CustomCommandExpander
{
    public static string Expand(CustomCommand command, string arguments) => Expand(command.Body, arguments);

    public static string Expand(string body, string arguments)
    {
        arguments = (arguments ?? string.Empty).Trim();
        var positional = SplitArguments(arguments);
        var used = false;
        var expanded = Placeholder().Replace(body, m =>
        {
            used = true;
            var key = m.Groups[1].Value;
            if (key == "ARGUMENTS") return arguments;
            var index = key[0] - '1';
            return index < positional.Count ? positional[index] : string.Empty;
        });
        if (!used && arguments.Length > 0) expanded = expanded.TrimEnd() + "\n\n" + arguments;
        return expanded.Trim();
    }

    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in arguments)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }

    [GeneratedRegex(@"\$(ARGUMENTS|[1-9])(?![0-9A-Za-z_])", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
