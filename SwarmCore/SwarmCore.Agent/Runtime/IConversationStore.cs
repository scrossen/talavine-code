namespace SwarmCore.Agent.Runtime;

/// <summary>
/// UI-facing conversation storage. Holds the presentation layer — one row
/// per user/assistant turn pair — distinct from the low-level
/// transcript stored by <see cref="ToolUseEngine.IAgentSessionStore"/>,
/// which keeps the agent's full tool-use history. This separation lets the
/// conversation list render quickly without loading every tool call and
/// result.
/// </summary>
public interface IConversationStore
{
    /// <summary>Create a new conversation, optionally bound to a profile.</summary>
    Task<ChatConversationInfo> CreateAsync(Guid? profileId = null, CancellationToken ct = default);

    /// <summary>Create a conversation with a stable caller-provided id, or return it if it already exists.</summary>
    Task<ChatConversationInfo> GetOrCreateAsync(
        string conversationId,
        string title,
        Guid? profileId = null,
        CancellationToken ct = default);

    Task<ChatConversationInfo?> GetAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Most recent N conversations, newest first.</summary>
    Task<IReadOnlyList<ChatConversationInfo>> ListRecentAsync(int take = 25, CancellationToken ct = default);

    /// <summary>
    /// Search all user-visible saved conversations by title and visible message text.
    /// Host-authored continuation instructions, tool payloads and reasoning are excluded.
    /// An empty query returns recent history. Implementations must not silently search
    /// only the recent-history page, which would hide older matching conversations.
    /// </summary>
    Task<IReadOnlyList<ChatConversationSearchResult>> SearchAsync(
        string query, int take = 50, CancellationToken ct = default)
        => throw new NotSupportedException("Conversation search is unavailable in this store.");

    Task<string?> GetLatestIdAsync(CancellationToken ct = default);

    /// <summary>Load ordered turn history for UI rendering.</summary>
    Task<IReadOnlyList<ChatTurnInfo>> LoadTurnsAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Append a user turn; returns the created turn info (id, sequence, timestamp).</summary>
    /// <param name="images">Optional image attachments, persisted on the turn row so the chat history can re-render them on reload.</param>
    /// <param name="files">Optional text-file attachments (CSV, JSON, …), persisted so the history shows what data the turn carried.</param>
    Task<ChatTurnInfo> AppendUserTurnAsync(
        string conversationId,
        string userText,
        IReadOnlyList<TurnImageAttachment>? images = null,
        IReadOnlyList<TurnFileAttachment>? files = null,
        CancellationToken ct = default);

    /// <summary>
    /// Append a host-authored continuation prompt. Stores that do not expose a
    /// distinct presentation flag remain source-compatible and persist it as a
    /// normal turn; stores with a richer UI should override this method.
    /// </summary>
    Task<ChatTurnInfo> AppendContinuationTurnAsync(
        string conversationId,
        string continuationPrompt,
        CancellationToken ct = default)
        => AppendUserTurnAsync(conversationId, continuationPrompt, ct: ct);

    /// <summary>Append/attach the assistant reply to a user turn.</summary>
    /// <param name="reasoningContent">
    /// Optional chain-of-thought emitted by thinking-capable models. When
    /// supplied, persisted on the turn so the UI can render a collapsible
    /// "Thinking" widget after the conversation reloads.
    /// </param>
    Task CompleteAssistantTurnAsync(
        Guid turnId,
        string assistantText,
        Guid? profileId,
        TimeSpan duration,
        string? reasoningContent = null,
        CancellationToken ct = default,
        string? metadataJson = null);

    /// <summary>Delete a conversation and all its turns (cascade handled by caller / EF).</summary>
    Task DeleteAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Permanently retire a caller-owned ID prefix and remove its conversations
    /// and owned payloads atomically. Future writes into that namespace must be
    /// rejected, including stale background writers. No recent-list fallback.
    /// Hosts define matching consistently for deletion and future-write guards.
    /// Unsupported stores fail before acknowledging retirement.
    /// </summary>
    Task RetireByIdPrefixAsync(string conversationIdPrefix, CancellationToken ct = default)
        => throw new NotSupportedException("This conversation store does not support namespace cleanup.");

    /// <summary>
    /// Bounded retention for a caller-owned internal namespace: delete conversations under
    /// <paramref name="conversationIdPrefix"/> idle since before <paramref name="inactiveBeforeUtc"/>
    /// that hold no unfinished durable work. The namespace stays writable. Returns the number
    /// deleted; stores without retention support delete nothing.
    /// </summary>
    Task<int> DeleteInactiveByIdPrefixAsync(string conversationIdPrefix, DateTime inactiveBeforeUtc, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>Set a user-chosen title. Suppresses the first-turn auto-derived title thereafter.</summary>
    Task RenameAsync(string conversationId, string title, CancellationToken ct = default);

    /// <summary>Pin/unpin a conversation so it sorts above unpinned ones in the history drawer.</summary>
    Task SetPinnedAsync(string conversationId, bool isPinned, CancellationToken ct = default);

    /// <summary>
    /// Conversation-level sticky state (profile choice, last selected model, grid snapshots). Key-level: only the
    /// keys in <paramref name="stateUpdates"/> change, atomically, so concurrent writers of different keys never
    /// drop each other's keys. Pass only the keys being changed, never a full state copy read earlier. A null value
    /// removes its key.
    /// </summary>
    Task UpdateStateAsync(string conversationId, IReadOnlyDictionary<string, object> stateUpdates, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object>> GetStateAsync(string conversationId, CancellationToken ct = default);
}

public sealed record ChatConversationInfo(
    string Id,
    string Title,
    Guid? ProfileId,
    DateTime CreatedUtc,
    DateTime LastActivityUtc,
    bool IsPinned = false);

/// <summary>A bounded excerpt from saved presentation history, with its original turn identity.</summary>
public sealed record ChatConversationSearchResult(
    ChatConversationInfo Conversation,
    string Preview,
    Guid? TurnId = null);

public sealed record ChatTurnInfo(
    Guid Id,
    string ConversationId,
    int Sequence,
    string UserText,
    string? AssistantText,
    Guid? ProfileId,
    string? ProfileName,
    DateTime CreatedUtc,
    DateTime? CompletedUtc,
    string? ReasoningContent = null,
    string? UserImageBase64 = null,
    string? UserImageMediaType = null,
    string? MetadataJson = null,
    IReadOnlyList<TurnImageAttachment>? UserImages = null,
    IReadOnlyList<TurnFileAttachment>? UserFiles = null,
    bool IsContinuation = false)
{
    /// <summary>
    /// All images attached to this user turn, merging the multi-image list
    /// with the legacy single-image columns so callers render one shape
    /// regardless of which store version wrote the row.
    /// </summary>
    public IReadOnlyList<TurnImageAttachment> AllUserImages =>
        UserImages is { Count: > 0 }
            ? UserImages
            : !string.IsNullOrEmpty(UserImageBase64)
                ? new[] { new TurnImageAttachment(UserImageBase64, UserImageMediaType ?? "image/png") }
                : Array.Empty<TurnImageAttachment>();

    /// <summary>Text-file attachments on this user turn; never null.</summary>
    public IReadOnlyList<TurnFileAttachment> AllUserFiles =>
        UserFiles ?? Array.Empty<TurnFileAttachment>();
}

/// <summary>
/// One image attached to a user turn. <see cref="MediaType"/> must be a
/// provider-acceptable vision format (png/jpeg/gif/webp) — intake surfaces
/// normalize exotic formats before they reach this record.
/// </summary>
public sealed record TurnImageAttachment(
    string Base64Data,
    string MediaType,
    string? FileName = null);

/// <summary>
/// One text-file attachment on a user turn (CSV, JSON, plain text, …).
/// <see cref="TextContent"/> is the decoded file body; the runner injects it
/// into the LLM message as a fenced block so the model can read the data.
/// Intake surfaces enforce a size cap — this record assumes the content is
/// already prompt-sized.
/// </summary>
public sealed record TurnFileAttachment(
    string FileName,
    string MediaType,
    string TextContent);
