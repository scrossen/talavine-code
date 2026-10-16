using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// Claude Code loads the connectors linked on the user's claude.ai account (Gmail, Drive...) unless told not to.
/// A SwarmCode run asked about the user's Talavine inbox answered from their live Gmail instead. A run excludes
/// them unless the host allows them, and a confined run always excludes them, matching Codex's app connectors.
/// </summary>
public sealed class ClaudeAiConnectorTests
{
    private static readonly string Repo = Path.GetTempPath();

    private static NativeLaneTurn Turn(NativeLaneContainment? containment)
        => new("conv", "do X", new CodingLane("claude", ClaudeCodeNativeLaneRunner.ProviderId, "claude-code:default"), Repo, "swarmcode-cli", false)
            { Containment = containment };

    private static IDictionary<string, string?> Env(NativeLaneContainment containment, bool allow)
        => ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", Turn(containment), null, null, containment,
            restrictedSupported: true, allowClaudeAiConnectors: allow).Environment;

    [Fact]
    public void A_bypass_run_turns_claude_ai_connectors_off_by_default()
        => Assert.Equal("false", Env(NativeLaneContainment.Bypass, allow: false)[ClaudeCodeNativeLaneRunner.ClaudeAiConnectorsVariable]);

    [Fact]
    public void A_bypass_run_keeps_claude_ai_connectors_when_the_user_allows_them()
        => Assert.False(Env(NativeLaneContainment.Bypass, allow: true).ContainsKey(ClaudeCodeNativeLaneRunner.ClaudeAiConnectorsVariable));

    [Fact]
    public void A_confined_run_excludes_claude_ai_connectors_even_when_allowed()
        => Assert.Equal("false", Env(NativeLaneContainment.WorkspaceConfined, allow: true)[ClaudeCodeNativeLaneRunner.ClaudeAiConnectorsVariable]);

    [Fact]
    public void The_option_is_off_unless_set()
        => Assert.False(new NativeAgentOptions().AllowClaudeAiConnectors);
}
