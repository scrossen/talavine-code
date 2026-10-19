using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Numeric usage from the CLI-owned terminal JSONL event. Never infer usage from answer text.</summary>
internal static class CodexCliReportedUsage
{
    internal static ChatUsage? Read(string? jsonl)
    {
        if (string.IsNullOrWhiteSpace(jsonl)) return null;
        ChatUsage? found = null;
        using var reader = new StringReader(jsonl);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String || type.GetString() != "turn.completed") continue;
                if (found is not null || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
                    || !Count(usage, "input_tokens", out var input) || !Count(usage, "output_tokens", out var output)
                    || (long)input + output > int.MaxValue) return null;
                var cached = 0;
                if (usage.TryGetProperty("cached_input_tokens", out _) && !Count(usage, "cached_input_tokens", out cached)) return null;
                if (cached > input) return null;
                found = new(input, output) { CachedInputTokens = cached };
            }
            catch (JsonException) { return null; } // An incomplete capture is not complete accounting.
        }
        return found;
    }

    private static bool Count(JsonElement usage, string name, out int count)
    {
        count = 0;
        return usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out count) && count >= 0;
    }
}
