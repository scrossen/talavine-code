using SwarmCore.Coding.Files;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public class NativeAttachmentTests
{
    private static NativeLaneTurn Turn() => new("session", "Inspect the image", new("native", "CodexNative", "codex-cli:default"), Path.GetTempPath(), "test", false)
    { Attachments = [new("id", "image.png", Path.Combine(Path.GetTempPath(), "attached files", "image.png"), "image/png", 10, "hash")] };

    [Theory]
    [InlineData(null)]
    [InlineData("previous-session")]
    public void Codex_ReceivesImageOnNewAndResumedTurns(string? resume)
    {
        var turn = Turn();
        var info = CodexNativeLaneRunner.BuildStartInfo("codex.exe", turn, resume, new());
        var args = info.ArgumentList.ToList();
        Assert.Equal(turn.Attachments[0].LocalPath, args[args.IndexOf("--image") + 1]);
    }

    [Fact]
    public void ClaudeAndGemini_GrantOnlyAttachmentDirectories()
    {
        var turn = Turn();
        var folder = Path.GetDirectoryName(turn.Attachments[0].LocalPath);
        var claude = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", turn, null).ArgumentList.ToList();
        var gemini = GeminiCliNativeLaneRunner.BuildStartInfo("gemini.exe", turn, null, null, new()).ArgumentList.ToList();
        Assert.Equal(folder, claude[claude.IndexOf("--add-dir") + 1]);
        Assert.Equal(folder, gemini[gemini.IndexOf("--include-directories") + 1]);
    }

    [Fact]
    public void ResumedPrompt_KeepsSourceBoundaryAndAttachmentPaths()
    {
        var turn = Turn();
        var text = NativeLaneSupport.BuildPrompt(turn, false, null, "CLAUDE.md");
        Assert.Contains(CodingAttachments.EvidenceBoundary, text);
        Assert.Contains("attached files", text);
        var gemini = NativeLaneSupport.BuildPrompt(turn, false, null, "GEMINI.md");
        Assert.Contains("@\"", gemini);
    }
}
