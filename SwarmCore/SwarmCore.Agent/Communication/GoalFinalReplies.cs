using System.Text.Json;

namespace SwarmCore.Agent.Communication;

public interface IGoalWorkerReplySink
{
    Task<GoalReply> ReplyForWorkerAsync(Guid goalId, string conversationId, string deliveryId, string messageId,
        string text, GoalDeliveryState disposition, string expectedLeaseToken, CancellationToken ct = default);
}

public sealed record GoalFinalReply(string DeliveryId, string MessageId, string Text, GoalDeliveryState Disposition);
public sealed record GoalFinalReplyEnvelope(Guid GoalId, IReadOnlyList<GoalFinalReply> Replies);
public sealed record GoalFinalReplyParseResult(bool Present, bool Valid, string? Error,
    GoalFinalReplyEnvelope? Envelope, string VisibleText);

/// <summary>Explicit terminal output protocol for a worker that is forbidden to invoke even a reply tool.</summary>
public static class GoalFinalReplies
{
    public const string OpenMarker = "<goal-message-replies>";
    public const string CloseMarker = "</goal-message-replies>";
    public const int MaximumReplies = 16;
    public const int MaximumTextCharacters = 16_000;

    // Parsing proposes no mutation and never removes text. The runtime may use VisibleText only
    // after every exact reply has been persisted through the current host-bound worker lease.
    public static GoalFinalReplyParseResult Parse(string output)
    {
        var start = output.IndexOf(OpenMarker, StringComparison.Ordinal);
        var close = output.IndexOf(CloseMarker, StringComparison.Ordinal);
        if (start < 0 && close < 0) return new(false, true, null, null, output);
        GoalFinalReplyParseResult Invalid() => new(true, false,
            "The terminal goal-message reply envelope is malformed; the original output and inbox were retained.", null, output);
        if (start < 0 || close < start || output[(close + CloseMarker.Length)..].Any(c => !char.IsWhiteSpace(c))
            || output.IndexOf(OpenMarker, start + OpenMarker.Length, StringComparison.Ordinal) >= 0
            || output.IndexOf(CloseMarker, close + CloseMarker.Length, StringComparison.Ordinal) >= 0)
            return Invalid();
        // A marker quoted inside a code fence is an example, not an acknowledgement.
        var prefix = output[..start];
        var fenceCount = prefix.Split("```", StringSplitOptions.None).Length - 1;
        var tildeFenceCount = prefix.Split("~~~", StringSplitOptions.None).Length - 1;
        if (fenceCount % 2 != 0 || tildeFenceCount % 2 != 0) return Invalid();
        var json = output[(start + OpenMarker.Length)..close];
        if (json.Length > 131_072) return Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            var root = document.RootElement;
            if (!Properties(root, "goalId", "replies") || !root.TryGetProperty("goalId", out var goalValue)
                || goalValue.ValueKind != JsonValueKind.String || !Guid.TryParseExact(goalValue.GetString(), "D", out var goalId)
                || goalId == Guid.Empty || !root.TryGetProperty("replies", out var values) || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() is < 1 or > MaximumReplies) return Invalid();
            var replies = new List<GoalFinalReply>(); var seen = new HashSet<string>(StringComparer.Ordinal); var total = 0;
            foreach (var item in values.EnumerateArray())
            {
                if (!Properties(item, "deliveryId", "messageId", "text", "disposition")
                    || !String(item, "deliveryId", out var delivery) || !Identity(delivery)
                    || !String(item, "messageId", out var message) || !Identity(message) || !seen.Add(message)
                    || !String(item, "text", out var text) || string.IsNullOrWhiteSpace(text)
                    || (total += text.Length) > MaximumTextCharacters || !String(item, "disposition", out var state)) return Invalid();
                var disposition = state.ToLowerInvariant() switch
                { "read" => GoalDeliveryState.Read, "incorporated" => GoalDeliveryState.Incorporated, "replied" => GoalDeliveryState.Replied, _ => (GoalDeliveryState?)null };
                if (disposition is null) return Invalid();
                replies.Add(new(delivery, message, text, disposition.Value));
            }
            return new(true, true, null, new(goalId, replies.ToArray()), prefix.TrimEnd());
        }
        catch (JsonException) { return Invalid(); }
    }

    private static bool Identity(string value) => value.Length is > 0 and <= 256 && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    private static bool String(JsonElement item, string property, out string value)
    {
        value = "";
        if (!item.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString()!; return true;
    }
    private static bool Properties(JsonElement item, params string[] expected)
        => item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == expected.Length
            && item.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == expected.Length
            && item.EnumerateObject().All(p => expected.Contains(p.Name, StringComparer.Ordinal));
}
