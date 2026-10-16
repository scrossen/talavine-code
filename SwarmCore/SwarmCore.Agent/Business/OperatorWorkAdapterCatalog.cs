using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.AgentWork;

namespace SwarmCore.Agent.Business;

public sealed record OperatorWorkInputDescriptor(string TypeName,string SchemaJson,Guid AssemblyModuleVersionId);
public sealed record OperatorWorkToolDescriptor(string ToolId, string EffectId, string SideEffect, string TargetKind,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OperatorWorkInputDescriptor? InputDescriptor=null);
public sealed record OperatorWorkResultDescriptor(string Kind, string FirstToolId, IReadOnlyList<string> RequiredToolIds,
    IReadOnlyList<string> OptionalToolIds, string CanonicalProofKind,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OperatorWorkResourceProjection? ResourceProjection=null);

/// <summary>Serializable host registrations. A catalog describes bounded intent; it grants no execution or effect authority.</summary>
public sealed record OperatorWorkAdapterCatalog(int SchemaVersion, IReadOnlyList<OperatorWorkToolDescriptor> Tools,
    IReadOnlyList<OperatorWorkResultDescriptor> Results)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    [System.Text.Json.Serialization.JsonIgnore]
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));
    [System.Text.Json.Serialization.JsonIgnore]
    public string CanonicalJson => JsonSerializer.Serialize(new
    {
        SchemaVersion,
        Tools = Tools.OrderBy(t => t.ToolId, StringComparer.Ordinal).ToArray(),
        Results = Results.OrderBy(r => r.Kind, StringComparer.Ordinal).Select(r => r with
        {
            RequiredToolIds = r.RequiredToolIds.Order(StringComparer.Ordinal).ToArray(),
            OptionalToolIds = r.OptionalToolIds.Order(StringComparer.Ordinal).ToArray(),
        }).ToArray(),
    }, WebJson);

    public OperatorWorkResultDescriptor? Resolve(OperatorWorkContract work, string partition)
    {
        if (SchemaVersion != 1 || Tools.Count is < 1 or > 128 || Results.Count is < 1 or > 128
            || Tools.Select(t => t.ToolId).Distinct(StringComparer.Ordinal).Count() != Tools.Count
            || Results.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Count() != Results.Count) return null;
        var result = Results.SingleOrDefault(r => r.Kind == work.RequiredLocalResult);
        if (result is null || string.IsNullOrWhiteSpace(result.CanonicalProofKind)
            || !result.RequiredToolIds.Contains(result.FirstToolId, StringComparer.Ordinal)
            || result.RequiredToolIds.Count == 0 || result.RequiredToolIds.Distinct().Count() != result.RequiredToolIds.Count
            || result.OptionalToolIds.Distinct().Count() != result.OptionalToolIds.Count
            || result.RequiredToolIds.Intersect(result.OptionalToolIds).Any()) return null;
        OperatorWorkEffect? Effect(string id)
        {
            var t = Tools.SingleOrDefault(t => t.ToolId == id);
            return t?.TargetKind switch
            {
                "Source" => new(id, t.EffectId, work.SourceId, t.SideEffect),
                "Partition" => new(id, t.EffectId, partition, t.SideEffect),
                _ => null,
            };
        }
        return work.AllowedEffects.Count > 0
            && work.AllowedEffects.All(e => e == Effect(e.ToolId)
                && (result.RequiredToolIds.Contains(e.ToolId) || result.OptionalToolIds.Contains(e.ToolId))
                && e.SideEffect is "ReadOnly" or "WritesLocal")
            && result.RequiredToolIds.All(id => Effect(id) is { } e && work.AllowedEffects.Contains(e)) ? result : null;
    }
}

public sealed partial class OperatorWorkAdapterRegistry
{
    public OperatorWorkAdapterCatalog Describe(IReadOnlyDictionary<string, string> proofKinds)
    {
        if (proofKinds.Count != results.Length || results.Any(r => !proofKinds.TryGetValue(r.Kind, out var kind) || string.IsNullOrWhiteSpace(kind)))
            throw new ArgumentException("Every registered result requires its exact canonical proof adapter.");
        return new(1, tools.Select(t => new OperatorWorkToolDescriptor(t.ToolId, t.EffectId, t.SideEffect.ToString(), t.TargetKind.ToString(),
            new(t.InputType.AssemblyQualifiedName!,SwarmCore.Agent.Catalog.ToolSchemaGenerator.Generate(t.InputType).ToJsonString(),t.InputType.Module.ModuleVersionId))).ToArray(),
            results.Select(r => new OperatorWorkResultDescriptor(r.Kind, r.FirstToolId, r.RequiredToolIds.ToArray(), r.OptionalToolIds?.ToArray() ?? [], proofKinds[r.Kind])).ToArray());
    }
}
