using System.Text.Json;

namespace SwarmCore.Services;

/// <summary>
/// Recognizes conventional failure envelopes returned with a technically
/// successful transport/runtime result. C# services can return an HTTP body as
/// a string, which the script adapter serializes as a JSON string, so inspect a
/// small number of encoded-string layers as well as a direct JSON object.
/// </summary>
internal static class ServiceResultSemantics
{
    public static string? DetectFailure(string? output)
    {
        var candidate = output?.Trim();
        if (string.IsNullOrEmpty(candidate)) return null;

        for (var depth = 0; depth < 3; depth++)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    candidate = root.GetString()?.Trim();
                    if (string.IsNullOrEmpty(candidate)) return null;
                    continue;
                }

                if (root.ValueKind != JsonValueKind.Object) return null;
                if (TryGetBoolean(root, "success", out var success) && !success)
                    return "service returned success=false";
                if (TryGetBoolean(root, "ok", out var ok) && !ok)
                    return "service returned ok=false";
                if (TryGetInt32(root, "statusCode", out var statusCode) && statusCode >= 400)
                    return $"service returned statusCode={statusCode}";
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryGetBoolean(JsonElement root, string name, out bool value)
    {
        value = default;
        if (!TryGetProperty(root, name, out var property)) return false;
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && bool.TryParse(property.GetString(), out value);
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        value = default;
        if (!TryGetProperty(root, name, out var property)) return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt32(out value)
            : property.ValueKind == JsonValueKind.String
              && int.TryParse(property.GetString(), out value);
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}
