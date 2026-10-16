using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Produces bounded, safe evidence for durable agent-action receipts. Hosts own persistence and
/// authorization; this shared helper prevents tool input/output evidence from retaining secrets.
/// </summary>
public static partial class AgentActionReceiptSanitizer
{
    public const string Redacted = "(redacted)";

    private const int DefaultMaximumCharacters = 8_000;

    /// <summary>Sanitizes a tool input object without mutating the live tool-call payload.</summary>
    public static string SanitizeInput(JsonObject? input, int maximumCharacters = DefaultMaximumCharacters)
        => Limit(SanitizeNode(input)?.ToJsonString() ?? "{}", maximumCharacters);

    /// <summary>
    /// Sanitizes a serialized tool result. JSON results retain safe structure; plain text receives
    /// secret-pattern masking too, because catalog tools may return arbitrary text.
    /// </summary>
    public static string SanitizeResult(string? result, int maximumCharacters = DefaultMaximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(result)) return string.Empty;

        try
        {
            var node = JsonNode.Parse(result);
            if (node is not null)
                return Limit(SanitizeNode(node)?.ToJsonString() ?? string.Empty, maximumCharacters);
        }
        catch (JsonException)
        {
            // Tool output is allowed to be plain text. Apply the conservative text masks below.
        }

        return Limit(MaskInlineSecrets(result), maximumCharacters);
    }

    /// <summary>Returns a compact summary suitable for an activity timeline.</summary>
    public static string Summarize(string? safeEvidence, int maximumCharacters = 1_000)
    {
        if (string.IsNullOrWhiteSpace(safeEvidence)) return string.Empty;
        var normalized = Regex.Replace(safeEvidence, "\\s+", " ").Trim();
        return Limit(normalized, maximumCharacters);
    }

    private static JsonNode? SanitizeNode(JsonNode? node, string? propertyName = null)
    {
        if (node is null) return null;
        if (propertyName is not null && IsSensitiveName(propertyName))
            return JsonValue.Create(Redacted);

        return node switch
        {
            JsonObject source => SanitizeObject(source),
            JsonArray source => SanitizeArray(source),
            JsonValue source => SanitizeValue(source),
            _ => null,
        };
    }

    private static JsonObject SanitizeObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var property in source)
            result[property.Key] = SanitizeNode(property.Value, property.Key);
        return result;
    }

    private static JsonArray SanitizeArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
            result.Add(SanitizeNode(item));
        return result;
    }

    private static JsonNode? SanitizeValue(JsonValue source)
    {
        if (source.TryGetValue<string>(out var text))
            return JsonValue.Create(MaskInlineSecrets(text));
        return source.DeepClone();
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized is "bearer" or "cookie";
    }

    private static string MaskInlineSecrets(string text)
    {
        var masked = InlineAssignmentRegex().Replace(text, "${1}" + Redacted);
        return BearerTokenRegex().Replace(masked, "Bearer " + Redacted);
    }

    private static string Limit(string value, int maximumCharacters)
    {
        if (maximumCharacters <= 0) return string.Empty;
        return value.Length <= maximumCharacters
            ? value
            : value[..Math.Max(0, maximumCharacters - 15)] + "… (truncated)";
    }

    [GeneratedRegex("(?i)\\b(password|secret|token|api[_-]?key|credential|authorization)\\s*[:=]\\s*([^\\s,;]+)")]
    private static partial Regex InlineAssignmentRegex();

    [GeneratedRegex("(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerTokenRegex();
}
