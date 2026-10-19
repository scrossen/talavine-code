using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

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
        => ParseCatalog(output, protocol, prefix).Models.ToList();

    internal static LlmModelCatalog ParseCatalog(string output, SubscriptionCliCatalogProtocol protocol, string prefix)
    {
        var models = new List<string>();
        var details = new List<LlmCatalogModel>();
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
                    && value.GetString() is { Length: > 0 } name)
                {
                    var id = prefix + ":" + name;
                    models.Add(id);
                    if (protocol == SubscriptionCliCatalogProtocol.CodexAppServer
                        && entry.TryGetProperty("supportedReasoningEfforts", out var efforts))
                    {
                        // Only expose levels the installed CLI advertises AND our execution adapter translates.
                        var supported = ProviderPacks.ProviderPackCatalog.GetRequired(SubscriptionCliCatalog.CodexCliProvider).SubscriptionCli!.ReasoningEfforts;
                        var levels = efforts.EnumerateArray().Select(e => e.GetProperty("reasoningEffort").GetString())
                            .OfType<string>().Where(e => supported.Contains(e, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
                        details.Add(new(id, null, true, null, null, null) { ReasoningEfforts = levels });
                    }
                }
            }
        }
        return new(models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "Installed CLI catalog")
        { Details = details.DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseReasoningEfforts(string output,
        SubscriptionCliCatalogProtocol protocol, string prefix, IReadOnlyList<string> executableEfforts)
    {
        var choices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            JsonElement entries;
            if (protocol == SubscriptionCliCatalogProtocol.CodexAppServer)
            {
                if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out entries)) continue;
            }
            else if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("response", out var result)
                     || !result.TryGetProperty("models", out entries)) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                if (protocol == SubscriptionCliCatalogProtocol.ClaudeInitialize)
                {
                    if (entry.TryGetProperty("value", out var id) && id.GetString() is { Length: > 0 } modelId
                        && entry.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                        choices[prefix + ":" + modelId] = levels.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!).Where(e => executableEfforts.Contains(e, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
                    continue;
                }
                if (!entry.TryGetProperty("model", out var model) || model.GetString() is not { Length: > 0 } name
                    || !entry.TryGetProperty("supportedReasoningEfforts", out var efforts) || efforts.ValueKind != JsonValueKind.Array) continue;
                choices[prefix + ":" + name] = efforts.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("reasoningEffort", out var effort) && effort.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetProperty("reasoningEffort").GetString()!)
                    .Where(e => executableEfforts.Contains(e, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToArray();
            }
        }
        return choices;
    }
}
