using SwarmCore.Abstractions.LlmRouting;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Runtime;

/// <summary>Host-verified presentation identity. This does not authorize execution.</summary>
public sealed record ToolCallPresentationIdentity(string ConversationId, Guid? ProfileId,
    Guid TurnId, string ToolCallId, string ToolName);

public enum ToolCallPresentationState { Unknown, Completed, Failed }

public sealed record ToolCallPresentationHeader(ToolCallPresentationIdentity Identity,
    ToolCallPresentationState State, TimeSpan? Elapsed);

public sealed record ToolCallPresentationPayload(bool Available, string? InputJson, string? Result)
{
    public static ToolCallPresentationPayload Unavailable { get; } = new(false, null, null);
}

/// <summary>Optional read-only host access to original, already persisted tool payloads.</summary>
public interface IToolCallPresentationPayloadReader
{
    Task<ToolCallPresentationPayload> ReadToolCallPayloadAsync(ToolCallPresentationHeader header,
        CancellationToken ct = default);
}

public static class ToolCallPresentation
{
    public static ToolCallPresentationPayload DecodeStored(ToolCallPresentationHeader header,
        IEnumerable<(string Role, string PartsJson)> rows, Func<string, string> scrub)
    {
        var messages = new List<ChatMessage>();
        var size = 0;
        try
        {
            foreach (var row in rows)
            {
                if (messages.Count >= 10_000 || (size += row.PartsJson.Length) > 8_388_608
                    || row.Role is not ("user" or "assistant" or "tool" or "system")
                    || JsonNode.Parse(row.PartsJson) is not JsonArray parts)
                    return ToolCallPresentationPayload.Unavailable;
                // The general transcript codec supplies legacy defaults. Presentation must
                // not turn a missing original input/result into fabricated empty values.
                foreach (var part in parts)
                {
                    if (part is not JsonObject obj) return ToolCallPresentationPayload.Unavailable;
                    if ((string?)obj["type"] == "tool_use"
                        && (obj["id"] is not JsonValue id || !id.TryGetValue<string>(out _)
                            || obj["name"] is not JsonValue name || !name.TryGetValue<string>(out _)
                            || obj["input"] is not JsonObject))
                        return ToolCallPresentationPayload.Unavailable;
                    if ((string?)obj["type"] == "tool_result"
                        && (obj["tool_use_id"] is not JsonValue id2 || !id2.TryGetValue<string>(out _)
                            || obj["content"] is not JsonValue content || !content.TryGetValue<string>(out _)
                            || obj["is_error"] is not JsonValue error || !error.TryGetValue<bool>(out _)))
                        return ToolCallPresentationPayload.Unavailable;
                }
                messages.Add(new(ChatTranscriptJson.RoleFromString(row.Role), ChatTranscriptJson.Deserialize(row.PartsJson)));
            }
            return Decode(header, messages, scrub);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { return ToolCallPresentationPayload.Unavailable; }
    }

    public static IReadOnlyList<ToolCallPresentationHeader> Project(string conversationId,
        Guid? profileId, Guid turnId, IEnumerable<AgentThreadItem> items)
    {
        var all = items.ToArray();
        var anchors = all.Where(x => x.ThreadId == conversationId && x.TurnId == turnId
            && x.Kind == AgentThreadItemKind.TurnStarted && x.Event is TurnStarted).ToArray();
        var scoped = all.Where(x => x.ThreadId == conversationId && x.TurnId == turnId
            && (x.Kind == AgentThreadItemKind.ToolCallStarted && x.Event is ToolCallStarted
                || x.Kind == AgentThreadItemKind.ToolCallCompleted && x.Event is ToolCallCompleted));
        return scoped.OrderBy(x => x.Sequence).GroupBy(x => CallId(x.Event), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key)).Select(group =>
            {
                var starts = group.Where(x => x.Event is ToolCallStarted).ToArray();
                var ends = group.Where(x => x.Event is ToolCallCompleted).ToArray();
                var name = group.First().Event switch
                {
                    ToolCallStarted s => s.ToolName,
                    ToolCallCompleted e => e.ToolName,
                    _ => ""
                };
                var state = ToolCallPresentationState.Unknown;
                TimeSpan? elapsed = null;
                // Ambiguous identities and incomplete/not-dispatched calls never become success.
                if (starts.Length == 1 && ends.Length == 1 && !string.IsNullOrWhiteSpace(name)
                    && anchors.Length == 1 && anchors[0].Event is TurnStarted anchor
                    && !string.IsNullOrWhiteSpace(anchor.TurnId)
                    && (profileId is null || anchor.ProfileId == profileId)
                    && anchors[0].Sequence < starts[0].Sequence
                    && starts[0].Event.TurnId == anchor.TurnId
                    && starts[0].Sequence < ends[0].Sequence
                    && starts[0].Event.TurnId == ends[0].Event.TurnId
                    && ((ToolCallCompleted)ends[0].Event) is { WasDispatched: true } end
                    && end.ToolName == name && end.Elapsed >= TimeSpan.Zero
                    && !all.Any(x => x.ThreadId == conversationId && x.TurnId != turnId
                        && CallId(x.Event) == group.Key))
                {
                    state = end.IsError ? ToolCallPresentationState.Failed : ToolCallPresentationState.Completed;
                    elapsed = end.Elapsed;
                }
                return new ToolCallPresentationHeader(new(conversationId, profileId, turnId, group.Key, name), state, elapsed);
            }).ToArray();
    }

    private static string? CallId(TurnEvent value) => value switch
    {
        ToolCallStarted s => s.ToolCallId,
        ToolCallCompleted e => e.ToolCallId,
        _ => null
    };

    /// <summary>Decode only an unambiguous original call/result pair; never synthesize arguments.</summary>
    public static ToolCallPresentationPayload Decode(ToolCallPresentationHeader header,
        IEnumerable<ChatMessage> messages, Func<string, string> scrub)
    {
        if (header.State == ToolCallPresentationState.Unknown) return ToolCallPresentationPayload.Unavailable;
        ToolUsePart? use = null;
        ToolResultPart? result = null;
        foreach (var message in messages)
        foreach (var part in message.Parts)
        {
            if (part is ToolUsePart u && u.Id == header.Identity.ToolCallId)
            {
                if (use is not null || message.Role != ChatRole.Assistant || u.Name != header.Identity.ToolName)
                    return ToolCallPresentationPayload.Unavailable;
                use = u;
            }
            if (part is ToolResultPart r && r.ToolUseId == header.Identity.ToolCallId)
            {
                if (result is not null || use is null || message.Role is not (ChatRole.User or ChatRole.Tool)
                    || r.IsError != (header.State == ToolCallPresentationState.Failed))
                    return ToolCallPresentationPayload.Unavailable;
                result = r;
            }
        }
        if (use is null || result is null) return ToolCallPresentationPayload.Unavailable;
        var input = use.Input.ToJsonString();
        if (input.Length > 1_048_576 || result.Content.Length > 1_048_576)
            return ToolCallPresentationPayload.Unavailable;
        return new(true, scrub(input), scrub(result.Content));
    }
}
