using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// A native lane's <see cref="TurnStarted"/> names the resolved agent profile (the host supplies it through the
/// request), the host's own profile name when it runs under its own profile, and never the lane name.
/// </summary>
public sealed class NativeTurnProfileNameTests
{
    private static readonly CodingLane Claude = new("claude", "ClaudeCodeNative", "claude-code:default");

    private static NativeLaneTurn Turn(Guid? profileId, string? profileName)
        => new("conv", "do X", Claude, Path.GetTempPath(), "test", false) { ProfileId = profileId, ProfileName = profileName };

    [Fact]
    public void The_reported_profile_is_the_hosts_resolved_profile_never_the_lane()
    {
        var options = new CodingOptions { ProfileName = "SwarmCode" };
        var talavineProfile = Guid.NewGuid();

        Assert.Equal((talavineProfile, "General Assistant"), Turn(talavineProfile, "General Assistant").ReportedProfile(options));
        Assert.Equal((options.ProfileId, "SwarmCode"), Turn(null, null).ReportedProfile(options));
        Assert.Equal((options.ProfileId, "SwarmCode"), Turn(options.ProfileId, null).ReportedProfile(options));
        // A host profile whose name was not supplied reports its identity, not the lane.
        var unnamed = Turn(talavineProfile, null).ReportedProfile(options);
        Assert.Equal(talavineProfile.ToString(), unnamed.Name);
        Assert.NotEqual(Claude.Name, unnamed.Name);
    }

    private sealed class Capture(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("n", "done", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task The_orchestrator_hands_the_requests_profile_name_to_the_native_lane()
    {
        var seen = new List<NativeLaneTurn>();
        var services = new ServiceCollection();
        services.AddSingleton<INativeLaneRunner>(new Capture(seen));
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [Claude], HandOffOnRefusal = false }, new CodingOptions());
        var profile = Guid.NewGuid();

        await foreach (var _ in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "profile-name-" + Guid.NewGuid().ToString("N"), UserMessage = "change it", LaneName = "claude",
            CommunicationScope = AgentCommunicationScope.UserRequested("test"), AllowNativeLanes = true,
            ProfileId = profile, ProfileName = "General Assistant",
        })) { }

        var turn = Assert.Single(seen);
        Assert.Equal(profile, turn.ProfileId);
        Assert.Equal("General Assistant", turn.ProfileName);
    }
}
