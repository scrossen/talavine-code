using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Coding.Native;

/// <summary>
/// Same-process bridge evidence for one private native launch. Neither provider output nor
/// model-authored JSON can create an entry. Missing, ambiguous and retired matches stay unknown.
/// This is transient transport correlation, never a durable recovery proof or replay permission.
/// </summary>
internal static class NativeHostReceiptJournal
{
    internal sealed record Evidence(bool WasDispatched, bool LocalProcessCompleted, ToolSideEffect? SideEffect);
    private sealed class Attempt
    {
        public bool Enabled = true;
        public readonly Dictionary<string, List<Evidence>> Entries = new(StringComparer.Ordinal);
        public int Count;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Attempt> Attempts = new(StringComparer.Ordinal);

    public static IDisposable Begin(string conversationId, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return new Scope(() => { });
        var key = AttemptKey(conversationId, capability);
        lock (Gate)
        {
            // Concurrent ownership of one capability is ambiguous. Do not replace its evidence.
            if (Attempts.TryGetValue(key, out var prior))
            {
                prior.Enabled = false;
                prior.Entries.Clear();
                return new Scope(() => { });
            }
            if (Attempts.Count >= 256) return new Scope(() => { });
            var attempt = new Attempt();
            Attempts.Add(key, attempt);
            return new Scope(() => { lock (Gate) if (Attempts.GetValueOrDefault(key) == attempt) Attempts.Remove(key); });
        }
    }

    public static void Record(string conversationId, string? capability, string tool, JsonElement? input, HostToolResult result)
    {
        if (string.IsNullOrWhiteSpace(capability)) return;
        var attemptKey = AttemptKey(conversationId, capability);
        lock (Gate)
            if (!Attempts.TryGetValue(attemptKey, out var active) || !active.Enabled) return;
        var key = MatchKey(tool, input is { ValueKind: JsonValueKind.Object } value ? value : JsonSerializer.SerializeToElement(new { }),
            ResultFingerprint(result));
        if (key is null) return;
        lock (Gate)
        {
            if (!Attempts.TryGetValue(attemptKey, out var attempt) || !attempt.Enabled) return;
            if (++attempt.Count > 2048)
            {
                attempt.Enabled = false;
                attempt.Entries.Clear();
                return;
            }
            if (!attempt.Entries.TryGetValue(key, out var entries)) attempt.Entries.Add(key, entries = []);
            entries.Add(new(result.WasDispatched, result.LocalProcessCompleted, result.SideEffect));
        }
    }

    public static bool TryTake(string? conversationId, string? capability, string tool, JsonObject input,
        JsonObject result, out Evidence? evidence, bool? outcomeIsError = null)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(capability)
            || !TryResultFingerprint(result, outcomeIsError, out var resultHash)) return false;
        var key = MatchKey(tool, JsonSerializer.SerializeToElement(input), resultHash);
        if (key is null) return false;
        lock (Gate)
        {
            if (!Attempts.TryGetValue(AttemptKey(conversationId, capability), out var attempt) || !attempt.Enabled
                || !attempt.Entries.TryGetValue(key, out var entries) || entries.Count != 1) return false;
            evidence = entries[0];
            attempt.Entries.Remove(key);
            return true;
        }
    }

    private static bool TryResultFingerprint(JsonObject result, bool? outcomeIsError, out string fingerprint)
    {
        fingerprint = "";
        if (result["content"] is not JsonArray { Count: > 0 } content
            || content[0] is not JsonObject text || !String(text, "type", out var type) || type != "text"
            || !String(text, "text", out var value)) return false;
        var images = new List<HostToolImage>();
        foreach (var node in content.Skip(1))
        {
            if (node is not JsonObject image || !String(image, "type", out type) || type != "image"
                || !String(image, "data", out var data) || !String(image, "mimeType", out var mime)) return false;
            images.Add(new(data, mime));
        }
        // App-server DTOs can omit MCP's error flag. Only the already-computed
        // item outcome may supply that missing bit; text never determines it.
        var isError = outcomeIsError ?? false;
        if (result.TryGetPropertyValue("isError", out var flag))
        {
            if (flag is not JsonValue error || !error.TryGetValue<bool>(out isError)
                || outcomeIsError is { } outcome && isError != outcome) return false;
        }
        fingerprint = ResultFingerprint(new(value, images, isError));
        return true;
    }

    private static bool String(JsonObject obj, string key, out string value)
    {
        value = "";
        return obj[key] is JsonValue node && node.TryGetValue(out value!);
    }

    private static string ResultFingerprint(HostToolResult result)
        => Hash(JsonSerializer.Serialize(new { result.Text, result.Images, result.IsError }));

    private static string AttemptKey(string conversationId, string capability) => Hash(conversationId + "\n" + capability);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? MatchKey(string tool, JsonElement input, string resultHash)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            if (!WriteCanonical(writer, input)) return null;
        return Hash(tool + "\n" + Convert.ToHexString(SHA256.HashData(buffer.ToArray())) + "\n" + resultHash);
    }

    private static bool WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.DistinctBy(p => p.Name, StringComparer.Ordinal).Count() != properties.Length) return false;
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (!WriteCanonical(writer, property.Value)) return false;
                }
                writer.WriteEndObject();
                return true;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) if (!WriteCanonical(writer, item)) return false;
                writer.WriteEndArray();
                return true;
            default:
                value.WriteTo(writer);
                return true;
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
