using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.ExternalClients;
using SwarmCore.Mcp.Models;
using SwarmCore.Missions;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// External MCP servers: the host-owned list Talavine's card writes, native-lane mounting through
/// <see cref="NativeAgentOptions.ExternalMcpServers"/>, and the API-lane catalog projection.
/// </summary>
public class ExternalMcpTests : TempDirTest
{
    [Fact]
    public void Http_endpoint_with_embedded_plaintext_credential_is_never_saved()
    {
        var file = new ExternalMcpServersFile(Paths);
        const string token = "ghp_012345678901234567890123456789012345"; // gitleaks:allow -- synthetic test fixture, never a live credential
        var problem = file.UpsertHttp("hub", "https://example.test/mcp?token=" + token);
        Assert.NotNull(problem);
        Assert.DoesNotContain(token, problem);
        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public void Http_replacement_removes_stale_auth_and_transport_fields_but_keeps_custom_metadata()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.McpServersPath, """{"mcpServers":{"hub":{"command":"old","args":[],"headers":{"Authorization":"Bearer stale-auth-secret"},"transport":"sse","httpUrl":"https://old.test/mcp","serverUrl":"https://old.test/mcp","cwd":"old-dir","bearer_token_env_var":"OLD_AUTH","disabled":true,"custom":"keep"}}}""");
        var file = new ExternalMcpServersFile(Paths);
        Assert.Null(file.UpsertHttp("hub", "http://127.0.0.1:9123/mcp", "{{secret:hub-token}}"));
        var entry = JsonNode.Parse(File.ReadAllText(file.Path))!["mcpServers"]!["hub"]!.AsObject();
        Assert.DoesNotContain("stale-auth-secret", entry.ToJsonString());
        foreach (var key in new[] { "command", "args", "headers", "transport", "httpUrl", "serverUrl", "cwd", "bearer_token_env_var" })
            Assert.False(entry.ContainsKey(key), key);
        Assert.True(entry["disabled"]!.GetValue<bool>());
        Assert.Equal("keep", entry["custom"]!.GetValue<string>());
        Assert.Single(file.Read().Servers);
    }

    [Fact]
    public void Saving_an_http_server_keeps_the_files_escaping_and_line_endings()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.McpServersPath, "{\r\n  \"mcpServers\": {}\r\n}\r\n");
        var file = new ExternalMcpServersFile(Paths);

        Assert.Null(file.UpsertHttp("hub", "https://example.test/mcp?region=eu&mode=read+only"));

        var text = File.ReadAllText(file.Path);
        Assert.Contains("https://example.test/mcp?region=eu&mode=read+only", text);
        Assert.DoesNotContain("\\u0026", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Http_and_stdio_replacement_keep_flags_and_remove_old_transport_coordinates()
    {
        var file = new ExternalMcpServersFile(Paths);
        Assert.Null(file.Upsert("hub", TalavineEntry, trusted: true, required: true));
        Assert.Null(file.UpsertHttp("hub", "http://127.0.0.1:9123/mcp", "{{secret:hub-token}}", trusted: true, required: true));
        var http = Assert.Single(file.Read().Servers);
        Assert.Null(http.Launch);
        Assert.True(http.Trusted);
        Assert.True(http.Required);
        Assert.Null(file.Upsert("hub", TalavineEntry));
        var stdio = Assert.Single(file.Read().Servers);
        Assert.Equal(TalavineEntry, stdio.Launch);
        Assert.Null(stdio.Url);
        Assert.True(stdio.Trusted);
        Assert.True(stdio.Required);
        Assert.DoesNotContain("bearerToken", File.ReadAllText(file.Path));
    }

    [Fact]
    public void Http_entry_rejects_even_a_malformed_command_property()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.McpServersPath, """{"mcpServers":{"hub":{"url":"http://127.0.0.1:9123/mcp","command":42}}}""");
        var snapshot = new ExternalMcpServersFile(Paths).Read();
        Assert.Empty(snapshot.Servers);
        Assert.Single(snapshot.Problems);
    }

    [Fact]
    public async Task Http_connection_failure_cannot_echo_bearer_into_status()
    {
        Assert.Null(new ExternalMcpServersFile(Paths).UpsertHttp("hub", "http://127.0.0.1:9123/mcp", "{{secret:hub-token}}"));
        var fake = new FakeConnectorManager { EchoBearerFailure = true };
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(fake));
        await sp.GetRequiredService<SwarmCore.Secrets.ISecretVault>().SetAsync(new() { Name = "hub-token", Value = "synthetic-http-bearer" });
        var external = sp.GetRequiredService<ExternalMcpToolCatalog>();
        await external.EnsurePublishedAsync();
        var problem = Assert.Single(external.Status()).Problem!;
        Assert.DoesNotContain("synthetic-http-bearer", problem);
        Assert.Contains("[redacted]", problem);
    }
    [Fact]
    public async Task Http_server_uses_runtime_vault_bearer_and_one_shared_connection()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.McpServersPath, """{"mcpServers":{"hub":{"url":"http://127.0.0.1:9123/mcp","bearerToken":"{{secret:hub-token}}"}}}""");
        var snapshot = new ExternalMcpServersFile(Paths).Read();
        Assert.Empty(snapshot.Problems);
        Assert.Single(snapshot.Servers);
        var fake = new FakeConnectorManager();
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(fake));
        await sp.GetRequiredService<SwarmCore.Secrets.ISecretVault>().SetAsync(new() { Name = "hub-token", Value = "test-http-bearer" });
        var resolver = sp.GetRequiredService<IProfileResolver>();
        await resolver.ResolveAsync("one");
        await resolver.ResolveAsync("two");
        var connection = Assert.Single(fake.Connected);
        Assert.Equal("streamable-http", connection.TransportType);
        Assert.Equal("http://127.0.0.1:9123/mcp", connection.Url);
        Assert.Equal("test-http-bearer", connection.ApiKey);
        Assert.Null(connection.Command);
        Assert.DoesNotContain("test-http-bearer", File.ReadAllText(Paths.McpServersPath));
    }

    [Fact]
    public async Task Missing_http_credential_is_visible_and_never_sends_a_placeholder()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.McpServersPath, """{"mcpServers":{"hub":{"url":"http://127.0.0.1:9123/mcp","bearerToken":"{{secret:missing}}"}}}""");
        var fake = new FakeConnectorManager();
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(fake));
        var external = sp.GetRequiredService<ExternalMcpToolCatalog>();
        await external.EnsurePublishedAsync();
        Assert.Empty(fake.Connected);
        Assert.Contains("credential", Assert.Single(external.Status()).Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://example.test/mcp", "{{secret:key}}")]
    [InlineData("https://user:password@example.test/mcp", "{{secret:key}}")]
    [InlineData("https://example.test/mcp", "plain-token")]
    public void Http_config_rejects_invalid_endpoints_and_plaintext_auth(string url, string bearer)
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.McpServersPath, new JsonObject { ["mcpServers"] = new JsonObject { ["hub"] =
            new JsonObject { ["url"] = url, ["bearerToken"] = bearer } } }.ToJsonString());
        var snapshot = new ExternalMcpServersFile(Paths).Read();
        Assert.Empty(snapshot.Servers);
        Assert.Single(snapshot.Problems);
        Assert.DoesNotContain(bearer, snapshot.Problems[0]);
    }
    private static readonly McpServerLaunchEntry TalavineEntry = new(
        @"C:\Program Files\Talavine\Talavine.McpServer.exe",
        ["--data-dir", @"C:\Users\someone\Talavine\profiles\work"],
        [new("Talavine_MCP_SCOPES", "messages.read,context.export,status.read")]);

    // ----- the server list file -----

    [Fact]
    public void Talavine_card_registration_lands_in_the_file_swarmcode_reads()
    {
        // The Talavine desktop card uses the shared catalog, locator and registrar; point the locator at this data folder.
        var environment = new ExternalMcpClientEnvironment
        {
            Platform = ExternalMcpClientPlatform.Windows,
            HomeDirectory = Tmp,
            LocalAppDataDirectory = Path.Combine(Tmp, "local"),
            SwarmCodeHome = Paths.RootDirectory,
        };
        var location = new ExternalMcpClientLocator(environment).Locate(ExternalMcpClients.SwarmCode);
        Assert.True(location.IsInstalled);
        Assert.Equal(Paths.McpServersPath, location.ConfigPath);

        var registrar = new ExternalMcpClientRegistrar(new McpClientConfigFileStore(backupLabel: "talavine"));
        var result = registrar.Connect(location, McpServerRegistration.Available("talavine", TalavineEntry));
        Assert.Equal(ExternalMcpClientChangeOutcome.Connected, result.Outcome);
        Assert.Equal(ExternalMcpClientConnectionState.Connected,
            registrar.GetStatus(location, McpServerRegistration.Available("talavine", TalavineEntry)).State);

        var snapshot = new ExternalMcpServersFile(Paths).Read();
        var server = Assert.Single(snapshot.Servers);
        Assert.Equal("talavine", server.Name);
        Assert.Equal(TalavineEntry, server.Launch);
        Assert.Empty(snapshot.Problems);
    }

    [Fact]
    public void File_upsert_read_and_remove_round_trip_and_report_skipped_entries()
    {
        var file = new ExternalMcpServersFile(Paths);
        Assert.Empty(file.Read().Servers);
        Assert.Null(file.Upsert("talavine", TalavineEntry));
        Assert.NotNull(file.Upsert("swarmcode", TalavineEntry)); // reserved for the credential bridge
        Assert.NotNull(file.Upsert("has space", TalavineEntry));
        Assert.Equal("talavine", Assert.Single(file.Read().Servers).Name);

        // Entries written by hand that SwarmCode cannot use are reported, not silently dropped.
        File.WriteAllText(Paths.McpServersPath, """
            { "mcpServers": {
                "talavine": { "command": "t.exe", "args": ["--data-dir", "p"] },
                "swarmcode": { "command": "x.exe" },
                "remote": { "url": "https://example.test/mcp" },
                "dot.name": { "command": "y.exe" } } }
            """);
        var snapshot = file.Read();
        Assert.Equal(["talavine", "remote"], snapshot.Servers.Select(s => s.Name));
        Assert.Equal(2, snapshot.Problems.Count);

        Assert.Null(file.Remove("talavine"));
        Assert.Equal("remote", Assert.Single(file.Read().Servers).Name);
        Assert.Null(file.Remove("not-there"));
    }

    // ----- composition and native lanes -----

    private ServiceProvider Provider(Action<IServiceCollection>? configure = null)
    {
        var repo = Directory.CreateDirectory(Path.Combine(Tmp, "repo")).FullName;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SwarmCore.Secrets.IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = repo, DataPaths = Paths, ConfigureServices = configure });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Listed_servers_are_mounted_for_native_lanes_and_the_profile_resolver_is_decorated()
    {
        Assert.Null(new ExternalMcpServersFile(Paths).Upsert("talavine", TalavineEntry));
        using var sp = Provider();

        var native = sp.GetRequiredService<IOptions<CodingOptions>>().Value.NativeAgents;
        var mounted = Assert.Single(native.ExternalMcpServers);
        Assert.Equal("talavine", mounted.ServerName);
        Assert.Equal(TalavineEntry.Command, mounted.Command);
        Assert.Equal(TalavineEntry.Arguments, mounted.Arguments);
        Assert.Equal("messages.read,context.export,status.read", mounted.Environment["Talavine_MCP_SCOPES"]);

        Assert.IsType<ExternalMcpProfileResolver>(Assert.IsType<SwarmCore.Coding.Extensions.HookedProfileResolver>(sp.GetRequiredService<IProfileResolver>()).Inner);
        Assert.NotNull(sp.GetRequiredService<IMcpConnectorManager>());
    }

    // ----- API-lane projection -----

    [Theory]
    [InlineData(true, null, null, true, SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly)]
    [InlineData(true, true, true, true, SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly)]
    [InlineData(true, null, null, false, SwarmCore.Agent.Catalog.ToolSideEffect.ExternalSend)]
    [InlineData(true, false, false, false, SwarmCore.Agent.Catalog.ToolSideEffect.ExternalSend)]
    [InlineData(null, null, null, false, SwarmCore.Agent.Catalog.ToolSideEffect.Destructive)]
    [InlineData(false, true, null, true, SwarmCore.Agent.Catalog.ToolSideEffect.Destructive)]
    [InlineData(false, false, null, false, SwarmCore.Agent.Catalog.ToolSideEffect.ExternalSend)]
    [InlineData(false, false, false, false, SwarmCore.Agent.Catalog.ToolSideEffect.WritesLocal)]
    public void Side_effect_trusts_a_read_only_hint_only_from_a_trusted_server(bool? readOnly, bool? destructive, bool? openWorld, bool trusted, SwarmCore.Agent.Catalog.ToolSideEffect expected)
    {
        Assert.Equal(expected, McpCatalogProjection.ClassifySideEffect(new McpToolAnnotationsInfo(null, readOnly, destructive, null, openWorld), trusted));
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.Destructive, McpCatalogProjection.ClassifySideEffect(null));
    }

    [Fact]
    public async Task Provider_names_stay_within_64_characters_and_unique_across_servers()
    {
        var fake = new FakeConnectorManager();
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(fake));
        var catalog = sp.GetRequiredService<ICapabilityCatalog>();
        var longName = "summarize_every_conversation_with_this_contact_across_every_source_and_account";

        fake.Tools = [new(longName, "long", null), new("c", "c", null)];
        var hub = await McpCatalogProjection.PublishAsync(catalog, fake, Guid.NewGuid(), "a_b");
        fake.Tools = [new("b_c", "collides", null)];
        var other = await McpCatalogProjection.PublishAsync(catalog, fake, Guid.NewGuid(), "a");

        var all = hub.Concat(other).ToList();
        Assert.All(all, id => Assert.True(McpCatalogProjection.ProviderName(id).Length <= McpCatalogProjection.MaxProviderNameLength, id));
        Assert.All(all, id => Assert.Equal(3, id.Split('.').Length));
        Assert.Equal(all.Count, all.Select(McpCatalogProjection.ProviderName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("mcp.a_b.c", hub); // the first server keeps the plain name
        var collided = Assert.Single(other);
        Assert.StartsWith("mcp.a.b_c-", collided); // mcp.a.b_c would reach providers as mcp_a_b_c too
        Assert.Equal(collided, McpCatalogProjection.HashedToolId("a", "b_c")); // stable across runs
        Assert.NotNull(catalog.Get(collided));

        // Republishing the same server is idempotent: its own earlier ids do not count as collisions.
        Assert.Equal(other, await McpCatalogProjection.PublishAsync(catalog, fake, Guid.NewGuid(), "a"));
    }

    [Fact]
    public void Trusted_and_required_flags_round_trip_and_survive_a_talavine_card_update()
    {
        var file = new ExternalMcpServersFile(Paths);
        Assert.Null(file.Upsert("talavine", TalavineEntry, trusted: true, required: true));
        var server = Assert.Single(file.Read().Servers);
        Assert.True(server.Trusted);
        Assert.True(server.Required);
        Assert.True(server.ToLaunch().Trusted);

        // The Talavine card rewrites only the launch coordinates.
        var location = new ExternalMcpClientLocator(new ExternalMcpClientEnvironment
        {
            Platform = ExternalMcpClientPlatform.Windows, HomeDirectory = Tmp, SwarmCodeHome = Paths.RootDirectory,
        }).Locate(ExternalMcpClients.SwarmCode);
        var moved = new McpServerLaunchEntry(TalavineEntry.Command, ["--data-dir", @"C:\Users\someone\Talavine\profiles\home"], TalavineEntry.EnvironmentVariables);
        Assert.Equal(ExternalMcpClientChangeOutcome.Connected,
            new ExternalMcpClientRegistrar().Connect(location, McpServerRegistration.Available("talavine", moved)).Outcome);
        server = Assert.Single(file.Read().Servers);
        Assert.Equal(moved, server.Launch);
        Assert.True(server.Trusted && server.Required);

        Assert.Null(file.Upsert("talavine", moved, trusted: false, required: false));
        server = Assert.Single(file.Read().Servers);
        Assert.False(server.Trusted || server.Required);
    }

    [Fact]
    public async Task A_trusted_server_s_read_only_tools_are_read_only_and_an_untrusted_one_s_are_not()
    {
        Assert.Null(new ExternalMcpServersFile(Paths).Upsert("talavine", TalavineEntry, trusted: true, required: false));
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(new FakeConnectorManager()));
        var resolved = await sp.GetRequiredService<IProfileResolver>().ResolveAsync("conv");
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly, Assert.Single(resolved.Tools, t => t.ToolId == "mcp.talavine.query_email_context").SideEffect);
    }

    [Fact]
    public async Task A_server_that_dies_while_its_tools_are_published_is_restarted_on_the_next_turn()
    {
        Assert.Null(new ExternalMcpServersFile(Paths).Upsert("talavine", TalavineEntry));
        var fake = new FakeConnectorManager { FaultDuringList = true };
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(fake));
        var resolver = sp.GetRequiredService<IProfileResolver>();

        var first = await resolver.ResolveAsync("conv");
        Assert.DoesNotContain(first.Tools, t => t.ToolId.StartsWith("mcp.", StringComparison.Ordinal));

        fake.FaultDuringList = false;
        var second = await resolver.ResolveAsync("conv");
        Assert.Equal(2, fake.ConnectAttempts);
        Assert.Contains(second.Tools, t => t.ToolId == "mcp.talavine.query_email_context");
    }

    [Fact]
    public void Tool_ids_are_mcp_server_tool_with_unsafe_characters_replaced()
    {
        Assert.Equal("mcp.talavine.query_email_context", McpCatalogProjection.ToolId("talavine", "query_email_context"));
        Assert.Equal("mcp.my_hub.read_mail_now", McpCatalogProjection.ToolId("my hub", "read.mail now"));
    }

    [Fact]
    public async Task First_api_turn_publishes_the_servers_tools_and_calls_route_through_the_connector_manager()
    {
        Assert.Null(new ExternalMcpServersFile(Paths).Upsert("talavine", TalavineEntry));
        var fake = new FakeConnectorManager();
        using var sp = Provider(s => s.AddSingleton<IMcpConnectorManager>(fake));
        CodingToolRegistrar.RegisterAll(sp); // what the workspace host's hosted service does at start

        var resolved = await sp.GetRequiredService<IProfileResolver>().ResolveAsync("conv");

        var started = Assert.Single(fake.Connected);
        Assert.Equal("talavine", started.Name);
        Assert.Equal("stdio", started.TransportType);
        Assert.Equal(TalavineEntry.Command, started.Command);
        Assert.Equal("messages.read,context.export,status.read", started.EnvironmentVariables!["Talavine_MCP_SCOPES"]);

        var read = Assert.Single(resolved.Tools, t => t.ToolId == "mcp.talavine.query_email_context");
        // The server says read-only, but it is not marked trusted: the claim does not skip approval.
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ExternalSend, read.SideEffect);
        Assert.Equal(ToolDataAccessClass.UnclassifiedExternal, read.DataAccess);
        Assert.Equal("object", (string?)read.InputSchema!["type"]);
        var send = Assert.Single(resolved.Tools, t => t.ToolId == "mcp.talavine.send_message");
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.Destructive, send.SideEffect);
        Assert.Contains(resolved.Tools, t => t.ToolId == "git.status"); // the coding tools are still there

        var output = await read.Invoke(new JsonObject { ["topic"] = "renewal", ["limit"] = 3 }, CancellationToken.None);
        Assert.Equal("evidence for renewal", output);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("query_email_context", call.Tool);
        Assert.Equal(3L, call.Arguments["limit"]);

        // A second turn reuses the running server.
        await sp.GetRequiredService<IProfileResolver>().ResolveAsync("conv");
        Assert.Single(fake.Connected);
    }

    [Fact]
    public async Task A_server_that_fails_to_start_is_skipped_and_retried_later()
    {
        Assert.Null(new ExternalMcpServersFile(Paths).Upsert("talavine", TalavineEntry));
        var fake = new FakeConnectorManager { FailConnect = true };
        var clock = new ManualClock();
        using var sp = Provider(s =>
        {
            s.AddSingleton<IMcpConnectorManager>(fake);
            s.AddSingleton(p => new ExternalMcpToolCatalog(p.GetRequiredService<IOptions<CodingOptions>>(), fake,
                p.GetRequiredService<ICapabilityCatalog>(), NullLogger<ExternalMcpToolCatalog>.Instance, clock));
        });
        var resolver = sp.GetRequiredService<IProfileResolver>();

        var first = await resolver.ResolveAsync("conv");
        Assert.DoesNotContain(first.Tools, t => t.ToolId.StartsWith("mcp.", StringComparison.Ordinal));
        Assert.Equal(1, fake.ConnectAttempts);
        Assert.Contains(sp.GetRequiredService<ExternalMcpToolCatalog>().Status(), s => s.Server == "talavine" && s.Problem is not null);

        await resolver.ResolveAsync("conv");
        Assert.Equal(1, fake.ConnectAttempts); // not before the retry delay

        fake.FailConnect = false;
        clock.Advance(ExternalMcpToolCatalog.RetryAfter);
        var later = await resolver.ResolveAsync("conv");
        Assert.Equal(2, fake.ConnectAttempts);
        Assert.Contains(later.Tools, t => t.ToolId == "mcp.talavine.query_email_context");

        // The server dies: its tools are withdrawn and it is restarted on the next turn.
        fake.RaiseFaulted();
        var afterCrash = sp.GetRequiredService<ICapabilityCatalog>().All();
        Assert.DoesNotContain(afterCrash, t => t.ToolId.StartsWith("mcp.talavine.", StringComparison.Ordinal));
        var restarted = await resolver.ResolveAsync("conv");
        Assert.Equal(3, fake.ConnectAttempts);
        Assert.Contains(restarted.Tools, t => t.ToolId == "mcp.talavine.query_email_context");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeConnectorManager : IMcpConnectorManager
    {
        public List<McpConnectorOptions> Connected { get; } = [];
        public List<(string Tool, IDictionary<string, object?> Arguments)> Calls { get; } = [];
        public bool FailConnect { get; set; }
        public bool EchoBearerFailure { get; set; }
        public bool FaultDuringList { get; set; }
        public List<McpToolInfo>? Tools { get; set; }
        public int ConnectAttempts { get; private set; }
        private Guid _id;
        private string _name = "";

        public event EventHandler<McpConnectionChangedEventArgs>? ConnectionsChanged;

        public Task<Guid> ConnectAsync(McpConnectorOptions options, CancellationToken ct = default)
        {
            ConnectAttempts++;
            if (EchoBearerFailure) throw new InvalidOperationException("Authorization rejected: " + options.ApiKey);
            if (FailConnect) throw new InvalidOperationException("server exited with code 3");
            Connected.Add(options);
            _id = Guid.NewGuid();
            _name = options.Name;
            return Task.FromResult(_id);
        }

        public void RaiseFaulted() => ConnectionsChanged?.Invoke(this, new McpConnectionChangedEventArgs(_id, _name, McpConnectionChangeKind.Faulted));

        public Task DisconnectAsync(Guid connectionId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpConnectionInfo>>([]);

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(Guid connectionId, CancellationToken ct = default)
        {
            if (FaultDuringList) RaiseFaulted();
            if (Tools is not null) return Task.FromResult<IReadOnlyList<McpToolInfo>>(Tools);
            return Task.FromResult<IReadOnlyList<McpToolInfo>>(
            [
                new("query_email_context", "Query the user's email.", """{"type":"object","properties":{"topic":{"type":"string"},"limit":{"type":"integer"}}}""",
                    new McpToolAnnotationsInfo(ReadOnlyHint: true)),
                new("send_message", "Send a message.", null),
            ]);
        }

        public Task<McpToolResult> InvokeToolAsync(Guid connectionId, string toolName, IDictionary<string, object?>? arguments = null, CancellationToken ct = default)
        {
            Calls.Add((toolName, arguments ?? new Dictionary<string, object?>()));
            return Task.FromResult(new McpToolResult(false, "evidence for " + arguments?["topic"]));
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
