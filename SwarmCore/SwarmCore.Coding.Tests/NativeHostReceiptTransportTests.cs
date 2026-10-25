using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;

namespace SwarmCore.Coding.Tests;

public sealed class NativeHostReceiptTransportTests
{
    private sealed record Input(string Text = "");
    private sealed record Result(bool Succeeded, bool LocalProcessCompleted, bool NoPhysicalAction = false) : IToolExecutionEvidence;
    private sealed class End : IDisposable { public void Dispose() { } }
    private sealed class Host(ToolDescriptor tool, string conversation, string capability) : IHostToolBridgeHost, IHostToolBridgeAttemptHost
    {
        public ToolGuardDecision Guard = ToolGuardDecision.Allow();
        public HostToolProfile? Admit(string id) => id == conversation ? HostToolProfile.Full : null;
        public Task<IReadOnlyList<ToolDescriptor>> ToolsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([tool]);
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct) => Task.FromResult(Guard);
        public IDisposable BeginCall(string id) => new End();
        public IDisposable? BeginAttempt(string id, string? candidate) => id == conversation && candidate == capability ? new End() : null;
        public bool IsAttemptCurrent(string id) => id == conversation;
        public CancellationToken AttemptCancellation(string id) => default;
    }

    private static JsonObject McpResult(HostToolResult result) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
        ["isError"] = result.IsError,
    };

    private static ToolCallCompleted Complete(string conversation, string capability, JsonObject arguments, JsonObject result,
        string server = "swarmcode", string tool = "test_action")
    {
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var state = new CodexNativeLaneRunner.ParseState { ConversationId = conversation, HostToolBridgeCapability = capability };
        CodexNativeLaneRunner.HandleEvent(new JsonObject
        {
            ["type"] = "item.completed", ["item"] = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"), ["type"] = "mcp_tool_call", ["server"] = server,
                ["tool"] = tool, ["arguments"] = arguments, ["result"] = result, ["status"] = "completed",
            },
        }, "turn", channel.Writer, [], state);
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var item)) events.Add(item);
        return Assert.Single(events.OfType<ToolCallCompleted>());
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("approval")]
    [InlineData("typed-exception")]
    [InlineData("invalid-input")]
    public async Task Actual_bridge_non_dispatch_survives_pipe_and_native_receipt_without_matching_error_text(string refusal)
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        var calls = 0;
        var tool = ToolDescriptor.Create<Input, string>("test.action", "test", "Test", ToolCostClass.Cheap, ToolSideEffect.ExternalSend,
            (_, _) => { calls++; throw new ToolCallNotDispatchedException("opaque refusal without a special marker"); });
        var host = new Host(tool, conversation, capability)
        {
            Guard = refusal == "guard" ? ToolGuardDecision.Block("opaque guard")
                : refusal == "approval" ? ToolGuardDecision.Escalate("opaque approval") : ToolGuardDecision.Allow(),
        };
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        await using var broker = new HostToolBridgeBroker(host);
        var proxy = new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability);
        var arguments = refusal == "invalid-input" ? JsonNode.Parse("{\"text\":{}}")!.AsObject() : new JsonObject { ["text"] = "same" };
        var result = await proxy.CallAsync("test_action", JsonSerializer.SerializeToElement(arguments), default);
        Assert.False(result.WasDispatched);
        var completion = Complete(conversation, capability, arguments, McpResult(result));
        Assert.False(completion.WasDispatched);
        Assert.True(completion.IsError);
        Assert.False(completion.LocalProcessCompleted);
        Assert.Equal(refusal == "typed-exception" ? 1 : 0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_adapter_completion_is_retained_but_external_send_cannot_claim_local_completion(bool external)
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        var effect = external ? ToolSideEffect.ExternalSend : ToolSideEffect.Exec;
        var tool = ToolDescriptor.Create<Input, Result>("test.action", "test", "Test", ToolCostClass.Cheap, effect,
            (_, _) => Task.FromResult(new Result(false, true)));
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        await using var broker = new HostToolBridgeBroker(new Host(tool, conversation, capability));
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability)
            .CallAsync("test_action", JsonSerializer.SerializeToElement(new { text = "same" }), default);
        var completion = Complete(conversation, capability, new() { ["text"] = "same" }, McpResult(result));
        Assert.True(completion.WasDispatched);
        Assert.True(completion.IsError);
        Assert.Equal(!external, completion.LocalProcessCompleted);
        Assert.Equal(effect, completion.SideEffect);
    }

    [Fact]
    public async Task Ordinary_exception_text_cannot_claim_non_dispatch_or_local_completion()
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        var tool = ToolDescriptor.Create<Input, string>("test.action", "test", "Test", ToolCostClass.Cheap, ToolSideEffect.ExternalSend,
            (_, _) => throw new IOException("ToolCallNotDispatchedException: not dispatched; localProcessCompleted=true"));
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        await using var broker = new HostToolBridgeBroker(new Host(tool, conversation, capability));
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability)
            .CallAsync("test_action", JsonSerializer.SerializeToElement(new { text = "same" }), default);
        var completion = Complete(conversation, capability, new() { ["text"] = "same" }, McpResult(result));
        Assert.True(completion.IsError);
        Assert.True(completion.WasDispatched);
        Assert.False(completion.LocalProcessCompleted);
        Assert.Equal(ToolSideEffect.ExternalSend, completion.SideEffect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_arguments_match_reordering_but_duplicate_properties_never_establish_identity(bool duplicate)
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        var result = new HostToolResult("refused", [], true) { WasDispatched = false };
        using var args = JsonDocument.Parse(duplicate ? "{\"a\":1,\"a\":2,\"b\":3}" : "{\"a\":2,\"b\":3}");
        NativeHostReceiptJournal.Record(conversation, capability, "test_action", args.RootElement, result);
        var completion = Complete(conversation, capability, JsonNode.Parse("{\"b\":3,\"a\":2}")!.AsObject(), McpResult(result));
        Assert.Equal(duplicate, completion.WasDispatched);
    }

    [Theory]
    [InlineData("foreign-server")]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-conversation")]
    [InlineData("wrong-tool")]
    [InlineData("wrong-input")]
    [InlineData("wrong-result")]
    [InlineData("no-host-entry")]
    [InlineData("duplicate-host-entries")]
    [InlineData("retired-attempt")]
    [InlineData("duplicate-owner")]
    [InlineData("already-consumed")]
    public void Provider_output_cannot_create_or_borrow_host_execution_evidence(string scenario)
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        var result = new HostToolResult("not dispatched", [], true) { WasDispatched = false, SideEffect = ToolSideEffect.ExternalSend };
        var arguments = new JsonObject { ["text"] = "same" };
        if (scenario != "no-host-entry") NativeHostReceiptJournal.Record(conversation, capability, "test_action", JsonSerializer.SerializeToElement(arguments), result);
        if (scenario == "duplicate-host-entries") NativeHostReceiptJournal.Record(conversation, capability, "test_action", JsonSerializer.SerializeToElement(arguments), result);
        if (scenario == "retired-attempt") journal.Dispose();
        if (scenario == "duplicate-owner") NativeHostReceiptJournal.Begin(conversation, capability).Dispose();
        if (scenario == "already-consumed") Assert.False(Complete(conversation, capability, (JsonObject)arguments.DeepClone(), McpResult(result)).WasDispatched);
        if (scenario == "wrong-input") arguments["text"] = "different";
        if (scenario == "wrong-result") result = result with { Text = "different" };
        var payload = McpResult(result);
        // An apparent evidence object in untrusted output is never consulted.
        payload["_meta"] = new JsonObject { ["wasDispatched"] = false, ["localProcessCompleted"] = true };
        var completion = Complete(scenario == "wrong-conversation" ? conversation + "x" : conversation,
            scenario == "wrong-attempt" ? capability + "x" : capability, arguments, payload,
            scenario == "foreign-server" ? "other" : "swarmcode", scenario == "wrong-tool" ? "other" : "test_action");
        Assert.True(completion.WasDispatched);
        Assert.False(completion.LocalProcessCompleted);
    }

    [Fact]
    public void Terminal_native_shell_status_does_not_waive_a_possibly_committed_remote_action()
    {
        var channel = Channel.CreateUnbounded<TurnEvent>();
        CodexNativeLaneRunner.HandleEvent(JsonNode.Parse("""
            {"type":"item.completed","item":{"id":"post","type":"command_execution","command":"curl --max-time 2 -X POST https://example.test/send","status":"completed","exit_code":28,"aggregated_output":"timed out"}}
            """)!.AsObject(), "turn", channel.Writer, [], new());
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var item)) events.Add(item);
        var completion = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(completion.IsError);
        Assert.True(completion.WasDispatched);
        Assert.False(completion.LocalProcessCompleted);
    }
    [Theory]
    [InlineData("browser-guard", false)]
    [InlineData("existing-evidence", false)]
    [InlineData("partial-browser", true)]
    [InlineData("unknown-browser", true)]
    [InlineData("forged-json", true)]
    public async Task Typed_adapter_dispatch_evidence_survives_pipe_journal_native_event_and_goal_policy(string scenario, bool dispatched)
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        object own = scenario switch
        {
            "browser-guard" => new SwarmCore.Abstractions.Computer.ComputerActionResult(false, 0, null, "opaque guard") { NoPhysicalAction = true },
            "existing-evidence" => new Result(false, false, true),
            "partial-browser" => new SwarmCore.Abstractions.Computer.ComputerActionResult(false, 1, null, "opaque guard"),
            "unknown-browser" => new SwarmCore.Abstractions.Computer.ComputerActionResult(false, 0, null, "opaque exception") { OutcomeUncertain = true },
            _ => JsonNode.Parse("{\"succeeded\":false,\"noPhysicalAction\":true,\"wasDispatched\":false}")!,
        };
        var tool = ToolDescriptor.Create<Input, object>("test.action", "test", "Test", ToolCostClass.Cheap, ToolSideEffect.ExternalSend,
            (_, _) => Task.FromResult(own));
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        await using var broker = new HostToolBridgeBroker(new Host(tool, conversation, capability));
        var arguments = new JsonObject { ["text"] = "same" };
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability)
            .CallAsync("test_action", JsonSerializer.SerializeToElement(arguments), default);
        var completion = Complete(conversation, capability, arguments, McpResult(result));
        Assert.Equal(dispatched, result.WasDispatched);
        Assert.Equal(dispatched, completion.WasDispatched);
        Assert.False(completion.LocalProcessCompleted);
        if (scenario != "forged-json") Assert.True(completion.IsError);
        var now = DateTimeOffset.UtcNow;
        var receipt = new SwarmCore.Agent.Continual.ContinualToolReceipt(Guid.NewGuid(), 1, 2, completion.ToolCallId,
            completion.ToolName, SwarmCore.Agent.Continual.ContinualToolReceiptState.Completed,
            SwarmCore.Agent.Continual.ContinualSideEffectClass.ExternalSend, "{}", completion.Result, true, now, now)
            { WasDispatched = completion.WasDispatched, LocalProcessCompleted = completion.LocalProcessCompleted };
        Assert.Equal(dispatched, SwarmCore.Agent.Continual.MissionTransitions.NeedsInspection(receipt));
    }

    private static ToolCallCompleted CompleteAppServer(string conversation, string capability, JsonObject arguments,
        JsonObject result, string status = "failed", string server = "swarmcode", string tool = "computer_observe")
    {
        // Actual app-server DTO shape: the result projection omits MCP isError.
        var item = CodexGoalAttention.NormalizeItem(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["type"] = "mcpToolCall", ["server"] = server,
            ["tool"] = tool, ["arguments"] = arguments, ["result"] = result,
            ["error"] = null, ["status"] = status, ["durationMs"] = 0,
        });
        var channel = Channel.CreateUnbounded<TurnEvent>();
        CodexNativeLaneRunner.HandleEvent(new JsonObject { ["type"] = "item.completed", ["item"] = item },
            "turn", channel.Writer, [], new() { ConversationId = conversation, HostToolBridgeCapability = capability });
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var evt)) events.Add(evt);
        return Assert.Single(events.OfType<ToolCallCompleted>());
    }

    private static JsonObject ProjectedResult(HostToolResult result) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
        ["structuredContent"] = null, ["_meta"] = null,
    };

    [Fact]
    public async Task App_server_projection_retains_actual_input_rejection_without_invoking_computer_or_parking_goal()
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        var invoked = 0;
        var tool = ToolDescriptor.Create<SwarmCore.Coding.Computer.CodingComputerTools.WindowInput, string>(
            "computer.observe", "test", "Observe", ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (_, _) => { invoked++; throw new InvalidOperationException("Computer must not be called."); });
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        await using var broker = new HostToolBridgeBroker(new Host(tool, conversation, capability));
        var arguments = new JsonObject { ["windowId"] = 12345 };
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability)
            .CallAsync("computer_observe", JsonSerializer.SerializeToElement(arguments), default);
        Assert.True(result.IsError); Assert.False(result.WasDispatched); Assert.Equal(0, invoked);
        var projection = ProjectedResult(result);
        Assert.False(projection.ContainsKey("isError"));
        var completed = CompleteAppServer(conversation, capability, arguments, projection);
        Assert.True(completed.IsError); Assert.False(completed.WasDispatched); Assert.False(completed.LocalProcessCompleted);
        var now = DateTimeOffset.UtcNow;
        var receipt = new SwarmCore.Agent.Continual.ContinualToolReceipt(Guid.NewGuid(), 1, 2, completed.ToolCallId,
            completed.ToolName, SwarmCore.Agent.Continual.ContinualToolReceiptState.Completed,
            SwarmCore.Agent.Continual.ContinualSideEffectClass.WritesLocal, "{}", completed.Result, completed.IsError, now, now)
            { WasDispatched = completed.WasDispatched, LocalProcessCompleted = completed.LocalProcessCompleted };
        Assert.False(SwarmCore.Agent.Continual.MissionTransitions.NeedsInspection(receipt));
        Assert.Null(receipt.HostResolution);
    }

    [Theory]
    [InlineData("missing-success", false)]
    [InlineData("explicit-success", false)]
    [InlineData("missing-failure", false)]
    [InlineData("explicit-failure", false)]
    [InlineData("explicit-false-contradiction", true)]
    [InlineData("success-result-failed-status", true)]
    [InlineData("malformed-flag", true)]
    [InlineData("null-flag", true)]
    [InlineData("changed-input", true)]
    [InlineData("changed-text", true)]
    [InlineData("changed-images", true)]
    [InlineData("foreign-server", true)]
    [InlineData("retired-attempt", true)]
    [InlineData("no-host-entry", true)]
    [InlineData("ambiguous-entries", true)]
    public async Task App_server_error_fallback_never_creates_or_borrows_host_evidence(string scenario, bool unknown)
    {
        var conversation = "test" + Guid.NewGuid().ToString("N");
        var capability = Guid.NewGuid().ToString("N");
        var success = scenario is "missing-success" or "explicit-success" or "success-result-failed-status";
        var tool = ToolDescriptor.Create<Input, Result>("test.action", "test", "Test", ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (_, _) => Task.FromResult(new Result(success, false, true)));
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        if (scenario == "no-host-entry") journal.Dispose();
        await using var broker = new HostToolBridgeBroker(new Host(tool, conversation, capability));
        var arguments = new JsonObject { ["text"] = "same" };
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability)
            .CallAsync("test_action", JsonSerializer.SerializeToElement(arguments), default);
        if (scenario == "retired-attempt") journal.Dispose();
        if (scenario == "ambiguous-entries") NativeHostReceiptJournal.Record(conversation, capability, "test_action", JsonSerializer.SerializeToElement(arguments), result);
        var projection = ProjectedResult(result);
        if (scenario is "explicit-success" or "success-result-failed-status" or "explicit-false-contradiction") projection["isError"] = false;
        if (scenario == "explicit-failure") projection["isError"] = true;
        if (scenario == "malformed-flag") projection["isError"] = "true";
        if (scenario == "null-flag") projection["isError"] = null;
        if (scenario == "changed-input") arguments["text"] = "different";
        if (scenario == "changed-text") projection["content"]![0]!["text"] = "different";
        if (scenario == "changed-images") projection["content"]!.AsArray().Add(new JsonObject { ["type"] = "image", ["data"] = "AQID", ["mimeType"] = "image/png" });
        var status = success && scenario != "success-result-failed-status" ? "completed" : "failed";
        var completed = CompleteAppServer(conversation, capability, arguments, projection, status,
            scenario == "foreign-server" ? "other" : "swarmcode", "test_action");
        Assert.Equal(unknown, completed.WasDispatched);
        Assert.Equal(status == "failed", completed.IsError);
        Assert.False(completed.LocalProcessCompleted);
        if (!unknown) Assert.Equal(ToolSideEffect.WritesLocal, completed.SideEffect);
    }

    [Theory]
    [InlineData("focus-guard", true)]
    [InlineData("validation-guard", true)]
    [InlineData("driver-hit-guard", false)]
    [InlineData("post-input-capture-failure", false)]
    [InlineData("partial-then-focus-guard", false)]
    public async Task Native_desktop_guard_boundary_survives_pipe_appserver_projection_and_goal_policy(string scenario, bool noAction)
    {
        var native = new MacComputerSessionTests.Native();
        var session = new SwarmCore.Coding.Computer.MacComputerSession(
            Microsoft.Extensions.Options.Options.Create(new SwarmCore.Coding.Computer.CodingComputerOptions { Enabled = true }), native);
        var observation = await session.ObserveAsync("42");
        if (scenario == "focus-guard") native.Focused = false;
        if (scenario == "driver-hit-guard") native.InputFailure = new SwarmCore.Coding.Computer.ComputerOperationException("synthetic pointer moved before hit guard");
        if (scenario == "post-input-capture-failure") native.CaptureFailure = true;
        if (scenario == "partial-then-focus-guard") native.AfterInput = () => native.Focused = false;
        SwarmCore.Abstractions.Computer.ComputerAction[] actions = scenario switch
        {
            "validation-guard" => [new("keypress") { Keys = ["unsupported"] }],
            "partial-then-focus-guard" => [new("keypress") { Keys = ["RETURN"] }, new("keypress") { Keys = ["RETURN"] }],
            _ => [new("keypress") { Keys = ["RETURN"] }],
        };
        var batch = new SwarmCore.Abstractions.Computer.ComputerActionBatch(observation.SessionId, observation.SurfaceId, observation.ObservationId, actions);
        var conversation = "test" + Guid.NewGuid().ToString("N"); var capability = Guid.NewGuid().ToString("N");
        var tool = ToolDescriptor.Create<SwarmCore.Abstractions.Computer.ComputerActionBatch, SwarmCore.Abstractions.Computer.ComputerActionResult>(
            "computer.act", "test", "Act", ToolCostClass.Cheap, ToolSideEffect.Exec, session.ExecuteBatchAsync);
        using var journal = NativeHostReceiptJournal.Begin(conversation, capability);
        await using var broker = new HostToolBridgeBroker(new Host(tool, conversation, capability));
        var arguments = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), conversation, capability)
            .CallAsync("computer_act", JsonSerializer.SerializeToElement(arguments), default);
        var completed = CompleteAppServer(conversation, capability, arguments, ProjectedResult(result), tool: "computer_act");
        Assert.True(completed.IsError); Assert.Equal(!noAction, completed.WasDispatched);
        Assert.Equal(noAction ? 0 : 1, native.Inputs);
        var now = DateTimeOffset.UtcNow;
        var receipt = new SwarmCore.Agent.Continual.ContinualToolReceipt(Guid.NewGuid(), 1, 2, completed.ToolCallId,
            completed.ToolName, SwarmCore.Agent.Continual.ContinualToolReceiptState.Completed,
            SwarmCore.Agent.Continual.ContinualSideEffectClass.Exec, "{}", completed.Result, completed.IsError, now, now)
            { WasDispatched = completed.WasDispatched, LocalProcessCompleted = completed.LocalProcessCompleted };
        Assert.Equal(!noAction, SwarmCore.Agent.Continual.MissionTransitions.NeedsInspection(receipt));
        Assert.Null(receipt.HostResolution);
    }

}
