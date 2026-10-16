using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// A permission mode that asks makes the real orchestrator skip the
/// chosen native lane, says why in the transcript, and runs the next non-native lane.
/// </summary>
public sealed class ApprovalModeNativeLaneTests : TempDirTest
{
    private sealed class Runner(List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            await Task.Yield();
            yield return new TurnCompleted("t", "done on the api lane", TimeSpan.Zero);
        }
    }

    private sealed class FixedWorkspace(string root) : ICodingWorkspaceResolver
    {
        public Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default)
            => Task.FromResult<CodingWorkspace?>(new CodingWorkspace { RepositoryRoot = root });
    }

    [Fact]
    public async Task Approval_mode_skips_the_native_lane_with_a_notice_and_falls_back()
    {
        var seen = new List<AgentTurnRequest>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddScoped<IAgentTurnRunner>(_ => new Runner(seen));
        services.AddSingleton<ICodingWorkspaceResolver>(new FixedWorkspace(Tmp));
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        {
            Lanes = [new("claude", "ClaudeCodeNative", "claude-code:default") { Priority = 0 }, new("claude-tools", "ClaudeCode", "claude-code:default") { Priority = 90 }],
        })));
        services.AddSingleton<ICodingTurnOrchestrator>(sp => new CodingTurnOrchestrator(sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CodingOptions()), sp.GetRequiredService<ILogger<CodingTurnOrchestrator>>()));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var runner = new ConversationRunner(provider, "conv");

        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("change it", "claude", permissionMode: SwarmCore.Coding.Permissions.CodingPermissionMode.Ask));
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var skip = Assert.Single(runner.Entries.OfType<NoticeEntry>(), n => n.Text.StartsWith("Skipping claude:", StringComparison.Ordinal));
        Assert.Equal(NoticeKind.Info, skip.Kind);
        Assert.Contains("native lanes run their own tools without approval prompts", skip.Text);
        Assert.Equal("ClaudeCode", Assert.Single(seen).ProviderOverride);
        Assert.True(seen[0].CommunicationScope.UserInitiated); // narrowed by the policy, not by dropping authority
        Assert.Equal(["claude-tools"], result.LanesUsed.ToArray());
    }
}
