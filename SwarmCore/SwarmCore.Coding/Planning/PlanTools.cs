using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Native;

namespace SwarmCore.Coding.Planning;

public sealed record PlanItemInput(string Content, string Status);
public sealed record PlanUpdateInput(IReadOnlyList<PlanItemInput>? Items);
public sealed record PlanReadInput;

public sealed record PlanItemView(string Content, string Status);

public sealed record PlanResult(bool Success, string? Message, IReadOnlyList<PlanItemView> Items, int Completed, int Total)
{
    public static PlanResult From(ConversationPlan plan, string? message = null)
        => new(true, message, plan.Items.Select(i => new PlanItemView(i.Content, ConversationPlanRules.Format(i.Status))).ToList(),
            plan.CompletedCount, plan.Total);
    public static PlanResult Fail(string message) => new(false, message, [], 0, 0);
}

/// <summary>
/// The <c>plan.*</c> task-list tools. <paramref name="owner"/> resolves the trusted calling conversation (the current
/// turn for engine lanes, the bridged native turn for CLI lanes); a model can never name another conversation.
/// </summary>
public static class PlanTools
{
    public const string OwnerAgentId = "coding.plan";
    public const string UpdateToolId = "plan.update";
    public const string ReadToolId = "plan.read";
    public static readonly IReadOnlyList<string> AllToolIds = [UpdateToolId, ReadToolId];

    /// <summary>Guidance for the coding prompt.</summary>
    public const string Guidance =
        "- For multi-step work (three or more steps, or whenever the person asks for a plan), keep a task list with plan.update: "
        + "send the full list each time (items with content and status pending, in_progress or completed). Keep exactly one item "
        + "in_progress, mark it completed as soon as it is done, and add or drop items as you learn more. The person sees the list live. "
        + "Skip it for single quick answers.";

    public static IReadOnlyList<ToolDescriptor> Create(IConversationPlanStore store, Func<string?> owner) =>
    [
        ToolDescriptor.Create<PlanUpdateInput, PlanResult>(UpdateToolId, OwnerAgentId,
            "Replace this conversation's task list (shown live to the person). items: the complete list, each {content, status} with "
            + "status pending, in_progress or completed; keep exactly one in_progress. Returns the saved list.",
            ToolCostClass.Trivial, ToolSideEffect.WritesLocal,
            (input, ct) => Run(owner, async id =>
            {
                var (items, error) = ConversationPlanRules.Normalize(input.Items?.Select(i => ((string?)i?.Content, (string?)i?.Status)), strict: true);
                if (items is null) return PlanResult.Fail(error!);
                var plan = await store.SetAsync(id, items, ct).ConfigureAwait(false);
                return PlanResult.From(plan, plan.Total == 0 ? "Cleared the task list." : $"Saved: {plan.CompletedCount}/{plan.Total} completed.");
            }),
            keywords: ["plan", "todo", "task list", "checklist", "steps", "progress"]),
        ToolDescriptor.Create<PlanReadInput, PlanResult>(ReadToolId, OwnerAgentId,
            "Read this conversation's current task list.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            (input, ct) => Run(owner, async id => PlanResult.From(await store.GetAsync(id, ct).ConfigureAwait(false))),
            keywords: ["plan", "todo", "task list", "checklist"]),
    ];

    private static async Task<PlanResult> Run(Func<string?> owner, Func<string, Task<PlanResult>> call)
    {
        if (owner() is not { Length: > 0 } id) return PlanResult.Fail("The task list can be used only inside a conversation turn.");
        try { return await call(id).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException) { return PlanResult.Fail(ex.Message); }
    }

    /// <summary>
    /// A native CLI's own task-list call mirrored into the conversation's list: Claude Code's <c>TodoWrite</c>
    /// (<c>{"todos":[{"content","status","activeForm"}]}</c>). Null when the call is not one or its input is not that shape.
    /// </summary>
    public static IReadOnlyList<PlanItem>? FromNativeTodoCall(string toolName, JsonObject? input)
    {
        if (!string.Equals(toolName, "TodoWrite", StringComparison.Ordinal) || input?["todos"] is not JsonArray todos) return null;
        var proposed = new List<(string?, string?)>();
        foreach (var node in todos)
        {
            if (node is not JsonObject todo) return null;
            proposed.Add((Text(todo["content"]), Text(todo["status"])));
        }
        return ConversationPlanRules.Normalize(proposed, strict: false).Items;

        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    }
}
