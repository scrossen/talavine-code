using SwarmCore.Coding.Native;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// A host that found no vault bridge (the Mac desktop head ships no console) tells the person once per workspace that
/// native coding agents have no access to stored credentials, instead of silently running them without the vault.
/// </summary>
public sealed class NativeLaneBridgeNoticeTests
{
    [Fact]
    public void A_host_without_a_bridge_gets_one_notice_per_workspace_on_unconfined_native_lanes()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridge-notice-" + Guid.NewGuid().ToString("N"));
        var native = new NativeAgentOptions { McpServerUnavailableReason = "No vault bridge: credentials are unavailable to native lanes." };
        var first = Path.Combine(root, "a");
        var second = Path.Combine(root, "b");

        Assert.Equal(native.McpServerUnavailableReason, NativeLaneSupport.BridgeUnavailableNotice(native, confined: false, first));
        Assert.Null(NativeLaneSupport.BridgeUnavailableNotice(native, confined: false, first));
        Assert.Null(NativeLaneSupport.BridgeUnavailableNotice(native, confined: false, first + Path.DirectorySeparatorChar));
        Assert.Equal(native.McpServerUnavailableReason, NativeLaneSupport.BridgeUnavailableNotice(native, confined: false, second));

        // A confined run mounts no servers at all; a configured bridge or a host with nothing to report says nothing.
        Assert.Null(NativeLaneSupport.BridgeUnavailableNotice(native, confined: true, Path.Combine(root, "c")));
        var bridged = new NativeAgentOptions { McpServer = new() { Command = "swarmcode" }, McpServerUnavailableReason = "x" };
        Assert.Null(NativeLaneSupport.BridgeUnavailableNotice(bridged, confined: false, Path.Combine(root, "d")));
        Assert.Null(NativeLaneSupport.BridgeUnavailableNotice(new NativeAgentOptions(), confined: false, Path.Combine(root, "e")));
    }
}
