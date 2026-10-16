using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// Codex loads the user's own Codex app connectors (its built-in "apps" feature) beside any configured MCP
/// servers. A run excludes them unless the host allows them, a confined run always excludes them, and the lane's
/// notice lists host-mounted servers and states the additional user-config boundary.
/// </summary>
public sealed class CodexAppConnectorTests
{
    private const string AppsOff = "features.apps=false";
    private static readonly string Repo = Path.GetTempPath();

    private static NativeLaneTurn Turn(NativeLaneContainment? containment)
        => new("conv", "do X", new CodingLane("codex", CodexNativeLaneRunner.ProviderId, "codex-cli:default"), Repo, "swarmcode-cli", false)
            { Containment = containment };

    private static List<string> Args(NativeAgentOptions options, NativeLaneContainment? containment = null)
        => CodexNativeLaneRunner.BuildStartInfo("codex.exe", Turn(containment), null, options,
            ignoreUserConfigSupported: true, ignoreRulesSupported: true).ArgumentList.ToList();

    private static McpServerLaunch External() => new() { ServerName = "talavine", Command = "talavine-mcp.exe", Arguments = ["--stdio"] };

    [Fact]
    public void A_bypass_run_turns_app_connectors_off_by_default()
    {
        var args = Args(new NativeAgentOptions());
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.Equal(["-c", AppsOff], args.Skip(args.IndexOf(AppsOff) - 1).Take(2));
    }

    [Fact]
    public void A_bypass_run_keeps_app_connectors_when_the_user_allows_them()
        => Assert.DoesNotContain(AppsOff, Args(new NativeAgentOptions { AllowCodexAppConnectors = true }));

    [Fact]
    public void A_confined_run_excludes_app_connectors_even_when_allowed()
    {
        var args = Args(new NativeAgentOptions { AllowCodexAppConnectors = true }, NativeLaneContainment.WorkspaceConfined);
        Assert.Contains("mcp_servers={}", args);
        Assert.Contains(AppsOff, args);
    }

    [Fact]
    public void The_notice_lists_configured_servers_and_says_whether_app_connectors_are_reachable()
    {
        Assert.Equal("External MCP servers configured by this host: talavine. Codex app connectors are off. Additional MCP servers from your Codex configuration can still load.",
            NativeLaneSupport.DescribeCodexReach(null, [External()], Repo, allowAppConnectors: false));
        Assert.Equal("External MCP servers configured by this host: talavine. Codex app connectors are on. Additional MCP servers from your Codex configuration can still load.",
            NativeLaneSupport.DescribeCodexReach(null, [External()], Repo, allowAppConnectors: true));
        Assert.Equal("No external MCP servers configured by this host. Codex app connectors are on. Additional MCP servers from your Codex configuration can still load.",
            NativeLaneSupport.DescribeCodexReach(null, [], Repo, allowAppConnectors: true));
        Assert.Equal("No external MCP servers configured by this host. Codex app connectors are off. Additional MCP servers from your Codex configuration can still load.",
            NativeLaneSupport.DescribeCodexReach(null, [], Repo, allowAppConnectors: false));
    }
}
