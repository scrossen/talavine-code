using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class GoalInboxScopeTests
{
    [Fact]
    public void Native_bridge_uses_exact_active_goal_lease_and_old_disposal_cannot_remove_replacement()
    {
        var active = new ActiveCodingWorkspaces();
        var accessor = new AsyncLocalCurrentTurnContextAccessor();
        var services = new ServiceCollection().AddSingleton(active).AddSingleton<ICurrentTurnContextAccessor>(accessor).BuildServiceProvider();
        var host = new ServiceHostToolBridgeHost(services, _ => HostToolProfile.Full);
        var goalId = Guid.NewGuid();
        var workspace = new CodingWorkspace { RepositoryRoot = Path.GetTempPath() };
        var prior = new CurrentTurnContext("prior", null, null);
        accessor.Current = prior;
        try
        {
            using var old = active.Enter("goal-chat", workspace, goalId: goalId, goalMessageLeaseToken: "old-runtime-lease");
            using (host.BeginCall("goal-chat"))
            {
                Assert.Equal(goalId, accessor.Current!.GoalMessageGoalId);
                Assert.Equal("old-runtime-lease", accessor.Current.GoalMessageLeaseToken);
            }
            Assert.Same(prior, accessor.Current);
            using var current = active.Enter("goal-chat", workspace, goalId: goalId, goalMessageLeaseToken: "new-runtime-lease");
            old.Dispose();
            using (host.BeginCall("goal-chat")) Assert.Equal("new-runtime-lease", accessor.Current!.GoalMessageLeaseToken);
            using (active.Enter("ordinary", workspace))
            using (host.BeginCall("ordinary"))
            {
                Assert.Null(accessor.Current!.GoalMessageGoalId);
                Assert.Null(accessor.Current.GoalMessageLeaseToken);
            }
        }
        finally { accessor.Current = null; services.Dispose(); }
    }

    [Fact]
    public void Lease_and_runtime_mailbox_never_enter_serialized_request_native_prompt_or_process_arguments()
    {
        var goalId = Guid.NewGuid();
        var request = new CodingTurnRequest { ConversationId = "goal-chat", UserMessage = "Continue the goal.", GoalId = goalId, GoalMessageLeaseToken = "private-runtime-lease" };
        var engine = CodingLaneRequestFactory.CreateEngine(request, new("engine", "provider", "model"), Path.GetTempPath(),
            AgentCommunicationScope.TrustedProfileWide("test"), Guid.NewGuid(), request.UserMessage, true, new(), null);
        Assert.Equal(goalId, engine.GoalId);
        Assert.Equal("private-runtime-lease", engine.GoalMessageLeaseToken);
        var turn = new NativeLaneTurn("goal-chat", request.UserMessage, new("native", "CodexNative", "codex-cli:default"), Path.GetTempPath(), "user", true)
            { GoalId = goalId, GoalMessageLeaseToken = request.GoalMessageLeaseToken };
        var ambient = new CurrentTurnContext("goal-chat", null, null) { GoalMessageGoalId = goalId, GoalMessageLeaseToken = request.GoalMessageLeaseToken };
        foreach (var serialized in new[] { JsonSerializer.Serialize(request), JsonSerializer.Serialize(engine), JsonSerializer.Serialize(turn), JsonSerializer.Serialize(ambient) })
        {
            Assert.DoesNotContain("private-runtime-lease", serialized);
            Assert.DoesNotContain("GoalMailbox", serialized);
            Assert.DoesNotContain("GoalMessageLeaseToken", serialized);
        }
        Assert.DoesNotContain("private-runtime-lease", NativeLaneSupport.BuildPrompt(turn, false, null, "AGENTS.md", [], 16000));
        Assert.DoesNotContain(CodexNativeLaneRunner.BuildAppServerStartInfo("codex", turn, new()).ArgumentList, argument => argument.Contains("private-runtime-lease"));
    }
}
