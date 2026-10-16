using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Routing;

/// <summary>
/// Stops a model from running the same failing action over and over. When the current turn
/// holds <see cref="CodingOptions.RepeatedCallLimit"/> identical failing calls, or the same tool
/// keeps returning the same error despite changed arguments, the next call is refused with a message
/// to the model asking for a different approach. This is advice to the model, not a user
/// gate: nothing waits on a person. The orchestrator counts these refusals and hands the
/// request to another lane when the model keeps insisting.
/// </summary>
public sealed class RepeatedToolCallGuard(IOptions<CodingOptions> options) : IToolInvocationGuard
{
    /// <summary>Marker the orchestrator looks for in a blocked result.</summary>
    public const string BlockMarker = "repeated identical call";
    public const string RecoveryMarker = "tool recovery exhausted";

    public static bool IsRecoveryBlock(string? result) => result?.Contains(BlockMarker, StringComparison.OrdinalIgnoreCase) == true
        || result?.Contains(RecoveryMarker, StringComparison.OrdinalIgnoreCase) == true;

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        var limit = Math.Max(2, options.Value.RepeatedCallLimit);
        var transcript = request.Transcript;
        if (transcript is null || transcript.Count == 0) return Task.FromResult(ToolGuardDecision.Allow());

        string inputJson;
        try { inputJson = Normalize(JsonSerializer.SerializeToNode(request.Input, Canonical)); }
        catch (Exception ex) when (ex is NotSupportedException or JsonException) { return Task.FromResult(ToolGuardDecision.Allow()); }

        var failures = CountIdenticalFailures(transcript, request.ToolId, inputJson);
        if (failures < limit)
        {
            var repeatedError = CountRepeatedError(transcript, request.ToolId);
            if (repeatedError < limit) return Task.FromResult(ToolGuardDecision.Allow());
            return Task.FromResult(ToolGuardDecision.Redirect(
                $"{RecoveryMarker}: {request.ToolId} returned the same failure {repeatedError} times despite retries. " +
                "Stop guessing arguments. Inspect the tool contract or use a different tool to make progress. " +
                "Do not repeat an external action whose outcome is uncertain. If no workaround succeeds, explain the blocker; the harness can hand off to another lane."));
        }

        return Task.FromResult(ToolGuardDecision.Redirect(
            $"This is a {BlockMarker}: {request.ToolId} has already run {failures} times with exactly these arguments and failed every time. " +
            "Running it again will fail the same way. Read the earlier error, change the approach (different arguments, a different tool, or inspect the state first), " +
            "or explain the blocker to the user."));
    }

    /// <summary>Counts earlier tool_use parts with the same tool and input whose matching tool_result was an error.</summary>
    internal static int CountIdenticalFailures(IReadOnlyList<ChatMessage> transcript, string toolId, string inputJson)
    {
        transcript = CurrentTurn(transcript);
        var calls = new Dictionary<string, ToolUsePart>(StringComparer.Ordinal);
        var failures = 0;
        foreach (var message in transcript)
        foreach (var part in message.Parts)
        {
            if (part is ToolUsePart use) calls[use.Id] = use;
            if (part is not ToolResultPart result || !calls.TryGetValue(result.ToolUseId, out var call)) continue;
            if (IsRecoveryBlock(result.Content)) continue;
            if (!result.IsError && !LooksLikeError(result.Content)) { failures = 0; continue; }
            if (call.Name.Equals(toolId, StringComparison.OrdinalIgnoreCase) && Normalize(call.Input) == inputJson) failures++;
        }
        return failures;
    }

    // A different argument spelling is not progress. Match the actual failure, not a shell exit code alone:
    // independent failing builds/tests must remain possible. A successful call or fresh user turn resets recovery.
    internal static int CountRepeatedError(IReadOnlyList<ChatMessage> transcript, string toolId)
    {
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        string? previous = null;
        var count = 0;
        foreach (var message in CurrentTurn(transcript))
        foreach (var part in message.Parts)
        {
            if (part is ToolUsePart use) calls[use.Id] = use.Name;
            if (part is not ToolResultPart result || !calls.TryGetValue(result.ToolUseId, out var tool)) continue;
            if (IsRecoveryBlock(result.Content)) continue;
            var failed = result.IsError || LooksLikeError(result.Content);
            if (!failed) { count = 0; previous = null; continue; }
            if (!tool.Equals(toolId, StringComparison.OrdinalIgnoreCase)) continue;
            var error = result.Content.Trim();
            count = error == previous ? count + 1 : 1;
            previous = error;
        }
        return count;
    }

    private static IReadOnlyList<ChatMessage> CurrentTurn(IReadOnlyList<ChatMessage> transcript)
    {
        for (var i = transcript.Count - 1; i >= 0; i--)
            if (transcript[i].Role == ChatRole.User && transcript[i].Parts.Any(p => p is not ToolResultPart))
                return transcript.Skip(i).ToArray();
        return transcript;
    }

    private static bool LooksLikeError(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        try
        {
            if (JsonNode.Parse(content) is not JsonObject obj) return false;
            foreach (var (key, value) in obj)
            {
                if (value is not JsonValue scalar) continue;
                if ((key.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || key.Equals("success", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("ok", StringComparison.OrdinalIgnoreCase)) && scalar.TryGetValue<bool>(out var ok) && !ok) return true;
                if (key.Equals("exitCode", StringComparison.OrdinalIgnoreCase) && scalar.TryGetValue<int>(out var code) && code != 0) return true;
                if (key.Equals("error", StringComparison.OrdinalIgnoreCase) && scalar.TryGetValue<string>(out var error) && !string.IsNullOrWhiteSpace(error)) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string Normalize(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                sorted[kv.Key] = kv.Value is null ? null : JsonNode.Parse(Normalize(kv.Value));
            return sorted.ToJsonString(Canonical);
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item is null ? null : JsonNode.Parse(Normalize(item)));
            return copy.ToJsonString(Canonical);
        }
        return node.ToJsonString(Canonical);
    }
}
