using SwarmCore.Coding;
using SwarmCore.Mcp.ExternalClients;

namespace SwarmCode.Hosting.Mcp;

/// <summary>
/// One external stdio or Streamable HTTP MCP server SwarmCode will connect. <paramref name="Trusted"/> (entry property
/// <c>"trusted": true</c>) lets the server's own read-only hints mark tools read-only;
/// <paramref name="Required"/> (<c>"required": true</c>) makes native agents that support it refuse to
/// run without the server. Both default to false and are written only by the user.
/// </summary>
public sealed record ExternalMcpServer(string Name, McpServerLaunchEntry? Launch, bool Trusted = false, bool Required = false)
{
    public string? Url { get; init; }
    public string? BearerTokenReference { get; init; }

    public const string TrustedProperty = "trusted";
    public const string RequiredProperty = "required";

    public McpServerLaunch ToLaunch() => new()
    {
        ServerName = Name,
        Command = Launch?.Command,
        Url = Url,
        BearerTokenReference = BearerTokenReference,
        Arguments = Launch is null ? [] : [.. Launch.Arguments],
        Environment = Launch is null ? [] : new Dictionary<string, string>(Launch.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
        Trusted = Trusted,
        Required = Required,
    };
}

/// <summary>What <see cref="ExternalMcpServersFile.Read"/> found; <see cref="Problems"/> lists entries that were skipped and why.</summary>
public sealed record ExternalMcpServersSnapshot(IReadOnlyList<ExternalMcpServer> Servers, IReadOnlyList<string> Problems)
{
    public static ExternalMcpServersSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// The host-owned list of external MCP servers: <see cref="SwarmCodeDataPaths.McpServersPath"/>,
/// a Claude-Desktop-compatible <c>mcpServers</c> document. Talavine's desktop card and the
/// SwarmCode Settings page write it through the same SwarmCore format and file store (backup
/// before change, atomic swap, refuse a file that cannot be parsed). Entries carry launch
/// coordinates only; never put secrets in them. All methods do blocking file I/O.
/// </summary>
public sealed class ExternalMcpServersFile(SwarmCodeDataPaths paths)
{
    private readonly McpClientConfigFileStore _store = new(backupLabel: "swarmcode");
    private readonly JsonMcpServersConfigFormat _format = new();

    public string Path => paths.McpServersPath;

    /// <summary>Save a shared Streamable HTTP endpoint. The file contains a vault reference, never its value.</summary>
    public string? UpsertHttp(string name, string url, string? bearerTokenReference = null, bool trusted = false, bool required = false)
    {
        if (!IsValidName(name) || string.Equals(name, McpServerLaunch.DefaultServerName, StringComparison.OrdinalIgnoreCase))
            return "Use a valid server name other than the reserved credential bridge name.";
        var launch = new McpServerLaunch { Url = url, BearerTokenReference = bearerTokenReference };
        if (McpHttpLaunch.ValidationProblem(launch) is { } problem) return problem;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var result = _store.Update(Path, snapshot =>
        {
            var document = JsonMcpServersConfigFormat.ReadAll(snapshot.Content);
            if (!document.IsValid) return McpClientConfigEdit.Refused(document.Problem ?? "The server list is invalid.");
            var root = System.Text.Json.Nodes.JsonNode.Parse(snapshot.Content ?? "{}") as System.Text.Json.Nodes.JsonObject;
            if (root is null) return McpClientConfigEdit.Refused("The server list must be an object.");
            var servers = root[JsonMcpServersConfigFormat.ServersPropertyName] as System.Text.Json.Nodes.JsonObject ?? new();
            if (servers.Parent is null) root[JsonMcpServersConfigFormat.ServersPropertyName] = servers;
            var entry = servers[name]?.DeepClone() as System.Text.Json.Nodes.JsonObject ?? new();
            JsonMcpServersConfigFormat.ClearLaunchCoordinates(entry);
            entry.Remove(ExternalMcpServer.TrustedProperty);
            entry.Remove(ExternalMcpServer.RequiredProperty);
            entry["type"] = "http";
            entry["url"] = url;
            if (bearerTokenReference is not null) entry["bearerToken"] = bearerTokenReference;
            if (trusted) entry[ExternalMcpServer.TrustedProperty] = true;
            if (required) entry[ExternalMcpServer.RequiredProperty] = true;
            servers[name] = entry;
            // Same escaping and line endings as every other edit of this shared, Claude-Desktop-compatible file.
            var options = WriteOptions(snapshot);
            var text = root.ToJsonString(options) + options.NewLine;
            return text == snapshot.Content ? McpClientConfigEdit.Unchanged() : McpClientConfigEdit.Changed(text);
        });
        return result.Outcome is McpClientConfigWriteOutcome.Changed or McpClientConfigWriteOutcome.Unchanged ? null
            : result.Problem ?? "The HTTP server could not be saved.";
    }

    public ExternalMcpServersSnapshot Read()
    {
        if (!File.Exists(Path)) return ExternalMcpServersSnapshot.Empty;
        var snapshot = _store.Read(Path);
        if (snapshot.Problem is not null) return new([], [snapshot.Problem]);
        var document = JsonMcpServersConfigFormat.ReadAll(snapshot.Content);
        if (!document.IsValid) return new([], [document.Problem ?? "The MCP server list could not be read."]);

        var servers = new List<ExternalMcpServer>();
        var problems = new List<string>();
        foreach (var entry in document.Servers)
        {
            if (!IsValidName(entry.Name))
                problems.Add($"\"{entry.Name}\" was skipped: server names may contain only letters, digits, '_' and '-'.");
            else if (string.Equals(entry.Name, McpServerLaunch.DefaultServerName, StringComparison.OrdinalIgnoreCase))
                problems.Add($"\"{entry.Name}\" was skipped: that name is reserved for Talavine Code's own credential bridge.");
            else if (entry.Properties?["url"] is not null)
            {
                if (entry.Properties.ContainsKey("command"))
                {
                    problems.Add($"\"{entry.Name}\" was skipped: choose an HTTP endpoint or a stdio command.");
                    continue;
                }
                var http = new McpServerLaunch { Url = entry.Properties["url"]?.ToString(), Command = entry.Entry?.Command,
                    BearerTokenReference = entry.Properties["bearerToken"]?.ToString() };
                if (McpHttpLaunch.ValidationProblem(http) is { } problem)
                    problems.Add($"\"{entry.Name}\" was skipped: {problem}");
                else servers.Add(new ExternalMcpServer(entry.Name, null,
                    entry.Flag(ExternalMcpServer.TrustedProperty), entry.Flag(ExternalMcpServer.RequiredProperty))
                    { Url = http.Url, BearerTokenReference = http.BearerTokenReference });
            }
            else if (entry.Entry is null)
                problems.Add($"\"{entry.Name}\" was skipped: use a stdio command or an HTTP endpoint.");
            else
                servers.Add(new ExternalMcpServer(entry.Name, entry.Entry,
                    entry.Flag(ExternalMcpServer.TrustedProperty), entry.Flag(ExternalMcpServer.RequiredProperty)));
        }
        return new(servers, problems);
    }

    /// <summary>
    /// Adds or replaces one server and sets its trusted/required flags (absent means false). Returns
    /// null on success, otherwise why nothing was written.
    /// </summary>
    public string? Upsert(string name, McpServerLaunchEntry launch, bool trusted, bool required)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (NameProblem(name) is { } nameProblem) return nameProblem;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // One edit: the launch and its flags change together or not at all (one backup, one atomic swap).
        var result = _store.Update(Path, snapshot =>
        {
            var launchEdit = _format.Upsert(snapshot.Content, name, launch, new McpClientConfigTextStyle(snapshot.NewLine));
            if (launchEdit.Outcome == McpClientConfigEditOutcome.Refused) return launchEdit;
            var withLaunch = launchEdit.Outcome == McpClientConfigEditOutcome.Changed ? snapshot with { Content = launchEdit.Content } : snapshot;
            var flagsEdit = SetFlags(withLaunch, name, trusted, required);
            return flagsEdit.Outcome == McpClientConfigEditOutcome.Unchanged ? launchEdit : flagsEdit;
        });
        return result.Outcome is McpClientConfigWriteOutcome.Changed or McpClientConfigWriteOutcome.Unchanged ? null : result.Problem ?? "The MCP server list could not be saved.";
    }

    private static string? NameProblem(string name)
        => !IsValidName(name) ? "Use a name made of letters, digits, '_' and '-'."
            : string.Equals(name, McpServerLaunch.DefaultServerName, StringComparison.OrdinalIgnoreCase)
                ? $"\"{name}\" is reserved for Talavine Code's own credential bridge."
                : null;

    private static System.Text.Json.JsonSerializerOptions WriteOptions(McpClientConfigSnapshot snapshot) => new()
    {
        WriteIndented = true,
        NewLine = snapshot.NewLine == "\r\n" ? "\r\n" : "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static McpClientConfigEdit SetFlags(McpClientConfigSnapshot snapshot, string name, bool trusted, bool required)
    {
        if (snapshot.Content is null) return McpClientConfigEdit.Refused("The MCP server list disappeared while it was being saved.");
        System.Text.Json.Nodes.JsonObject? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(snapshot.Content) as System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return McpClientConfigEdit.Refused("The MCP server list is not valid JSON."); }
        if (root?[JsonMcpServersConfigFormat.ServersPropertyName]?[name] is not System.Text.Json.Nodes.JsonObject server)
            return McpClientConfigEdit.Refused($"\"{name}\" is not in the MCP server list.");
        var before = server.ToJsonString();
        Set(server, ExternalMcpServer.TrustedProperty, trusted);
        Set(server, ExternalMcpServer.RequiredProperty, required);
        if (server.ToJsonString() == before) return McpClientConfigEdit.Unchanged();
        var options = WriteOptions(snapshot);
        return McpClientConfigEdit.Changed(root.ToJsonString(options) + options.NewLine);

        static void Set(System.Text.Json.Nodes.JsonObject server, string key, bool value)
        {
            if (value) server[key] = true;
            else server.Remove(key);
        }
    }

    /// <summary>Adds or replaces one server's launch, keeping its flags. Returns null on success, otherwise why nothing was written.</summary>
    public string? Upsert(string name, McpServerLaunchEntry launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (NameProblem(name) is { } nameProblem) return nameProblem;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var result = _store.Update(Path, snapshot => _format.Upsert(snapshot.Content, name, launch, new McpClientConfigTextStyle(snapshot.NewLine)));
        return result.Outcome is McpClientConfigWriteOutcome.Changed or McpClientConfigWriteOutcome.Unchanged ? null : result.Problem ?? "The MCP server list could not be saved.";
    }

    /// <summary>Removes one server. Returns null on success (including when it was not listed), otherwise why nothing was written.</summary>
    public string? Remove(string name)
    {
        if (!File.Exists(Path)) return null;
        var result = _store.Update(Path, snapshot => snapshot.Exists
            ? _format.Remove(snapshot.Content, name, new McpClientConfigTextStyle(snapshot.NewLine))
            : McpClientConfigEdit.Unchanged());
        return result.Outcome is McpClientConfigWriteOutcome.Changed or McpClientConfigWriteOutcome.Unchanged ? null : result.Problem ?? "The MCP server list could not be saved.";
    }

    /// <summary>Names become tool-id segments and TOML keys, so only plain identifiers are accepted.</summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 32 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
