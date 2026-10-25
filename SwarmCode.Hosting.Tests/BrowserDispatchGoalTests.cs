using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Goals;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class BrowserDispatchGoalTests : TempDirTest
{
    private sealed class Scope : IDisposable { public void Dispose() { } }
    private sealed class Host(ToolDescriptor tool) : IHostToolBridgeHost
    {
        public HostToolProfile? Admit(string id) => HostToolProfile.Full;
        public Task<IReadOnlyList<ToolDescriptor>> ToolsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([tool]);
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct) => Task.FromResult(ToolGuardDecision.Allow());
        public IDisposable BeginCall(string id) => new Scope();
    }
    private sealed class Inner(HostToolResult result) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [new("test", "test", "test")];
        public CodingLane? ResolveLane(string? name) => Lanes[0];
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Assert.False(request.ReviewOnly); // A refused action should not need physical recovery.
            yield return new ToolCallStarted("turn", "guard", "browser.act", new JsonObject()) { SideEffect = ToolSideEffect.ExternalSend };
            yield return new ToolCallCompleted("turn", "guard", "browser.act", result.Text, result.IsError, TimeSpan.Zero)
                { SideEffect = ToolSideEffect.ExternalSend, WasDispatched = result.WasDispatched };
            yield return new ToolCallStarted("turn", "read", "host.read", new JsonObject()) { SideEffect = ToolSideEffect.ReadOnly };
            yield return new ToolCallCompleted("turn", "read", "host.read", "Requested result independently read", false, TimeSpan.Zero) { SideEffect = ToolSideEffect.ReadOnly };
            yield return new TurnCompleted("turn", "The independent requested readback is complete", TimeSpan.Zero);
        }
    }
    private sealed class Acceptance : IContinualObjectiveAcceptanceService
    {
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
        {
            var receipt = Assert.Single(request.Receipts, r => r.ToolCallId == "guard");
            Assert.False(receipt.WasDispatched); Assert.True(receipt.IsError);
            Assert.False(MissionTransitions.NeedsInspection(receipt));
            return Task.FromResult(ContinualAcceptanceVerdict.Delivered);
        }
    }
    [Fact]
    public async Task New_typed_browser_guard_durably_preserves_failure_without_parking_goal_as_uncertain()
    {
        var tool = ToolDescriptor.Create<JsonObject, ComputerActionResult>("browser.act", "test", "Act", ToolCostClass.Cheap, ToolSideEffect.ExternalSend,
            (_, _) => Task.FromResult(new ComputerActionResult(false, 0, null, "opaque structural guard") { NoPhysicalAction = true }));
        await using var broker = new HostToolBridgeBroker(new Host(tool));
        var result = await broker.CallAsync("conversation", "browser_act", JsonSerializer.SerializeToElement(new { }), default);
        Assert.False(result.WasDispatched); Assert.True(result.IsError);
        var store = new FileCodingGoalStore(Paths);
        var goals = new CodingGoalOrchestrator(new Inner(result), store, new Acceptance());
        var request = SwarmCodeTurnPolicy.Apply(new() { ConversationId = "conversation", UserMessage = "Verify the requested result" },
            CodingPermissionPolicy.AutoNoRules, "test");
        var goal = await goals.StartAsync(request);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var _ in goals.RunAsync(request, goal.Id, deadline.Token)) { }
        var saved = (await new FileCodingGoalStore(Paths).GetAsync(goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status);
        var receipt = Assert.Single(saved.Receipts, r => r.ToolCallId == "guard");
        Assert.False(receipt.WasDispatched); Assert.True(receipt.IsError);
        Assert.Contains("opaque structural guard", receipt.Result);
        Assert.Null(receipt.HostResolution); // No historical rewrite or recovery waiver.
    }
}
