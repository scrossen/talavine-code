using System.Text;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Model-backed working-context summarizer that keeps the provider's prompt cache warm.
/// When the compactor supplies the last routed request, the summary call replays that
/// request's system prompt, tool catalog, options, and the leading messages being
/// condensed byte-for-byte, and appends a single engine-authored instruction as the only
/// new input. The call therefore shares the provider's cached prefix with the conversation
/// it just served: on Anthropic the system block, the tool catalog, and the message history
/// up to the last cache breakpoint; on OpenAI and DeepSeek the automatically cached prefix.
/// Only the instruction and the output are novel. Without a replay prefix it falls back to
/// a standalone request over the reasoning-free entries. The compactor still owns user
/// directives and evidence references; this class only contributes decisions, completed
/// work, unresolved work, and commitments.
/// </summary>
public sealed class PrefixReplayWorkingContextSummarizer(
    ILlmRouter router,
    ILogger<PrefixReplayWorkingContextSummarizer>? logger = null) : IWorkingContextSummarizer
{
    public const string DecisionsHeading = "Decisions";
    public const string CompletedHeading = "Completed work";
    public const string UnresolvedHeading = "Unresolved work";
    public const string CommitmentsHeading = "Commitments";

    /// <summary>The trailing instruction; the only novel input on a replayed call.</summary>
    public static readonly string Instruction =
        "Stop working on the task for a moment. Condense the conversation above into a checkpoint another model can resume from. "
        + "Output exactly these four Markdown sections, in this order, each as terse bullets (write \"- (none)\" for an empty section):\n"
        + $"## {DecisionsHeading}\n## {CompletedHeading}\n## {UnresolvedHeading}\n## {CommitmentsHeading}\n"
        + "Preserve exact file paths, commands, identifiers, error strings, and numbers. Record user corrections faithfully. "
        + "If an earlier checkpoint appears above, merge it: keep facts that are still true, drop stale ones. "
        + "Do not call any tool, do not continue the task, and do not mention this request.";

    public async Task<WorkingContextSnapshot?> SummarizeAsync(
        WorkingContextSummarizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var call = request.Replay is { Messages.Count: > 0 } replay
            ? BuildReplayRequest(replay)
            : BuildStandaloneRequest(request.Entries);
        if (call is null) return null;

        var response = await router.ChatWithToolsAsync(FunctionTypes.Summarization, call, cancellationToken).ConfigureAwait(false);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            logger?.LogInformation("Working-context summary returned no text (stop reason {StopReason}); using the deterministic checkpoint.", response.StopReason);
            return null;
        }
        var snapshot = Parse(text);
        if (snapshot.Decisions.Count + snapshot.CompletedWork.Count + snapshot.UnresolvedWork.Count + snapshot.Commitments.Count == 0)
        {
            logger?.LogInformation("Working-context summary had no recognizable sections; using the deterministic checkpoint.");
            return null;
        }
        return snapshot;
    }

    /// <summary>
    /// The replayed request: identical system prompt, tools, options, and leading messages;
    /// one engine-authored user message appended. Stateful/native protocol fields are
    /// cleared because this is a stateless side call, not a continuation.
    /// </summary>
    public static ChatToolUseRequest BuildReplayRequest(WorkingContextReplayPrefix replay)
    {
        // Same pins and identity, but not the turn's observers: the summary's usage must not
        // be reported into the turn's adaptive accounting as if it were a turn call.
        var options = replay.LastRequest.Options?.Clone();
        if (options is not null)
        {
            options.UsageCompletionObserver = null;
            options.ProviderExecutionStartCallback = null;
        }
        return replay.LastRequest with
        {
            Messages = [.. replay.Messages, ChatMessage.Text(ChatRole.User, EngineAuthoredNotice.Create("compaction request", Instruction))],
            Options = options,
            DisableToolCalls = true,
            RequireToolCall = false,
            IsContextCompaction = false,
            ToolExecutionProtocol = ToolExecutionProtocol.Conventional,
            SteeringSession = null,
            PreviousResponseId = null,
            ContextBinding = null,
            Computer = null,
        };
    }

    private static ChatToolUseRequest? BuildStandaloneRequest(IReadOnlyList<WorkingContextSourceEntry> entries)
    {
        if (entries.Count == 0) return null;
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Role switch { ChatRole.User => "User", ChatRole.Assistant => "Assistant", ChatRole.Tool => "Tool", _ => "System" }).Append(":\n");
            foreach (var text in entry.Text) builder.AppendLine(text);
            if (entry.ReferenceIdentifiers.Count > 0) builder.Append("References: ").AppendLine(string.Join(", ", entry.ReferenceIdentifiers));
            builder.AppendLine();
        }
        return new ChatToolUseRequest
        {
            SystemPrompt = "You condense a coding assistant's conversation into a structured checkpoint. Be terse and exact.",
            Tools = [],
            Messages =
            [
                ChatMessage.Text(ChatRole.User, "Conversation to condense:\n\n" + builder.ToString().TrimEnd()),
                ChatMessage.Text(ChatRole.User, EngineAuthoredNotice.Create("compaction request", Instruction)),
            ],
            DisableToolCalls = true,
        };
    }

    /// <summary>Parses the four Markdown sections; unknown headings are ignored and "(none)" bullets dropped.</summary>
    public static WorkingContextSnapshot Parse(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [DecisionsHeading] = [], [CompletedHeading] = [], [UnresolvedHeading] = [], [CommitmentsHeading] = [],
        };
        List<string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#').Trim().TrimEnd(':');
                current = sections.TryGetValue(heading, out var list) ? list : null;
                continue;
            }
            if (current is null) continue;
            var item = line.TrimStart('-', '*', '•', ' ').Trim();
            if (item.Length > 2 && char.IsDigit(item[0]) && (item[1] == '.' || item[1] == ')')) item = item[2..].Trim();
            if (item.Length == 0 || item.Equals("(none)", StringComparison.OrdinalIgnoreCase) || item.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            current.Add(item);
        }
        return new WorkingContextSnapshot([], sections[DecisionsHeading], sections[CompletedHeading], sections[UnresolvedHeading], sections[CommitmentsHeading], []);
    }
}
