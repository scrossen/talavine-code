using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.ToolUse;

internal static class ResponsesConfigurationUpdateValidation
{
    internal static void Validate(JsonArray input, string model, string transport,
        IReadOnlyDictionary<string, object>? settings = null)
    {
        for (var i = 0; i < input.Count; i++)
        {
            if ((string?)input[i]?["type"] != "configuration_update") continue;
            var effort = (string?)input[i]?["reasoning"]?["effort"] ?? "";
            bool Enabled(string key) => settings?.TryGetValue(key, out var value) == true
                && !string.Equals(value?.ToString(), "false", StringComparison.OrdinalIgnoreCase)
                && value is not null;
            var capability = ResponsesConfigurationUpdateCapability.Evaluate("OpenAI", model, effort, transport,
                multiAgent: Enabled("multi_agent"), pro: Enabled("pro") || settings?.GetValueOrDefault("mode")?.ToString() == "pro",
                background: Enabled("background"), automaticCompaction: Enabled("context_management"),
                automaticTruncation: Enabled("truncation") && settings?.GetValueOrDefault("truncation")?.ToString() != "disabled");
            if (!capability.Supported) throw new NotSupportedException(capability.Reason);
            if (i + 1 >= input.Count || (string?)input[i + 1]?["role"] != "user")
                throw new InvalidDataException("A configuration update must immediately precede the next real user input; adjacent updates and tool-only continuations require boundary reconstruction.");
        }
    }
}
