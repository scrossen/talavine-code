using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Prompting;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class FileMentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-mentions-" + Guid.NewGuid().ToString("N"));

    public FileMentionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "lib"));
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class P { }\n");
        File.WriteAllText(Path.Combine(_root, "src", "lib", "Util.cs"), "static class U { }\n");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "# notes\n```\nfenced\n```\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 2, 0, 3, 4]);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Find_ResolvesFilesAndFoldersUnderTheRepository_AndIgnoresTheRest()
    {
        var found = FileMentions.Find("Look at @src/Program.cs and @src/lib/ then mail user@example.com; also @missing.txt and @../outside and @src/Program.cs.", _root);

        Assert.Equal(["src/Program.cs", "src/lib"], found.Select(m => m.RelativePath));
        Assert.False(found[0].IsDirectory);
        Assert.True(found[1].IsDirectory);
    }

    [Fact]
    public void Find_IgnoresMessagesWithoutAtAndUnknownRoots()
    {
        Assert.Empty(FileMentions.Find("plain text", _root));
        Assert.Empty(FileMentions.Find("@src/Program.cs", null));
        Assert.Empty(FileMentions.Find("@src/Program.cs", Path.Combine(_root, "nope")));
        Assert.Empty(FileMentions.Find("@.", _root));
    }

    [Fact]
    public void Expand_AttachesFencedContentAfterTheTypedText_WithABoundaryNotice()
    {
        var expansion = FileMentions.Expand("Explain @src/Program.cs and @notes.md", _root);

        Assert.True(expansion.Any);
        Assert.StartsWith("Explain @src/Program.cs and @notes.md", expansion.LaneMessage);
        Assert.Contains(FileMentions.Boundary, expansion.LaneMessage);
        Assert.Contains("```csharp src/Program.cs\nclass P { }\n```", expansion.LaneMessage);
        // A file containing a fence gets a longer fence so it cannot close early.
        Assert.Contains("````markdown notes.md\n# notes\n```\nfenced\n```\n````", expansion.LaneMessage);
        Assert.Empty(expansion.Notes);
    }

    [Fact]
    public void Expand_ListsFolders_SkipsBinaries_AndLeavesUnmentionedTextAlone()
    {
        var expansion = FileMentions.Expand("See @src/ and @blob.bin", _root);

        Assert.Contains("src/ (folder listing)", expansion.LaneMessage);
        Assert.Contains("lib/\nProgram.cs", expansion.LaneMessage.Replace("\r", ""));
        Assert.DoesNotContain("```text blob.bin", expansion.LaneMessage);
        Assert.Contains(expansion.Notes, n => n.Contains("blob.bin") && n.Contains("binary"));

        var untouched = FileMentions.Expand("nothing here", _root);
        Assert.False(untouched.Any);
        Assert.Equal("nothing here", untouched.LaneMessage);
    }

    [Fact]
    public void Expand_TruncatesLargeFiles_AndStopsAtTheTotalBudget()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', FileMentions.MaxFileCharacters + 500));
        var expansion = FileMentions.Expand("@big.txt", _root);
        Assert.Contains("big.txt (truncated)", expansion.LaneMessage);
        Assert.Contains("[truncated]", expansion.LaneMessage);
        Assert.True(expansion.LaneMessage.Length < FileMentions.MaxFileCharacters + 1_000);

        for (var i = 0; i < 4; i++) File.WriteAllText(Path.Combine(_root, $"part{i}.txt"), new string('y', FileMentions.MaxFileCharacters));
        var budget = FileMentions.Expand("@part0.txt @part1.txt @part2.txt @part3.txt", _root);
        Assert.Contains(budget.Notes, n => n.Contains("total size limit"));
    }
}

public sealed class TranscriptExporterTests
{
    [Fact]
    public void ToMarkdown_RendersRolesToolsAndResults_WithoutReasoningByDefault()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Text(ChatRole.User, "Fix the build"),
            new(ChatRole.Assistant, [new ReasoningPart("thinking hard"), new TextPart("Looking."), new ToolUsePart("t1", "shell.exec", new JsonObject { ["command"] = "dotnet build" })]),
            new(ChatRole.User, [new ToolResultPart("t1", "Build succeeded.")]),
            ChatMessage.Text(ChatRole.Assistant, "Done."),
        };

        var markdown = TranscriptExporter.ToMarkdown(messages, "Build fix", exportedUtc: new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("# Build fix\nExported 2026-10-15 12:00 UTC · 4 messages", markdown.Replace("\r\n", "\n"));
        Assert.Contains("## You\n\nFix the build", markdown.Replace("\r\n", "\n"));
        Assert.Contains("**Tool call** `shell.exec`", markdown);
        Assert.Contains("\"command\": \"dotnet build\"", markdown.Replace("\\u0022", "\""));
        Assert.Contains("## Tool results", markdown);
        Assert.Contains("Build succeeded.", markdown);
        Assert.DoesNotContain("thinking hard", markdown);
        Assert.Contains("thinking hard", TranscriptExporter.ToMarkdown(messages, includeReasoning: true));
    }

    [Fact]
    public void SuggestFileName_IsPathSafeAndDated()
    {
        var name = TranscriptExporter.SuggestFileName("Fix: the build / again!", new DateTime(2026, 10, 15, 9, 5, 0, DateTimeKind.Utc));
        Assert.Equal("fix-the-build-again-20261015-0905.md", name);
        Assert.StartsWith("session-", TranscriptExporter.SuggestFileName(null));
    }
}
