using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class ModelVisionRecoveryAuthorityTests
{
    [Theory]
    [InlineData("deny")]
    [InlineData("ask")]
    [InlineData("plan")]
    [InlineData("unattended")]
    [InlineData("no-user-authority")]
    [InlineData("host-guard")]
    public async Task AutomaticRecoveryCannotBypassHostAuthorityOrPolicy(string blockedBy)
    {
        var services = new ServiceCollection();
        if (blockedBy == "host-guard") services.AddSingleton<IToolInvocationGuard>(new Deny());
        using var provider = services.BuildServiceProvider(); // Deliberately no provider, HTTP, store, or runtime: none may be touched.
        var request = new CodingTurnRequest { ConversationId = "fixture", UserMessage = "inspect the screenshot",
            CommunicationScope = blockedBy == "no-user-authority" ? AgentCommunicationScope.None : AgentCommunicationScope.UserRequested("fixture"),
            PlanMode = blockedBy == "plan", UnattendedAgentTurn = blockedBy == "unattended",
            Permissions = new() { Mode = blockedBy == "ask" ? CodingPermissionMode.Ask : CodingPermissionMode.Auto,
                Deny = blockedBy == "deny" ? [ModelVisionRecovery.ToolId] : [] } };
        Assert.Null(await new ModelVisionRecovery(provider).AutomaticallyAsync(request, Guid.NewGuid(), "Ollama", "arbitrary:quant", default));
    }
    private sealed class Deny : IToolInvocationGuard
    {
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
        {
            Assert.Equal(ModelVisionRecovery.ToolId, request.ToolId); Assert.Equal(ToolSideEffect.Exec, request.EffectiveSideEffect);
            return Task.FromResult(ToolGuardDecision.Block("host-denied"));
        }
    }
}
