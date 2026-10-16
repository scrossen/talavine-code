using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;

namespace SwarmCode.Hosting;

/// <summary>
/// Rebuilds visible transcript entries from the durable stores for sessions that predate
/// the presentation journal, and reconciles a journal against the provider transcript so
/// tool calls that ran without being journalled (a crash mid-turn) still get a card and
/// receipts that landed after the last journal write update their cards.
/// </summary>
public static class TranscriptHistory
{
    public const string InterruptedToolResult = "Outcome unknown: no saved result. Inspect the workspace before retrying.";
    public const string InterruptedTurnNotice = "No answer was recorded for this turn: the app was closed, or the turn was stopped, before it finished.";

    /// <summary>
    /// Interleaves conversation-store turns with the tool calls found in the provider
    /// transcript. The transcript is split into segments at each authentic user message
    /// (engine-authored notices are not boundaries); a turn takes the next segment whose
    /// user text matches its own, so a turn that never reached the provider (a dead lane,
    /// a refusal before any tool ran) consumes no segment and later turns stay aligned.
    /// </summary>
    public static List<TranscriptEntry> Build(IReadOnlyList<ChatTurnInfo> turns, IReadOnlyList<ChatMessage> transcript)
    {
        var results = IndexResults(transcript);
        var segments = new List<(string UserText, List<ToolUsePart> Calls)>();
        foreach (var message in transcript)
        {
            if (IsAuthenticUserMessage(message))
            {
                segments.Add((Text(message).Trim(), []));
                continue;
            }
            if (segments.Count == 0) continue;
            segments[^1].Calls.AddRange(message.Parts.OfType<ToolUsePart>());
        }

        var entries = new List<TranscriptEntry>();
        if (turns.Count == 0)
        {
            // No conversation-store rows (a session written only by a native lane, or a store
            // that was reset): the provider transcript alone is the history.
            var index = 0;
            foreach (var message in transcript)
            {
                if (IsAuthenticUserMessage(message))
                {
                    entries.Add(new UserEntry { Text = Text(message) });
                    foreach (var call in segments[index++].Calls) entries.Add(Card(call, results, DateTime.UtcNow));
                    continue;
                }
                if (index == 0 || message.Role != ChatRole.Assistant) continue;
                var text = Text(message);
                if (text.Length == 0) continue;
                var assistant = new AssistantEntry { IsStreaming = false };
                assistant.ReplaceText(text);
                foreach (var reasoning in message.Parts.OfType<ReasoningPart>()) assistant.AppendReasoning(reasoning.Text);
                entries.Add(assistant);
            }
            return entries;
        }

        var cursor = 0;
        foreach (var turn in turns)
        {
            entries.Add(new UserEntry { Text = turn.UserText, IsContinuation = turn.IsContinuation, AtUtc = turn.CreatedUtc });
            var wanted = turn.UserText.Trim();
            var match = -1;
            for (var j = cursor; j < segments.Count; j++)
            {
                if (string.Equals(segments[j].UserText, wanted, StringComparison.Ordinal)) { match = j; break; }
            }
            if (match >= 0)
            {
                foreach (var call in segments[match].Calls)
                    entries.Add(Card(call, results, turn.CreatedUtc));
                cursor = match + 1;
            }
            if (turn.AssistantText is not null)
            {
                var a = new AssistantEntry { IsStreaming = false, AtUtc = turn.CompletedUtc ?? turn.CreatedUtc };
                a.ReplaceText(turn.AssistantText);
                if (!string.IsNullOrEmpty(turn.ReasoningContent)) a.AppendReasoning(turn.ReasoningContent);
                entries.Add(a);
            }
        }
        return entries;
    }

    /// <summary>
    /// Brings the entries up to date with the provider transcript. Cards whose outcome is
    /// still unknown take the receipt the transcript holds for them (native or provider
    /// storage can be ahead of the UI relay when a process dies). Tool uses in the
    /// transcript's final turn that no entry mentions get a card (a crash can only cut off
    /// the turn in flight), considering only messages at or after
    /// <paramref name="fromMessage"/>, the watermark a transcript clear recorded. With
    /// <paramref name="addInterruptedNotice"/> a notice follows when the history ends without
    /// an answer. Returns the entries it appended so the caller can journal them; cards it
    /// updated in place are journalled by the caller through their own ids.
    /// </summary>
    public static IReadOnlyList<TranscriptEntry> ReconcileInterruptedTools(
        List<TranscriptEntry> entries,
        IReadOnlyList<ChatMessage> transcript,
        int fromMessage = 0,
        bool addInterruptedNotice = true)
    {
        var results = IndexResults(transcript);
        var known = new HashSet<string>(StringComparer.Ordinal);
        var updated = new List<TranscriptEntry>();
        foreach (var card in entries.OfType<ToolCallEntry>())
        {
            known.Add(card.ToolCallId);
            if (card.State is not (ToolCallState.Running or ToolCallState.Unknown)) continue;
            if (!results.TryGetValue(card.ToolCallId, out var receipt)) continue;
            card.Result = receipt.Content;
            card.State = receipt.IsError ? ToolCallState.Failed : ToolCallState.Completed;
            updated.Add(card);
        }

        var lastTurnStart = 0;
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (IsAuthenticUserMessage(transcript[i])) { lastTurnStart = i; break; }
        }
        var start = Math.Max(Math.Max(0, fromMessage), lastTurnStart);
        var appended = new List<TranscriptEntry>();
        for (var i = start; i < transcript.Count; i++)
        {
            foreach (var call in transcript[i].Parts.OfType<ToolUsePart>())
            {
                if (string.IsNullOrEmpty(call.Id) || !known.Add(call.Id)) continue;
                appended.Add(Card(call, results, DateTime.UtcNow));
            }
        }
        if (addInterruptedNotice && (appended.Count > 0 || (entries.Count > 0 && entries[^1] is UserEntry or ToolCallEntry)))
            appended.Add(new NoticeEntry { Kind = NoticeKind.Info, Text = InterruptedTurnNotice });
        entries.AddRange(appended);
        return [.. updated, .. appended];
    }

    private static bool IsAuthenticUserMessage(ChatMessage message)
        => message.Role == ChatRole.User
           && !message.Parts.OfType<ToolResultPart>().Any()
           && !EngineAuthoredNotice.IsEngineAuthored(Text(message));

    private static Dictionary<string, ToolResultPart> IndexResults(IReadOnlyList<ChatMessage> transcript)
    {
        var results = new Dictionary<string, ToolResultPart>(StringComparer.Ordinal);
        foreach (var result in transcript.SelectMany(m => m.Parts.OfType<ToolResultPart>()))
            results[result.ToolUseId ?? string.Empty] = result;
        return results;
    }

    private static ToolCallEntry Card(ToolUsePart call, Dictionary<string, ToolResultPart> results, DateTime atUtc)
    {
        var card = new ToolCallEntry
        {
            ToolCallId = call.Id ?? string.Empty,
            ToolName = call.Name ?? string.Empty,
            InputJson = call.Input?.ToJsonString() ?? "{}",
            AtUtc = atUtc,
        };
        if (results.TryGetValue(card.ToolCallId, out var result))
        {
            card.Result = result.Content;
            card.State = result.IsError ? ToolCallState.Failed : ToolCallState.Completed;
        }
        else
        {
            card.Result = InterruptedToolResult;
            card.State = ToolCallState.Unknown;
        }
        return card;
    }

    private static string Text(ChatMessage message) => string.Concat(message.Parts.OfType<TextPart>().Select(p => p.Text));
}
