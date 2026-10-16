using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using SwarmCore.Mcp.Models;

namespace SwarmCore.Mcp.Core;

/// <summary>Snapshots runtime credentials, including opaque values that pattern detectors cannot recognize.</summary>
internal sealed class McpCredentialRedactor
{
    private readonly string[] _known;
    public McpCredentialRedactor(IEnumerable<string?> secrets)
        => _known = secrets.Where(s => !string.IsNullOrEmpty(s)).SelectMany(s => new[]
            { s!, Uri.EscapeDataString(s!), Convert.ToBase64String(Encoding.UTF8.GetBytes(s!)) })
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();

    public static McpCredentialRedactor FromOptions(McpConnectorOptions options)
    {
        var known = new List<string?> { options.ApiKey?.Trim() };
        foreach (var (name, value) in options.Headers ?? [])
        {
            known.Add(value);
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.IndexOf(' ') is var space && space >= 0)
                known.Add(value[(space + 1)..].Trim());
        }
        return new(known);
    }

    public string Redact(string text)
    {
        foreach (var value in _known) text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }

    public Exception SafeException(Exception error)
        // Preserve exception types for normal lifecycle behavior, but never retain a credential-bearing inner exception.
        => Redact(error.ToString()) == error.ToString() ? error : new InvalidOperationException(Redact(error.Message));

    public T Sanitize<T>(T result)
    {
        if (_known.Length == 0) return result;
        JsonNode? Visit(JsonNode? current)
        {
            if (current is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return JsonValue.Create(Redact(text));
            if (current is JsonArray array) return new JsonArray(array.Select(Visit).ToArray());
            if (current is JsonObject obj) return new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(Redact(pair.Key), Visit(pair.Value))));
            return current?.DeepClone();
        }
        return Visit(JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions))!.Deserialize<T>(McpJsonUtilities.DefaultOptions)!;
    }
}
