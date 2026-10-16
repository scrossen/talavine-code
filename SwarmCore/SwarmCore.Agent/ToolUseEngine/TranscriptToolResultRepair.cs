using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Keeps the tool-use protocol well-formed when a turn ends abruptly. Strict
/// providers reject a request whose assistant message carries a
/// <see cref="ToolUsePart"/> with no matching <see cref="ToolResultPart"/>, so
/// whenever tool work is cut short (turn deadline, a fault between sibling
/// calls, cancellation) every dangling call must still receive a result before
/// the transcript is sent or persisted.
/// </summary>
public static class TranscriptToolResultRepair
{
    /// <summary>
    /// Tool-use parts at or after <paramref name="fromIndex"/> that have no
    /// tool result anywhere at or after the same index, in transcript order.
    /// </summary>
    public static IReadOnlyList<ToolUsePart> FindUnresolvedToolUses(
        IReadOnlyList<ChatMessage> transcript,
        int fromIndex = 0)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var uses = new List<ToolUsePart>();
        for (var index = Math.Max(0, fromIndex); index < transcript.Count; index++)
        {
            foreach (var part in transcript[index].Parts)
            {
                switch (part)
                {
                    case ToolUsePart use:
                        uses.Add(use);
                        break;
                    case ToolResultPart result:
                        resolved.Add(result.ToolUseId ?? string.Empty);
                        break;
                }
            }
        }

        return uses.Where(use => !resolved.Contains(use.Id ?? string.Empty)).ToList();
    }

    /// <summary>
    /// Appends one error tool result per unresolved tool use found at or after
    /// <paramref name="fromIndex"/>. Returns the appended messages so callers
    /// that mirror the transcript elsewhere can append the same items.
    /// </summary>
    public static IReadOnlyList<ChatMessage> AppendMissingToolResults(
        IList<ChatMessage> transcript,
        int fromIndex,
        string placeholder)
    {
        var unresolved = FindUnresolvedToolUses(transcript.ToList(), fromIndex);
        if (unresolved.Count == 0) return [];

        var appended = new List<ChatMessage>(unresolved.Count);
        foreach (var use in unresolved)
        {
            var message = new ChatMessage(
                ChatRole.Tool,
                [new ToolResultPart(use.Id ?? string.Empty, placeholder, IsError: true)]);
            transcript.Add(message);
            appended.Add(message);
        }
        return appended;
    }
}
