using System.Text;
using System.Text.Json;

namespace SwarmCore.LlmRouting.Providers;

public enum SubscriptionCliCatalogProtocol { CodexAppServer, ClaudeInitialize }

/// <summary>Read-only CLI control exchanges. Never submits a user turn or launches tools.</summary>
internal static class SubscriptionCliModelDiscovery
{
    internal static async Task<SubscriptionCliBoundedTextCapture> ExchangeAsync(
        StreamReader output, StreamWriter input, SubscriptionCliCatalogProtocol protocol, int limit, CancellationToken ct)
    {
        var retained = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        var nextId = 2;
        await SendAsync(protocol == SubscriptionCliCatalogProtocol.CodexAppServer
            ? new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "swarmcode_catalog", version = "1.0" } } }
            : (object)new { type = "control_request", request_id = "catalog", request = new { subtype = "initialize", hooks = new { }, sdkMcpServers = Array.Empty<object>() } });
        while (true)
        {
            var count = await output.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (retained.Length + count > limit) throw new InvalidDataException("The CLI catalog response exceeds the process output buffer.");
            retained.Append(buffer, 0, count);
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                var text = line.ToString().Trim(); line.Clear();
                if (text.Length == 0) continue;
                using var doc = JsonDocument.Parse(text);
                var message = doc.RootElement;
                if (protocol == SubscriptionCliCatalogProtocol.ClaudeInitialize)
                {
                    if (message.TryGetProperty("type", out var type) && type.GetString() == "control_response") input.Close();
                    continue;
                }
                if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                if (message.TryGetProperty("error", out _)) { input.Close(); continue; }
                if (id.GetInt32() == 1)
                {
                    await SendAsync(new { method = "initialized" });
                    await SendAsync(new { id = nextId++, method = "model/list", @params = new { } });
                }
                else if (message.TryGetProperty("result", out var result))
                {
                    if (result.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cursor.GetString()))
                        await SendAsync(new { id = nextId++, method = "model/list", @params = new { cursor = cursor.GetString() } });
                    else input.Close();
                }
            }
        }
        return new(retained.ToString(), false);

        async Task SendAsync(object message)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    internal static List<string> Parse(string output, SubscriptionCliCatalogProtocol protocol, string prefix)
    {
        var models = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var message = doc.RootElement;
            JsonElement entries;
            if (protocol == SubscriptionCliCatalogProtocol.CodexAppServer)
            {
                if (!message.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out entries)) continue;
            }
            else
            {
                if (!message.TryGetProperty("response", out var response) || !response.TryGetProperty("response", out var result) || !result.TryGetProperty("models", out entries)) continue;
            }
            foreach (var entry in entries.EnumerateArray())
            {
                // The CLI owns the ids and aliases. Prefix is a routing namespace, never a model name.
                if (entry.TryGetProperty(protocol == SubscriptionCliCatalogProtocol.CodexAppServer ? "model" : "value", out var value)
                    && value.GetString() is { Length: > 0 } name) models.Add(prefix + ":" + name);
            }
        }
        return models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
