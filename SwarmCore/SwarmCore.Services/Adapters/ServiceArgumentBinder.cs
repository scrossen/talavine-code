using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Binds catalog field/value arguments to a host's typed input contract.
/// Catalog chat tools carry values as text, including JSON for arrays/objects.
/// Decode only fields whose declared type is not string; a string that looks
/// like JSON must retain its exact meaning (identifiers and filter text too).
/// </summary>
public static class ServiceArgumentBinder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static object? Deserialize(IReadOnlyDictionary<string, object?> arguments, Type inputType)
    {
        var contract = Options.GetTypeInfo(inputType);
        var properties = contract.Properties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var input = new JsonObject();
        foreach (var (name, value) in arguments)
        {
            var node = JsonSerializer.SerializeToNode(value, Options);
            if (properties.TryGetValue(name, out var property)
                && property.PropertyType != typeof(string)
                && node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                try { node = JsonNode.Parse(text); }
                catch (JsonException) { /* Let typed deserialization validate the original text. */ }
            }
            input[name] = node;
        }
        return input.Deserialize(inputType, Options);
    }
}
