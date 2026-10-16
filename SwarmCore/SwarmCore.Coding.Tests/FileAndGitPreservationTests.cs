using System.Text;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tests;

public sealed class FileAndGitPreservationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coding-preservation-" + Guid.NewGuid().ToString("N"));
    private readonly SandboxPathResolver sandbox;

    public FileAndGitPreservationTests()
    {
        Directory.CreateDirectory(root);
        sandbox = new(Options.Create(new SandboxOptions { Root = root }));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task Editing_and_rewriting_preserves_supported_encoding(string name)
    {
        Encoding encoding = name switch
        {
            "utf8-bom" => new UTF8Encoding(true, true),
            "utf16-le" => new UnicodeEncoding(false, true, true),
            "utf16-be" => new UnicodeEncoding(true, true, true),
            "utf32-le" => new UTF32Encoding(false, true, true),
            _ => new UTF32Encoding(true, true, true)
        };
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes("héllo\r\nworld\r\n")]);
        var read = (ReadFileTool.Output)(await new ReadFileTool(sandbox).Descriptor.Invoke(new ReadFileTool.Input { Path = "file.txt" }, default))!;
        Assert.Equal("héllo\r\nworld\r\n", read.Content);
        var edit = (EditFileTool.Output)(await new EditFileTool(sandbox).Descriptor.Invoke(new EditFileTool.Input
        { Path = "file.txt", OldText = "world", NewText = "planet", ExpectedSha256 = read.Sha256 }, default))!;
        Assert.True(edit.Succeeded, edit.Error);
        byte[] expectedEdit = [.. encoding.GetPreamble(), .. encoding.GetBytes("héllo\r\nplanet\r\n")];
        Assert.Equal(expectedEdit, await File.ReadAllBytesAsync(path));
        await new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = "file.txt", Content = "new é", ExpectedSha256 = edit.Sha256 }, default);
        byte[] expectedWrite = [.. encoding.GetPreamble(), .. encoding.GetBytes("new é")];
        Assert.Equal(expectedWrite, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task Stale_read_rejects_both_edit_and_write_without_touching_user_changes()
    {
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "needle\noriginal");
        var read = (ReadFileTool.Output)(await new ReadFileTool(sandbox).Descriptor.Invoke(new ReadFileTool.Input { Path = "file.txt" }, default))!;
        await File.WriteAllTextAsync(path, "needle\nuser changes");
        var edit = (EditFileTool.Output)(await new EditFileTool(sandbox).Descriptor.Invoke(new EditFileTool.Input
        { Path = "file.txt", OldText = "needle", NewText = "replacement", ExpectedSha256 = read.Sha256 }, default))!;
        Assert.False(edit.Succeeded);
        await Assert.ThrowsAsync<MissionActionSkippedException>(() => new WriteFileTool(sandbox).Descriptor.Invoke(
            new WriteFileTool.Input { Path = "file.txt", Content = "overwrite", ExpectedSha256 = read.Sha256 }, default));
        Assert.Equal("needle\nuser changes", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Earlier_read_is_automatically_guarded_within_the_same_agent_turn()
    {
        var accessor = new AsyncLocalCurrentTurnContextAccessor();
        var previous = accessor.Current;
        accessor.Current = new CurrentTurnContext("preservation-turn", null, null, ToolAccessState: new CurrentTurnToolAccessState());
        try
        {
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "needle\nbase");
            await new ReadFileTool(sandbox).Descriptor.Invoke(new ReadFileTool.Input { Path = "file.txt" }, default);
            await File.WriteAllTextAsync(path, "needle\nuser change");
            var edit = (EditFileTool.Output)(await new EditFileTool(sandbox).Descriptor.Invoke(new EditFileTool.Input
            { Path = "file.txt", OldText = "needle", NewText = "replacement" }, default))!;
            Assert.False(edit.Succeeded);
            await Assert.ThrowsAsync<MissionActionSkippedException>(() => new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = "file.txt", Content = "overwrite" }, default));
            await new ReadFileTool(sandbox).Descriptor.Invoke(new ReadFileTool.Input { Path = "file.txt" }, default);
            await new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = "file.txt", Content = "integrated change" }, default);
            await new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = "file.txt", Content = "next valid write" }, default);
            Assert.Equal("next valid write", await File.ReadAllTextAsync(path));
        }
        finally { accessor.Current = previous; }
    }

    [Fact]
    public async Task Invalid_utf8_and_binary_files_are_unchanged()
    {
        foreach (var bytes in new byte[][] { [0x61, 0xFF, 0x62], [0x61, 0, 0x62] })
        {
            var path = Path.Combine(root, "file.bin");
            await File.WriteAllBytesAsync(path, bytes);
            var edit = (EditFileTool.Output)(await new EditFileTool(sandbox).Descriptor.Invoke(new EditFileTool.Input
            { Path = "file.bin", OldText = "a", NewText = "b" }, default))!;
            Assert.False(edit.Succeeded);
            await Assert.ThrowsAnyAsync<Exception>(() => new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = "file.bin", Content = "text" }, default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task Cancelled_atomic_write_keeps_target_and_removes_temporary_file()
    {
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "original");
        var snapshot = (await AtomicTextFile.ReadAsync(path, default))!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicTextFile.WriteAsync(path, Encoding.UTF8.GetBytes("new"), snapshot.Sha256, cancelled.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task Read_truncation_counts_bytes_without_splitting_utf8_character()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "abcdérest", new UTF8Encoding(false));
        var read = (ReadFileTool.Output)(await new ReadFileTool(sandbox).Descriptor.Invoke(new ReadFileTool.Input { Path = "file.txt", MaxBytes = 5 }, default))!;
        Assert.Equal("abcd", read.Content);
        Assert.True(read.Truncated);
        Assert.Equal(64, read.Sha256.Length);
    }

    [Fact]
    public async Task Missing_guard_cannot_overwrite_existing_file()
    {
        var writer = new WriteFileTool(sandbox).Descriptor;
        await writer.Invoke(new WriteFileTool.Input { Path = "file.txt", Content = "first", ExpectedSha256 = "missing" }, default);
        await Assert.ThrowsAsync<MissionActionSkippedException>(() => writer.Invoke(new WriteFileTool.Input { Path = "file.txt", Content = "second", ExpectedSha256 = "missing" }, default));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(root, "file.txt")));
    }

    [Fact]
    public async Task Competing_atomic_writers_cannot_both_replace_the_same_snapshot()
    {
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "baseline");
        var snapshot = (await AtomicTextFile.ReadAsync(path, default))!;
        async Task<bool> Attempt(string text)
        {
            try { await AtomicTextFile.WriteAsync(path, Encoding.UTF8.GetBytes(text), snapshot.Sha256, default); return true; }
            catch (MissionActionSkippedException) { return false; }
        }
        var results = await Task.WhenAll(Attempt("writer one"), Attempt("writer two"));
        Assert.Single(results, succeeded => succeeded);
        Assert.Contains(await File.ReadAllTextAsync(path), new[] { "writer one", "writer two" });
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private async Task Git(params string[] args)
    {
        var result = await GitProcess.RunAsync(root, args, TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Explicit_commit_excludes_and_preserves_unrelated_staged_and_unstaged_work()
    {
        await Git("init");
        await Git("config", "user.name", "Fixture");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "base");
        await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "base");
        await Git("add", "--", "target.txt", "unrelated.txt");
        await Git("commit", "-m", "baseline");
        await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "agent change");
        await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "staged user change");
        await Git("add", "--", "unrelated.txt");
        await File.AppendAllTextAsync(Path.Combine(root, "unrelated.txt"), " plus unstaged user change");
        await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "user new file");
        var result = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(new GitCommitTool.Input
            { Message = "scoped change", Paths = ["target.txt"] }, default))!;
        Assert.True(result.Succeeded, result.Error);
        var changed = await GitProcess.RunAsync(root, ["diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"], TimeSpan.FromSeconds(30), default);
        Assert.Equal("target.txt", changed.Stdout.Trim());
        var staged = await GitProcess.RunAsync(root, ["show", ":unrelated.txt"], TimeSpan.FromSeconds(30), default);
        Assert.Equal("staged user change", staged.Stdout);
        Assert.Equal("staged user change plus unstaged user change", await File.ReadAllTextAsync(Path.Combine(root, "unrelated.txt")));
        var status = await GitProcess.RunAsync(root, ["status", "--porcelain"], TimeSpan.FromSeconds(30), default);
        Assert.Contains("MM unrelated.txt", status.Stdout);
        Assert.Contains("?? untracked.txt", status.Stdout);
    }

    [Fact]
    public async Task Commit_rejects_implicit_all_directory_and_parent_paths()
    {
        var tool = new GitCommitTool(sandbox).Descriptor;
        foreach (var paths in new List<string>?[] { null, ["."], ["../outside.txt"] })
        {
            var result = (GitCommitTool.Output)(await tool.Invoke(new GitCommitTool.Input { Message = "bad", Paths = paths }, default))!;
            Assert.NotNull(result.Error);
        }
    }
}
