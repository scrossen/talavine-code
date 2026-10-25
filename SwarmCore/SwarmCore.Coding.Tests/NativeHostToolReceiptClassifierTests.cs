using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Peers;

namespace SwarmCore.Coding.Tests;

public sealed class NativeHostToolReceiptClassifierTests
{
    private static InMemoryCapabilityCatalog PeerCatalog()
    {
        var catalog = new InMemoryCapabilityCatalog();
        foreach (var tool in PeerTools.Create(null!, () => "conversation", () => true)) catalog.Register(tool);
        return catalog;
    }

    [Fact]
    public void Owning_peer_metadata_classifies_native_start_and_completion_without_invoking_the_tool()
    {
        var classifier = new NativeHostToolReceiptClassifier(PeerCatalog());
        var input = JsonNode.Parse("""{"server":"swarmcode","tool":"peers_wait","arguments":{"workId":"existing","timeoutSeconds":45}}""")!.AsObject();
        var effects = new Dictionary<string, ToolSideEffect?>();

        var start = Assert.IsType<ToolCallStarted>(classifier.Enrich(new ToolCallStarted("turn", "call",
            CodexNativeLaneRunner.McpToolName, input), effects));
        var end = Assert.IsType<ToolCallCompleted>(classifier.Enrich(new ToolCallCompleted("turn", "call",
            CodexNativeLaneRunner.McpToolName, "observed", false, TimeSpan.Zero), effects));

        Assert.Equal(ToolSideEffect.ReadOnly, start.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, end.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, classifier.Resolve("mcp__swarmcode__peers_wait",
            JsonNode.Parse("""{"workId":"existing"}""")!.AsObject()));
        Assert.Equal(ToolSideEffect.Exec, classifier.Resolve(CodexNativeLaneRunner.McpToolName,
            JsonNode.Parse("""{"server":"swarmcode","tool":"peers_cancel","arguments":{"workId":"existing"}}""")!.AsObject()));
    }

    [Theory]
    [InlineData("other", "peers_wait")]
    [InlineData("SWARMCODE", "peers_wait")]
    [InlineData("swarmcode", "unknown_tool")]
    public void Foreign_or_unknown_tools_remain_unclassified(string server, string tool)
    {
        var classifier = new NativeHostToolReceiptClassifier(PeerCatalog());
        Assert.Null(classifier.Resolve(CodexNativeLaneRunner.McpToolName,
            new JsonObject { ["server"] = server, ["tool"] = tool, ["arguments"] = new JsonObject() }));
    }

    [Fact]
    public void Ambiguous_normalized_names_and_malformed_inputs_never_establish_read_only_effects()
    {
        var catalog = PeerCatalog();
        catalog.Register(ToolDescriptor.Create<PeerWorkInput, string>("peers_wait", "other", "Collision",
            ToolCostClass.Cheap, ToolSideEffect.Exec, (_, _) => Task.FromResult("not invoked")));
        var classifier = new NativeHostToolReceiptClassifier(catalog);
        Assert.Null(classifier.Resolve(CodexNativeLaneRunner.McpToolName,
            JsonNode.Parse("""{"server":"swarmcode","tool":"peers_wait","arguments":{"workId":"existing"}}""")!.AsObject()));
        var ambiguous = new ToolCallStarted("turn", "ambiguous", CodexNativeLaneRunner.McpToolName,
            JsonNode.Parse("""{"server":"swarmcode","tool":"peers_wait","arguments":{"workId":"existing"}}""")!.AsObject())
            { SideEffect = ToolSideEffect.ReadOnly };
        var effects = new Dictionary<string, ToolSideEffect?>();
        Assert.Null(Assert.IsType<ToolCallStarted>(classifier.Enrich(ambiguous, effects)).SideEffect);
        Assert.Null(Assert.IsType<ToolCallCompleted>(classifier.Enrich(new ToolCallCompleted("turn", "ambiguous",
            CodexNativeLaneRunner.McpToolName, "unknown result", false, TimeSpan.Zero)
                { SideEffect = ToolSideEffect.ReadOnly }, effects)).SideEffect);
        Assert.Null(new NativeHostToolReceiptClassifier(PeerCatalog()).Resolve(CodexNativeLaneRunner.McpToolName,
            JsonNode.Parse("""{"server":"swarmcode","tool":"peers_wait","arguments":{"timeoutSeconds":"invalid"}}""")!.AsObject()));
    }

    private sealed record Input(bool Send);

    [Fact]
    public void Actual_arguments_can_escalate_effects_and_explicit_mutating_receipts_are_not_relaxed()
    {
        var catalog = new InMemoryCapabilityCatalog();
        catalog.Register(ToolDescriptor.Create<Input, string>("example.action", "test", "Argument-aware operation",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (_, _) => Task.FromResult("not invoked"))
            with { ResolveSideEffect = input => ((Input)input).Send ? ToolSideEffect.ExternalSend : ToolSideEffect.ReadOnly });
        var classifier = new NativeHostToolReceiptClassifier(catalog);
        var input = JsonNode.Parse("""{"server":"swarmcode","tool":"example_action","arguments":{"send":true}}""")!.AsObject();
        Assert.Equal(ToolSideEffect.ExternalSend, classifier.Resolve(CodexNativeLaneRunner.McpToolName, input));
        var legacyRead = new ToolCallStarted("turn", "call", CodexNativeLaneRunner.McpToolName, input)
            { SideEffect = ToolSideEffect.ReadOnly };
        var effects = new Dictionary<string, ToolSideEffect?>();
        Assert.Equal(ToolSideEffect.ExternalSend, Assert.IsType<ToolCallStarted>(classifier.Enrich(legacyRead, effects)).SideEffect);
        Assert.Equal(ToolSideEffect.ExternalSend, Assert.IsType<ToolCallCompleted>(classifier.Enrich(
            new ToolCallCompleted("turn", "call", CodexNativeLaneRunner.McpToolName, "result", false, TimeSpan.Zero)
                { SideEffect = ToolSideEffect.ReadOnly }, effects)).SideEffect);
        input["arguments"]!["send"] = false;
        var start = new ToolCallStarted("turn", "call", CodexNativeLaneRunner.McpToolName, input)
            { SideEffect = ToolSideEffect.ExternalSend };
        Assert.Equal(ToolSideEffect.ExternalSend, Assert.IsType<ToolCallStarted>(classifier.Enrich(start,
            new Dictionary<string, ToolSideEffect?>())).SideEffect);
    }
}
