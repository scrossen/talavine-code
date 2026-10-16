using System.Diagnostics;
using System.Text.Json.Nodes;
using SwarmCore.Coding.Native;
using SwarmCore.Secrets;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class NativeHttpMcpTests
{
    private static readonly McpServerLaunch Http = new() { ServerName = "hub", Url = "http://127.0.0.1:9123/mcp",
        BearerTokenReference = "{{secret:hub-token}}", Required = true };
    private static readonly McpServerLaunch Bridge = new() { Command = "swarmcode", Arguments = ["--mcp-server"],
        Environment = new() { ["SWARMCODE_HOME"] = "owned-home" } };

    [Fact]
    public void Codex_authenticated_http_uses_host_relay_without_bearer_environment()
    {
        var args = NativeLaneSupport.CodexMcpOverrides(Bridge, ".", [Http]).ToArray();
        Assert.Contains("mcp_servers.hub.command='swarmcode'", args);
        Assert.Contains(args, a => a.Contains("--mcp-http-server") && a.Contains("hub"));
        Assert.Contains("mcp_servers.hub.required=true", args);
        Assert.DoesNotContain(args, a => a.Contains(".url=") || a.Contains("bearer_token_env_var") || a.Contains("hub-token"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_json_authenticated_http_uses_host_relay(bool gemini)
    {
        var path = gemini ? NativeLaneSupport.WriteGeminiSettings(Bridge, ".", [Http])
            : NativeLaneSupport.WriteClaudeMcpConfig(Bridge, ".", [Http]);
        try
        {
            Assert.NotNull(path);
            var text = File.ReadAllText(path!);
            var entry = JsonNode.Parse(text)!["mcpServers"]!["hub"]!;
            Assert.Equal("swarmcode", entry["command"]!.GetValue<string>());
            Assert.Contains("--mcp-http-server", entry["args"]!.ToJsonString());
            Assert.Null(entry["headers"]);
            Assert.Null(entry[gemini ? "httpUrl" : "url"]);
            Assert.DoesNotContain("hub-token", text);
        }
        finally { NativeLaneSupport.DeleteTempConfig(path); }
    }

    [Fact]
    public async Task Native_process_never_receives_vault_bearer_even_when_inherited()
    {
        var psi = new ProcessStartInfo("unused");
        psi.Environment["SWARMCODE_MCP_AUTH_old"] = "stale-bearer";
        var notices = await NativeLaneSupport.PrepareHttpEnvironmentAsync(psi,
            new() { McpServer = Bridge, ExternalMcpServers = [Http] }, false, new TestVault("synthetic-http-bearer"), default);
        Assert.Empty(notices);
        Assert.DoesNotContain(psi.Environment, e => e.Key.StartsWith("SWARMCODE_MCP_AUTH_"));
        Assert.DoesNotContain(psi.Environment.Values, v => v == "synthetic-http-bearer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_relay_skips_optional_and_rejects_required_authenticated_mount(bool required)
    {
        var server = new McpServerLaunch { ServerName = "hub", Url = Http.Url, BearerTokenReference = Http.BearerTokenReference, Required = required };
        if (required) Assert.Throws<InvalidOperationException>(() => NativeLaneSupport.CodexMcpOverrides(null, ".", [server]).ToArray());
        else Assert.Empty(NativeLaneSupport.MountedServers(null, [server], "."));
    }

    [Fact]
    public void Unauthenticated_http_mounts_without_a_vault_bridge()
    {
        var args = NativeLaneSupport.CodexMcpOverrides(null, ".", [new() { ServerName = "public", Url = Http.Url }]).ToArray();
        Assert.Contains("mcp_servers.public.url='http://127.0.0.1:9123/mcp'", args);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("gemini")]
    public async Task Missing_optional_credential_removes_native_mount_and_reports_skip(string lane)
    {
        var optional = new McpServerLaunch { ServerName = "hub", Url = Http.Url, BearerTokenReference = Http.BearerTokenReference };
        var psi = new ProcessStartInfo("unused");
        string? path = null;
        if (lane == "codex") foreach (var arg in NativeLaneSupport.CodexMcpOverrides(Bridge, ".", [optional])) psi.ArgumentList.Add(arg);
        else
        {
            path = lane == "claude" ? NativeLaneSupport.WriteClaudeMcpConfig(Bridge, ".", [optional])
                : NativeLaneSupport.WriteGeminiSettings(Bridge, ".", [optional]);
            if (lane == "claude") { psi.ArgumentList.Add("--mcp-config"); psi.ArgumentList.Add(path!); }
            else psi.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = path;
        }
        try
        {
            var notices = await NativeLaneSupport.PrepareHttpEnvironmentAsync(psi,
                new() { McpServer = Bridge, ExternalMcpServers = [optional] }, false, new TestVault(null), default);
            Assert.Contains("skipped", Assert.Single(notices));
            if (path is not null) Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["hub"]);
            else Assert.DoesNotContain(psi.ArgumentList, a => a.StartsWith("mcp_servers.hub."));
        }
        finally { NativeLaneSupport.DeleteTempConfig(path); }
    }

    [Fact]
    public async Task Missing_required_credential_fails_before_native_launch()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => NativeLaneSupport.PrepareHttpEnvironmentAsync(new("unused"),
            new() { McpServer = Bridge, ExternalMcpServers = [Http] }, false, new TestVault(null), default));

    [Fact]
    public void Optional_codex_mcp_start_failure_is_visible()
        => Assert.True(NativeLaneSupport.IsMcpDiagnostic("MCP client for 'optional-hub' failed to start: connection closed during initialize"));

    private sealed class TestVault(string? value) : ISecretVault
    {
        public Task<string?> GetValueAsync(string name, CancellationToken ct = default) => Task.FromResult(value);
        public Task<SecretEntry> SetAsync(SecretWriteRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretLogin?> GetLoginAsync(string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretEntry?> GetEntryAsync(string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasAsync(string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
