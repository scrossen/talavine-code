using SwarmCode.Hosting.Memory;
using SwarmCore.Coding.Routing;
using SwarmCore.PersonalMemory;

namespace SwarmCode.Hosting.Tests;

/// <summary>SwarmCode has no automatic model selection, so memory calls default to the person's own lane.</summary>
public sealed class PersonalMemoryRouteTests
{
    private static CodingLaneOptions Lanes(string? defaultLane, params CodingLane[] lanes)
        => new() { DefaultLane = defaultLane, Lanes = [.. lanes] };

    [Fact]
    public void Memory_uses_the_default_lane_when_it_is_a_router_route()
    {
        var options = SwarmCodePersonalMemoryRegistration.WithDefaultRoute(new PersonalMemoryOptions(), Lanes("codex",
            new CodingLane("claude", "ClaudeCode", "claude-code:default") { Priority = 0 },
            new CodingLane("codex", "CodexCli", "codex:default") { Priority = 1 }));
        Assert.Equal(("CodexCli", "codex:default"), (options.RouteProvider, options.RouteModel));
    }

    [Fact]
    public void A_native_default_lane_falls_back_to_the_highest_priority_router_lane()
    {
        var options = SwarmCodePersonalMemoryRegistration.WithDefaultRoute(new PersonalMemoryOptions(), Lanes("claude-native",
            new CodingLane("claude-native", "ClaudeCodeNative", "default") { Priority = 0 },
            new CodingLane("openrouter", "OpenRouter", "anthropic/claude-sonnet-4.5") { Priority = 5 },
            new CodingLane("claude", "ClaudeCode", "claude-code:default") { Priority = 2 }));
        Assert.Equal(("ClaudeCode", "claude-code:default"), (options.RouteProvider, options.RouteModel));
    }

    [Fact]
    public void An_explicit_memory_route_wins_and_no_lanes_leaves_it_unset()
    {
        var pinned = SwarmCodePersonalMemoryRegistration.WithDefaultRoute(
            new PersonalMemoryOptions { RouteProvider = "Ollama", RouteModel = "qwen3.5:9b" },
            Lanes(null, new CodingLane("claude", "ClaudeCode", "claude-code:default")));
        Assert.Equal(("Ollama", "qwen3.5:9b"), (pinned.RouteProvider, pinned.RouteModel));

        var none = SwarmCodePersonalMemoryRegistration.WithDefaultRoute(new PersonalMemoryOptions(),
            Lanes(null, new CodingLane("claude-native", "ClaudeCodeNative", "default")));
        Assert.Null(none.RouteProvider);
    }
}
