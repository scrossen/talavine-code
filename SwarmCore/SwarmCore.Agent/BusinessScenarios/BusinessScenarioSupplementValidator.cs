using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Agent.BusinessScenarios;

/// <summary>
/// Operator-supplied world inputs registered after a scenario run is initialized.
/// This type deliberately has no business/profile override or agent-output surface.
/// </summary>
public sealed record BusinessScenarioSupplement(
    int SchemaVersion,
    string ScenarioId,
    IReadOnlyList<BusinessScenarioEvent> InjectedEvents);

public static class BusinessScenarioSupplementValidator
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<BusinessScenarioEventKind>() },
    };

    public static BusinessScenarioSupplement LoadAndValidate(
        string json,
        BusinessScenarioDocument baseScenario,
        BusinessScenarioDocument? currentScenario = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(baseScenario);
        var supplement = JsonSerializer.Deserialize<BusinessScenarioSupplement>(json, SerializerOptions)
            ?? throw new JsonException("The business scenario supplement is empty.");
        ValidateAgainst(baseScenario, currentScenario ?? baseScenario, supplement);
        return Clone(supplement);
    }

    public static BusinessScenarioDocument ValidateAndMerge(
        BusinessScenarioDocument baseScenario,
        BusinessScenarioSupplement supplement) => ValidateAgainst(baseScenario, baseScenario, supplement);

    public static BusinessScenarioDocument ValidateAgainst(
        BusinessScenarioDocument baseScenario,
        BusinessScenarioDocument currentScenario,
        BusinessScenarioSupplement supplement)
    {
        ArgumentNullException.ThrowIfNull(baseScenario);
        ArgumentNullException.ThrowIfNull(currentScenario);
        ArgumentNullException.ThrowIfNull(supplement);
        if (supplement.SchemaVersion != CurrentSchemaVersion)
            throw new BusinessScenarioValidationException([$"supplement schemaVersion must be {CurrentSchemaVersion}."]);
        if (!string.Equals(baseScenario.ScenarioId, supplement.ScenarioId, StringComparison.Ordinal))
            throw new BusinessScenarioValidationException(["supplement scenarioId must match the registered base scenario."]);
        if (supplement.InjectedEvents is not { Count: > 0 })
            throw new BusinessScenarioValidationException(["supplement injectedEvents must contain at least one event."]);

        var copied = supplement.InjectedEvents.Select(CloneEvent).ToArray();
        if (copied.Any(item => item.Kind != BusinessScenarioEventKind.Injected))
            throw new BusinessScenarioValidationException(["supplement events must all have kind 'Injected'."]);
        if (copied.Any(item => item.DeliveryEvidence is not null))
            throw new BusinessScenarioValidationException(["supplement events cannot contain agent delivery evidence."]);
        if (copied.GroupBy(item => item.EventId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new BusinessScenarioValidationException(["supplement event IDs must be unique, even when duplicate content is identical."]);

        if (currentScenario.ScenarioId != baseScenario.ScenarioId)
            throw new BusinessScenarioValidationException(["current scenario context must match the base scenario."]);
        var baseIds = (baseScenario.SeedEvents ?? []).Concat(baseScenario.InjectedEvents ?? [])
            .Concat(baseScenario.AgentOutputEvents ?? []).Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
        if (copied.Any(item => baseIds.Contains(item.EventId)))
            throw new BusinessScenarioValidationException(["supplement event IDs cannot replace or repeat base scenario events."]);

        var currentById = (currentScenario.SeedEvents ?? []).Concat(currentScenario.InjectedEvents ?? [])
            .Concat(currentScenario.AgentOutputEvents ?? []).ToDictionary(item => item.EventId, StringComparer.Ordinal);
        var fresh = new List<BusinessScenarioEvent>();
        foreach (var item in copied)
        {
            if (!currentById.TryGetValue(item.EventId, out var existing))
            {
                fresh.Add(item);
                continue;
            }
            if (!string.Equals(CanonicalEventHash(existing), CanonicalEventHash(item), StringComparison.Ordinal))
                throw new BusinessScenarioValidationException([$"supplement event '{item.EventId}' conflicts with a registered event."]);
        }

        // The normal scenario validator is the authority for event IDs, provenance,
        // timestamps, dependency ordering/cycles, evidence claims and merged references.
        var merged = currentScenario with
        {
            InjectedEvents = (currentScenario.InjectedEvents ?? []).Concat(fresh).ToArray(),
        };
        BusinessScenarioValidator.Validate(merged);
        return merged;
    }

    public static BusinessScenarioSupplement Clone(BusinessScenarioSupplement supplement) =>
        supplement with
        {
            InjectedEvents = (supplement.InjectedEvents ?? []).Select(CloneEvent).ToArray(),
        };

    public static BusinessScenarioEvent CloneEvent(BusinessScenarioEvent item) =>
        item with
        {
            DependsOn = item.DependsOn is null ? null! : item.DependsOn.ToArray(),
            Evidence = item.Evidence.ValueKind == JsonValueKind.Undefined ? default : item.Evidence.Clone(),
        };

    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string CanonicalEventHash(BusinessScenarioEvent item) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalJsonBytes(JsonSerializer.SerializeToElement(CloneEvent(item), SerializerOptions))));

    public static string CanonicalSupplementHash(BusinessScenarioSupplement supplement) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalJsonBytes(JsonSerializer.SerializeToElement(Clone(supplement), SerializerOptions))));

    public static string SerializeSnapshot(BusinessScenarioSupplement supplement) =>
        JsonSerializer.Serialize(Clone(supplement), SerializerOptions);

    private static byte[] CanonicalJsonBytes(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
