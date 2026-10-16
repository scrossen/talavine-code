using SwarmCore.Abstractions.AgentWork;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Business;

public enum OperatorWorkTargetKind { Source, Partition }
public sealed record OperatorWorkToolAdapter(string ToolId, string EffectId, ToolSideEffect SideEffect,
    OperatorWorkTargetKind TargetKind, Type InputType);
public sealed record OperatorWorkResultAdapter(string Kind, string FirstToolId, IReadOnlyList<string> RequiredToolIds,
    IReadOnlyList<string>? OptionalToolIds = null);

/// <summary>Immutable host registrations constrain original declared work; registration creates no grant or inference allowance.</summary>
public sealed partial class OperatorWorkAdapterRegistry
{
    private readonly OperatorWorkToolAdapter[] tools;
    private readonly OperatorWorkResultAdapter[] results;
    public OperatorWorkAdapterRegistry(IEnumerable<OperatorWorkToolAdapter> tools, IEnumerable<OperatorWorkResultAdapter> results)
    {
        this.tools = tools.ToArray(); this.results = results.Select(r => r with
        { RequiredToolIds = r.RequiredToolIds.ToArray(), OptionalToolIds = r.OptionalToolIds?.ToArray() }).ToArray();
        if (this.tools.Any(t => string.IsNullOrWhiteSpace(t.ToolId) || string.IsNullOrWhiteSpace(t.EffectId)
                || !Enum.IsDefined(t.SideEffect) || !Enum.IsDefined(t.TargetKind) || t.InputType is null)
            || this.tools.Select(t => t.ToolId).Distinct(StringComparer.Ordinal).Count() != this.tools.Length
            || this.results.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Count() != this.results.Length
            || this.results.Any(r => string.IsNullOrWhiteSpace(r.Kind) || !r.RequiredToolIds.Contains(r.FirstToolId, StringComparer.Ordinal)
                || r.RequiredToolIds.Count == 0 || r.RequiredToolIds.Distinct(StringComparer.Ordinal).Count() != r.RequiredToolIds.Count
                || r.RequiredToolIds.Any(id => this.tools.All(t => t.ToolId != id))
                || r.OptionalToolIds is { } optional && (optional.Distinct(StringComparer.Ordinal).Count() != optional.Count
                    || optional.Any(id => this.tools.All(t => t.ToolId != id) || r.RequiredToolIds.Contains(id, StringComparer.Ordinal)))))
            throw new ArgumentException("Unique complete typed tool/effect/result registrations are required.");
    }

    public OperatorWorkEffect? Effect(string toolId, string source, string partition)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(partition)) return null;
        var adapter = tools.SingleOrDefault(t => t.ToolId == toolId);
        return adapter is null ? null : new(toolId, adapter.EffectId,
            adapter.TargetKind == OperatorWorkTargetKind.Source ? source : partition, adapter.SideEffect.ToString());
    }
    public bool Supports(OperatorWorkContract work, string partition)
    {
        var result = results.SingleOrDefault(r => r.Kind == work.RequiredLocalResult);
        return result is not null && work.AllowedEffects.Count > 0
            && work.AllowedEffects.All(e => e == Effect(e.ToolId, work.SourceId, partition)
                && (result.RequiredToolIds.Contains(e.ToolId, StringComparer.Ordinal) || result.OptionalToolIds?.Contains(e.ToolId, StringComparer.Ordinal) == true))
            && result.RequiredToolIds.All(id => work.AllowedEffects.Contains(Effect(id, work.SourceId, partition)!));
    }
    public bool ValidInput(string toolId, object input)
        => tools.SingleOrDefault(t => t.ToolId == toolId) is { } adapter && input is not null && input.GetType() == adapter.InputType;
    public bool RegisteredReceipt(string toolId, string sideEffect)
        => tools.Any(t => t.ToolId == toolId && t.SideEffect.ToString() == sideEffect);
    public IReadOnlyList<string> RegisteredToolIds(ToolSideEffect sideEffect)
        => tools.Where(t => t.SideEffect == sideEffect).Select(t => t.ToolId).ToArray();
    public IReadOnlyList<OperatorWorkResultTool> FirstTools => results.Select(r => new OperatorWorkResultTool(r.Kind, r.FirstToolId)).ToArray();
}
