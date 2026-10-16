using SwarmCore.Coding;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCore.Coding.Tests;

public sealed class OllamaCodingLanesTests
{
    private const string Endpoint = "http://localhost:11434";
    private static OllamaInstalledModel Model(string name, string digest = "d1", bool tools = true) => new(name, digest, 123, tools ? ["tools"] : ["completion"], IsOnDevice: true);

    [Fact]
    public void ProviderPickerCollapsesModelsButPreservesTheSelectedRouteAndOtherNamedLanes()
    {
        var lanes = OllamaCodingLanes.Merge([new("claude", "ClaudeCodeNative", "default"), new("claude-tools", "ClaudeCode", "default")], Endpoint,
            [Model("a:latest"), Model("b-abliterated:latest")], [], true);
        var selected = lanes.Last();
        var choices = CodingLaneSelections.ForPicker(lanes, selected.Name);
        Assert.Equal(3, choices.Count);
        Assert.Equal(new[] { "claude", "claude-tools", "Ollama" }, choices.Select(l => l.SelectionLabel));
        Assert.Same(selected, choices.Last()); // Existing saved hash route and explicit model pins survive.
        Assert.Equal(2, lanes.Count(l => l.Provider == "Ollama")); // Execution still has every model.
        Assert.Single(CodingLaneSelections.ForPicker(lanes, "missing").Where(l => l.Provider == "Ollama"));
    }

    [Fact]
    public void RefusalsPreferInstalledReducedRefusalModelsWhileRouteFailuresKeepOrder()
    {
        var cloud = new CodingLane("cloud", "Cloud", "model");
        var other = new CodingLane("other", "Other", "model");
        var lanes = OllamaCodingLanes.Merge([cloud, other], Endpoint,
            [Model("community/abliterated:latest"), Model("community/uncensored:latest"), Model("no-tools-abliterated", tools:false)], [], true);
        Assert.Equal(4, lanes.Count);
        Assert.Equal("Ollama", CodingRoutingPolicy.HandOffCandidates(lanes, [cloud], LaneOutcomeKind.Refused).First().Provider);
        Assert.Equal("Ollama", CodingRoutingPolicy.HandOffCandidates(lanes, [cloud], LaneOutcomeKind.PartialRefusal).First().Provider);
        Assert.Equal(other, CodingRoutingPolicy.HandOffCandidates(lanes, [cloud], LaneOutcomeKind.RouteUnavailable).First());
        Assert.False(CodingRoutingPolicy.CanHandOff(new CodingTurnRequest { ConversationId = "c", UserMessage = "request", SelectionPinned = true }, true));
    }

    [Fact]
    public void CorrectionsAndOptOutAreScopedToServerAndDigest()
    {
        var model = Model("custom:latest");
        var preference = new LocalModelPreference(Endpoint, model.Name, model.Digest, LocalModelBehavior.Abliterated);
        Assert.True(Assert.Single(OllamaCodingLanes.Merge([], Endpoint, [model], [preference], true)).PreferOnDecline);
        Assert.False(Assert.Single(OllamaCodingLanes.Merge([], Endpoint, [model with { Digest = "d2" }], [preference], true)).PreferOnDecline);
        Assert.False(Assert.Single(OllamaCodingLanes.Merge([], "http://127.0.0.1:11435", [model], [preference], true)).PreferOnDecline);
        Assert.False(Assert.Single(OllamaCodingLanes.Merge([], Endpoint, [model], [preference with { UseOnDecline = false }], true)).PreferOnDecline);
        Assert.False(Assert.Single(OllamaCodingLanes.Merge([], Endpoint, [model], [preference], false)).PreferOnDecline);
    }

    [Fact]
    public void ExistingRoutesAreAnnotatedWithoutDuplicatesAndManualStandardCorrectionWins()
    {
        var model = Model("hf.co/user/abliterated:Q4_K_M");
        var lane = new CodingLane("mine", "Ollama", model.Name);
        var result = Assert.Single(OllamaCodingLanes.Merge([lane], Endpoint, [model], [], true));
        Assert.Equal("mine", result.Name); Assert.True(result.PreferOnDecline);
        Assert.False(Assert.Single(OllamaCodingLanes.Merge([lane], Endpoint, [model],
            [new(Endpoint, model.Name, model.Digest, LocalModelBehavior.Standard)], true)).PreferOnDecline);
    }

    [Fact]
    public void LatestAliasesAreNotDuplicatedAndCloudBackedModelsAreNotDiscovered()
    {
        var model = Model("abliterated:latest");
        var result = OllamaCodingLanes.Merge([new("mine", "Ollama", "abliterated")], Endpoint,
            [model, Model("uncensored") with { IsOnDevice = false }], [], true);
        Assert.True(Assert.Single(result).PreferOnDecline);
    }

    [Theory]
    [InlineData("huihui/abliterated:latest", LocalModelBehavior.Abliterated)]
    [InlineData("HauhauCS/Gemma-Uncensored:Q4_K_M", LocalModelBehavior.Uncensored)]
    [InlineData("ordinary:latest", LocalModelBehavior.Unknown)]
    public void PublisherLabelsAreDistinctAndAttributed(string model, LocalModelBehavior expected)
    {
        var result = LocalModelBehaviors.Classify(model);
        Assert.Equal(expected, result.Behavior);
        Assert.NotEmpty(result.Evidence);
    }
}
