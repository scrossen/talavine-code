using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// One assistant tool request whose result is not yet durable. <see cref="StartedUtc"/>
/// is set the moment the engine hands the call to the tool, so a later turn can tell a
/// call that never ran from one that ran with an unrecorded outcome.
/// </summary>
public sealed record PendingToolCall(
    string CallId,
    string ToolName,
    string InputJson,
    ToolSideEffect? SideEffect,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? StartedUtc = null);

/// <summary>
/// Host-owned journal of tool calls between request and durable result. The engine
/// records every call in a batch before dispatch and marks each one started
/// immediately before the tool runs; the runner clears the journal once the results
/// are in the durable transcript. The journal is optional: hosts that do not register
/// one keep the previous behaviour, where a crash mid-tool leaves no record. The engine
/// also persists the assistant's tool request to the session store before dispatch when
/// a journal is registered, so a crash leaves the request durable and the next turn
/// can close it with an explicit result rather than losing it.
/// </summary>
public interface IPendingToolCallJournal
{
    /// <summary>Records the calls the assistant asked for, before any of them is dispatched.</summary>
    Task RecordRequestedAsync(string conversationId, IReadOnlyList<PendingToolCall> calls, CancellationToken ct = default);

    /// <summary>Marks one call as handed to its tool. Called immediately before the tool runs.</summary>
    Task RecordStartedAsync(string conversationId, string callId, CancellationToken ct = default);

    /// <summary>Returns the calls still pending for a conversation, in request order.</summary>
    Task<IReadOnlyList<PendingToolCall>> LoadAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Forgets every pending call for a conversation. Called after their results became durable.</summary>
    Task ClearAsync(string conversationId, CancellationToken ct = default);
}

/// <summary>Why unanswered tool calls are being closed; selects the model-visible wording.</summary>
public enum PendingToolCallCloseCause
{
    /// <summary>A previous process ended (crash, kill, power loss) before recording results.</summary>
    Interrupted,
    /// <summary>The current turn was stopped (cancelled or detached) while calls were pending.</summary>
    Stopped,
}

/// <summary>
/// Closes tool calls left unanswered in a durable transcript by a session that ended
/// before recording their results. The wording tells the model what is actually known:
/// a call the journal never saw start did nothing and may simply be repeated, while a
/// call that started has an unknown outcome and must be verified before any retry
/// unless its tool is read-only. Without journal evidence the outcome is treated as
/// unknown, which is the conservative reading.
/// </summary>
public static class PendingToolCallRecovery
{
    public const string NotStartedText =
        "Not run: the previous session ended before this call was handed to its tool. Nothing was executed. "
        + "Repeat it if it is still needed.";

    public const string OutcomeUnknownText =
        "Not completed: the previous session ended after this call started but before its result was recorded. "
        + "Its outcome is unknown. It may have changed files, git state, or external systems, so inspect the current "
        + "state before repeating it, and do not repeat it blindly.";

    public const string OutcomeUnknownReadOnlyText =
        "Not completed: the previous session ended after this call started but before its result was recorded. "
        + "The tool only reads, so repeating it is safe if its result is still needed.";

    public const string StoppedNotStartedText =
        "Not run: the turn was stopped before this call was handed to its tool. Nothing was executed. "
        + "Repeat it only if it is still needed and the user asks to continue.";

    public const string StoppedOutcomeUnknownText =
        "Not completed: the turn was stopped while this call was in progress. Its outcome is unknown. It may have "
        + "changed files, git state, or external systems, so inspect the current state before repeating it.";

    public const string StoppedOutcomeUnknownReadOnlyText =
        "Not completed: the turn was stopped while this call was in progress. The tool only reads, so repeating it "
        + "is safe if its result is still needed.";

    /// <summary>
    /// Builds one error tool result per unresolved tool use in <paramref name="transcript"/>, in
    /// transcript order. <paramref name="pending"/> may be empty or null when the host has no
    /// journal; every unresolved call is then reported as outcome unknown. <paramref name="cause"/>
    /// selects the wording: a previous session that ended, or the current turn being stopped.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildRepairMessages(
        IReadOnlyList<ChatMessage> transcript,
        IReadOnlyList<PendingToolCall>? pending,
        PendingToolCallCloseCause cause = PendingToolCallCloseCause.Interrupted)
    {
        var unresolved = TranscriptToolResultRepair.FindUnresolvedToolUses(transcript);
        if (unresolved.Count == 0) return [];

        var journal = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
        foreach (var call in pending ?? [])
            journal[call.CallId] = call;

        var repaired = new List<ChatMessage>(unresolved.Count);
        foreach (var use in unresolved)
        {
            var id = use.Id ?? string.Empty;
            repaired.Add(new ChatMessage(ChatRole.Tool, [new ToolResultPart(id, DescribeOutcome(journal.GetValueOrDefault(id), hasJournal: pending is not null, cause), IsError: true)]));
        }
        return repaired;
    }

    internal static string DescribeOutcome(PendingToolCall? record, bool hasJournal, PendingToolCallCloseCause cause = PendingToolCallCloseCause.Interrupted)
    {
        var stopped = cause == PendingToolCallCloseCause.Stopped;
        if (!hasJournal || record is null) return stopped ? StoppedOutcomeUnknownText : OutcomeUnknownText;
        if (record.StartedUtc is null) return stopped ? StoppedNotStartedText : NotStartedText;
        if (record.SideEffect == ToolSideEffect.ReadOnly) return stopped ? StoppedOutcomeUnknownReadOnlyText : OutcomeUnknownReadOnlyText;
        return stopped ? StoppedOutcomeUnknownText : OutcomeUnknownText;
    }

    /// <summary>Journal rows for a batch of tool calls the assistant just requested.</summary>
    public static IReadOnlyList<PendingToolCall> Describe(
        IEnumerable<ToolUsePart> calls,
        Func<string, ToolSideEffect?> sideEffectOf,
        DateTimeOffset requestedUtc)
        => calls.Select(call => new PendingToolCall(
            call.Id,
            call.Name,
            (call.Input ?? new JsonObject()).ToJsonString(),
            sideEffectOf(call.Name),
            requestedUtc)).ToArray();
}
