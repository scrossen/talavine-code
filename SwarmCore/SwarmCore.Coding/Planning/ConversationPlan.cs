using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace SwarmCore.Coding.Planning;

/// <summary>Where a task-list item stands. Serialized as <c>pending</c>, <c>in_progress</c> or <c>completed</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlanItemStatus>))]
public enum PlanItemStatus
{
    [JsonStringEnumMemberName("pending")] Pending = 0,
    [JsonStringEnumMemberName("in_progress")] InProgress = 1,
    [JsonStringEnumMemberName("completed")] Completed = 2,
}

/// <summary>One step of a conversation's task list.</summary>
public sealed record PlanItem(string Content, PlanItemStatus Status);

/// <summary>A conversation's current task list: always replaced whole, never merged.</summary>
public sealed record ConversationPlan(IReadOnlyList<PlanItem> Items, DateTime UpdatedUtc)
{
    public static ConversationPlan Empty { get; } = new([], DateTime.MinValue);
    [JsonIgnore] public int Total => Items.Count;
    [JsonIgnore] public int CompletedCount => Items.Count(i => i.Status == PlanItemStatus.Completed);
    [JsonIgnore] public PlanItem? Current => Items.FirstOrDefault(i => i.Status == PlanItemStatus.InProgress);
}

/// <summary>
/// Per-conversation task-list state for <c>plan.update</c>/<c>plan.read</c> (and a native CLI's own task list mirrored
/// into it). The default is in memory; a host replaces it with a durable store. <see cref="Changed"/> is raised after a
/// successful write, on the writer's thread; a UI marshals it to its dispatcher.
/// </summary>
public interface IConversationPlanStore
{
    ValueTask<ConversationPlan> GetAsync(string conversationId, CancellationToken ct = default);
    ValueTask<ConversationPlan> SetAsync(string conversationId, IReadOnlyList<PlanItem> items, CancellationToken ct = default);
    ValueTask DeleteAsync(string conversationId, CancellationToken ct = default);
    event Action<string, ConversationPlan>? Changed;
}

public sealed class InMemoryConversationPlanStore : IConversationPlanStore
{
    private readonly ConcurrentDictionary<string, ConversationPlan> _plans = new(StringComparer.Ordinal);
    public event Action<string, ConversationPlan>? Changed;

    public ValueTask<ConversationPlan> GetAsync(string conversationId, CancellationToken ct = default)
        => ValueTask.FromResult(_plans.TryGetValue(conversationId, out var plan) ? plan : ConversationPlan.Empty);

    public ValueTask<ConversationPlan> SetAsync(string conversationId, IReadOnlyList<PlanItem> items, CancellationToken ct = default)
    {
        var plan = new ConversationPlan(items.ToArray(), DateTime.UtcNow);
        _plans[conversationId] = plan;
        Changed?.Invoke(conversationId, plan);
        return ValueTask.FromResult(plan);
    }

    public ValueTask DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        if (_plans.TryRemove(conversationId, out _)) Changed?.Invoke(conversationId, ConversationPlan.Empty);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Validation shared by the tools, the native mirror and durable stores.</summary>
public static class ConversationPlanRules
{
    public const int MaxItems = 50;
    public const int MaxContentLength = 500;

    /// <summary>
    /// Normalizes a proposed list. Strict (the model's <c>plan.update</c>) rejects an unknown status, empty content and
    /// more than one in-progress item; lenient (a mirrored native list) drops empty items, clips long ones and keeps
    /// the first in-progress item only. Both cap the list at <see cref="MaxItems"/>.
    /// </summary>
    public static (IReadOnlyList<PlanItem>? Items, string? Error) Normalize(IEnumerable<(string? Content, string? Status)>? proposed, bool strict)
    {
        var items = new List<PlanItem>();
        var inProgress = false;
        foreach (var (rawContent, rawStatus) in proposed ?? [])
        {
            var content = (rawContent ?? "").Trim().ReplaceLineEndings(" ");
            if (content.Length == 0)
            {
                if (strict) return (null, "Every item needs content.");
                continue;
            }
            if (content.Length > MaxContentLength)
            {
                if (strict) return (null, $"Keep each item under {MaxContentLength} characters.");
                content = content[..MaxContentLength];
            }
            if (ParseStatus(rawStatus) is not { } status)
            {
                if (strict) return (null, $"Unknown status '{rawStatus}'. Use pending, in_progress or completed.");
                status = PlanItemStatus.Pending;
            }
            if (status == PlanItemStatus.InProgress)
            {
                if (inProgress)
                {
                    if (strict) return (null, "Keep exactly one item in_progress at a time.");
                    status = PlanItemStatus.Pending;
                }
                inProgress = true;
            }
            items.Add(new PlanItem(content, status));
            if (items.Count > MaxItems)
            {
                if (strict) return (null, $"Keep the list to {MaxItems} items or fewer.");
                items.RemoveAt(items.Count - 1);
                break;
            }
        }
        return (items, null);
    }

    public static PlanItemStatus? ParseStatus(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
    {
        "pending" or "todo" => PlanItemStatus.Pending,
        "in_progress" or "inprogress" or "active" => PlanItemStatus.InProgress,
        "completed" or "complete" or "done" => PlanItemStatus.Completed,
        _ => null,
    };

    public static string Format(PlanItemStatus status) => status switch
    {
        PlanItemStatus.InProgress => "in_progress",
        PlanItemStatus.Completed => "completed",
        _ => "pending",
    };
}
