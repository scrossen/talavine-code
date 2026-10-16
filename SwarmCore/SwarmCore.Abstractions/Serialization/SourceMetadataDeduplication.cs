using System.Text;
using System.Text.Json;

namespace SwarmCore.Abstractions.Serialization;

/// <summary>Removes only exact copies of values retained by an authoritative source.</summary>
public static class SourceMetadataDeduplication
{
    public static string? RemoveMatchingValues(string? metadataJson, IReadOnlyDictionary<string, string> retainedValues)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || retainedValues.Count == 0) return metadataJson;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return metadataJson;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var redundant = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Ambiguous metadata cannot prove which source value was intended.
                if (!names.Add(property.Name)) return metadataJson;
                if (property.Value.ValueKind == JsonValueKind.String
                    && retainedValues.TryGetValue(property.Name, out var retained)
                    && !string.IsNullOrEmpty(retained)
                    && string.Equals(property.Value.GetString(), retained, StringComparison.Ordinal))
                    redundant.Add(property.Name);
            }
            if (redundant.Count == 0) return metadataJson;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!redundant.Contains(property.Name)) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException) { return metadataJson; }
    }
}
