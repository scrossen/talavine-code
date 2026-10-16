using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>A tool the loop may call: its wire definition and the local code that answers it.</summary>
public sealed record ToolUseLoopTool(ChatToolDefinition Definition, Func<JsonObject, CancellationToken, Task<string>> Invoke);

public sealed record ToolUseLoopResult(
    string FinalText,
    int Iterations,
    IReadOnlyList<string> ToolCalls,
    bool StoppedAtIterationLimit);

/// <summary>
/// A minimal tool-use loop over a single <see cref="IToolUseProvider"/>: send the conversation,
/// run any requested tools, return their results, and repeat until the model answers without
/// calling a tool or the iteration limit is reached. For hosts that need an agent over a small,
/// fixed tool set without the full <c>ILlmRouter</c> and agent engine.
/// </summary>
public static class ToolUseLoop
{
    public static async Task<ToolUseLoopResult> RunAsync(
        IToolUseProvider provider,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<ToolUseLoopTool> tools,
        LlmRouterOptions? options,
        int maxIterations,
        CancellationToken ct,
        Action<string>? onToolCall = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        var byName = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
        var definitions = tools.Select(t => t.Definition).ToArray();
        var transcript = new List<ChatMessage> { ChatMessage.Text(ChatRole.User, userMessage) };
        var calls = new List<string>();

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await provider.ChatAsync(new ChatToolUseRequest
            {
                Messages = transcript,
                Tools = definitions,
                SystemPrompt = systemPrompt,
                Options = options,
            }, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(response.ErrorMessage) && response.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(response.Text))
                throw new InvalidOperationException($"{provider.ProviderName} returned an error: {response.ErrorMessage}");

            var assistantParts = response.OrderedParts?.ToList() ?? BuildAssistantParts(response);
            if (assistantParts.Count > 0)
                transcript.Add(new ChatMessage(ChatRole.Assistant, assistantParts));

            if (response.ToolCalls.Count == 0)
                return new ToolUseLoopResult(response.Text?.Trim() ?? string.Empty, iteration, calls, StoppedAtIterationLimit: false);

            foreach (var call in response.ToolCalls)
            {
                calls.Add(call.Name);
                onToolCall?.Invoke(call.Name);
                string content;
                var isError = false;
                if (!byName.TryGetValue(call.Name, out var tool))
                {
                    content = $"There is no tool named '{call.Name}'. Available tools: {string.Join(", ", byName.Keys)}.";
                    isError = true;
                }
                else
                {
                    try
                    {
                        content = await tool.Invoke(call.Input ?? new JsonObject(), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        content = "The tool failed: " + ex.Message;
                        isError = true;
                    }
                }

                transcript.Add(new ChatMessage(ChatRole.Tool, [new ToolResultPart(call.Id, content, isError)]));
            }
        }

        return new ToolUseLoopResult(
            $"Stopped after {maxIterations} steps without a final answer.",
            maxIterations,
            calls,
            StoppedAtIterationLimit: true);
    }

    private static List<ChatContentPart> BuildAssistantParts(ChatToolUseResponse response)
    {
        var parts = new List<ChatContentPart>();
        if (!string.IsNullOrWhiteSpace(response.Text)) parts.Add(new TextPart(response.Text));
        parts.AddRange(response.ToolCalls);
        return parts;
    }
}
