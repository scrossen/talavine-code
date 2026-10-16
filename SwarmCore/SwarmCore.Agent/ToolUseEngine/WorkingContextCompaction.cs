using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Why the provider-facing transcript needed to be compacted.
/// </summary>
public enum WorkingContextCompactionReason
{
    PreflightSizeLimit,
    ProviderContextOverflow,
    RepeatedProviderFailure,
    /// <summary>The person asked for the working context to be compacted before this turn (a host's /compact).</summary>
    UserRequested
}

/// <summary>
/// Provider-neutral request for a smaller working transcript. The supplied
/// transcript is already a provider-facing copy; the durable transcript must
/// never be rewritten by a compactor.
/// </summary>
public sealed record WorkingContextCompactionRequest(
    IReadOnlyList<ChatMessage> Transcript,
    WorkingContextCompactionReason Reason,
    int? PreserveFromIndex = null,
    int MaxSummaryCharacters = 6_000,
    int MaxRetainedUserCharacters = 16_000,
    ChatToolUseRequest? LastRequest = null);

/// <summary>
/// The exact request the provider last saw, plus the leading messages of it that are being
/// summarized. A summarizer that replays this prefix verbatim and appends only its
/// instruction sends a request whose system prompt, tools, and leading messages are
/// byte-identical to the last routed call, so the provider serves them from its prompt
/// cache instead of re-reading the whole conversation.
/// </summary>
public sealed record WorkingContextReplayPrefix(ChatToolUseRequest LastRequest, IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// Factual working state retained across context compaction. Implementations
/// must not place hidden reasoning or chain-of-thought in these fields.
/// </summary>
public sealed record WorkingContextSnapshot(
    IReadOnlyList<string> UserDirectivesAndCorrections,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> CompletedWork,
    IReadOnlyList<string> UnresolvedWork,
    IReadOnlyList<string> Commitments,
    IReadOnlyList<string> EvidenceReferences);

/// <summary>
/// A reasoning-free source entry offered to an optional semantic summarizer.
/// Image bytes, tool payloads, and <see cref="ReasoningPart"/> content are
/// deliberately absent.
/// </summary>
public sealed record WorkingContextSourceEntry(
    int MessageIndex,
    ChatRole Role,
    IReadOnlyList<string> Text,
    IReadOnlyList<string> ReferenceIdentifiers);

public sealed record WorkingContextSummarizationRequest(
    IReadOnlyList<WorkingContextSourceEntry> Entries,
    int MaxSummaryCharacters,
    WorkingContextReplayPrefix? Replay = null);

/// <summary>
/// Optional host-supplied semantic summarizer. SwarmCore does not require a
/// second model route: when this service is absent or fails, the deterministic
/// compactor remains fully functional.
/// </summary>
public interface IWorkingContextSummarizer
{
    Task<WorkingContextSnapshot?> SummarizeAsync(
        WorkingContextSummarizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkingContextCompactionResult(
    IReadOnlyList<ChatMessage> Messages,
    WorkingContextSnapshot Snapshot,
    int OriginalCharacters,
    int CompactedCharacters,
    int CompactedMessageCount,
    bool UsedDeterministicFallback)
{
    public int ReclaimedCharacters => Math.Max(0, OriginalCharacters - CompactedCharacters);
}

public interface IWorkingContextCompactor
{
    Task<WorkingContextCompactionResult?> CompactAsync(
        WorkingContextCompactionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Replaces older provider-facing history with a bounded factual checkpoint,
/// while retaining real user messages separately and leaving the durable
/// transcript untouched. The deterministic extractor is also the fallback
/// when no semantic summarizer/model route is configured.
/// </summary>
public sealed partial class SemanticWorkingContextCompactor : IWorkingContextCompactor
{
    private const int MaxItemsPerSection = 12;
    private const int MaxItemCharacters = 700;
    private const int MaxRetainedUserMessageCharacters = 4_000;
    private readonly IWorkingContextSummarizer? _summarizer;

    public SemanticWorkingContextCompactor(IWorkingContextSummarizer? summarizer = null)
    {
        _summarizer = summarizer;
    }

    public async Task<WorkingContextCompactionResult?> CompactAsync(
        WorkingContextCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var transcript = request.Transcript;
        var currentTurnStart = request.PreserveFromIndex is >= 0
                               && request.PreserveFromIndex < transcript.Count
            ? request.PreserveFromIndex.Value
            : FindLatestUserMessage(transcript);
        currentTurnStart = WorkingContextCodec.SafeBoundary(transcript, currentTurnStart);
        if (currentTurnStart <= 0)
        {
            return null;
        }

        var olderHistory = transcript.Take(currentTurnStart).ToList();
        var sourceEntries = BuildReasoningFreeSourceEntries(olderHistory);
        var deterministic = BuildDeterministicSnapshot(olderHistory, sourceEntries);
        var snapshot = deterministic;
        var usedFallback = true;

        if (_summarizer is not null)
        {
            try
            {
                var semantic = await _summarizer.SummarizeAsync(
                    new WorkingContextSummarizationRequest(sourceEntries, request.MaxSummaryCharacters,
                        request.LastRequest is { } lastRequest && IsReplayablePrefix(lastRequest, olderHistory) ? new WorkingContextReplayPrefix(lastRequest, olderHistory) : null),
                    cancellationToken).ConfigureAwait(false);
                if (semantic is not null)
                {
                    // User-authored directives and concrete reference IDs are
                    // deterministic authority/evidence. A generated summary may
                    // enrich other sections but cannot erase or rewrite them.
                    snapshot = NormalizeSnapshot(semantic) with
                    {
                        UserDirectivesAndCorrections = deterministic.UserDirectivesAndCorrections,
                        EvidenceReferences = deterministic.EvidenceReferences
                    };
                    usedFallback = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Compaction is recovery infrastructure. A missing or broken
                // optional summary route must not prevent the requested turn.
                snapshot = deterministic;
                usedFallback = true;
            }
        }

        snapshot = NormalizeSnapshot(snapshot);
        var checkpointText = FormatCheckpoint(snapshot, request.MaxSummaryCharacters);

        // Layout, in order: the conversation's original request verbatim, then the
        // checkpoint, then other retained user messages, then the current turn. The
        // checkpoint is a user-role, engine-authored message rather than a system
        // message: providers fold system-role messages into the cached system block,
        // so a system-role checkpoint invalidated the system-prompt-and-tools cache on
        // every compaction, and automatic prefix caches (OpenAI, DeepSeek) lost the
        // whole conversation because message zero changed. Keeping the original
        // request first means the system block and the first message stay
        // byte-identical across compactions.
        var compacted = new List<ChatMessage>();
        var firstUserIndex = FindFirstAuthenticUserMessage(olderHistory);
        var retainedBudget = request.MaxRetainedUserCharacters;
        if (firstUserIndex >= 0)
        {
            var original = olderHistory[firstUserIndex];
            var limit = Math.Min(MaxRetainedUserMessageCharacters, Math.Max(0, retainedBudget));
            var text = BoundAuthenticUserText(UserText(original), limit);
            if (text.Length > 0)
            {
                // Reuse the original instance when it fits, so the provider payload for message
                // zero is byte-identical to every earlier request, not merely equal in text.
                var fits = original.Parts.Count == 1 && original.Parts[0] is TextPart single && single.Text == text;
                compacted.Add(fits ? original : ChatMessage.Text(ChatRole.User, text));
                retainedBudget -= text.Length;
            }
        }
        compacted.Add(CreateCheckpointMessage(checkpointText));

        // Keep authentic earlier user messages as their own messages, rather
        // than trusting a generated summary to reproduce user authority. Start
        // with the newest and retain within a separate bounded budget.
        var retainedUsers = RetainRealUserMessages(olderHistory, retainedBudget, excludeIndex: firstUserIndex);
        compacted.AddRange(retainedUsers);
        compacted.AddRange(transcript.Skip(currentTurnStart));

        var originalCharacters = CountCharacters(transcript);
        var compactedCharacters = CountCharacters(compacted);
        if (compactedCharacters >= originalCharacters)
        {
            return null;
        }

        return new WorkingContextCompactionResult(
            compacted,
            snapshot,
            originalCharacters,
            compactedCharacters,
            olderHistory.Count,
            usedFallback);
    }

    internal static WorkingContextSnapshot BuildDeterministicSnapshot(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<WorkingContextSourceEntry>? preparedEntries = null)
    {
        var entries = preparedEntries ?? BuildReasoningFreeSourceEntries(history);
        var directives = entries
            .Where(entry => entry.Role == ChatRole.User)
            .SelectMany(entry => entry.Text)
            // An earlier checkpoint or engine notice is not user authority.
            .Where(text => !EngineAuthoredNotice.IsEngineAuthored(text))
            .Select(NormalizeItem)
            .Where(static text => text.Length > 0)
            .ToList();

        var decisions = new List<string>();
        var completed = new List<string>();
        var unresolved = new List<string>();
        var commitments = new List<string>();

        foreach (var entry in entries.Where(entry => entry.Role == ChatRole.Assistant))
        {
            foreach (var sentence in entry.Text.SelectMany(SplitSentences))
            {
                if (DecisionPattern().IsMatch(sentence)) decisions.Add(sentence);
                if (CompletedPattern().IsMatch(sentence)) completed.Add(sentence);
                if (UnresolvedPattern().IsMatch(sentence)) unresolved.Add(sentence);
                if (CommitmentPattern().IsMatch(sentence)) commitments.Add(sentence);
            }
        }

        return NormalizeSnapshot(new WorkingContextSnapshot(
            directives,
            decisions,
            completed,
            unresolved,
            commitments,
            entries.SelectMany(entry => entry.ReferenceIdentifiers).ToList()));
    }

    internal static IReadOnlyList<WorkingContextSourceEntry> BuildReasoningFreeSourceEntries(
        IReadOnlyList<ChatMessage> history)
    {
        var result = new List<WorkingContextSourceEntry>(history.Count);
        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index];
            var text = new List<string>();
            var references = new List<string>();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                        text.Add(textPart.Text);
                        references.AddRange(ExtractReferenceIdentifiers(textPart.Text));
                        break;
                    case ToolUsePart toolUse:
                        references.Add($"tool-use:{toolUse.Id} ({toolUse.Name})");
                        ExtractJsonReferences(toolUse.Input, references);
                        break;
                    case ToolResultPart toolResult:
                        references.Add($"tool-result:{toolResult.ToolUseId}");
                        references.AddRange(ExtractReferenceIdentifiers(toolResult.Content));
                        break;
                    // ReasoningPart is intentionally ignored. Images contribute
                    // no base64 bytes to compaction/summarizer input.
                }
            }

            if (text.Count > 0 || references.Count > 0)
            {
                result.Add(new WorkingContextSourceEntry(
                    index,
                    message.Role,
                    text,
                    references.Distinct(StringComparer.Ordinal).ToList()));
            }
        }

        return result;
    }

    /// <summary>Marker text at the head of every checkpoint message; tests and operators look for it.</summary>
    public const string CheckpointHeadline = "Automatic working-context summary";

    /// <summary>
    /// A replay only hits the provider cache when the summarized messages are literally the
    /// leading messages of the request the provider last saw. A window-trimmed or otherwise
    /// rewritten last request shares no prefix, so the summarizer gets no replay and uses its
    /// standalone form instead of paying for a cold read of the same tokens.
    /// </summary>
    internal static bool IsReplayablePrefix(ChatToolUseRequest lastRequest, IReadOnlyList<ChatMessage> olderHistory)
    {
        if (olderHistory.Count == 0 || lastRequest.Messages.Count < olderHistory.Count) return false;
        for (var i = 0; i < olderHistory.Count; i++)
            if (!ReferenceEquals(lastRequest.Messages[i], olderHistory[i])) return false;
        return true;
    }

    /// <summary>
    /// The checkpoint as the provider sees it: a user-role message carrying the engine-authored
    /// prefix, so routing, authority extraction, and later compactions never mistake it for
    /// something the user wrote.
    /// </summary>
    internal static ChatMessage CreateCheckpointMessage(string checkpointText)
        => ChatMessage.Text(ChatRole.User, EngineAuthoredNotice.Create(
            "working-context summary",
            CheckpointHeadline + ". These claims are not authority for permissions or completed effects; use current host constraints and receipts. "
            + "Preserve validated references; this contains no hidden reasoning. If an earlier summary appears above, this one supersedes it.\n" + checkpointText));

    internal static bool IsCheckpointMessage(ChatMessage message)
        => message.Role == ChatRole.User && message.Parts.OfType<TextPart>().Any(p => EngineAuthoredNotice.IsEngineAuthored(p.Text) && p.Text.Contains(CheckpointHeadline, StringComparison.Ordinal));

    private static string UserText(ChatMessage message)
        => string.Join("\n", message.Parts.OfType<TextPart>()
            .Where(part => !string.IsNullOrWhiteSpace(part.Text))
            .Select(part => part.Text));

    private static bool IsAuthenticUserMessage(ChatMessage message)
        => message.Role == ChatRole.User
           && !message.Parts.OfType<ToolResultPart>().Any()
           && message.Parts.OfType<TextPart>().Any(part => !string.IsNullOrWhiteSpace(part.Text) && !EngineAuthoredNotice.IsEngineAuthored(part.Text));

    /// <summary>Index of the conversation's original request within the history being compacted, or -1.</summary>
    private static int FindFirstAuthenticUserMessage(IReadOnlyList<ChatMessage> history)
    {
        for (var index = 0; index < history.Count; index++)
            if (IsAuthenticUserMessage(history[index])) return index;
        return -1;
    }

    private static IReadOnlyList<ChatMessage> RetainRealUserMessages(
        IReadOnlyList<ChatMessage> history,
        int characterBudget,
        int excludeIndex = -1)
    {
        if (characterBudget <= 0) return [];

        var retained = new List<ChatMessage>();
        var used = 0;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var message = history[index];
            if (index == excludeIndex || !IsAuthenticUserMessage(message)) continue;

            var text = string.Join("\n", message.Parts.OfType<TextPart>()
                .Where(part => !string.IsNullOrWhiteSpace(part.Text) && !EngineAuthoredNotice.IsEngineAuthored(part.Text))
                .Select(part => part.Text));
            if (text.Length == 0 || used >= characterBudget) continue;

            var available = Math.Min(
                MaxRetainedUserMessageCharacters,
                characterBudget - used);
            var retainedText = BoundAuthenticUserText(text, available);
            if (retainedText.Length == 0) continue;

            retained.Add(ChatMessage.Text(ChatRole.User, retainedText));
            used += retainedText.Length;
        }

        retained.Reverse();
        return retained;
    }

    private static string BoundAuthenticUserText(string text, int maxCharacters)
    {
        if (maxCharacters <= 0) return string.Empty;
        if (text.Length <= maxCharacters) return text;
        const string marker = "\n[… older user message bounded for context …]\n";
        if (maxCharacters <= marker.Length + 2) return text[..maxCharacters];

        var contentBudget = maxCharacters - marker.Length;
        var prefixLength = contentBudget / 2;
        var suffixLength = contentBudget - prefixLength;
        return text[..prefixLength] + marker + text[^suffixLength..];
    }

    private static WorkingContextSnapshot NormalizeSnapshot(WorkingContextSnapshot snapshot)
        => new(
            Bound(snapshot.UserDirectivesAndCorrections),
            Bound(snapshot.Decisions),
            Bound(snapshot.CompletedWork),
            Bound(snapshot.UnresolvedWork),
            Bound(snapshot.Commitments),
            Bound(snapshot.EvidenceReferences));

    private static IReadOnlyList<string> Bound(IEnumerable<string> items)
        => items
            .Select(NormalizeItem)
            .Where(static item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .TakeLast(MaxItemsPerSection)
            .ToList();

    private static string NormalizeItem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = WhitespacePattern().Replace(value, " ").Trim();
        return normalized.Length <= MaxItemCharacters
            ? normalized
            : normalized[..MaxItemCharacters] + "…";
    }

    private static string FormatCheckpoint(WorkingContextSnapshot snapshot, int maxCharacters)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "User directives and corrections", snapshot.UserDirectivesAndCorrections);
        AppendSection(builder, "Decisions", snapshot.Decisions);
        AppendSection(builder, "Completed work", snapshot.CompletedWork);
        AppendSection(builder, "Unresolved work", snapshot.UnresolvedWork);
        AppendSection(builder, "Commitments", snapshot.Commitments);
        AppendSection(builder, "Evidence and tool references", snapshot.EvidenceReferences);
        if (builder.Length == 0) builder.Append("No durable working facts were extracted.");

        if (maxCharacters > 0 && builder.Length > maxCharacters)
        {
            return builder.ToString(0, maxCharacters) + "\n[checkpoint truncated to budget]";
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        builder.Append("## ").Append(title).AppendLine();
        foreach (var item in items) builder.Append("- ").AppendLine(item);
    }

    private static int FindLatestUserMessage(IReadOnlyList<ChatMessage> transcript)
    {
        for (var index = transcript.Count - 1; index >= 0; index--)
        {
            if (transcript[index].Role == ChatRole.User) return index;
        }
        return -1;
    }

    private static int CountCharacters(IEnumerable<ChatMessage> messages)
        => messages.Sum(message => message.Parts.Sum(part => part switch
        {
            TextPart text => text.Text.Length,
            ReasoningPart reasoning => reasoning.Text.Length,
            ResponsesReasoningPart reasoning => reasoning.Item.ToJsonString().Length,
            ResponsesCompactionPart compact => compact.Items.ToJsonString().Length,
            ToolUsePart toolUse => toolUse.Id.Length + toolUse.Name.Length + toolUse.Input.ToJsonString().Length,
            ToolResultPart result => result.ToolUseId.Length + result.Content.Length,
            ImagePart image => image.Base64Data.Length + image.MediaType.Length,
            _ => 0
        }));

    private static IEnumerable<string> SplitSentences(string text)
        => SentenceBoundaryPattern().Split(text)
            .Select(NormalizeItem)
            .Where(static sentence => sentence.Length > 0);

    private static IReadOnlyList<string> ExtractReferenceIdentifiers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var references = new List<string>();

        if (text.Length <= 100_000)
        {
            try
            {
                var node = JsonNode.Parse(text);
                if (node is not null) ExtractJsonReferences(node, references);
            }
            catch (JsonException)
            {
                // Plain text is expected for most assistant/user content.
            }
        }

        foreach (Match match in ReferencePattern().Matches(text))
        {
            references.Add(match.Value.Trim());
        }
        return references.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ExtractJsonReferences(JsonNode? node, ICollection<string> references, string? key = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) ExtractJsonReferences(pair.Value, references, pair.Key);
                break;
            case JsonArray array:
                foreach (var child in array) ExtractJsonReferences(child, references, key);
                break;
            case JsonValue value when key is not null && ReferenceKeyPattern().IsMatch(key):
                var rendered = value.ToJsonString().Trim('"');
                if (rendered.Length is > 0 and <= 180)
                {
                    references.Add($"{key}:{rendered}");
                }
                break;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(?<=[.!?])\s+|[\r\n]+")]
    private static partial Regex SentenceBoundaryPattern();

    [GeneratedRegex(@"\b(decided|decision|selected|chose|agreed|will use|using)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecisionPattern();

    [GeneratedRegex(@"\b(completed|finished|implemented|fixed|verified|passed|done|created|added|removed|updated)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletedPattern();

    [GeneratedRegex(@"\b(remains?|remaining|unresolved|pending|todo|next|still need|blocked|failed|not yet|incomplete)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnresolvedPattern();

    [GeneratedRegex(@"\b(I(?:'ll| will)|we(?:'ll| will)|committed|promised|must|need to|should)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommitmentPattern();

    [GeneratedRegex(@"\b(?:evidence|source|citation|message|record|document|tool(?:-use|-result)?)[-_ ]?(?:id|ref)?\s*[:=#]\s*[A-Za-z0-9][A-Za-z0-9._:/-]{2,180}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"(?:id|ids|ref|refs|reference|references|source|sources|evidence|citation|citations)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceKeyPattern();
}
