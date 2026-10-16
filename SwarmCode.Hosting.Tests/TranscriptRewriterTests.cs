using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public class TranscriptRewriterTests : TempDirTest
{
    [Fact]
    public async Task ReplaceLastAssistantText_RewritesOnlyTheRefusal_KeepsReceipts_AndPersists()
    {
        var store = new FileAgentSessionStore(Paths);
        await store.AppendRangeAsync("c", new List<ChatMessage>
        {
            ChatMessage.Text(ChatRole.User, "rename Foo"),
            new(ChatRole.Assistant, [new ToolUsePart("t1", "code.edit_file", new JsonObject { ["path"] = "a.cs" })]),
            new(ChatRole.Tool, [new ToolResultPart("t1", "{\"succeeded\":true}")]),
            ChatMessage.Text(ChatRole.Assistant, "I renamed Foo. I won't touch the auth code."),
        });

        Assert.True(await store.ReplaceLastAssistantTextAsync("c", "I renamed Foo. I won't touch the auth code.", CodingTurnOrchestrator.NeutralStopNote));

        var reloaded = await new FileAgentSessionStore(Paths).LoadAsync("c");
        Assert.Equal(4, reloaded.Count);
        Assert.Equal(CodingTurnOrchestrator.NeutralStopNote, Assert.IsType<TextPart>(Assert.Single(reloaded[3].Parts)).Text);
        Assert.Contains(reloaded[1].Parts, p => p is ToolUsePart { Id: "t1" });
        Assert.Contains(reloaded[2].Parts, p => p is ToolResultPart { ToolUseId: "t1" });
        Assert.DoesNotContain(await File.ReadAllTextAsync(Path.Combine(Paths.TranscriptsDirectory, "c.jsonl")), "won't touch");

        Assert.False(await store.ReplaceLastAssistantTextAsync("c", "text that is not there", "x"));
        Assert.False(await store.ReplaceLastAssistantTextAsync("missing", "anything", "x"));
    }

    [Fact]
    public async Task ReplaceLastAssistantText_StopsAtTheMostRecentAssistantMessage()
    {
        var store = new FileAgentSessionStore(Paths);
        await store.AppendRangeAsync("c", new List<ChatMessage>
        {
            ChatMessage.Text(ChatRole.Assistant, "old decline"),
            ChatMessage.Text(ChatRole.User, "again"),
            ChatMessage.Text(ChatRole.Assistant, "Done, all tests pass."),
        });
        // The latest assistant message does not match, so an older one must not be rewritten by mistake.
        Assert.False(await store.ReplaceLastAssistantTextAsync("c", "old decline", "neutral"));
        Assert.Equal("old decline", Assert.IsType<TextPart>((await store.LoadAsync("c"))[0].Parts[0]).Text);
    }
}
