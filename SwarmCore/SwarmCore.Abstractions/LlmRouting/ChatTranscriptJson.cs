using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Serializes an ordered <see cref="ChatContentPart"/> list to/from the
/// string stored in <c>AgentTranscriptMessage.ContentPartsJson</c>. Schema
/// mirrors Anthropic's content-block array so a dump of the DB is
/// immediately useful for debugging.
/// </summary>
public static class ChatTranscriptJson
{
    public static string Serialize(IReadOnlyList<ChatContentPart> parts)
    {
        var arr = new JsonArray();
        foreach (var p in parts)
        {
            switch (p)
            {
                case TextPart t:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = t.Text
                    });
                    break;
                case ReasoningPart r:
                    // Persisting reasoning is what makes DeepSeek thinking
                    // mode survive across persisted turns: the OpenAI-style
                    // provider echoes ReasoningPart back as `reasoning_content`
                    // on the next request, which DeepSeek requires. Without
                    // this case the serializer threw "Unknown content part
                    // type" and AgentTurnRunner's persist-catch swallowed it,
                    // so every thinking-mode turn was effectively lost.
                    arr.Add(new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["text"] = r.Text
                    });
                    break;
                case ResponsesReasoningPart r:
                    arr.Add(new JsonObject { ["type"] = "responses_reasoning", ["item"] = r.Item.DeepClone(), ["binding"] = JsonSerializer.SerializeToNode(r.Binding) });
                    break;
                case ResponsesCompactionPart compact:
                    arr.Add(new JsonObject { ["type"] = "responses_compaction", ["items"] = compact.Items.DeepClone(), ["binding"] = JsonSerializer.SerializeToNode(compact.Binding) });
                    break;
                case ResponsesConfigurationUpdatePart update:
                    arr.Add(new JsonObject { ["type"] = "responses_configuration_update", ["effort"] = update.Effort, ["binding"] = JsonSerializer.SerializeToNode(update.Binding) });
                    break;
                case ResponsesOutputPart output:
                    arr.Add(new JsonObject { ["type"] = "responses_output", ["items"] = output.Items.DeepClone(), ["binding"] = JsonSerializer.SerializeToNode(output.Binding) });
                    break;
                case ToolUsePart u:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = u.Id,
                        ["name"] = u.Name,
                        ["input"] = (JsonNode)u.Input.DeepClone(),
                        ["is_native_async"] = u.IsNativeAsync,
                        ["native_computer_call"] = u.NativeComputerCall?.DeepClone()
                    });
                    break;
                case ToolResultPart r:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = r.ToolUseId,
                        ["content"] = r.Content,
                        ["is_error"] = r.IsError
                    });
                    break;
                case ImagePart img:
                    // We persist the bytes so a reloaded conversation still
                    // shows the image the user pasted — otherwise scrolling
                    // through history would have user turns like "what is
                    // this?" with no referent. Image parts are only on user
                    // messages and only when the user explicitly attached
                    // one, so the storage cost is bounded by user behavior.
                    arr.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["media_type"] = img.MediaType,
                        ["data"] = img.Base64Data
                    });
                    break;
                default:
                    throw new InvalidOperationException($"Unknown content part type: {p.GetType().FullName}");
            }
        }
        return arr.ToJsonString();
    }

    public static IReadOnlyList<ChatContentPart> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var node = JsonNode.Parse(json);
        if (node is not JsonArray arr) return [];

        var parts = new List<ChatContentPart>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var type = (string?)obj["type"];
            switch (type)
            {
                case "text":
                    parts.Add(new TextPart((string?)obj["text"] ?? ""));
                    break;
                case "reasoning":
                    parts.Add(new ReasoningPart((string?)obj["text"] ?? ""));
                    break;
                case "responses_reasoning":
                    if (obj["item"] is JsonObject reasoning && (string?)reasoning["type"] == "reasoning")
                        parts.Add(new ResponsesReasoningPart((JsonObject)reasoning.DeepClone()) { Binding = obj["binding"]?.Deserialize<ProviderContextBinding>() });
                    break;
                case "responses_compaction":
                    if (obj["items"] is JsonArray compact && obj["binding"]?.Deserialize<ProviderContextBinding>() is { } binding)
                        parts.Add(new ResponsesCompactionPart((JsonArray)compact.DeepClone(), binding));
                    break;
                case "responses_configuration_update":
                    if ((string?)obj["effort"] is { } effort && obj["binding"]?.Deserialize<ProviderContextBinding>() is { } updateBinding)
                        parts.Add(new ResponsesConfigurationUpdatePart(effort, updateBinding));
                    break;
                case "responses_output":
                    if (obj["items"] is JsonArray output && obj["binding"]?.Deserialize<ProviderContextBinding>() is { } outputBinding)
                        parts.Add(new ResponsesOutputPart((JsonArray)output.DeepClone(), outputBinding));
                    break;
                case "tool_use":
                    var input = obj["input"] as JsonObject ?? new JsonObject();
                    parts.Add(new ToolUsePart(
                        Id: (string?)obj["id"] ?? Guid.NewGuid().ToString("N"),
                        Name: (string?)obj["name"] ?? "",
                        Input: (JsonObject)input.DeepClone()) { IsNativeAsync = (bool?)obj["is_native_async"] ?? false, NativeComputerCall = obj["native_computer_call"]?.DeepClone() as JsonObject });
                    break;
                case "tool_result":
                    parts.Add(new ToolResultPart(
                        ToolUseId: (string?)obj["tool_use_id"] ?? "",
                        Content: (string?)obj["content"] ?? "",
                        IsError: (bool?)obj["is_error"] ?? false));
                    break;
                case "image":
                    parts.Add(new ImagePart(
                        Base64Data: (string?)obj["data"] ?? "",
                        MediaType: (string?)obj["media_type"] ?? "image/png"));
                    break;
                // Ignore unknown types for forward compatibility.
            }
        }
        return parts;
    }

    public static string RoleToString(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        ChatRole.System => "system",
        _ => "user"
    };

    public static ChatRole RoleFromString(string role) => role.ToLowerInvariant() switch
    {
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        "system" => ChatRole.System,
        _ => ChatRole.User
    };
}
