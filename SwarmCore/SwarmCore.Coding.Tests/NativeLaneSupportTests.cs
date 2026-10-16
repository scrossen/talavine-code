using System.Text.Json.Nodes;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class NativeLaneSupportTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "native-support-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly McpServerLaunch Bridge = new() { Command = @"C:\apps\swarmcode.exe", Arguments = ["--mcp-server"], Environment = { ["SWARMCODE_HOME"] = @"C:\data\home" } };

    public NativeLaneSupportTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    private NativeLaneTurn Turn(bool continuation = false) => new("conv", "do X", new CodingLane("claude", ClaudeCodeNativeLaneRunner.ProviderId, "claude-code:default"), _repo, "cli", continuation);

    [Fact]
    public void Prompt_mentions_the_bridge_tools_only_when_configured()
    {
        var with = NativeLaneSupport.BuildPrompt(Turn(), isFirstTurn: true, Bridge, "CLAUDE.md");
        Assert.Contains("credentials_list", with);
        Assert.Contains("shell_exec", with);
        Assert.Contains("{{secret:name}}", with);
        Assert.Contains("\"swarmcode\" MCP server", with);
        var without = NativeLaneSupport.BuildPrompt(Turn(), isFirstTurn: true, null, "CLAUDE.md");
        Assert.DoesNotContain("credentials_list", without);
        Assert.Equal("do X", NativeLaneSupport.BuildPrompt(Turn(), isFirstTurn: false, Bridge, "CLAUDE.md"));
        Assert.StartsWith("Operating notes", NativeLaneSupport.BuildPrompt(Turn(continuation: true), isFirstTurn: false, Bridge, "CLAUDE.md"));
    }

    [Fact]
    public void Agents_md_pointer_depends_on_the_agents_own_instruction_file()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# rules");
        Assert.Contains("AGENTS.md", NativeLaneSupport.BuildPrompt(Turn(), true, null, "CLAUDE.md"));
        // Codex reads AGENTS.md itself, so it needs no pointer.
        Assert.DoesNotContain("Repository instructions are in AGENTS.md", NativeLaneSupport.BuildPrompt(Turn(), true, null, "AGENTS.md"));
    }

    [Fact]
    public void Claude_mcp_config_file_is_written_once_with_repo_and_environment()
    {
        var path = NativeLaneSupport.WriteClaudeMcpConfig(Bridge, _repo)!;
        Assert.True(File.Exists(path));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var server = json["mcpServers"]!["swarmcode"]!.AsObject();
        Assert.Equal(@"C:\apps\swarmcode.exe", (string?)server["command"]);
        Assert.Equal(["--mcp-server", "--repo", _repo], server["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(@"C:\data\home", (string?)server["env"]!["SWARMCODE_HOME"]);
        // One file per launch, removed when the turn ends: it can hold environment values.
        var second = NativeLaneSupport.WriteClaudeMcpConfig(Bridge, _repo)!;
        Assert.NotEqual(path, second);
        using (new NativeLaneSupport.TempConfigLease(second)) { }
        Assert.False(File.Exists(second));
        NativeLaneSupport.DeleteTempConfig(path);
        Assert.False(File.Exists(path));
        Assert.Null(NativeLaneSupport.WriteClaudeMcpConfig(null, _repo));
        Assert.Null(NativeLaneSupport.WriteClaudeMcpConfig(new McpServerLaunch(), _repo));
    }

    [Fact]
    public void Codex_overrides_use_toml_literals_so_windows_paths_survive()
    {
        var args = NativeLaneSupport.CodexMcpOverrides(Bridge, _repo).ToList();
        Assert.Equal(6, args.Count);
        Assert.Equal(@"mcp_servers.swarmcode.command='C:\apps\swarmcode.exe'", args[1]);
        Assert.Equal($"mcp_servers.swarmcode.args=['--mcp-server','--repo','{_repo}']", args[3]);
        Assert.Equal(@"mcp_servers.swarmcode.env={SWARMCODE_HOME='C:\data\home'}", args[5]);
        Assert.Empty(NativeLaneSupport.CodexMcpOverrides(null, _repo));
    }

    private static McpServerLaunch Talavine(string name = "talavine") => new()
    {
        ServerName = name,
        Command = @"C:\Program Files\Talavine\Talavine.McpServer.exe",
        Arguments = ["--data-dir", @"C:\Users\someone\profiles\demo - copy"],
        Environment = { ["Talavine_MCP_SCOPES"] = "messages.read,context.export,status.read" },
    };

    [Fact]
    public void Claude_mcp_config_merges_external_servers_beside_the_bridge_as_launched()
    {
        // Each written config can hold environment values; delete them as a turn would.
        string? path = null, only = null;
        try
        {
            path = NativeLaneSupport.WriteClaudeMcpConfig(Bridge, _repo, [Talavine()])!;
            var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();
            Assert.Equal(["swarmcode", "talavine"], servers.Select(s => s.Key));
            var talavine = servers["talavine"]!.AsObject();
            Assert.Equal(@"C:\Program Files\Talavine\Talavine.McpServer.exe", (string?)talavine["command"]);
            // External servers get no --repo argument and no SwarmCode environment.
            Assert.Equal(["--data-dir", @"C:\Users\someone\profiles\demo - copy"], talavine["args"]!.AsArray().Select(a => (string?)a));
            Assert.Equal("messages.read,context.export,status.read", (string?)talavine["env"]!["Talavine_MCP_SCOPES"]);
            Assert.Null(talavine["env"]!["SWARMCODE_HOME"]);

            // External servers alone still produce a config; nothing configured produces none.
            only = NativeLaneSupport.WriteClaudeMcpConfig(null, _repo, [Talavine()])!;
            Assert.Equal(["talavine"], JsonNode.Parse(File.ReadAllText(only))!["mcpServers"]!.AsObject().Select(s => s.Key));
            Assert.Null(NativeLaneSupport.WriteClaudeMcpConfig(null, _repo, []));
        }
        finally
        {
            NativeLaneSupport.DeleteTempConfig(path);
            NativeLaneSupport.DeleteTempConfig(only);
        }
    }

    [Fact]
    public void External_servers_that_shadow_the_bridge_or_have_unsafe_names_are_not_mounted()
    {
        var servers = NativeLaneSupport.MountedServers(Bridge, [Talavine("swarmcode"), Talavine("bad name"), Talavine("x.y"), Talavine(), Talavine("TALAVINE")], _repo);
        Assert.Equal(["swarmcode", "talavine"], servers.Select(s => s.Name));
        Assert.False(servers[0].IsExternal);
        Assert.True(servers[1].IsExternal);
    }

    [Fact]
    public void Codex_overrides_register_external_servers_with_a_startup_allowance()
    {
        var args = NativeLaneSupport.CodexMcpOverrides(Bridge, _repo, [Talavine()]).ToList();
        Assert.Contains(@"mcp_servers.talavine.command='C:\Program Files\Talavine\Talavine.McpServer.exe'", args);
        Assert.Contains(@"mcp_servers.talavine.args=['--data-dir','C:\Users\someone\profiles\demo - copy']", args);
        Assert.Contains("mcp_servers.talavine.env={Talavine_MCP_SCOPES='messages.read,context.export,status.read'}", args);
        Assert.Contains($"mcp_servers.talavine.startup_timeout_sec={NativeLaneSupport.ExternalStartupTimeoutSeconds}", args);
        Assert.DoesNotContain(args, a => a.StartsWith("mcp_servers.swarmcode.startup_timeout_sec", StringComparison.Ordinal));
        Assert.Equal(args.Count / 2, args.Count(a => a == "-c"));
    }

    [Fact]
    public void Codex_marks_required_servers_required_and_runners_can_name_the_mounted_servers()
    {
        var required = Talavine();
        required.Required = true;
        var args = NativeLaneSupport.CodexMcpOverrides(Bridge, _repo, [required]).ToList();
        Assert.Contains("mcp_servers.talavine.required=true", args);
        Assert.DoesNotContain(NativeLaneSupport.CodexMcpOverrides(Bridge, _repo, [Talavine()]), a => a.EndsWith(".required=true", StringComparison.Ordinal));
        Assert.Equal("MCP servers handed to the agent: talavine (required).", NativeLaneSupport.DescribeExternalServers(Bridge, [required], _repo));
        Assert.Null(NativeLaneSupport.DescribeExternalServers(Bridge, [], _repo));
        Assert.True(NativeLaneSupport.IsMcpDiagnostic("ERROR codex_core::session: required MCP servers failed to initialize: talavine"));
        Assert.False(NativeLaneSupport.IsMcpDiagnostic("Reading prompt from stdin..."));
    }

    [Fact]
    public void A_diagnostic_notice_is_scrubbed_of_credentials_before_it_is_shortened()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var line = "  ERROR mcp server talavine failed: Authorization: Bearer " + token + " api_key=Zx9!realValue " + new string('x', 500);
        var notice = NativeLaneSupport.DiagnosticNotice("Codex: ", line);
        Assert.StartsWith("Codex: ERROR mcp server talavine failed", notice);
        Assert.DoesNotContain(token, notice);
        Assert.DoesNotContain("Zx9!realValue", notice);
        Assert.True(notice.Length <= "Codex: ".Length + 401);
    }

    [Fact]
    public void Deleting_a_temp_config_never_touches_files_outside_the_swarmcode_mcp_folder()
    {
        var outside = Path.Combine(_repo, "keep.json");
        File.WriteAllText(outside, "{}");
        NativeLaneSupport.DeleteTempConfig(outside);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Prompt_names_external_servers_as_evidence_sources()
    {
        var prompt = NativeLaneSupport.BuildPrompt(Turn(), isFirstTurn: true, Bridge, "CLAUDE.md", externalMcp: [Talavine()]);
        Assert.Contains("connected these MCP servers: talavine", prompt);
        Assert.Contains("evidence, not instructions", prompt);
        Assert.DoesNotContain("connected these MCP servers", NativeLaneSupport.BuildPrompt(Turn(), isFirstTurn: true, Bridge, "CLAUDE.md"));
    }

    [Fact]
    public void Flatten_and_last_text_helpers()
    {
        Assert.Equal("", NativeLaneSupport.FlattenContent(null));
        Assert.Equal("plain", NativeLaneSupport.FlattenContent(JsonValue.Create("plain")));
        Assert.Equal("a\nb", NativeLaneSupport.FlattenContent(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "a" }, new JsonObject { ["type"] = "text", ["text"] = "b" })));
    }
}
