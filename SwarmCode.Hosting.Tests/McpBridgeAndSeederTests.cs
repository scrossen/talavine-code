using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.Missions;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Tests;

public class McpBridgeToolsTests : TempDirTest
{
    [Fact]
    public void Locator_adds_overrides_when_the_bridge_is_found()
    {
        // The launch is injected: whether a console executable sits beside the test host depends on the machine.
        var overrides = new Dictionary<string, string?>();
        var launch = new SwarmCore.Coding.McpServerLaunch { Command = "bridge-host", Arguments = ["--profile", "p", McpBridgeLocator.McpServerFlag] };
        McpBridgeLocator.AddOverrides(overrides, Paths, launch);

        Assert.Equal("bridge-host", overrides["Coding:NativeAgents:McpServer:Command"]);
        Assert.Equal("--profile", overrides["Coding:NativeAgents:McpServer:Arguments:0"]);
        Assert.Equal("p", overrides["Coding:NativeAgents:McpServer:Arguments:1"]);
        Assert.Equal(McpBridgeLocator.McpServerFlag, overrides["Coding:NativeAgents:McpServer:Arguments:2"]);
        // The bridge opens this host's vault even without SWARMCODE_HOME in the process environment.
        Assert.Equal(Paths.RootDirectory, overrides["Coding:NativeAgents:McpServer:Environment:SWARMCODE_HOME"]);
        Assert.False(overrides.ContainsKey("Coding:NativeAgents:McpServerUnavailableReason"));
        Assert.Equal(5, overrides.Count);
    }

    [Fact]
    public void Without_a_bridge_the_host_tells_native_lanes_that_credentials_are_unavailable()
    {
        // An incomplete bundle or missing console must expose the unavailable capability.
        var overrides = new Dictionary<string, string?>();
        McpBridgeLocator.AddOverrides(overrides, Paths, launch: null);
        Assert.False(overrides.ContainsKey("Coding:NativeAgents:McpServer:Command"));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var options = new SwarmCore.Coding.CodingOptions();
        configuration.GetSection("Coding").Bind(options);
        Assert.Equal(McpBridgeLocator.BridgeUnavailableReason, options.NativeAgents.McpServerUnavailableReason);
        Assert.Contains("credentials", McpBridgeLocator.BridgeUnavailableReason, StringComparison.OrdinalIgnoreCase);

        // A found bridge clears nothing and adds no reason.
        var found = new Dictionary<string, string?>();
        McpBridgeLocator.AddOverrides(found, Paths, new SwarmCore.Coding.McpServerLaunch { Command = "swarmcode", Arguments = [McpBridgeLocator.McpServerFlag] });
        Assert.Equal("swarmcode", found["Coding:NativeAgents:McpServer:Command"]);
        Assert.False(found.ContainsKey("Coding:NativeAgents:McpServerUnavailableReason"));
    }
}

public class LaneSeederTests
{
    private sealed class VeniceKeys : ILlmApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerName) => Task.FromResult<string?>(providerName == "Venice" ? "fixture-key" : null);
        public Task SetApiKeyAsync(string providerName, string apiKey) => Task.CompletedTask;
        public Task<bool> HasApiKeyAsync(string providerName) => Task.FromResult(providerName == "Venice");
    }

    [Fact]
    public void VeniceKeySeedsItsOwnProviderLane()
    {
        var options = new CodingLaneOptions();
        new DefaultLaneSeeder(new VeniceKeys(), new LlmProvidersOptions(), _ => false).PostConfigure(null, options);
        Assert.Equal(2, options.Lanes.Count);
        var lane = options.Lanes[0];
        Assert.Equal("venice", lane.Name);
        Assert.Equal("Venice", lane.Provider);
        Assert.Equal("zai-org-glm-5-2", lane.Model);
        Assert.False(lane.PreferOnDecline);
        // The decline fallback comes after the ordinary Venice lane: tried first only once another model declines.
        var fallback = options.Lanes[1];
        Assert.Equal(DefaultLaneSeeder.DeclineFallbackLaneName, fallback.Name);
        Assert.Equal("Venice", fallback.Provider);
        Assert.Equal("venice-uncensored-1-2", fallback.Model);
        Assert.True(fallback.PreferOnDecline);
        Assert.True(fallback.Priority > lane.Priority);
    }

    [Fact]
    public void VeniceDeclineFallbackLaneIsOptional()
    {
        var options = new CodingLaneOptions();
        var providers = new LlmProvidersOptions();
        providers.Venice.DeclineFallbackModel = "";
        new DefaultLaneSeeder(new VeniceKeys(), providers, _ => false).PostConfigure(null, options);
        Assert.Single(options.Lanes);
        // The same model configured as the default lane is not seeded twice.
        options = new CodingLaneOptions();
        providers = new LlmProvidersOptions();
        providers.Venice.DefaultModel = "venice-uncensored-1-2";
        new DefaultLaneSeeder(new VeniceKeys(), providers, _ => false).PostConfigure(null, options);
        Assert.Single(options.Lanes);
    }

    private sealed class NoKeys : ILlmApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerName) => Task.FromResult<string?>(providerName == "OpenAI" ? "sk-test" : null);
        public Task SetApiKeyAsync(string providerName, string apiKey) => Task.CompletedTask;
        public Task<bool> HasApiKeyAsync(string providerName) => Task.FromResult(providerName == "OpenAI");
    }

    private static List<CodingLane> Seed(Func<string, bool> installed)
    {
        var options = new CodingLaneOptions();
        new DefaultLaneSeeder(new NoKeys(), new LlmProvidersOptions(), installed).PostConfigure(null, options);
        return options.Lanes.OrderBy(l => l.Priority).ToList();
    }

    [Fact]
    public void Both_clis_installed_seed_native_lanes_first_and_tool_fallbacks_last()
    {
        var lanes = Seed(_ => true);
        Assert.Equal(["claude", "codex", "gemini-cli", "openai", "claude-tools", "codex-tools"], lanes.Select(l => l.Name));
        Assert.Equal(GeminiCliNativeLaneRunner.ProviderId, lanes[2].Provider);
        Assert.Equal(ClaudeCodeNativeLaneRunner.ProviderId, lanes[0].Provider);
        Assert.Equal(CodexNativeLaneRunner.ProviderId, lanes[1].Provider);
        Assert.True(lanes[0].IsNative && lanes[1].IsNative);
        Assert.Equal(SubscriptionCliCatalog.ClaudeCodeProvider, lanes[4].Provider);
        Assert.Equal(SubscriptionCliCatalog.CodexCliProvider, lanes[5].Provider);
        Assert.False(lanes[4].IsNative);
    }

    [Fact]
    public void Missing_clis_are_not_seeded()
    {
        var lanes = Seed(provider => provider == SubscriptionCliCatalog.CodexCliProvider);
        Assert.Equal(["codex", "openai", "codex-tools"], lanes.Select(l => l.Name));
        Assert.Equal(["openai"], Seed(_ => false).Select(l => l.Name));
    }

    [Fact]
    public void Configured_lanes_are_left_alone()
    {
        var options = new CodingLaneOptions { Lanes = [new("mine", "OpenRouter", "x")] };
        new DefaultLaneSeeder(new NoKeys(), new LlmProvidersOptions(), _ => true).PostConfigure(null, options);
        Assert.Single(options.Lanes);
    }
}
