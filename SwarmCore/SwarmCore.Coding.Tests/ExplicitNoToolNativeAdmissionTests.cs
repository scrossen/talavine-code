using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class ExplicitNoToolNativeAdmissionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_selected_inspection_route_cannot_substitute_a_blocked_native_route_or_claim_a_user_pin(bool named)
    {
        var native = new NativeProbe(); var engine = new EngineProbe();
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native).AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [
            new("native", "CodexNative", "codex-cli:default"), new("engine", "fixture", "other-model")] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var request = new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Inspect prior uncertain effects without replay.",
                LaneName = named ? "native" : null, ModelOverride = named ? "codex-cli:chosen@high" : null,
                RouteProvider = named ? null : "CodexNative", RouteModel = named ? null : "codex-cli:chosen@high",
                CommunicationScope = AgentCommunicationScope.UserRequested("fixture"), PlanMode = true, ReviewOnly = true,
                AllowNativeLanes = false, AllowHandOff = true, RequireSelectedRoute = true,
                RouteOverrideProvenance = SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.Policy };
            var events = new List<TurnEvent>(); await foreach (var item in orchestrator.RunAsync(request)) events.Add(item);
            Assert.Equal(0, native.Calls); Assert.Equal(0, engine.Calls); Assert.False(request.SelectionPinned);
            Assert.Empty(events.OfType<CodingLaneSelected>()); Assert.Empty(events.OfType<CodingLaneHandOff>());
            Assert.Contains("selected route is retained", Assert.Single(events.OfType<CodingLanesExhausted>()).Reason);
            Assert.DoesNotContain("RequireSelectedRoute", System.Text.Json.JsonSerializer.Serialize(request));
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Selected_route_failure_stays_on_policy_route_while_ordinary_handoff_stays_available(bool requireSelected, int expectedCalls)
    {
        var engine = new EngineProbe { Fails = true };
        var services = new ServiceCollection().AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { HandOffOnRefusal = true, Lanes = [
            new("selected", "fixture", "chosen"), new("alternative", "fixture", "other")] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var request = new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Inspect only.", LaneName = "selected",
                CommunicationScope = AgentCommunicationScope.UserRequested("fixture"), RequireSelectedRoute = requireSelected,
                RouteOverrideProvenance = SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.Policy };
            var events = new List<TurnEvent>(); await foreach (var item in orchestrator.RunAsync(request)) events.Add(item);
            Assert.Equal(expectedCalls, engine.Calls); Assert.False(request.SelectionPinned);
            if (requireSelected)
            {
                Assert.True(engine.Request!.DisableRouteFallback);
                Assert.Equal(request.RouteOverrideProvenance, engine.Request.RouteOverrideProvenance);
                Assert.Empty(events.OfType<CodingLaneHandOff>());
                Assert.Contains("selected inspection route is retained", Assert.Single(events.OfType<CodingLanesExhausted>()).Reason);
                Assert.DoesNotContain("your explicit", Assert.Single(events.OfType<CodingLanesExhausted>()).Reason);
            }
            else Assert.Single(events.OfType<CodingLaneHandOff>());
        }
    }
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task Readonly_inspection_requires_verified_candidate_capability_and_preserves_named_or_exact_pin(bool named, bool supported, bool toolsWithheld)
    {
        var native = new NativeProbe { InspectionSupported = supported };
        var engine = new EngineProbe();
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native).AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [
            new("native", "CodexNative", "codex-cli:default"), new("engine", "fixture", "other-model")] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var request = new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Inspect retained input only.",
                AuthenticatedOriginalUserMessage = "Keep working toward the saved objective.", CommunicationScope = AgentCommunicationScope.UserRequested("fixture"),
                LaneName = named ? "native" : null, ModelOverride = named ? "codex-cli:exact@high" : null,
                RouteProvider = named ? null : "CodexNative", RouteModel = named ? null : "codex-cli:exact@high",
                SelectionPinned = true, PlanMode = true, ReviewOnly = true, ReadOnlyInspection = true, HostToolsWithheld = toolsWithheld };
            var events = new List<TurnEvent>();
            await foreach (var item in orchestrator.RunAsync(request)) events.Add(item);
            Assert.Equal(supported && !toolsWithheld ? 1 : 0, native.Calls);
            Assert.Equal(0, engine.Calls);
            if (native.Calls == 1)
            {
                Assert.True(native.Turn!.ReadOnlyInspection); Assert.True(native.Turn.PlanMode);
                Assert.Equal("codex-cli:exact@high", native.Turn.Lane.Model);
            }
            else Assert.Single(events.OfType<CodingLanesExhausted>());
        }
    }

    [Fact]
    public async Task Inspection_capability_never_grants_ordinary_plan_mode_or_an_unfenced_inspection()
    {
        var native = new NativeProbe { InspectionSupported = true };
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [new("native", "CodexNative", "codex-cli:default")] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var request = new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Inspect only.", LaneName = "native",
                SelectionPinned = true, PlanMode = true, CommunicationScope = AgentCommunicationScope.UserRequested("fixture") };
            var plan = new List<TurnEvent>(); await foreach (var item in orchestrator.RunAsync(request)) plan.Add(item);
            Assert.Single(plan.OfType<CodingLanesExhausted>());
            var unfenced = new List<TurnEvent>(); await foreach (var item in orchestrator.RunAsync(request with { PlanMode = false, ReadOnlyInspection = true, ReviewOnly = true })) unfenced.Add(item);
            Assert.Single(unfenced.OfType<TurnErrored>());
            Assert.Equal(0, native.Calls);
        }
    }

    [Fact]
    public async Task Supplied_context_inspection_does_not_compute_optional_native_injectors()
    {
        var native = new NativeProbe { InspectionSupported = true };
        var injector = new CountingInjector();
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native).AddSingleton<IContextInjector>(injector);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [new("native", "CodexNative", "codex-cli:default")] },
            new CodingOptions { NativeLaneInjectorNames = ["optional-context"] }, out var provider);
        using (provider)
        {
            await foreach (var _ in orchestrator.RunAsync(new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Only the supplied text.",
                LaneName = "native", SelectionPinned = true, PlanMode = true, ReviewOnly = true, ReadOnlyInspection = true,
                CommunicationScope = AgentCommunicationScope.UserRequested("fixture") })) { }
            Assert.Equal(1, native.Calls); Assert.Equal(0, injector.Calls); Assert.Empty(native.Turn!.HostContext);
        }
    }

    private sealed class CountingInjector : IContextInjector
    {
        public string Name => "optional-context";
        public int Calls;
        public Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
        { Calls++; return Task.FromResult<string?>("Extra personal context."); }
    }
    [Fact]
    public async Task Later_attempt_host_restriction_refuses_pinned_native_without_changing_route_or_falling_back()
    {
        var native = new NativeProbe();
        var engine = new EngineProbe();
        var services = new ServiceCollection().AddSingleton<INativeLaneRunner>(native).AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [
            new("native", "ClaudeCodeNative", "claude-code:default") { Priority = 0 },
            new("engine", "fixture", "api-model") { Priority = 1 }] }, new CodingOptions(), out var provider);
        using (provider)
        {
            var request = new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Continue saved work.",
                AuthenticatedOriginalUserMessage = "Inspect the supplied task.", CommunicationScope = AgentCommunicationScope.UserRequested("fixture"),
                LaneName = "native", ModelOverride = "claude-code:exact-choice", SelectionPinned = true, DisableRouteFallback = true };
            await foreach (var _ in orchestrator.RunAsync(request)) { }
            Assert.Equal("claude-code:exact-choice", native.Turn!.Lane.Model);
            var second = request with { IsContinuation = true, HostToolsWithheld = true };
            var events = new List<TurnEvent>();
            await foreach (var item in orchestrator.RunAsync(second)) events.Add(item);
            Assert.Equal(1, native.Calls);
            Assert.Equal(0, engine.Calls);
            Assert.Empty(events.OfType<CodingLaneSelected>());
            Assert.Contains("withholds all tools", Assert.Single(events.OfType<CodingLanesExhausted>()).Reason);
            Assert.Equal(request.AuthenticatedOriginalUserMessage, second.AuthenticatedOriginalUserMessage);
            Assert.Equal(request.ModelOverride, second.ModelOverride);
            Assert.True(second.SelectionPinned);
            Assert.DoesNotContain("HostToolsWithheld", System.Text.Json.JsonSerializer.Serialize(second));
        }
    }

    [Fact]
    public async Task Later_engine_attempt_retains_exact_pin_original_intent_and_narrowed_tool_policy()
    {
        var engine = new EngineProbe();
        var services = new ServiceCollection().AddSingleton<IAgentTurnRunner>(engine);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [new("engine", "fixture", "configured-model")] },
            new CodingOptions(), out var provider);
        using (provider)
        {
            var request = new CodingTurnRequest { ConversationId = Guid.NewGuid().ToString(), UserMessage = "Continue the next step.",
                AuthenticatedOriginalUserMessage = "Inspect supplied evidence.", CommunicationScope = AgentCommunicationScope.UserRequested("fixture"),
                LaneName = "engine", ModelOverride = "exact-model", SelectionPinned = true,
                RouteOverrideProvenance = SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.User };
            await foreach (var _ in orchestrator.RunAsync(request)) { }
            Assert.False(engine.Request!.HostToolsWithheld);
            await foreach (var _ in orchestrator.RunAsync(request with { IsContinuation = true, HostToolsWithheld = true })) { }
            Assert.True(engine.Request!.HostToolsWithheld);
            Assert.True(UserToolUseRestriction.WithholdsAllTools(engine.Request));
            Assert.Equal(request.AuthenticatedOriginalUserMessage, engine.Request.AuthenticatedOriginalUserMessage);
            Assert.Equal("fixture", engine.Request.ProviderOverride);
            Assert.Equal("exact-model", engine.Request.ModelOverride);
            Assert.Equal(request.RouteOverrideProvenance, engine.Request.RouteOverrideProvenance);
            Assert.True(engine.Request.DisableRouteFallback);
        }
    }
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
        public NativeLaneTurn? Turn;
        public bool InspectionSupported;
        public bool SupportsReadOnlyInspection => InspectionSupported;
        public bool Supports(CodingLane lane) => lane.IsNative;
        public bool SupportsContainment(NativeLaneContainment containment) => true;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        { Calls++; Turn = turn; await Task.Yield(); yield return new TurnCompleted("native", "The answer is four.", TimeSpan.Zero); }
    }
    private sealed class EngineProbe : IAgentTurnRunner
    {
        public int Calls;
        public bool Fails;
        public AgentTurnRequest? Request;
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { Calls++; Request = request; await Task.Yield(); if (Fails) yield return new TurnErrored("engine", "Selected route unavailable.")
            { FailureCategory = SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.ModelNotFound };
            else yield return new TurnCompleted("engine", "The answer is four.", TimeSpan.Zero); }
    }
}
