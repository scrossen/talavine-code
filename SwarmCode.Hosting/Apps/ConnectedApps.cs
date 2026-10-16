using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Agent.AppControl;
using SwarmCore.Mcp.Core;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Apps;

/// <summary>
/// The person's apps SwarmCode can operate by prompt: Talavine and other apps running on this computer that publish
/// an app-control endpoint, plus any MCP server connected by URL (the CRM signs in through its OAuth). Connecting
/// saves the server in <c>mcp-servers.json</c> (token in the vault, only a <c>{{secret:...}}</c> reference in the
/// file), so it also mounts as ordinary MCP tools in later turns; the <c>apps</c> tools here work at once, in every
/// lane, because they resolve the app on each call.
/// </summary>
public sealed class ConnectedApps(SwarmCodeDataPaths paths, ISecretVault vault, Microsoft.Extensions.Logging.ILogger? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly HttpClient OAuthHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The URL of SwarmCode's published OAuth client document, when one is configured for hosted servers.</summary>
    public Uri? PublicClientIdUrl { get; init; } = Uri.TryCreate(Environment.GetEnvironmentVariable("SWARMCODE_OAUTH_CLIENT_ID"), UriKind.Absolute, out var u) ? u : null;

    /// <summary>Opens a URL for the person; the default uses the OS shell.</summary>
    public Func<Uri, Task> OpenBrowser { get; init; } = DefaultOpenBrowser;

    private ExternalMcpServersFile File => new(paths);

    public sealed record AppInfo(string Name, string DisplayName, string Url, string Source, bool Running, bool Saved);

    public async Task<IReadOnlyList<AppInfo>> ListAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var saved = File.Read().Servers.Where(s => s.Url is not null).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppInfo>();
        foreach (var endpoint in AppControlDiscovery.List())
            apps.Add(new(endpoint.App, endpoint.DisplayName, endpoint.Url, "running on this computer", true, saved.ContainsKey(endpoint.App)));
        foreach (var server in saved.Values.Where(s => apps.All(a => !string.Equals(a.Name, s.Name, StringComparison.OrdinalIgnoreCase))))
            apps.Add(new(server.Name, server.Name, server.Url!, "connected by URL", false, true));
        return apps;
    }

    /// <summary>
    /// Connects <paramref name="target"/>: the name of an app running on this computer (for example "talavine"),
    /// or the MCP URL of a server such as a CRM (<c>https://crm.example.com/mcp</c>). A URL signs in through the
    /// server's OAuth in the person's browser; <paramref name="tokenSecret"/> instead names a vault entry that
    /// already holds an API key for it.
    /// </summary>
    public async Task<string> ConnectAsync(string target, string? name, string? tokenSecret, string? scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Say which app to connect: an app name such as talavine, or a server URL.");
        target = target.Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            var endpoint = AppControlDiscovery.Find(target)
                ?? throw new InvalidOperationException(
                    $"No app named '{target}' is running on this computer with app control on. Running apps: "
                    + string.Join(", ", AppControlDiscovery.List().Select(a => a.App).DefaultIfEmpty("none"))
                    + ". Open it (Talavine publishes its endpoint when it starts) or give its MCP URL.");
            var secretName = SecretFor(endpoint.App);
            await vault.SetAsync(new SecretWriteRequest { Name = secretName, Value = endpoint.Token, Kind = SecretKinds.Generic, Site = endpoint.Url }, ct).ConfigureAwait(false);
            var problem = File.UpsertHttp(endpoint.App, endpoint.Url, SecretReference.Placeholder(secretName), trusted: true, required: false);
            if (problem is not null) throw new InvalidOperationException(problem);
            return Describe(endpoint.App, endpoint.DisplayName, endpoint.Url, "Connected to the app running on this computer.");
        }

        var serverName = string.IsNullOrWhiteSpace(name) ? NameFromUrl(url) : name.Trim().ToLowerInvariant();
        if (!ExternalMcpServersFile.IsValidName(serverName)) throw new ArgumentException($"'{serverName}' is not a usable name; pass a short name such as crm.");
        var tokenName = SecretFor(serverName);
        string note;
        if (!string.IsNullOrWhiteSpace(tokenSecret))
        {
            var entry = await vault.GetEntryAsync(tokenSecret.Trim(), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No stored credential named '{tokenSecret}'. Ask the person to save the key with /secret {tokenSecret} and try again.");
            // A stored key goes only to the site it was saved for, so an injected instruction cannot send, say, a
            // GitHub token to another server. The person records the site when saving the key.
            if (!Uri.TryCreate(entry.Site, UriKind.Absolute, out var site)
                || !string.Equals(site.GetLeftPart(UriPartial.Authority), url.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The stored credential '{tokenSecret}' is not saved for {url.GetLeftPart(UriPartial.Authority)}. Ask the person to save the key "
                    + $"for that site (in Settings > Credentials, with the site set to {url.GetLeftPart(UriPartial.Authority)}) and try again.");
            tokenName = tokenSecret.Trim();
            note = "Connected with the stored key.";
        }
        else
        {
            var oauth = new McpOAuthLoopbackClient(OAuthHttp);
            var result = await oauth.SignInAsync(url, "Talavine Code", PublicClientIdUrl, scope, async approveUrl =>
            {
                // The browser may not open (no desktop, another default browser); the link is in the log to open by hand.
                logger?.LogInformation("Sign-in for {Server}: approve at {Url}", serverName, approveUrl);
                await OpenBrowser(approveUrl).ConfigureAwait(false);
            }, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            await vault.SetAsync(new SecretWriteRequest { Name = tokenName, Value = result.AccessToken, Kind = SecretKinds.Generic, Site = url.GetLeftPart(UriPartial.Authority) }, ct).ConfigureAwait(false);
            note = "Signed in" + (string.IsNullOrWhiteSpace(result.Scope) ? "." : " with access: " + result.Scope + ".");
        }
        var saveProblem = File.UpsertHttp(serverName, url.ToString(), SecretReference.Placeholder(tokenName), trusted: false, required: false);
        if (saveProblem is not null) throw new InvalidOperationException(saveProblem);
        // Prove the connection now rather than on first use.
        await using var relay = await OpenAsync(serverName, ct).ConfigureAwait(false);
        var tools = await relay.ListToolsAsync(null, ct).ConfigureAwait(false);
        return Describe(serverName, serverName, url.ToString(), note + $" It offers {tools.Tools.Count} tools.");
    }

    public async Task<string> DisconnectAsync(string name, CancellationToken ct = default)
    {
        var problem = File.Remove(name);
        await vault.DeleteAsync(SecretFor(name), ct).ConfigureAwait(false);
        return problem ?? $"Disconnected {name}.";
    }

    /// <summary>What the app can do. Apps with app control answer from their own catalog; other servers list their MCP tools.</summary>
    public async Task<string> FindToolsAsync(string app, string? query, int skip, CancellationToken ct = default)
    {
        await using var relay = await OpenAsync(app, ct).ConfigureAwait(false);
        var tools = (await relay.ListToolsAsync(null, ct).ConfigureAwait(false)).Tools;
        if (Named(tools, "find_tools") is { } find)
            return await CallRawAsync(relay, find, new JsonObject { ["query"] = query, ["skip"] = skip }, ct).ConfigureAwait(false);
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = tools
            .Where(t => terms.Length == 0 || terms.Any(term => (t.Name + " " + t.Description).Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(t => new { id = t.Name, description = t.Description, readOnly = t.Annotations?.ReadOnlyHint, inputSchema = t.InputSchema })
            .ToList();
        return JsonSerializer.Serialize(new { app, tools = matches.Skip(skip).Take(AppControlToolSurface.PageSize), total = matches.Count,
            how = "Call one with apps_call_tool using its id and an input matching inputSchema." }, Json);
    }

    /// <summary>Runs one of the app's tools for the person.</summary>
    public async Task<string> CallToolAsync(string app, string tool, JsonObject input, string request, string? operationId, CancellationToken ct = default)
    {
        await using var relay = await OpenAsync(app, ct).ConfigureAwait(false);
        var tools = (await relay.ListToolsAsync(null, ct).ConfigureAwait(false)).Tools;
        var call = Named(tools, "call_tool");
        if (tools.Any(t => t.Name == tool) && tool != call && tool != Named(tools, "find_tools") && tool != Named(tools, "ask"))
            return await CallRawAsync(relay, tool, input, ct).ConfigureAwait(false);
        if (call is not null)
            return await CallRawAsync(relay, call, new JsonObject
            {
                ["tool"] = tool, ["input"] = input.DeepClone(), ["request"] = request, ["operation_id"] = operationId,
            }, ct).ConfigureAwait(false);
        throw new InvalidOperationException($"{app} has no tool '{tool}'. Use apps_find_tools to list what it offers.");
    }

    /// <summary>Asks the app's own assistant, when it has one.</summary>
    public async Task<string> AskAsync(string app, string request, string? conversationId, CancellationToken ct = default)
    {
        await using var relay = await OpenAsync(app, ct).ConfigureAwait(false);
        var tools = (await relay.ListToolsAsync(null, ct).ConfigureAwait(false)).Tools;
        var ask = Named(tools, "ask")
            ?? throw new InvalidOperationException($"{app} has no assistant to ask. Use apps_find_tools and apps_call_tool instead.");
        return await CallRawAsync(relay, ask, new JsonObject { ["request"] = request, ["conversation_id"] = conversationId }, ct).ConfigureAwait(false);
    }

    private async Task<HttpMcpRelay> OpenAsync(string app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("app is required (see apps_list).");
        // A running local app is always reached through its current discovery record, so a restart on another
        // port never strands a saved connection.
        if (AppControlDiscovery.Find(app) is { } endpoint)
            return await Connect(new Uri(endpoint.Url), endpoint.Token, app, ct).ConfigureAwait(false);
        var server = File.Read().Servers.FirstOrDefault(s => string.Equals(s.Name, app, StringComparison.OrdinalIgnoreCase) && s.Url is not null)
            ?? throw new InvalidOperationException($"'{app}' is not connected. Use apps_list to see apps, then apps_connect.");
        string? bearer = null;
        if (server.BearerTokenReference is { } reference)
        {
            var secret = SecretReference.Names(reference).FirstOrDefault()
                ?? throw new InvalidOperationException($"{app}'s saved token reference is malformed; connect it again.");
            bearer = await vault.GetValueAsync(secret, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{app}'s token is missing from the vault; connect it again.");
        }
        return await Connect(new Uri(server.Url!), bearer, app, ct).ConfigureAwait(false);
    }

    private static async Task<HttpMcpRelay> Connect(Uri url, string? bearer, string app, CancellationToken ct)
    {
        try { return await HttpMcpRelay.ConnectAsync(url, bearer, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Could not reach {app} at {url}: {ex.Message} If it was restarted or its sign-in expired, connect it again.");
        }
    }

    private static async Task<string> CallRawAsync(HttpMcpRelay relay, string tool, JsonObject arguments, CancellationToken ct)
    {
        var args = arguments.Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value, Json));
        var result = await relay.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = args }, ct).ConfigureAwait(false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return result.IsError == true ? throw new InvalidOperationException(text.Length > 0 ? text : "The app reported an error.") : text;
    }

    /// <summary>
    /// The app-control operation's actual tool name: bare ("find_tools") on an app's loopback server, or prefixed by
    /// the app ("crm.find_tools") on a server that also has its own tools. The assistant is "ask" or "*.assistant_ask".
    /// </summary>
    internal static string? Named(IEnumerable<Tool> tools, string operation)
    {
        var names = tools.Select(t => t.Name).ToList();
        if (names.Contains(operation)) return operation;
        var suffix = operation == "ask" ? ".assistant_ask" : "." + operation;
        return names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static string SecretFor(string app) => "app-" + app.ToLowerInvariant();

    private static string NameFromUrl(Uri url)
    {
        var host = url.Host.Split('.')[0].ToLowerInvariant();
        var clean = new string(host.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return clean is "localhost" or "127" or "" ? "crm" : clean.Length > 32 ? clean[..32] : clean;
    }

    private static string Describe(string name, string display, string url, string note)
        => JsonSerializer.Serialize(new
        {
            app = name, name = display, url, status = note,
            next = $"Use apps_find_tools with app=\"{name}\" to see what you can do, apps_call_tool to act, and apps_ask for why-questions.",
        }, Json);

    private static Task DefaultOpenBrowser(Uri url)
    {
        if (url.Scheme is not ("https" or "http")) return Task.CompletedTask;
        try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return Task.CompletedTask;
    }
}
