using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Coding.History;

/// <summary>Independent history, never a provider resume token, pending operation, or new instruction.</summary>
public static class ConversationFork
{
    public static IReadOnlyList<ChatMessage> CopyTranscript(IReadOnlyList<ChatMessage> source)
    {
        var copy = new List<ChatMessage>();
        for (var index = 0; index < source.Count; index++)
        {
            var message = source[index];
            var parts = message.Parts.Where(p => p is not (ResponsesReasoningPart or ResponsesCompactionPart or ResponsesConfigurationUpdatePart or ResponsesOutputPart)).Select(p => p switch
            {
                ToolUsePart call => (ChatContentPart)new ToolUsePart(call.Id, call.Name, call.Input.DeepClone().AsObject()),
                ToolResultPart result => new ToolResultPart(result.ToolUseId, result.Content, result.IsError),
                _ => p,
            }).ToArray();
            if (parts.Length == 0) continue;
            copy.Add(new(message.Role, ChatTranscriptJson.Deserialize(ChatTranscriptJson.Serialize(parts))));
            var calls = parts.OfType<ToolUsePart>().ToArray();
            if (calls.Length == 0) continue;
            // Native CLIs reuse call IDs in later turns. Only results adjacent to this call group can close it.
            var results = source.Skip(index + 1)
                .TakeWhile(m => m.Role == ChatRole.Tool || m.Role == ChatRole.User && m.Parts.All(p => p is ToolResultPart))
                .SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(p => p.ToolUseId).ToHashSet(StringComparer.Ordinal);
            var unresolved = calls.Where(p => !results.Contains(p.Id))
                .Select(p => (ChatContentPart)new ToolResultPart(p.Id,
                    "Historical tool call without a saved outcome. It was not dispatched by this fork. Inspect current state; do not replay an uncertain action.", true)).ToArray();
            if (unresolved.Length > 0) copy.Add(new(ChatRole.Tool, unresolved));
        }
        return copy;
    }
}
