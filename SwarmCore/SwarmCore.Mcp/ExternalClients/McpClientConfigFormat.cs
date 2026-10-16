namespace SwarmCore.Mcp.ExternalClients;

public enum McpClientConfigEditOutcome
{
    /// <summary>The document must be rewritten with <see cref="McpClientConfigEdit.Content"/>.</summary>
    Changed,

    /// <summary>The document already says what was requested; nothing to write.</summary>
    Unchanged,

    /// <summary>The document cannot be edited safely; it must be left exactly as it is.</summary>
    Refused,
}

public sealed record McpClientConfigEdit(McpClientConfigEditOutcome Outcome, string? Content, string? Problem)
{
    public static McpClientConfigEdit Changed(string content) => new(McpClientConfigEditOutcome.Changed, content, null);

    public static McpClientConfigEdit Unchanged() => new(McpClientConfigEditOutcome.Unchanged, null, null);

    public static McpClientConfigEdit Refused(string problem) => new(McpClientConfigEditOutcome.Refused, null, problem);
}

/// <summary>
/// What a configuration document says about one named server.
/// </summary>
/// <param name="IsValid">False when the document is malformed; <paramref name="Problem"/> explains why.</param>
/// <param name="HasEntry">True when the document defines a server with the requested name.</param>
/// <param name="Entry">The decoded launch entry, or null when absent or not a plain stdio entry.</param>
/// <param name="CanEdit">False when the document or entry uses a form SwarmCore will not rewrite.</param>
public sealed record McpClientConfigInspection(
    bool IsValid,
    string? Problem,
    bool HasEntry,
    McpServerLaunchEntry? Entry,
    bool CanEdit)
{
    public static McpClientConfigInspection Invalid(string problem) => new(false, problem, false, null, false);
}

/// <summary>
/// One named server in an <c>mcpServers</c> document; <see cref="Entry"/> is null when it is not a plain
/// stdio launch. <see cref="Properties"/> is a copy of the whole entry object, so a host can read its own
/// extra settings (for example a boolean flag) without SwarmCore knowing them.
/// </summary>
public sealed record McpServersDocumentEntry(string Name, McpServerLaunchEntry? Entry, System.Text.Json.Nodes.JsonObject? Properties = null)
{
    /// <summary>A boolean property of the entry; false when absent or not a JSON boolean.</summary>
    public bool Flag(string name)
        => Properties?[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}

/// <summary>All servers an <c>mcpServers</c> document defines, or why it could not be read.</summary>
public sealed record McpServersDocument(bool IsValid, string? Problem, IReadOnlyList<McpServersDocumentEntry> Servers);

/// <summary>Text conventions preserved when a document is rewritten.</summary>
public sealed record McpClientConfigTextStyle(string NewLine)
{
    public static McpClientConfigTextStyle Default { get; } = new("\n");
}

/// <summary>
/// Pure text transformations for one client configuration format. Implementations never
/// touch the file system, preserve every other server and setting, and refuse rather than
/// guess when the existing text is malformed. A null document means the file does not exist.
/// </summary>
public interface IMcpClientConfigFormat
{
    McpClientConfigInspection Inspect(string? document, string serverName);

    McpClientConfigEdit Upsert(
        string? document,
        string serverName,
        McpServerLaunchEntry entry,
        McpClientConfigTextStyle style);

    McpClientConfigEdit Remove(string? document, string serverName, McpClientConfigTextStyle style);
}
