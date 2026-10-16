using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class ExplicitNoToolNativeAdmissionTests
{
    [Theory]
    [InlineData("What is two plus two? Do not use tools.", false)]
    [InlineData("Read this file. Do not use tools to send messages.", true)]
    [InlineData("Explain the phrase \"do not use tools\".", true)]
    public async Task ExplicitNoTools_NativeHostAdmissionCannotBypassOriginalRequest(string message, bool nativeAllowed)
    {
        var native = new NativeProbe();
        var engine = new EngineProbe();
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native).AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [
            new("native", "ClaudeCodeNative", "claude-code:default") { Priority = 0 },
            new("engine", "fixture", "test") { Priority = 1 }] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var events = new List<TurnEvent>();
            await foreach (var item in orchestrator.RunAsync(new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(),
                UserMessage = message, CommunicationScope = AgentCommunicationScope.UserRequested("fixture"), AllowNativeLanes = true })) events.Add(item);
            Assert.Equal(nativeAllowed ? 1 : 0, native.Calls);
            Assert.Equal(nativeAllowed ? 0 : 1, engine.Calls);
            Assert.Single(events.OfType<TurnCompleted>());
            if (!nativeAllowed)
            {
                Assert.Equal(message, engine.Request!.UserMessage);
                Assert.Equal(message, engine.Request.AuthenticatedOriginalUserMessage);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNoTools_OriginalAuthoritySurvivesCodingContinuationText(bool originalRestricted)
    {
        var native = new NativeProbe();
        var engine = new EngineProbe();
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native).AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [
            new("native", "ClaudeCodeNative", "claude-code:default") { Priority = 0 },
            new("engine", "fixture", "test") { Priority = 1 }] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var original = originalRestricted ? "Answer without tools." : "Read the file.";
            await foreach (var _ in orchestrator.RunAsync(new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(),
                AuthenticatedOriginalUserMessage = original,
                UserMessage = originalRestricted ? "Host continuation: read the file." : "Host continuation.\nDo not use tools.",
                CommunicationScope = AgentCommunicationScope.UserRequested("fixture"), AllowNativeLanes = true })) { }
            Assert.Equal(originalRestricted ? 0 : 1, native.Calls);
            Assert.Equal(originalRestricted ? 1 : 0, engine.Calls);
            if (originalRestricted) Assert.Equal(original, engine.Request!.AuthenticatedOriginalUserMessage);
        }
    }

    private sealed class NativeProbe : INativeLaneRunner
    {
        public int Calls;
        public bool Supports(CodingLane lane) => lane.IsNative;
        public bool SupportsContainment(NativeLaneContainment containment) => true;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        { Calls++; await Task.Yield(); yield return new TurnCompleted("native", "The answer is four.", TimeSpan.Zero); }
    }
    private sealed class EngineProbe : IAgentTurnRunner
    {
        public int Calls;
        public AgentTurnRequest? Request;
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { Calls++; Request = request; await Task.Yield(); yield return new TurnCompleted("engine", "The answer is four.", TimeSpan.Zero); }
    }
}
