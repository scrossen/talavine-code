using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// Every file-backed store keyed by a conversation id applies one explicit, platform-independent id rule,
/// so an id that is a path on one OS is rejected on every OS (not only where the file system forbids it).
/// </summary>
public class StorageIdTests : TempDirTest
{
    public static TheoryData<string> UnsafeIds => new()
    {
        "",
        " ",
        ".",
        "..",
        "...",
        ".hidden",
        "a..b",
        "../x",
        "..\\x",
        "a/b",
        "a\\b",
        "a:b",
        "C:",
        "/abs",
        "\\\\server\\share",
        "a b",
        "a\0b",
        "a\tb",
        "a*b",
        "a?b",
        "a|b",
        "a<b",
        "a\"b",
        "é",
        new string('a', StorageIds.MaxLength + 1),
    };

    public static TheoryData<string> SafeIds => new()
    {
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("D"),
        "abc",
        "c1",
        "session-1",
        "session_1",
        "v1.2",
        new string('a', StorageIds.MaxLength),
    };

    [Theory]
    [MemberData(nameof(UnsafeIds))]
    public void Rule_rejects_unsafe_shapes(string id)
    {
        Assert.False(StorageIds.IsValid(id));
        Assert.Throws<ArgumentException>(() => StorageIds.Validate(id));
    }

    [Fact]
    public void Rule_rejects_null()
    {
        Assert.False(StorageIds.IsValid(null));
        Assert.Throws<ArgumentException>(() => StorageIds.Validate(null));
    }

    [Theory]
    [MemberData(nameof(SafeIds))]
    public void Rule_accepts_existing_session_id_shapes(string id)
    {
        Assert.True(StorageIds.IsValid(id));
        Assert.Equal(id, StorageIds.Validate(id));
    }

    [Theory]
    [MemberData(nameof(UnsafeIds))]
    public async Task Every_file_store_rejects_unsafe_ids(string id)
    {
        var conversations = new FileConversationStore(Paths);
        var transcripts = new FileAgentSessionStore(Paths);
        var pending = new FilePendingToolCallJournal(Paths);
        var journal = new FileTranscriptJournal(Paths);
        var jobs = new FileConversationJobStore(Paths);
        var attachments = new AttachmentStore(Paths);
        var before = FilesUnder(Tmp);

        await Assert.ThrowsAsync<ArgumentException>(() => conversations.GetOrCreateAsync(id, "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => conversations.GetAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => transcripts.LoadAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => transcripts.AppendAsync(id, ChatMessage.Text(ChatRole.User, "x")));
        await Assert.ThrowsAsync<ArgumentException>(() => pending.LoadAsync(id));
        Assert.Throws<ArgumentException>(() => journal.Exists(id));
        Assert.Throws<ArgumentException>(() => journal.Load(id));
        Assert.Throws<ArgumentException>(() => jobs.Load(id));
        Assert.Throws<ArgumentException>(() => jobs.Delete(id));
        await Assert.ThrowsAsync<ArgumentException>(() => attachments.ImportAsync(id, "f.txt", new MemoryStream([1])));

        // A rejected id writes nothing, inside the data root or beside it.
        Assert.Equal(before, FilesUnder(Tmp));
    }

    private static string[] FilesUnder(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [MemberData(nameof(SafeIds))]
    public async Task Every_file_store_accepts_existing_session_ids(string id)
    {
        var conversations = new FileConversationStore(Paths);
        Assert.Equal(id, (await conversations.GetOrCreateAsync(id, "t")).Id);
        Assert.NotNull(await new FileConversationStore(Paths).GetAsync(id));

        var transcripts = new FileAgentSessionStore(Paths);
        await transcripts.AppendAsync(id, ChatMessage.Text(ChatRole.User, "x"));
        Assert.Single(await new FileAgentSessionStore(Paths).LoadAsync(id));

        Assert.Empty(await new FilePendingToolCallJournal(Paths).LoadAsync(id));
        Assert.False(new FileTranscriptJournal(Paths).Exists(id));
        Assert.Null(new FileConversationJobStore(Paths).Load(id));
    }
    [Fact]
    public async Task A_stray_file_whose_name_is_not_a_valid_id_is_skipped_when_listing_and_finding_turns()
    {
        var store = new FileConversationStore(Paths);
        var id = Guid.NewGuid().ToString("N");
        await store.GetOrCreateAsync(id, "kept");
        var turn = await store.AppendUserTurnAsync(id, "hello");

        // A copy made by Explorer ("<id> - Copy.json") or any other stray file in the sessions folder.
        var sessions = Paths.SessionsDirectory;
        File.Copy(Path.Combine(sessions, id + ".json"), Path.Combine(sessions, id + " - Copy.json"));
        File.WriteAllText(Path.Combine(sessions, "notes (1).json"), "{}");

        var log = new StrayFileLog();
        var cold = new FileConversationStore(Paths, log);
        var listed = await cold.ListRecentAsync();
        Assert.Equal([id], listed.Select(c => c.Id).ToArray());
        Assert.Equal(id, await cold.GetLatestIdAsync());
        // Each skipped file is reported once at Information, however often the folder is listed.
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, e.Level));
        Assert.Contains(log.Entries, e => e.Message.Contains(id + " - Copy.json"));
        Assert.Contains(log.Entries, e => e.Message.Contains("notes (1).json"));

        // A cold-cache turn lookup enumerates every header; the stray file must not make it throw.
        await new FileConversationStore(Paths).CompleteAssistantTurnAsync(turn.Id, "done", null, TimeSpan.FromSeconds(1));
        Assert.Equal("done", (await new FileConversationStore(Paths).LoadTurnsAsync(id)).Single().AssistantText);
    }

    private sealed class StrayFileLog : Microsoft.Extensions.Logging.ILogger<FileConversationStore>
    {
        public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
