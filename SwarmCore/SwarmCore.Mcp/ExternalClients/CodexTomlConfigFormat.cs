using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Mcp.ExternalClients;

/// <summary>
/// Codex CLI's <c>config.toml</c>. A server is a <c>[mcp_servers.&lt;name&gt;]</c> table with
/// <c>command</c>, <c>args</c>, and an <c>env</c> sub-table. Only the named server's own
/// tables are replaced or removed; every other byte of the file is kept as it was. A server
/// written with dotted keys or as an inline table, or an <c>mcp_servers</c> value that is not
/// a table, is reported instead of rewritten.
/// </summary>
public sealed class CodexTomlConfigFormat : IMcpClientConfigFormat
{
    public const string ServersTableName = "mcp_servers";

    private static readonly Regex BareKey = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private readonly int? _startupTimeoutSeconds;

    /// <param name="startupTimeoutSeconds">
    /// Written as <c>startup_timeout_sec</c> so a server that initializes a local database has
    /// time to start; null omits it. It is not part of the equivalence check.
    /// </param>
    public CodexTomlConfigFormat(int? startupTimeoutSeconds = 60)
    {
        if (startupTimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupTimeoutSeconds));
        _startupTimeoutSeconds = startupTimeoutSeconds;
    }

    public McpClientConfigInspection Inspect(string? document, string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        if (document is null)
            return new McpClientConfigInspection(true, null, false, null, true);
        if (!TomlDocument.TryParse(document, out var parsed, out var error))
            return McpClientConfigInspection.Invalid($"The settings file is not valid TOML ({error}).");

        var analysis = Analyze(parsed!, serverName);
        if (analysis.ForeignDefinition is not null)
            return new McpClientConfigInspection(true, analysis.ForeignDefinition, true, null, false);
        if (analysis.Blocks.Count == 0 && analysis.ServersNotExtensible is not null)
            return new McpClientConfigInspection(true, analysis.ServersNotExtensible, false, null, false);

        return new McpClientConfigInspection(true, null, analysis.Blocks.Count > 0, analysis.Entry, true);
    }

    public McpClientConfigEdit Upsert(
        string? document,
        string serverName,
        McpServerLaunchEntry entry,
        McpClientConfigTextStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(style);

        var text = document ?? string.Empty;
        if (!TomlDocument.TryParse(text, out var parsed, out var error))
            return McpClientConfigEdit.Refused($"The settings file is not valid TOML ({error}).");

        var analysis = Analyze(parsed!, serverName);
        if (analysis.ForeignDefinition is not null)
            return McpClientConfigEdit.Refused(analysis.ForeignDefinition);
        if (analysis.Blocks.Count > 0 && entry.Equals(analysis.Entry))
            return McpClientConfigEdit.Unchanged();
        if (analysis.Blocks.Count == 0 && analysis.ServersNotExtensible is not null)
            return McpClientConfigEdit.Refused(analysis.ServersNotExtensible);

        var newLine = style.NewLine == "\r\n" ? "\r\n" : "\n";
        string updated;
        if (analysis.Blocks.Count == 0)
        {
            updated = Append(text, Render(serverName, entry, newLine), newLine);
        }
        else
        {
            var primary = analysis.Blocks.FirstOrDefault(block => block.Path.Count == 2) ?? analysis.Blocks[0];
            // Only the launch keys are ours. Anything else the user put in the server's own table
            // (cwd, enabled, tool_timeout_sec, a tuned startup_timeout_sec) is carried over, as the
            // JSON formats keep non-launch properties.
            var preserved = primary.Path.Count == 2
                ? primary.KeyValues
                    .Where(statement => statement.Key[0] is not ("command" or "args" or "env"))
                    .Select(statement => (Key: statement.Key[0], Line: text[statement.Start..statement.End].Trim()))
                    .ToList()
                : [];
            var edits = new List<(int Start, int End, string Replacement)>
            {
                (primary.Start, primary.ContentEnd, Render(serverName, entry, newLine, preserved)),
            };
            edits.AddRange(analysis.Blocks
                .Where(block => !ReferenceEquals(block, primary))
                .Select(block => (block.Start, block.ExtendedEnd, string.Empty)));
            updated = ApplyEdits(text, edits);
        }

        return Verify(updated, serverName, entry)
            ? McpClientConfigEdit.Changed(updated)
            : McpClientConfigEdit.Refused("The updated settings could not be verified, so nothing was changed.");
    }

    public McpClientConfigEdit Remove(string? document, string serverName, McpClientConfigTextStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(style);
        if (document is null)
            return McpClientConfigEdit.Unchanged();
        if (!TomlDocument.TryParse(document, out var parsed, out var error))
            return McpClientConfigEdit.Refused($"The settings file is not valid TOML ({error}).");

        var analysis = Analyze(parsed!, serverName);
        if (analysis.ForeignDefinition is not null)
            return McpClientConfigEdit.Refused(analysis.ForeignDefinition);
        if (analysis.Blocks.Count == 0)
            return McpClientConfigEdit.Unchanged();

        var updated = ApplyEdits(document, analysis.Blocks.Select(block => (block.Start, block.ExtendedEnd, string.Empty)).ToList());
        if (analysis.Blocks.Any(block => block.ExtendedEnd == document.Length))
            updated = TrimTrailingBlankLines(updated);

        return Verify(updated, serverName, expected: null)
            ? McpClientConfigEdit.Changed(updated)
            : McpClientConfigEdit.Refused("The updated settings could not be verified, so nothing was changed.");
    }

    internal string Render(
        string serverName,
        McpServerLaunchEntry entry,
        string newLine,
        IReadOnlyList<(string Key, string Line)>? preserved = null)
    {
        preserved ??= [];
        var table = $"{ServersTableName}.{FormatKey(serverName)}";
        var builder = new StringBuilder()
            .Append('[').Append(table).Append(']').Append(newLine)
            .Append("command = ").Append(FormatString(entry.Command)).Append(newLine)
            .Append("args = [").Append(string.Join(", ", entry.Arguments.Select(FormatString))).Append(']').Append(newLine);
        if (_startupTimeoutSeconds is { } seconds && !preserved.Any(item => item.Key == "startup_timeout_sec"))
            builder.Append("startup_timeout_sec = ").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(newLine);
        foreach (var (_, line) in preserved)
            builder.Append(line).Append(newLine);

        if (entry.EnvironmentVariables.Count > 0)
        {
            builder.Append(newLine).Append('[').Append(table).Append(".env]").Append(newLine);
            foreach (var (key, value) in entry.EnvironmentVariables)
                builder.Append(FormatKey(key)).Append(" = ").Append(FormatString(value)).Append(newLine);
        }

        return builder.ToString();
    }

    private static bool Verify(string updated, string serverName, McpServerLaunchEntry? expected)
    {
        if (!TomlDocument.TryParse(updated, out var parsed, out _))
            return false;
        var analysis = Analyze(parsed!, serverName);
        if (analysis.ForeignDefinition is not null)
            return false;
        return expected is null
            ? analysis.Blocks.Count == 0
            : expected.Equals(analysis.Entry);
    }

    private sealed record Block(IReadOnlyList<string> Path, int Start, int ContentEnd, int ExtendedEnd, IReadOnlyList<TomlStatement> KeyValues);

    private sealed record Analysis(
        IReadOnlyList<Block> Blocks,
        McpServerLaunchEntry? Entry,
        string? ForeignDefinition,
        string? ServersNotExtensible);

    private static Analysis Analyze(TomlDocument document, string serverName)
    {
        string[] servers = [ServersTableName];
        string[] target = [ServersTableName, serverName];
        var statements = document.Statements;
        var blocks = new List<Block>();
        string? foreign = null;
        string? notExtensible = null;

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            switch (statement.Kind)
            {
                case TomlStatementKind.ArrayTable when StartsWith(statement.Path, target):
                    foreign ??= $"The \"{serverName}\" server is written as an array of tables, which can't be updated automatically.";
                    break;
                case TomlStatementKind.ArrayTable when statement.Path.Count == 1 && StartsWith(statement.Path, servers):
                    notExtensible ??= $"\"{ServersTableName}\" is written as an array of tables, so a server can't be added automatically.";
                    break;
                case TomlStatementKind.Table when StartsWith(statement.Path, target):
                    blocks.Add(BuildBlock(statements, i));
                    break;
                case TomlStatementKind.KeyValue:
                {
                    var full = statement.FullPath;
                    if (full.Count == 1 && full[0] == ServersTableName)
                    {
                        if (statement.Value is TomlInlineTable inline
                            && inline.Entries.Any(item => item.Key[0] == serverName))
                        {
                            foreign ??= $"The \"{serverName}\" server is written inside an inline \"{ServersTableName}\" table, which can't be updated automatically.";
                        }

                        notExtensible ??= $"\"{ServersTableName}\" is written as an inline value, so a server can't be added automatically.";
                    }
                    else if (StartsWith(full, target) && !StartsWith(statement.Path, target))
                    {
                        foreign ??= $"The \"{serverName}\" server is written with dotted keys or an inline table, which can't be updated automatically.";
                    }

                    break;
                }
            }
        }

        return new Analysis(blocks, DecodeEntry(blocks), foreign, notExtensible);
    }

    private static Block BuildBlock(IReadOnlyList<TomlStatement> statements, int headerIndex)
    {
        var header = statements[headerIndex];
        var contentEnd = header.End;
        var keyValues = new List<TomlStatement>();
        var index = headerIndex + 1;
        for (; index < statements.Count; index++)
        {
            var statement = statements[index];
            if (statement.Kind is TomlStatementKind.Table or TomlStatementKind.ArrayTable)
                break;
            if (statement.Kind == TomlStatementKind.KeyValue)
            {
                keyValues.Add(statement);
                contentEnd = statement.End;
            }
        }

        // Trailing comments stay with whatever follows; trailing blank lines go with the block
        // when it is removed so the file does not accumulate empty lines.
        var extendedEnd = contentEnd;
        foreach (var statement in statements.Skip(headerIndex + 1))
        {
            if (statement.Start < contentEnd)
                continue;
            if (statement.Kind != TomlStatementKind.Blank || statement.Start != extendedEnd)
                break;
            extendedEnd = statement.End;
        }

        return new Block(header.Path, header.Start, contentEnd, extendedEnd, keyValues);
    }

    private static McpServerLaunchEntry? DecodeEntry(IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0)
            return null;

        string? command = null;
        List<string>? arguments = null;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var statement in blocks.SelectMany(block => block.KeyValues))
        {
            var relative = statement.FullPath.Skip(2).ToArray();
            switch (relative)
            {
                case ["command"]:
                    if (statement.Value is not TomlString commandValue)
                        return null;
                    command = commandValue.Value;
                    break;
                case ["args"]:
                    if (statement.Value is not TomlArray array || array.Items.Any(item => item is not TomlString))
                        return null;
                    arguments = array.Items.Cast<TomlString>().Select(item => item.Value).ToList();
                    break;
                case ["env"]:
                    if (statement.Value is not TomlInlineTable table)
                        return null;
                    foreach (var item in table.Entries)
                    {
                        if (item.Key.Count != 1 || item.Value is not TomlString value)
                            return null;
                        environment[item.Key[0]] = value.Value;
                    }

                    break;
                case ["env", var name]:
                    if (statement.Value is not TomlString envValue)
                        return null;
                    environment[name] = envValue.Value;
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(command)
            ? null
            : new McpServerLaunchEntry(command, arguments ?? [], environment);
    }

    private static string Append(string text, string rendered, string newLine)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text + rendered;

        var builder = new StringBuilder(text);
        if (!text.EndsWith('\n'))
            builder.Append(newLine);

        // Separate the new tables from the previous content with exactly one blank line.
        if (!EndsWithBlankLine(builder.ToString()))
            builder.Append(newLine);
        return builder.Append(rendered).ToString();
    }

    /// <summary>True when <paramref name="text"/> (which ends in a line break) ends with an empty line.</summary>
    private static bool EndsWithBlankLine(string text)
    {
        var breakStart = text.Length - 1;
        if (breakStart > 0 && text[breakStart - 1] == '\r')
            breakStart--;
        return breakStart == 0 || text[breakStart - 1] == '\n';
    }

    private static string ApplyEdits(string text, List<(int Start, int End, string Replacement)> edits)
    {
        var builder = new StringBuilder(text);
        foreach (var (start, end, replacement) in edits.OrderByDescending(edit => edit.Start))
        {
            builder.Remove(start, end - start);
            builder.Insert(start, replacement);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Drops whitespace-only lines at the very end of the document (left behind when the last
    /// table was removed) while keeping the final content line, and its line break, exactly.
    /// </summary>
    private static string TrimTrailingBlankLines(string text)
    {
        var end = text.Length;
        while (end > 0)
        {
            var breakLength = text[end - 1] == '\n' ? (end >= 2 && text[end - 2] == '\r' ? 2 : 1) : 0;
            if (breakLength == 0)
                break;

            var contentEnd = end - breakLength;
            var lineStart = contentEnd == 0 ? 0 : text.LastIndexOf('\n', contentEnd - 1) + 1;
            if (!string.IsNullOrWhiteSpace(text[lineStart..contentEnd]))
                break;
            end = lineStart;
        }

        return text[..end];
    }

    private static bool StartsWith(IReadOnlyList<string> path, IReadOnlyList<string> prefix)
    {
        if (path.Count < prefix.Count)
            return false;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(path[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    internal static string FormatKey(string key) => BareKey.IsMatch(key) ? key : FormatString(key);

    internal static string FormatString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append(@"\b"); break;
                case '\t': builder.Append(@"\t"); break;
                case '\n': builder.Append(@"\n"); break;
                case '\f': builder.Append(@"\f"); break;
                case '\r': builder.Append(@"\r"); break;
                default:
                    if (c < 0x20 || c == 0x7F)
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
