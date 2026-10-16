using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Mcp.ExternalClients;

/// <summary>
/// The <c>{"mcpServers": {"name": {"command", "args", "env"}}}</c> document used by Claude
/// Desktop and Cursor. Parsing is strict (no comments, trailing commas, or duplicate
/// property names) so that a document SwarmCore cannot round-trip is refused rather than
/// silently normalized. Every other property is preserved in its original order.
/// </summary>
public sealed class JsonMcpServersConfigFormat : IMcpClientConfigFormat
{
    public const string ServersPropertyName = "mcpServers";

    /// <summary>Properties that describe how a server is launched or reached; an upsert replaces them all.</summary>
    private static readonly HashSet<string> LaunchProperties = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "type", "transport", "url", "serverUrl", "httpUrl", "headers", "cwd", "bearerToken", "bearer_token_env_var",
    };

    /// <summary>Removes old launch/authentication coordinates before selecting another transport; other metadata survives.</summary>
    public static void ClearLaunchCoordinates(JsonObject server)
    {
        foreach (var key in LaunchProperties) server.Remove(key);
    }

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 64,
    };

    public McpClientConfigInspection Inspect(string? document, string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        if (!TryLoad(document, out var root, out var problem))
            return McpClientConfigInspection.Invalid(problem);

        if (!TryGetServers(root, create: false, out var servers, out problem))
            return new McpClientConfigInspection(true, problem, false, null, false);

        if (servers is null || !servers.TryGetPropertyValue(serverName, out var node))
            return new McpClientConfigInspection(true, null, false, null, true);

        return new McpClientConfigInspection(true, null, true, TryDecode(node), true);
    }

    /// <summary>
    /// Every server the document defines, in document order. An entry that is not a plain stdio
    /// launch (for example a URL-only entry) is listed with a null <see cref="McpServersDocumentEntry.Entry"/>
    /// so a host can report it instead of silently dropping it. A missing or blank document is
    /// valid and empty.
    /// </summary>
    public static McpServersDocument ReadAll(string? document)
    {
        if (!TryLoad(document, out var root, out var problem))
            return new McpServersDocument(false, problem, []);
        if (!TryGetServers(root, create: false, out var servers, out problem))
            return new McpServersDocument(false, problem, []);
        if (servers is null)
            return new McpServersDocument(true, null, []);

        var entries = servers
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => new McpServersDocumentEntry(pair.Key, TryDecode(pair.Value), pair.Value?.DeepClone() as JsonObject))
            .ToArray();
        return new McpServersDocument(true, null, entries);
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

        if (!TryLoad(document, out var root, out var problem))
            return McpClientConfigEdit.Refused(problem);
        if (!TryGetServers(root, create: true, out var servers, out problem))
            return McpClientConfigEdit.Refused(problem);

        if (servers!.TryGetPropertyValue(serverName, out var existing) && entry.Equals(TryDecode(existing)))
            return McpClientConfigEdit.Unchanged();

        var encoded = Encode(entry);
        // Keep the client's own per-server settings (flags such as "trusted" or "disabled"); only
        // the launch coordinates and transport-shaping properties are replaced.
        if (existing is JsonObject previous)
        {
            foreach (var (key, value) in previous)
            {
                if (!LaunchProperties.Contains(key) && !encoded.ContainsKey(key))
                    encoded[key] = value?.DeepClone();
            }
        }

        servers[serverName] = encoded;
        var text = Serialize(root, document, style);

        // Re-read what will be written; never hand back a document that does not say
        // exactly what was requested.
        var verification = Inspect(text, serverName);
        return verification.IsValid && entry.Equals(verification.Entry)
            ? McpClientConfigEdit.Changed(text)
            : McpClientConfigEdit.Refused("The updated settings could not be verified, so nothing was changed.");
    }

    public McpClientConfigEdit Remove(string? document, string serverName, McpClientConfigTextStyle style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(style);

        if (document is null)
            return McpClientConfigEdit.Unchanged();
        if (!TryLoad(document, out var root, out var problem))
            return McpClientConfigEdit.Refused(problem);
        if (!TryGetServers(root, create: false, out var servers, out problem))
            return McpClientConfigEdit.Refused(problem);
        if (servers is null || !servers.ContainsKey(serverName))
            return McpClientConfigEdit.Unchanged();

        servers.Remove(serverName);
        var text = Serialize(root, document, style);
        var verification = Inspect(text, serverName);
        return verification.IsValid && !verification.HasEntry
            ? McpClientConfigEdit.Changed(text)
            : McpClientConfigEdit.Refused("The updated settings could not be verified, so nothing was changed.");
    }

    private static bool TryLoad(string? document, out JsonObject root, out string problem)
    {
        root = null!;
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(document))
        {
            root = new JsonObject();
            return true;
        }

        try
        {
            var node = JsonNode.Parse(document, nodeOptions: null, StrictDocumentOptions);
            if (node is not JsonObject obj)
            {
                problem = "The settings file does not contain a JSON object.";
                return false;
            }

            // Materialize the whole tree now so that any latent error surfaces here,
            // before anything is written.
            _ = obj.ToJsonString();
            root = obj;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            problem = "The settings file is not valid JSON" + DescribeJsonError(ex) + ".";
            return false;
        }
    }

    private static string DescribeJsonError(Exception ex) =>
        ex is JsonException { LineNumber: { } line }
            ? $" (line {line + 1})"
            : string.Empty;

    private static bool TryGetServers(JsonObject root, bool create, out JsonObject? servers, out string problem)
    {
        problem = string.Empty;
        servers = null;
        if (!root.TryGetPropertyValue(ServersPropertyName, out var node))
        {
            if (create)
            {
                servers = new JsonObject();
                root[ServersPropertyName] = servers;
            }

            return true;
        }

        if (node is JsonObject obj)
        {
            servers = obj;
            return true;
        }

        problem = $"The \"{ServersPropertyName}\" setting is not a list of servers, so it was left unchanged.";
        return false;
    }

    internal static McpServerLaunchEntry? TryDecode(JsonNode? node)
    {
        if (node is not JsonObject server)
            return null;
        if (!server.TryGetPropertyValue("command", out var commandNode)
            || commandNode is not JsonValue commandValue
            || !commandValue.TryGetValue<string>(out var command)
            || string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var arguments = new List<string>();
        if (server.TryGetPropertyValue("args", out var argsNode) && argsNode is not null)
        {
            if (argsNode is not JsonArray array)
                return null;
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                    return null;
                arguments.Add(text);
            }
        }

        var environment = new List<KeyValuePair<string, string>>();
        if (server.TryGetPropertyValue("env", out var envNode) && envNode is not null)
        {
            if (envNode is not JsonObject env)
                return null;
            foreach (var (key, valueNode) in env)
            {
                if (string.IsNullOrWhiteSpace(key)
                    || valueNode is not JsonValue value
                    || !value.TryGetValue<string>(out var text))
                {
                    return null;
                }

                environment.Add(new KeyValuePair<string, string>(key, text));
            }
        }

        return new McpServerLaunchEntry(command, arguments, environment);
    }

    private static JsonObject Encode(McpServerLaunchEntry entry)
    {
        var server = new JsonObject
        {
            ["command"] = entry.Command,
            ["args"] = new JsonArray(entry.Arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
        };

        if (entry.EnvironmentVariables.Count > 0)
        {
            var env = new JsonObject();
            foreach (var (key, value) in entry.EnvironmentVariables)
                env[key] = value;
            server["env"] = env;
        }

        return server;
    }

    private static string Serialize(JsonObject root, string? original, McpClientConfigTextStyle style)
    {
        var (indentCharacter, indentSize) = DetectIndentation(original);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = indentCharacter,
            IndentSize = indentSize,
            NewLine = style.NewLine == "\r\n" ? "\r\n" : "\n",
            // The file is read by desktop apps, not embedded in HTML: keep paths and
            // non-ASCII text readable instead of \u-escaping them.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        return root.ToJsonString(options) + options.NewLine;
    }

    private static (char Character, int Size) DetectIndentation(string? original)
    {
        if (string.IsNullOrEmpty(original))
            return (' ', 2);

        foreach (var rawLine in original.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.Trim().Length == 0)
                continue;
            if (line[0] == '\t')
                return ('\t', 1);
            if (line[0] != ' ')
                continue;

            var spaces = line.Length - line.TrimStart(' ').Length;
            return (' ', Math.Clamp(spaces, 1, 8));
        }

        return (' ', 2);
    }
}
