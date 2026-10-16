using System.Text.Json;
using SwarmCore.Abstractions.Context;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

public sealed record MissionChildToolSummary(Guid Id, string DelegationId, string Status, bool Verified, bool RetiredByParent,
    string? Summary, IReadOnlyList<Guid> DependencyIds, IReadOnlyList<string> EvidenceReferences);
public sealed record MissionDelegateToolResult(string Status, MissionChildToolSummary? Child, string? Reason) : IContextPackEvidence
{
    ContextPack? IContextPackEvidence.WorkingContextSourcePack => null;
}
public sealed record MissionChildrenToolResult(IReadOnlyList<MissionChildToolSummary> Children, int OmittedCount) : IContextPackEvidence
{
    ContextPack? IContextPackEvidence.WorkingContextSourcePack => null;
}
public sealed record MissionJoinToolResult(bool Complete, IReadOnlyList<MissionChildToolSummary> Children,
    IReadOnlyList<string> Blockers, int OmittedChildren, int OmittedBlockers) : IContextPackEvidence
{
    ContextPack? IContextPackEvidence.WorkingContextSourcePack => null;
}

/// <summary>Model-facing views stay below durable receipt limits; durable snapshots remain in the store.</summary>
public static class MissionDelegationToolProjection
{
    private const int MaximumCharacters = 6000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string? Clip(string? value, int length) => value is null || value.Length <= length ? value : value[..length] + "…";
    public static MissionChildToolSummary Child(ContinualObjectiveSnapshot child) => new(child.Id,
        child.Mission?.Delegation?.Spec.DelegationId ?? child.Id.ToString("N"), child.Status.ToString(),
        child.Mission?.Delegation?.RetiredByParent != true && MissionDelegationPolicy.IsVerified(child), child.Mission?.Delegation?.RetiredByParent == true,
        Clip(child.LastOutput ?? child.ObjectiveSummary, 240), child.Mission?.Delegation?.Spec.DependencyIds.Take(16).ToArray() ?? [],
        child.Receipts.Where(r => r.State == ContinualToolReceiptState.Completed && !r.IsError)
            .TakeLast(4).Select(MissionVerification.Reference).ToArray());
    public static MissionDelegateToolResult Delegate(MissionDelegationResult result)
    {
        var projected = new MissionDelegateToolResult(result.Status, result.Child is null ? null : Child(result.Child), Clip(result.Reason, 500));
        return JsonSerializer.Serialize(projected, Json).Length <= MaximumCharacters ? projected : projected with { Child = projected.Child! with { Summary = null, EvidenceReferences = [], DependencyIds = [] } };
    }
    public static MissionChildrenToolResult Children(IReadOnlyList<ContinualObjectiveSnapshot> children)
    {
        var result = new MissionChildrenToolResult(children.Select(Child).ToArray(), 0);
        while (JsonSerializer.Serialize(result, Json).Length > MaximumCharacters && result.Children.Count > 0)
            result = result with { Children = result.Children.Take(result.Children.Count - 1).ToArray(), OmittedCount = result.OmittedCount + 1 };
        return result;
    }
    public static MissionJoinToolResult Join(MissionDelegationJoin join)
    {
        var result = new MissionJoinToolResult(join.Complete, join.Children.Select(Child).ToArray(), join.Blockers.Select(b => Clip(b, 300)!).ToArray(), 0, 0);
        while (JsonSerializer.Serialize(result, Json).Length > MaximumCharacters)
        {
            if (result.Children.Count > 0) result = result with { Children = result.Children.Take(result.Children.Count - 1).ToArray(), OmittedChildren = result.OmittedChildren + 1 };
            else if (result.Blockers.Count > 0) result = result with { Blockers = result.Blockers.Take(result.Blockers.Count - 1).ToArray(), OmittedBlockers = result.OmittedBlockers + 1 };
            else break;
        }
        return result;
    }
}
