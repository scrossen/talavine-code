using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Compacts superseded host continuation scaffolding in provider context only.
/// The caller must obtain continuation text from trusted host metadata, never
/// infer it from message wording. Durable transcript and audit rows stay intact.
/// </summary>
public static class ContinuationPromptCompactor
{
    public const string HistoricalContinuationText =
        "[Earlier host continuation omitted; the latest continuation contains current objective context.]";

    public static List<ChatMessage> Compact(
        IReadOnlyList<ChatMessage> messages,
        IEnumerable<string> hostContinuationTexts,
        IEnumerable<string> authenticUserTexts)
    {
        var trustedTexts = new HashSet<string>(hostContinuationTexts, StringComparer.Ordinal);
        // A text-only legacy transcript has no turn foreign key. If the same
        // text was also authored by a user, attribution is ambiguous: retain it.
        trustedTexts.ExceptWith(authenticUserTexts);
        trustedTexts.Remove(string.Empty);
        if (trustedTexts.Count == 0)
            return [.. messages];

        var hostContinuations = new bool[messages.Count];
        var latest = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            // A multipart user message may contain attachments or tool receipts.
            // Exact single-text matches are the only safe legacy mapping.
            if (message.Role == ChatRole.User
                && message.Parts.Count == 1
                && message.Parts[0] is TextPart text
                && trustedTexts.Contains(text.Text))
            {
                hostContinuations[i] = true;
                latest = i;
            }
        }

        var result = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (!hostContinuations[i] || i == latest)
            {
                result.Add(messages[i]);
                continue;
            }

            // Failed-provider retries may have no intervening output at all.
            // Keep only the last prompt in such a run, while preserving each
            // assistant/tool response and its original message ordering.
            if (i + 1 < messages.Count && hostContinuations[i + 1])
                continue;

            result.Add(ChatMessage.Text(ChatRole.User, HistoricalContinuationText));
        }

        return result;
    }
}
