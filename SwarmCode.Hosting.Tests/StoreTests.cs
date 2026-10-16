using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

public class FileConversationStoreTests : TempDirTest
{
    [Fact]
    public async Task GetOrCreate_ThenGet_ReturnsSameInfo()
    {
        var store = new FileConversationStore(Paths);
        var info = await store.GetOrCreateAsync("abc", "Title");
        var again = await store.GetOrCreateAsync("abc", "Other");
        Assert.Equal("Title", again.Title);
        Assert.Equal(info.Id, (await store.GetAsync("abc"))!.Id);
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task Turns_RoundTrip_AndSurviveNewInstance()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("c1", "t");
        var turn = await store.AppendUserTurnAsync("c1", "hello");
        await store.CompleteAssistantTurnAsync(turn.Id, "world", null, TimeSpan.FromSeconds(1), "thinking");
        Assert.True(File.Exists(Path.Combine(Paths.SessionsDirectory, "c1.json")));

        var reloaded = new FileConversationStore(Paths);
        var turns = await reloaded.LoadTurnsAsync("c1");
        var t = Assert.Single(turns);
        Assert.Equal("hello", t.UserText);
        Assert.Equal("world", t.AssistantText);
        Assert.Equal("thinking", t.ReasoningContent);
        Assert.NotNull(t.CompletedUtc);
    }

    [Fact]
    public async Task ListRecent_OrdersByLastActivityDescending_AndExcludesIndex()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("a", "A");
        await Task.Delay(20);
        await store.GetOrCreateAsync("b", "B");
        await Task.Delay(20);
        await store.AppendUserTurnAsync("a", "bump");
        File.WriteAllText(Paths.SessionIndexPath, "{}");

        var list = await new FileConversationStore(Paths).ListRecentAsync();
        Assert.Equal(["a", "b"], list.Select(c => c.Id).ToArray());
        Assert.Equal("a", await store.GetLatestIdAsync());
        Assert.Single(await store.ListRecentAsync(1));
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("gone", "t");
        var path = Path.Combine(Paths.SessionsDirectory, "gone.json");
        Assert.True(File.Exists(path));
        await store.DeleteAsync("gone");
        Assert.False(File.Exists(path));
        Assert.Null(await store.GetAsync("gone"));
    }

    [Fact]
    public async Task State_UpdateAndGet_Persists()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("s", "t");
        await store.UpdateStateAsync("s", new Dictionary<string, object> { ["k"] = "v", ["n"] = 3 });
        var state = await new FileConversationStore(Paths).GetStateAsync("s");
        Assert.Equal(2, state.Count);
        Assert.Contains("v", state["k"].ToString());
        Assert.Contains("3", state["n"].ToString());
    }

    [Fact]
    public async Task State_NullValueRemovesTheKey()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("rm", "t");
        await store.UpdateStateAsync("rm", new Dictionary<string, object> { ["keep"] = "v", ["drop"] = "x" });
        await store.UpdateStateAsync("rm", new Dictionary<string, object> { ["drop"] = null! });
        var state = await new FileConversationStore(Paths).GetStateAsync("rm");
        Assert.Equal(["keep"], state.Keys);
    }

    [Fact]
    public async Task RenameAndPin_Persist()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("r", "old");
        await store.RenameAsync("r", "new");
        await store.SetPinnedAsync("r", true);
        var info = (await new FileConversationStore(Paths).GetAsync("r"))!;
        Assert.Equal("new", info.Title);
        Assert.True(info.IsPinned);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..\\x")]
    [InlineData("a:b")]
    public async Task InvalidId_Throws(string id)
    {
        var store = new FileConversationStore(Paths);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetOrCreateAsync(id, "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(id));
    }

    private string HeaderPath(string id) => Path.Combine(Paths.SessionsDirectory, id + ".json");
    private string JournalPath(string id) => Path.Combine(Paths.SessionsDirectory, id + ".turns.jsonl");

    private static void AssertHeaderHasNoTurns(string headerJson)
    {
        using var doc = JsonDocument.Parse(headerJson);
        Assert.True(doc.RootElement.TryGetProperty("info", out _));
        Assert.False(doc.RootElement.TryGetProperty("turns", out _), "header must not carry a turns array");
    }

    [Fact]
    public async Task AppendAndComplete_PersistViaJournal_AndHeaderStaysSmall()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("j", "t");
        var first = await store.AppendUserTurnAsync("j", "one");
        await store.CompleteAssistantTurnAsync(first.Id, "reply one", null, TimeSpan.FromSeconds(1), "why", metadataJson: "{\"m\":1}");
        var second = await store.AppendContinuationTurnAsync("j", "continue");

        Assert.True(File.Exists(JournalPath("j")));
        Assert.Equal(3, File.ReadAllLines(JournalPath("j")).Count(l => l.Length > 0));
        AssertHeaderHasNoTurns(File.ReadAllText(HeaderPath("j")));

        var turns = await new FileConversationStore(Paths).LoadTurnsAsync("j");
        Assert.Equal(2, turns.Count);
        Assert.Equal(first.Id, turns[0].Id);
        Assert.Equal("one", turns[0].UserText);
        Assert.Equal("reply one", turns[0].AssistantText);
        Assert.Equal("why", turns[0].ReasoningContent);
        Assert.Equal("{\"m\":1}", turns[0].MetadataJson);
        Assert.NotNull(turns[0].CompletedUtc);
        Assert.Equal(second.Id, turns[1].Id);
        Assert.Equal(1, turns[1].Sequence);
        Assert.True(turns[1].IsContinuation);
        Assert.Null(turns[1].AssistantText);
    }

    [Fact]
    public async Task ListRecent_ReadsHeadersOnly_IgnoringJournalContent()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("h1", "One");
        await Task.Delay(20);
        await store.GetOrCreateAsync("h2", "Two");
        File.WriteAllText(JournalPath("h1"), "this is not json at all\n{\"turn\":{\"id\":\"x\"}}\n");
        File.WriteAllText(JournalPath("h2"), "\0\0\0garbage");

        var fresh = new FileConversationStore(Paths);
        var list = await fresh.ListRecentAsync();
        Assert.Equal(["h2", "h1"], list.Select(c => c.Id).ToArray());
        Assert.Equal("h2", await fresh.GetLatestIdAsync());
        Assert.Equal("One", (await fresh.GetAsync("h1"))!.Title);
    }

    [Fact]
    public async Task LegacyDocument_IsMigratedToJournalOnFirstLoad()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var legacy = new
        {
            info = new { id = "old", title = "Legacy", profileId = (Guid?)null, createdUtc = DateTime.UtcNow.AddMinutes(-5), lastActivityUtc = DateTime.UtcNow.AddMinutes(-1), isPinned = true },
            turns = new object[]
            {
                new { id = t1, conversationId = "old", sequence = 0, userText = "first", assistantText = "a1", profileId = (Guid?)null, profileName = (string?)null, createdUtc = DateTime.UtcNow.AddMinutes(-5), completedUtc = DateTime.UtcNow.AddMinutes(-4), reasoningContent = "r1" },
                new { id = t2, conversationId = "old", sequence = 1, userText = "second", assistantText = (string?)null, profileId = (Guid?)null, profileName = (string?)null, createdUtc = DateTime.UtcNow.AddMinutes(-2), completedUtc = (DateTime?)null, reasoningContent = (string?)null },
            },
            state = new Dictionary<string, object> { ["k"] = "v" },
        };
        File.WriteAllText(HeaderPath("old"), JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(File.Exists(JournalPath("old")));

        var store = new FileConversationStore(Paths);
        var turns = await store.LoadTurnsAsync("old");
        Assert.Equal(2, turns.Count);
        Assert.Equal(t1, turns[0].Id);
        Assert.Equal("a1", turns[0].AssistantText);
        Assert.Equal("r1", turns[0].ReasoningContent);
        Assert.Equal(t2, turns[1].Id);
        Assert.Null(turns[1].AssistantText);

        Assert.True(File.Exists(JournalPath("old")));
        Assert.Equal(2, File.ReadAllLines(JournalPath("old")).Count(l => l.Length > 0));
        AssertHeaderHasNoTurns(File.ReadAllText(HeaderPath("old")));

        var second = new FileConversationStore(Paths);
        var again = await second.LoadTurnsAsync("old");
        Assert.Equal(new[] { t1, t2 }, again.Select(t => t.Id).ToArray());
        var info = (await second.GetAsync("old"))!;
        Assert.Equal("Legacy", info.Title);
        Assert.True(info.IsPinned);
        Assert.Contains("v", (await second.GetStateAsync("old"))["k"].ToString());

        // Completing a migrated turn appends to the journal rather than re-growing the header.
        await second.CompleteAssistantTurnAsync(t2, "a2", null, TimeSpan.Zero);
        Assert.Equal(3, File.ReadAllLines(JournalPath("old")).Count(l => l.Length > 0));
        AssertHeaderHasNoTurns(File.ReadAllText(HeaderPath("old")));
        Assert.Equal("a2", (await new FileConversationStore(Paths).LoadTurnsAsync("old"))[1].AssistantText);
    }

    [Fact]
    public async Task LegacyDocument_WithEmptyTurns_LoadsAsHeader()
    {
        File.WriteAllText(HeaderPath("empty"), "{\"info\":{\"id\":\"empty\",\"title\":\"E\",\"createdUtc\":\"2024-01-01T00:00:00Z\",\"lastActivityUtc\":\"2024-01-01T00:00:00Z\"},\"turns\":[],\"state\":{}}");
        var store = new FileConversationStore(Paths);
        Assert.Empty(await store.LoadTurnsAsync("empty"));
        Assert.Equal("E", (await store.GetAsync("empty"))!.Title);
        Assert.False(File.Exists(JournalPath("empty")));
        await store.RenameAsync("empty", "Renamed");
        AssertHeaderHasNoTurns(File.ReadAllText(HeaderPath("empty")));
    }

    [Fact]
    public async Task CorruptJournalLines_AreSkipped()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("bad", "t");
        var a = await store.AppendUserTurnAsync("bad", "a");
        await store.CompleteAssistantTurnAsync(a.Id, "A", null, TimeSpan.Zero);
        var b = await store.AppendUserTurnAsync("bad", "b");

        var lines = File.ReadAllLines(JournalPath("bad")).Where(l => l.Length > 0).ToList();
        Assert.Equal(3, lines.Count);
        var unknownId = Guid.NewGuid();
        File.WriteAllLines(JournalPath("bad"),
        [
            lines[0],
            "{not json",
            "",
            "{\"something\":\"else\"}",
            $"{{\"completion\":{{\"turnId\":\"{unknownId}\",\"assistantText\":\"orphan\",\"completedUtc\":\"2024-01-01T00:00:00Z\",\"duration\":\"00:00:00\"}}}}",
            lines[1],
            "\"a bare string\"",
            lines[2],
        ]);

        var turns = await new FileConversationStore(Paths).LoadTurnsAsync("bad");
        Assert.Equal(2, turns.Count);
        Assert.Equal(a.Id, turns[0].Id);
        Assert.Equal("A", turns[0].AssistantText);
        Assert.Equal(b.Id, turns[1].Id);
        Assert.Null(turns[1].AssistantText);
    }

    [Fact]
    public async Task CompleteAssistantTurn_AfterRestart_FindsTurnWithColdCache()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("other", "noise");
        await store.AppendUserTurnAsync("other", "unrelated");
        await store.GetOrCreateAsync("cold", "t");
        var turn = await store.AppendUserTurnAsync("cold", "question");

        var restarted = new FileConversationStore(Paths);
        await restarted.CompleteAssistantTurnAsync(turn.Id, "answer", null, TimeSpan.FromMilliseconds(5), "thought");

        var turns = await new FileConversationStore(Paths).LoadTurnsAsync("cold");
        var t = Assert.Single(turns);
        Assert.Equal("answer", t.AssistantText);
        Assert.Equal("thought", t.ReasoningContent);
        Assert.NotNull(t.CompletedUtc);
        Assert.Null(Assert.Single(await restarted.LoadTurnsAsync("other")).AssistantText);
        Assert.Equal("cold", await restarted.GetLatestIdAsync());
    }

    [Fact]
    public async Task CompleteAssistantTurn_UnknownId_IsNoOp()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("x", "t");
        await store.CompleteAssistantTurnAsync(Guid.NewGuid(), "nothing", null, TimeSpan.Zero);
        Assert.False(File.Exists(JournalPath("x")));
        Assert.Empty(await store.LoadTurnsAsync("x"));
    }

    [Fact]
    public async Task Delete_RemovesHeaderJournalAndCache()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("d", "t");
        await store.AppendUserTurnAsync("d", "hi");
        Assert.True(File.Exists(JournalPath("d")));
        await store.DeleteAsync("d");
        Assert.False(File.Exists(HeaderPath("d")));
        Assert.False(File.Exists(JournalPath("d")));
        Assert.Null(await store.GetAsync("d"));
        Assert.Empty(await store.LoadTurnsAsync("d"));
        Assert.Empty(await store.ListRecentAsync());
    }
}

public class FileAgentSessionStoreTests : TempDirTest
{
    private static List<ChatMessage> Sample() =>
    [
        ChatMessage.Text(ChatRole.User, "hi"),
        new(ChatRole.Assistant, [new TextPart("calling"), new ToolUsePart("t1", "code.edit_file", new JsonObject { ["path"] = "a.txt" })]),
        new(ChatRole.Tool, [new ToolResultPart("t1", "ok", false)]),
        ChatMessage.Text(ChatRole.Assistant, "done"),
    ];

    private static void AssertSame(IReadOnlyList<ChatMessage> expected, IReadOnlyList<ChatMessage> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Role, actual[i].Role);
            Assert.Equal(expected[i].Parts.Count, actual[i].Parts.Count);
            for (var j = 0; j < expected[i].Parts.Count; j++)
            {
                Assert.Equal(expected[i].Parts[j].GetType(), actual[i].Parts[j].GetType());
                switch (expected[i].Parts[j])
                {
                    case TextPart tp: Assert.Equal(tp.Text, ((TextPart)actual[i].Parts[j]).Text); break;
                    case ToolUsePart tu:
                        var au = (ToolUsePart)actual[i].Parts[j];
                        Assert.Equal(tu.Id, au.Id); Assert.Equal(tu.Name, au.Name);
                        Assert.Equal(tu.Input.ToJsonString(), au.Input.ToJsonString());
                        break;
                    case ToolResultPart tr:
                        var ar = (ToolResultPart)actual[i].Parts[j];
                        Assert.Equal(tr.ToolUseId, ar.ToolUseId); Assert.Equal(tr.Content, ar.Content); Assert.Equal(tr.IsError, ar.IsError);
                        break;
                }
            }
        }
    }

    [Fact]
    public async Task AppendRange_ThenLoadFromNewInstance_RoundTrips()
    {
        var store = new FileAgentSessionStore(Paths);
        var msgs = Sample();
        await store.AppendRangeAsync("conv", msgs);
        AssertSame(msgs, await store.LoadAsync("conv"));
        AssertSame(msgs, await new FileAgentSessionStore(Paths).LoadAsync("conv"));
    }

    [Fact]
    public async Task Append_Single_AccumulatesAcrossInstances()
    {
        var a = new FileAgentSessionStore(Paths);
        await a.AppendAsync("c", ChatMessage.Text(ChatRole.User, "one"));
        var b = new FileAgentSessionStore(Paths);
        await b.AppendAsync("c", ChatMessage.Text(ChatRole.Assistant, "two"));
        Assert.Equal(2, (await new FileAgentSessionStore(Paths).LoadAsync("c")).Count);
    }

    [Fact]
    public async Task Clear_DeletesFileAndMessages()
    {
        var store = new FileAgentSessionStore(Paths);
        await store.AppendAsync("c", ChatMessage.Text(ChatRole.User, "x"));
        var path = Path.Combine(Paths.TranscriptsDirectory, "c.jsonl");
        Assert.True(File.Exists(path));
        await store.ClearAsync("c");
        Assert.False(File.Exists(path));
        Assert.Empty(await store.LoadAsync("c"));
    }

    [Fact]
    public async Task CorruptLine_IsSkipped()
    {
        await new FileAgentSessionStore(Paths).AppendAsync("seed", ChatMessage.Text(ChatRole.User, "ok"));
        var good = File.ReadAllLines(Path.Combine(Paths.TranscriptsDirectory, "seed.jsonl")).Single();
        File.WriteAllLines(Path.Combine(Paths.TranscriptsDirectory, "bad.jsonl"), [good, "{not json", "", "{\"role\":\"user\"}", good]);
        var loaded = await new FileAgentSessionStore(Paths).LoadAsync("bad");
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task TornFinalLine_DoesNotSwallowTheNextAppendedMessage()
    {
        await new FileAgentSessionStore(Paths).AppendAsync("torn", ChatMessage.Text(ChatRole.User, "first"));
        var path = Path.Combine(Paths.TranscriptsDirectory, "torn.jsonl");
        // A crash mid-write left a partial line without a newline.
        File.AppendAllText(path, "{\"role\":\"assistant\",\"parts\":[{\"type\":\"te");

        await new FileAgentSessionStore(Paths).AppendAsync("torn", ChatMessage.Text(ChatRole.User, "second"));

        var loaded = await new FileAgentSessionStore(Paths).LoadAsync("torn");
        Assert.Equal(["first", "second"],
            loaded.Select(m => string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text))).ToArray());
    }

    [Fact]
    public async Task InvalidId_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => new FileAgentSessionStore(Paths).LoadAsync("a/b"));
}
