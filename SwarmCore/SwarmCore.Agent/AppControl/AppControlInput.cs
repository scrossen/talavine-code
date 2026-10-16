using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.AppControl;

/// <summary>Small shared conversions for app-control hosts and clients.</summary>
public static class AppControlInput
{
    /// <summary>A stable id from text (profile path, tenant and person), so receipts and journals group per identity.</summary>
    public static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);

    /// <summary>
    /// A tool input as a JSON object. Agents send the object itself, or sometimes the object serialized as a string;
    /// anything else is an empty input (the tool's own validation then says what is missing).
    /// </summary>
    public static JsonObject ParseObject(JsonElement input) => input.ValueKind switch
    {
        JsonValueKind.Object => JsonNode.Parse(input.GetRawText())!.AsObject(),
        JsonValueKind.String when TryParseObject(input.GetString()) is { } fromText => fromText,
        _ => new JsonObject(),
    };

    public static JsonObject ParseObject(JsonNode? input) => input switch
    {
        JsonObject obj => obj,
        JsonValue value when value.TryGetValue<string>(out var text) && TryParseObject(text) is { } fromText => fromText,
        _ => new JsonObject(),
    };

    private static JsonObject? TryParseObject(string? text)
    {
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
