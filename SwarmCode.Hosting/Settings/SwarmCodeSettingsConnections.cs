using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using SwarmCore.Coding;
using SwarmCore.Coding.Settings;
using SwarmCore.Mcp.ExternalClients;
using SwarmCode.Hosting.Mcp;

namespace SwarmCode.Hosting.Settings;

public sealed partial class SwarmCodeSettingsHost
{
    private AgentSettingsDocument ReadConnections()
    {
        var store = new McpClientConfigFileStore(); var snapshot = store.Read(paths.McpServersPath);
        if (snapshot.Problem is not null) throw new InvalidDataException("The MCP settings could not be read safely.");
        var root = JsonNode.Parse(snapshot.Content ?? "{}")!.AsObject();
        var servers = root["mcpServers"]?.DeepClone().AsObject() ?? new();
        foreach (var entry in servers.Select(p => p.Value).OfType<JsonObject>())
        {
            if (entry["env"] is JsonObject env) foreach (var key in env.Select(p => p.Key).ToArray()) env[key] = "[keep protected value]";
            if (entry["bearerToken"] is JsonValue bearer && !bearer.ToString().StartsWith("{{secret:", StringComparison.Ordinal)) entry["bearerToken"] = "[keep protected value]";
        }
        var values = new JsonObject { ["servers"] = servers };
        var schema = Json.GetJsonSchemaAsNode(typeof(ConnectionSettings));
        return new("connections", "Connected data MCP servers. Partial server objects merge; null removes a server. Environment and bearer values must use {{secret:name}} references. Protected placeholders preserve existing values.",
            snapshot.Exists ? Convert.ToHexString(SHA256.HashData(snapshot.Bytes)) : "missing", values, schema, "Reopen the repository after this request finishes.");
    }

    private Task<SettingsChangeResult> SaveConnectionsAsync(SettingsUpdateInput input, JsonObject merged, CancellationToken ct) => Task.Run(() =>
    {
        var editServers = input.Changes["servers"] as JsonObject ?? throw new InvalidOperationException("Supply a servers object.");
        var store = new McpClientConfigFileStore(backupLabel: "swarmcode");
        var result = store.Update(paths.McpServersPath, snapshot =>
        {
            var revision = snapshot.Exists ? Convert.ToHexString(SHA256.HashData(snapshot.Bytes)) : "missing";
            if (revision != input.Revision) return McpClientConfigEdit.Refused("MCP settings changed or were deleted. Read again.");
            var root = JsonNode.Parse(snapshot.Content ?? "{}")!.AsObject();
            var servers = root["mcpServers"] as JsonObject;
            if (servers is null) root["mcpServers"] = servers = new();
            foreach (var pair in editServers)
            {
                if (!ExternalMcpServersFile.IsValidName(pair.Key) || pair.Key.Equals(McpServerLaunch.DefaultServerName, StringComparison.OrdinalIgnoreCase)) return McpClientConfigEdit.Refused("Use an unreserved MCP server name.");
                if (pair.Value is null) { servers.Remove(pair.Key); continue; }
                if (pair.Value is not JsonObject patch) return McpClientConfigEdit.Refused("Each server must be an object or null.");
                var entry = (servers[pair.Key]?.DeepClone() as JsonObject) ?? new();
                var change = patch.DeepClone().AsObject();
                if (change["env"] is JsonObject env)
                {
                    foreach (var key in env.Select(p => p.Key).ToArray())
                    {
                        var value = env[key]?.ToString();
                        if (value == "[keep protected value]") env.Remove(key);
                        else if (value is not null && !IsSecretReference(value)) return McpClientConfigEdit.Refused("Use saved credential references for environment values.");
                    }
                }
                if (change["bearerToken"]?.ToString() == "[keep protected value]") change.Remove("bearerToken");
                else if (change["bearerToken"] is { } token && !IsSecretReference(token.ToString())) return McpClientConfigEdit.Refused("Use a saved bearer credential reference.");
                SettingsJsonFile.Merge(entry, change);
                var typed = entry.Deserialize<ConnectionEntry>(Json) ?? throw new InvalidOperationException("Invalid MCP server.");
                if (typed.Url is not null)
                {
                    if (McpHttpLaunch.ValidationProblem(new McpServerLaunch { Url = typed.Url, Command = typed.Command, BearerTokenReference = typed.BearerToken }) is { } error) return McpClientConfigEdit.Refused(error);
                }
                else if (string.IsNullOrWhiteSpace(typed.Command)) return McpClientConfigEdit.Refused("A local server requires a command.");
                servers[pair.Key] = entry;
            }
            return McpClientConfigEdit.Changed(root.ToJsonString(SettingsJsonFile.Json));
        });
        if (result.Outcome is not (McpClientConfigWriteOutcome.Changed or McpClientConfigWriteOutcome.Unchanged)) throw new InvalidOperationException(result.Problem ?? "Could not save MCP settings.");
        activation.RequestReload(); return new SettingsChangeResult(activation.Message, true);
    }, ct);

    private AgentSettingsDocument ReadCredentials()
    {
        var entries = vault.ListAsync().GetAwaiter().GetResult();
        var revision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries.Select(e => new { e.Name, e.UpdatedUtc }).OrderBy(e => e.Name)))));
        var refs = new JsonObject();
        foreach (var entry in entries.Where(e => e.Name.StartsWith("llm.", StringComparison.OrdinalIgnoreCase) || e.Name == Web.SwarmCodeWebSearch.SecretName)) refs[entry.Name] = entry.Name;
        return new("credentials", "API keys and Brave web search: bind one existing saved secret per update to a destination secret name (llm.Provider or brave-search-api-key). Null removes the destination. New passwords/keys use protected /secret or CSV intake; credentials.list shows names only.",
            revision, new JsonObject { ["bindings"] = refs }, Json.GetJsonSchemaAsNode(typeof(CredentialBindings)), "Immediately. Values never pass through the model.");
    }
    private async Task<SettingsChangeResult> SaveCredentialsAsync(SettingsUpdateInput input, JsonObject merged, CancellationToken ct)
    {
        var bindings = input.Changes["bindings"] as JsonObject ?? throw new InvalidOperationException("Supply credential bindings by destination name.");
        if (bindings.Count != 1) throw new InvalidOperationException("Change one credential binding per call; read again before the next change.");
        var expected = await vault.GetEntryAsync(bindings.First().Key, ct);
        // Resolve all sources before any changes, without returning values or including them in errors.
        var resolved = new Dictionary<string, string?>();
        foreach (var pair in bindings)
        {
            if (!(pair.Key.StartsWith("llm.", StringComparison.OrdinalIgnoreCase) && pair.Key.Length > 4) && pair.Key != Web.SwarmCodeWebSearch.SecretName) throw new InvalidOperationException("Use an LLM provider key or the Brave Search key destination.");
            if (pair.Value is null) resolved[pair.Key] = null;
            else resolved[pair.Key] = await vault.GetValueAsync(pair.Value.ToString(), ct) ?? throw new InvalidOperationException("A named source credential is missing. Use protected credential intake first.");
        }
        if (ReadCredentials().Revision != input.Revision) throw new InvalidOperationException("Credentials changed. Read again.");
        foreach (var pair in resolved)
        {
            bool applied;
            if (pair.Key == Web.SwarmCodeWebSearch.SecretName)
            {
                if (vault is not SwarmCore.Secrets.IConditionalSecretVault conditional) throw new InvalidOperationException("This vault does not support checked setting changes.");
                applied = pair.Value is null ? expected is null || await conditional.TryDeleteAsync(expected, ct)
                    : await conditional.TrySetAsync(new SwarmCore.Secrets.SecretWriteRequest { Name = pair.Key, Value = pair.Value, Kind = SwarmCore.Secrets.SecretKinds.ApiKey }, expected, ct);
            }
            else applied = pair.Value is null ? await apiKeys.ClearApiKeyIfUnchangedAsync(pair.Key[4..], expected, ct)
                : await apiKeys.SetApiKeyIfUnchangedAsync(pair.Key[4..], pair.Value, expected, ct);
            if (!applied) throw new InvalidOperationException("The credential changed or was deleted. Read again before changing it.");
        }
        if (resolved.Keys.Any(k => k.StartsWith("llm.", StringComparison.OrdinalIgnoreCase)))
        { activation.RequestReload(); return new("Credential bindings saved in the protected vault. " + activation.Message, true); }
        return new("Credential bindings saved in the protected vault. Applies to the next search request.");
    }
    private static bool IsSecretReference(string value) => value.StartsWith("{{secret:", StringComparison.Ordinal) && value.EndsWith("}}", StringComparison.Ordinal) && value.Length > 11;
    public sealed class ConnectionSettings { public Dictionary<string, ConnectionEntry?> Servers { get; set; } = []; }
    public sealed class ConnectionEntry
    {
        public string? Type { get; set; }
        public string? Command { get; set; }
        public string? Url { get; set; }
        public string? BearerToken { get; set; }
        public List<string> Args { get; set; } = [];
        public Dictionary<string, string?> Env { get; set; } = [];
        public bool Trusted { get; set; }
        public bool Required { get; set; }
    }
    public sealed class CredentialBindings { public Dictionary<string, string?> Bindings { get; set; } = []; }
}
