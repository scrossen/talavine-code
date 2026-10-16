using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Business;

/// <summary>Pure artifact continuity check. It grants no execution, retry, or completion authority.</summary>
public static class OperatorWorkArtifactRefreshPolicy
{
    public const string Version = "operator-work-exact-module-refresh-v1";

    public static bool Matches(JsonElement original, JsonElement current,
        IReadOnlyDictionary<Guid, Guid> verifiedModuleMap)
    {
        try
        {
            if (!Unique(original) || !Unique(current) || verifiedModuleMap.Count is < 1 or > 8
                || verifiedModuleMap.Any(pair => pair.Key == Guid.Empty || pair.Value == Guid.Empty)) return false;
            var adjusted = JsonNode.Parse(original.GetRawText())!.AsObject();
            if (adjusted["schemaVersion"]!.GetValue<int>() != 1 || current.GetProperty("schemaVersion").GetInt32() != 1)
                return false;
            var tools = adjusted["tools"]!.AsArray();
            if (tools.Count is < 1 or > 128) return false;
            foreach (var tool in tools)
            {
                var descriptor = tool!["inputDescriptor"]!.AsObject();
                var module = descriptor["assemblyModuleVersionId"]!.GetValue<Guid>();
                if (!verifiedModuleMap.TryGetValue(module, out var replacement)) return false;
                descriptor["assemblyModuleVersionId"] = JsonValue.Create(replacement);
            }
            // Compare the entire raw structure. No typed deserialization can drop
            // unknown fields; array order, schemas, effects, results and resources remain exact.
            return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(adjusted), current);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or NullReferenceException)
        { return false; }
    }

    public static bool Unique(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == element.EnumerateObject().Count() && element.EnumerateObject().All(p => Unique(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(Unique),
        _ => true,
    };
}
