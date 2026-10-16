using System.ComponentModel;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tools;

/// <summary>
/// Lets a model declare, in a structured way, that it will not do all or part of the
/// request. The orchestrator reads the call and hands exactly the declined remainder to
/// the next lane, so a decline is never lost in prose and finished work is never redone.
/// </summary>
public sealed class HarnessDeclineTool
{
    public const string ToolId = "harness.decline";

    public sealed class Input
    {
        [Description("\"all\" when you will not do any of the request; \"part\" when you did or will do some of it and decline the rest.")]
        public required string Scope { get; set; }

        [Description("One sentence on why, in plain words.")]
        public required string Reason { get; set; }

        [Description("For scope \"part\": the exact remaining work another model should do, stated so it can act without reading this conversation.")]
        public string? Remaining { get; set; }
    }

    public sealed class Output
    {
        public required bool Acknowledged { get; init; }
        public required string Next { get; init; }
    }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: false,
        ownerAgentId: "coding",
        description: "Declare that you will not do all or part of the user's request (policy, capability, or judgement). " +
                     "Call it as soon as you know, with scope \"all\" or \"part\" and, for \"part\", the exact remaining work. " +
                     "Another model takes over what you decline, so finish everything you can first and never redo work that is already done.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["decline", "refuse", "cannot", "hand off", "harness"],
        invoke: (input, _) =>
        {
            var scope = Parse(input.Scope);
            var next = scope == "part"
                ? "Recorded. Finish every other part of the request now, then summarize what you completed and what remains."
                : "Recorded. Reply with one plain sentence stating that you are not doing this request and why; another model will take over.";
            return Task.FromResult(new Output { Acknowledged = true, Next = next });
        });

    /// <summary>Normalizes a model-supplied scope to "all" or "part".</summary>
    public static string Parse(string? scope)
        => scope is not null && scope.Trim().StartsWith("part", StringComparison.OrdinalIgnoreCase) ? "part" : "all";

    /// <summary>Builds the typed signal from a tool call's input, or null when the input is unusable.</summary>
    public static DeclineSignal? FromInput(JsonObject? input)
    {
        if (input is null) return null;
        var reason = (string?)input["reason"] ?? (string?)input["Reason"] ?? "";
        var remaining = (string?)input["remaining"] ?? (string?)input["Remaining"];
        var scope = Parse((string?)input["scope"] ?? (string?)input["Scope"]);
        return new DeclineSignal(scope, reason.Trim(), string.IsNullOrWhiteSpace(remaining) ? null : remaining.Trim());
    }
}
