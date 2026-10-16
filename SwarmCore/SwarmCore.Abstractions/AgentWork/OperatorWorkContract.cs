using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Abstractions.AgentWork;

/// <summary>A host-reviewed effect identity. This is a task limit, not a capability grant.</summary>
public sealed record OperatorWorkEffect(string ToolId, string EffectId, string TargetId, string SideEffect);

/// <summary>
/// Frozen operator intent. Only an authenticated host may create or accept this contract;
/// neither source evidence nor model output may supply authorization.
/// </summary>
public sealed class OperatorWorkContract
{
    public const int CurrentVersion = 2;
    private static readonly HashSet<string> SideEffects = new(StringComparer.Ordinal)
        { "ReadOnly", "WritesLocal", "ExternalSend", "Destructive", "Exec" };

    private OperatorWorkContract(string taskId, string objective, JsonElement parameters, string sourceId,
        string requiredLocalResult, IReadOnlyList<OperatorWorkEffect> allowedEffects, string canonicalJson)
    {
        TaskId = taskId;
        Objective = objective;
        Parameters = parameters;
        SourceId = sourceId;
        RequiredLocalResult = requiredLocalResult;
        AllowedEffects = allowedEffects;
        CanonicalJson = canonicalJson;
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    public int Version => CurrentVersion;
    public string TaskId { get; }
    public string Objective { get; }
    public JsonElement Parameters { get; }
    public string SourceId { get; }
    /// <summary>Host-registered postcondition kind; this label is never proof of completion.</summary>
    public string RequiredLocalResult { get; }
    public IReadOnlyList<OperatorWorkEffect> AllowedEffects { get; }
    public string CanonicalJson { get; }
    public string Sha256 { get; }

    public static OperatorWorkContract Create(string taskId, string objective, JsonElement parameters,
        string sourceId, string requiredLocalResult, IReadOnlyList<OperatorWorkEffect> allowedEffects)
    {
        if (!ValidId(taskId, 150) || !ValidId(sourceId, 200)
            || string.IsNullOrWhiteSpace(objective) || objective.Length > 2000 || objective.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(requiredLocalResult) || requiredLocalResult.Length > 200
            || requiredLocalResult.Any(char.IsControl) || parameters.ValueKind != JsonValueKind.Object
            || allowedEffects is null || allowedEffects.Count > 32)
            throw new ArgumentException("Operator work requires bounded task, source, objective, local result and effects.");

        var normalizedParameters = Normalize(parameters, 0);
        if (Encoding.UTF8.GetByteCount(normalizedParameters.GetRawText()) > 8192)
            throw new ArgumentException("Operator work parameters exceed the byte limit.", nameof(parameters));
        var effects = allowedEffects.ToArray();
        if (effects.Any(e => e is null || !ValidId(e.ToolId, 200) || !ValidId(e.EffectId, 100)
                || !ValidId(e.TargetId, 200) || !SideEffects.Contains(e.SideEffect)))
            throw new ArgumentException("Operator work contains an unknown or unbounded effect.", nameof(allowedEffects));
        effects = effects.OrderBy(e => e.ToolId, StringComparer.Ordinal)
            .ThenBy(e => e.EffectId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetId, StringComparer.Ordinal)
            .ThenBy(e => e.SideEffect, StringComparer.Ordinal).ToArray();
        if (effects.Zip(effects.Skip(1)).Any(pair => pair.First.ToolId == pair.Second.ToolId
                && pair.First.EffectId == pair.Second.EffectId
                && pair.First.TargetId == pair.Second.TargetId))
            throw new ArgumentException("Operator work contains a duplicate effect.", nameof(allowedEffects));

        var canonical = JsonSerializer.Serialize(new
        {
            version = CurrentVersion, taskId, objective, parameters = normalizedParameters, sourceId,
            requiredLocalResult,
            allowedEffects = effects.Select(e => new
                { toolId = e.ToolId, effectId = e.EffectId, targetId = e.TargetId, sideEffect = e.SideEffect }).ToArray()
        });
        if (Encoding.UTF8.GetByteCount(canonical) > 16384)
            throw new ArgumentException("Operator work exceeds the byte limit.");
        return new(taskId, objective, normalizedParameters, sourceId, requiredLocalResult,
            Array.AsReadOnly(effects), canonical);
    }

    /// <summary>Strictly parse persisted work; unknown or duplicate fields never acquire meaning.</summary>
    public static OperatorWorkContract Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 16384)
            throw new ArgumentException("Operator work JSON is empty or oversized.", nameof(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireFields(root, "version", "taskId", "objective", "parameters", "sourceId", "requiredLocalResult", "allowedEffects");
        if (root.GetProperty("version").GetInt32() != CurrentVersion)
            throw new ArgumentException("Unknown operator work version.", nameof(json));
        var effectArray = root.GetProperty("allowedEffects");
        if (effectArray.ValueKind != JsonValueKind.Array || effectArray.GetArrayLength() > 32)
            throw new ArgumentException("Invalid operator work effects.", nameof(json));
        var effects = new List<OperatorWorkEffect>();
        foreach (var item in effectArray.EnumerateArray())
        {
            RequireFields(item, "toolId", "effectId", "targetId", "sideEffect");
            effects.Add(new(item.GetProperty("toolId").GetString()!, item.GetProperty("effectId").GetString()!,
                item.GetProperty("targetId").GetString()!, item.GetProperty("sideEffect").GetString()!));
        }
        return Create(root.GetProperty("taskId").GetString()!, root.GetProperty("objective").GetString()!,
            root.GetProperty("parameters"), root.GetProperty("sourceId").GetString()!,
            root.GetProperty("requiredLocalResult").GetString()!, effects);
    }

    private static bool ValidId(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':');

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Operator work must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new ArgumentException("Operator work has an unknown or duplicate field.");
        if (seen.Count != names.Length)
            throw new ArgumentException("Operator work is missing a required field.");
    }

    private static JsonElement Normalize(JsonElement value, int depth)
    {
        if (depth > 12) throw new ArgumentException("Operator work parameters are too deep.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteNormalized(writer, value, depth);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 12) throw new ArgumentException("Operator work parameters are too deep.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var fields = value.EnumerateObject().ToArray();
                if (fields.Length > 64 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                    throw new ArgumentException("Operator work parameters contain duplicate or excess fields.");
                foreach (var field in fields.OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(field.Name);
                    WriteNormalized(writer, field.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                if (value.GetArrayLength() > 64) throw new ArgumentException("Operator work parameter array is too large.");
                foreach (var item in value.EnumerateArray()) WriteNormalized(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                throw new ArgumentException("Undefined operator work parameter.");
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
