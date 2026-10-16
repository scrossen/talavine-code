using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.Runtime;

namespace SwarmCode.Hosting;

/// <summary>
/// Conversation presentation store persisted per conversation under &lt;data&gt;/sessions/:
/// <list type="bullet">
/// <item><c>&lt;conversationId&gt;.json</c> — a small header { Info, State }, rewritten atomically only
/// when the header changes (create, rename, pin, state update, last-activity bump).</item>
/// <item><c>&lt;conversationId&gt;.turns.jsonl</c> — an append-only journal of turns: one line when a
/// user/continuation turn is created and one line when the assistant reply completes it. Loading folds
/// the lines in order; corrupt or unknown lines are skipped.</item>
/// </list>
/// Holds the user/assistant turns the UI shows again after a restart; the provider transcript lives in
/// <see cref="FileAgentSessionStore"/>. Listing recent conversations reads header files only.
/// A legacy single-document file that still carries a <c>Turns</c> array is migrated to the journal
/// the first time it is fully loaded.
/// </summary>
public sealed class FileConversationStore : IConversationStore
{
    private const string HeaderExtension = ".json";
    private const string JournalExtension = ".turns.jsonl";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Conversation> _cache = new();
    private readonly ILogger _logger;
    private readonly HashSet<string> _reportedStrayFiles = new(StringComparer.OrdinalIgnoreCase);

    public FileConversationStore(SwarmCodeDataPaths paths, ILogger<FileConversationStore>? logger = null)
    {
        _directory = paths.SessionsDirectory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Directory.CreateDirectory(_directory);
    }

    public Task<ChatConversationInfo> CreateAsync(Guid? profileId = null, CancellationToken ct = default)
        => GetOrCreateAsync(Guid.NewGuid().ToString("N"), "Untitled", profileId, ct);

    public Task<ChatConversationInfo> GetOrCreateAsync(string conversationId, string title, Guid? profileId = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var conversation = Load(conversationId) ?? Create(conversationId, title, profileId);
            return Task.FromResult(conversation.Header.Info);
        }
    }

    public Task<ChatConversationInfo?> GetAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(conversationId, out var cached)) return Task.FromResult<ChatConversationInfo?>(cached.Header.Info);
            return Task.FromResult(ReadHeader(conversationId)?.Info);
        }
    }

    public Task<IReadOnlyList<ChatConversationInfo>> ListRecentAsync(int take = 25, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = new List<ChatConversationInfo>();
            foreach (var id in EnumerateHeaderIds())
            {
                var info = _cache.TryGetValue(id, out var cached) ? cached.Header.Info : ReadHeader(id)?.Info;
                if (info is not null) list.Add(info);
            }
            return Task.FromResult<IReadOnlyList<ChatConversationInfo>>(list.OrderByDescending(c => c.LastActivityUtc).Take(take).ToList());
        }
    }

    public async Task<string?> GetLatestIdAsync(CancellationToken ct = default)
        => (await ListRecentAsync(1, ct).ConfigureAwait(false)).FirstOrDefault()?.Id;

    /// <summary>
    /// Searches every conversation's title and visible turn text (the person's requests and the replies) with
    /// <see cref="SwarmCore.Coding.History.ConversationSearch"/>; continuation prompts are not searched. Reads every
    /// journal, so it is a user action, not something to call per keystroke on a hot path. An empty query lists
    /// recent conversations with their first request as the preview.
    /// </summary>
    public Task<IReadOnlyList<ChatConversationSearchResult>> SearchAsync(string query, int take = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var documents = new List<SwarmCore.Coding.History.HistorySessionDocument>();
            var infos = new Dictionary<string, ChatConversationInfo>(StringComparer.Ordinal);
            foreach (var id in EnumerateHeaderIds())
            {
                ct.ThrowIfCancellationRequested();
                var conversation = Load(id);
                if (conversation is null) continue;
                var info = conversation.Header.Info;
                infos[info.Id] = info;
                documents.Add(new SwarmCore.Coding.History.HistorySessionDocument(info.Id, info.Title, "", info.CreatedUtc, info.LastActivityUtc,
                    conversation.Turns.Select(t => new SwarmCore.Coding.History.HistoryTurn(t.Id, t.UserText, t.AssistantText, t.CreatedUtc, t.CompletedUtc, t.IsContinuation)).ToList()));
            }
            var options = new SwarmCore.Coding.History.HistorySearchOptions(query ?? "") { IncludeResponses = true };
            var results = SwarmCore.Coding.History.ConversationSearch.Search(documents, options, take)
                .Select(r => new ChatConversationSearchResult(infos[r.Session.Id], r.Preview, r.TurnId))
                .ToList();
            return Task.FromResult<IReadOnlyList<ChatConversationSearchResult>>(results);
        }
    }

    public Task<IReadOnlyList<ChatTurnInfo>> LoadTurnsAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ChatTurnInfo>>(Load(conversationId)?.Turns.ToList() ?? []);
    }

    public Task<ChatTurnInfo> AppendUserTurnAsync(string conversationId, string userText, IReadOnlyList<TurnImageAttachment>? images = null, IReadOnlyList<TurnFileAttachment>? files = null, CancellationToken ct = default)
        => Append(conversationId, userText, images, files, isContinuation: false);

    public Task<ChatTurnInfo> AppendContinuationTurnAsync(string conversationId, string continuationPrompt, CancellationToken ct = default)
        => Append(conversationId, continuationPrompt, null, null, isContinuation: true);

    private Task<ChatTurnInfo> Append(string conversationId, string userText, IReadOnlyList<TurnImageAttachment>? images, IReadOnlyList<TurnFileAttachment>? files, bool isContinuation)
    {
        lock (_gate)
        {
            var conversation = Load(conversationId) ?? Create(conversationId, "Untitled", null);
            var turn = new ChatTurnInfo(Guid.NewGuid(), conversationId, conversation.Turns.Count, userText, null, null, null, DateTime.UtcNow, null,
                UserImages: images, UserFiles: files, IsContinuation: isContinuation);
            AppendJournalLine(conversationId, new JournalEntry { Turn = turn });
            conversation.Turns.Add(turn);
            conversation.Header.Info = conversation.Header.Info with { LastActivityUtc = DateTime.UtcNow };
            SaveHeader(conversationId, conversation.Header);
            return Task.FromResult(turn);
        }
    }

    public Task CompleteAssistantTurnAsync(Guid turnId, string assistantText, Guid? profileId, TimeSpan duration, string? reasoningContent = null, CancellationToken ct = default, string? metadataJson = null)
    {
        lock (_gate)
        {
            var found = FindTurn(turnId);
            if (found is null) return Task.CompletedTask;
            var (conversationId, conversation, index) = found.Value;
            var completion = new TurnCompletion(turnId, assistantText, profileId, DateTime.UtcNow, reasoningContent, metadataJson, duration);
            AppendJournalLine(conversationId, new JournalEntry { Completion = completion });
            conversation.Turns[index] = ApplyCompletion(conversation.Turns[index], completion);
            conversation.Header.Info = conversation.Header.Info with { LastActivityUtc = completion.CompletedUtc };
            SaveHeader(conversationId, conversation.Header);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Locates the conversation holding <paramref name="turnId"/>. Cached conversations are checked first;
    /// after a restart (cold cache) every header's journal is loaded lazily until the turn is found.
    /// </summary>
    private (string ConversationId, Conversation Conversation, int Index)? FindTurn(Guid turnId)
    {
        foreach (var (id, conversation) in _cache)
        {
            var i = conversation.Turns.FindIndex(t => t.Id == turnId);
            if (i >= 0) return (id, conversation, i);
        }
        foreach (var id in EnumerateHeaderIds())
        {
            if (_cache.ContainsKey(id)) continue;
            var conversation = Load(id);
            if (conversation is null) continue;
            var i = conversation.Turns.FindIndex(t => t.Id == turnId);
            if (i >= 0) return (id, conversation, i);
        }
        return null;
    }

    public Task DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _cache.Remove(conversationId);
            var header = HeaderPathFor(conversationId);
            if (File.Exists(header)) File.Delete(header);
            var journal = JournalPathFor(conversationId);
            if (File.Exists(journal)) File.Delete(journal);
        }
        return Task.CompletedTask;
    }

    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default)
        => MutateHeader(conversationId, header => header.Info = header.Info with { Title = title });

    public Task SetPinnedAsync(string conversationId, bool isPinned, CancellationToken ct = default)
        => MutateHeader(conversationId, header => header.Info = header.Info with { IsPinned = isPinned });

    public Task UpdateStateAsync(string conversationId, IReadOnlyDictionary<string, object> stateUpdates, CancellationToken ct = default)
        => MutateHeader(conversationId, header =>
        {
            foreach (var kv in stateUpdates)
            {
                // A null value removes the key (IConversationStore contract).
                if (kv.Value is null) header.State.Remove(kv.Key);
                else header.State[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
            }
        });

    public Task<IReadOnlyDictionary<string, object>> GetStateAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var header = _cache.TryGetValue(conversationId, out var cached) ? cached.Header : ReadHeader(conversationId);
            var result = new Dictionary<string, object>();
            if (header is not null) foreach (var kv in header.State) result[kv.Key] = kv.Value;
            return Task.FromResult<IReadOnlyDictionary<string, object>>(result);
        }
    }

    private Task MutateHeader(string conversationId, Action<Header> change)
    {
        lock (_gate)
        {
            var conversation = Load(conversationId);
            if (conversation is not null) { change(conversation.Header); SaveHeader(conversationId, conversation.Header); }
        }
        return Task.CompletedTask;
    }

    private Conversation Create(string conversationId, string title, Guid? profileId)
    {
        var header = new Header { Info = new ChatConversationInfo(conversationId, title, profileId, DateTime.UtcNow, DateTime.UtcNow) };
        var conversation = new Conversation(header, []);
        _cache[conversationId] = conversation;
        SaveHeader(conversationId, header);
        return conversation;
    }

    private static void ValidateId(string conversationId) => StorageIds.Validate(conversationId);

    private string HeaderPathFor(string conversationId)
    {
        ValidateId(conversationId);
        return Path.Combine(_directory, conversationId + HeaderExtension);
    }

    private string JournalPathFor(string conversationId)
    {
        ValidateId(conversationId);
        return Path.Combine(_directory, conversationId + JournalExtension);
    }

    /// <summary>
    /// Conversation ids that have a header file. Journals, temp files, the sidebar index and stray files whose
    /// name is not a valid id (an Explorer "&lt;id&gt; - Copy.json", for example) are excluded, so one stray file
    /// never makes listing or a cold-cache turn lookup throw.
    /// </summary>
    private IEnumerable<string> EnumerateHeaderIds()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + HeaderExtension))
        {
            if (!string.Equals(Path.GetExtension(file), HeaderExtension, StringComparison.OrdinalIgnoreCase)) continue;
            var id = Path.GetFileNameWithoutExtension(file);
            if (id == "index") continue;
            if (!StorageIds.IsValid(id))
            {
                // Reported once per file name per store instance, not on every listing.
                if (_reportedStrayFiles.Add(Path.GetFileName(file)))
                    _logger.LogInformation("Skipping {File} in the sessions folder: its name is not a valid conversation id.", Path.GetFileName(file));
                continue;
            }
            yield return id;
        }
    }

    /// <summary>Reads only the header file; never touches the journal and never populates the cache.</summary>
    private Header? ReadHeader(string conversationId)
    {
        var path = HeaderPathFor(conversationId);
        if (!File.Exists(path)) return null;
        try
        {
            var header = JsonSerializer.Deserialize<Header>(File.ReadAllText(path), Json);
            return header?.Info is null ? null : header;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Loads header plus folded journal into the cache, migrating a legacy single-document file on first load.</summary>
    private Conversation? Load(string conversationId)
    {
        if (_cache.TryGetValue(conversationId, out var cached)) return cached;
        var header = ReadHeader(conversationId);
        if (header is null) return null;

        var turns = ReadJournal(conversationId);
        if (header.Turns is { Count: > 0 } legacyTurns)
        {
            var known = turns.Select(t => t.Id).ToHashSet();
            foreach (var turn in legacyTurns)
            {
                if (turn is null || !known.Add(turn.Id)) continue;
                AppendJournalLine(conversationId, new JournalEntry { Turn = turn });
                turns.Add(turn);
            }
            turns.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            header.Turns = null;
            SaveHeader(conversationId, header);
        }
        header.Turns = null;

        var conversation = new Conversation(header, turns);
        _cache[conversationId] = conversation;
        return conversation;
    }

    private List<ChatTurnInfo> ReadJournal(string conversationId)
    {
        var turns = new List<ChatTurnInfo>();
        var path = JournalPathFor(conversationId);
        if (!File.Exists(path)) return turns;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JournalEntry? entry;
            try { entry = JsonSerializer.Deserialize<JournalEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (entry?.Turn is { } turn)
            {
                if (turn.Id == Guid.Empty || turns.Any(t => t.Id == turn.Id)) continue;
                turns.Add(turn);
            }
            else if (entry?.Completion is { } completion)
            {
                var i = turns.FindIndex(t => t.Id == completion.TurnId);
                if (i >= 0) turns[i] = ApplyCompletion(turns[i], completion);
            }
        }
        return turns;
    }

    private static ChatTurnInfo ApplyCompletion(ChatTurnInfo turn, TurnCompletion completion)
        => turn with
        {
            AssistantText = completion.AssistantText,
            ProfileId = completion.ProfileId,
            CompletedUtc = completion.CompletedUtc,
            ReasoningContent = completion.ReasoningContent,
            MetadataJson = completion.MetadataJson,
        };

    private void AppendJournalLine(string conversationId, JournalEntry entry)
    {
        using var writer = new StreamWriter(JournalPathFor(conversationId), append: true);
        writer.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    private void SaveHeader(string conversationId, Header header)
    {
        var path = HeaderPathFor(conversationId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(header, Json));
        // A virus scanner or indexer can hold the just-replaced header for a moment; retry the swap.
        SwarmCore.Abstractions.Storage.TransientFileLock.Retry(() => File.Move(tmp, path, overwrite: true), accessDeniedIsTransient: true);
    }

    private sealed record Conversation(Header Header, List<ChatTurnInfo> Turns);

    /// <summary>
    /// Header document. <see cref="Turns"/> is only ever populated when reading a legacy single-document
    /// file; it is null (and therefore omitted) whenever the header is written.
    /// </summary>
    private sealed class Header
    {
        public ChatConversationInfo Info { get; set; } = null!;
        public Dictionary<string, JsonElement> State { get; set; } = new();
        public List<ChatTurnInfo?>? Turns { get; set; }
    }

    /// <summary>One journal line: exactly one of <see cref="Turn"/> or <see cref="Completion"/> is set.</summary>
    private sealed class JournalEntry
    {
        public ChatTurnInfo? Turn { get; set; }
        public TurnCompletion? Completion { get; set; }
    }

    private sealed record TurnCompletion(
        Guid TurnId,
        string AssistantText,
        Guid? ProfileId,
        DateTime CompletedUtc,
        string? ReasoningContent,
        string? MetadataJson,
        TimeSpan Duration);
}
