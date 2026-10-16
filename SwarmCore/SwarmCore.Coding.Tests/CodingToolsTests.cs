using System.Diagnostics;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

// One test here sets the process-wide GIT_CEILING_DIRECTORIES, so the class runs alone with the other
// process-environment tests instead of beside classes that start git in parallel.
[Collection(GitProcessEnvironmentCollection.Name)]
public sealed class CodingToolsTests : IDisposable
{
    private readonly string _root;
    private readonly SandboxPathResolver _sandbox;

    public CodingToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "coding-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sandbox = new SandboxPathResolver(Options.Create(new SandboxOptions { Root = _root }));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { /* best effort */ }
    }

    private static async Task<TOut> Call<TIn, TOut>(ToolDescriptor d, TIn input) where TIn : notnull
        => (TOut)(await d.Invoke(input, CancellationToken.None))!;

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private Task<EditFileTool.Output> Edit(string path, string old, string @new, bool all = false) =>
        Call<EditFileTool.Input, EditFileTool.Output>(new EditFileTool(_sandbox).Descriptor,
            new() { Path = path, OldText = old, NewText = @new, ReplaceAll = all });

    // ---- edit ----

    [Fact]
    public async Task Edit_replaces_exactly_once()
    {
        var p = Write("a.txt", "one\ntwo\nthree\n");
        var r = await Edit("a.txt", "two", "TWO");
        Assert.True(r.Succeeded);
        Assert.Equal(1, r.Replacements);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(p));
        Assert.Contains("-two", r.Preview);
        Assert.Contains("+TWO", r.Preview);
    }

    [Fact]
    public async Task Edit_rejects_not_found_with_hint()
    {
        Write("a.txt", "alpha beta gamma delta\n");
        var r = await Edit("a.txt", "alpha beta gamma delta XYZ", "x");
        Assert.False(r.Succeeded);
        Assert.StartsWith("old_text not found; it must match exactly including whitespace", r.Error);
        Assert.Contains("1: alpha beta gamma delta", r.Error);
    }

    [Fact]
    public async Task Edit_rejects_ambiguous_match()
    {
        var p = Write("a.txt", "x\nx\n");
        var r = await Edit("a.txt", "x", "y");
        Assert.False(r.Succeeded);
        Assert.Contains("matched 2 times", r.Error);
        Assert.Equal("x\nx\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Edit_replace_all()
    {
        var p = Write("a.txt", "x\nx\nx\n");
        var r = await Edit("a.txt", "x", "y", all: true);
        Assert.True(r.Succeeded);
        Assert.Equal(3, r.Replacements);
        Assert.Equal("y\ny\ny\n", File.ReadAllText(p));
    }

    [Fact]
    public async Task Edit_preserves_crlf_and_bom_absence()
    {
        var p = Path.Combine(_root, "crlf.txt");
        File.WriteAllBytes(p, System.Text.Encoding.UTF8.GetBytes("a\r\nb\r\nc\r\n"));
        var r = await Edit("crlf.txt", "b\nc", "B\nC\nD");
        Assert.True(r.Succeeded);
        var bytes = File.ReadAllBytes(p);
        Assert.Equal("a\r\nB\r\nC\r\nD\r\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public async Task Edit_identical_text_throws()
    {
        Write("a.txt", "x");
        await Assert.ThrowsAsync<ArgumentException>(() => Edit("a.txt", "x", "x"));
    }

    // ---- grep ----

    private Task<GrepTool.Output> Grep(GrepTool.Input input) =>
        Call<GrepTool.Input, GrepTool.Output>(new GrepTool(_sandbox).Descriptor, input);

    [Fact]
    public async Task Grep_finds_matches_with_context()
    {
        Write("src/a.cs", "l1\nl2\nneedle here\nl4\nl5\n");
        var r = await Grep(new() { Pattern = "needle", ContextLines = 1 });
        var m = Assert.Single(r.Matches);
        Assert.Equal("src/a.cs", m.Path);
        Assert.Equal(3, m.Line);
        Assert.Equal(["l2"], m.Before);
        Assert.Equal(["l4"], m.After);
        Assert.Equal(1, r.TotalMatches);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Grep_respects_glob_skips_bin_obj_and_binary()
    {
        Write("a.cs", "needle\n");
        Write("b.txt", "needle\n");
        Write("bin/c.cs", "needle\n");
        Write("obj/d.cs", "needle\n");
        Write("sub/e.razor", "needle\n");
        File.WriteAllBytes(Path.Combine(_root, "f.cs"), [(byte)'n', 0, (byte)'e']);

        var all = await Grep(new() { Pattern = "needle" });
        Assert.Equal(["a.cs", "b.txt", "sub/e.razor"], all.Matches.Select(m => m.Path).Order().ToArray());

        var cs = await Grep(new() { Pattern = "needle", Glob = "*.cs" });
        Assert.Equal(["a.cs"], cs.Matches.Select(m => m.Path).ToArray());

        var razor = await Grep(new() { Pattern = "needle", Glob = "**/*.razor" });
        Assert.Equal(["sub/e.razor"], razor.Matches.Select(m => m.Path).ToArray());
    }

    [Fact]
    public async Task Grep_fixed_string_case_insensitive_and_max()
    {
        Write("a.txt", "A.B\naxb\nA.b\n");
        var literal = await Grep(new() { Pattern = "a.b", FixedString = true, CaseInsensitive = true });
        Assert.Equal(2, literal.TotalMatches);
        var capped = await Grep(new() { Pattern = "a", CaseInsensitive = true, MaxResults = 1 });
        Assert.Single(capped.Matches);
        Assert.True(capped.Truncated);
        Assert.Equal(3, capped.TotalMatches);
    }

    // ---- glob ----

    [Fact]
    public async Task Glob_matches_double_star_patterns()
    {
        Write("App.csproj", "");
        Write("Lib/Lib.csproj", "");
        Write("Lib/Deep/Tool1.cs", "");
        Write("Lib/Deep/Other.cs", "");
        Write("Lib/bin/skip.csproj", "");
        var tool = new GlobTool(_sandbox).Descriptor;

        var proj = await Call<GlobTool.Input, GlobTool.Output>(tool, new() { Pattern = "**/*.csproj" });
        Assert.Equal(["App.csproj", "Lib/Lib.csproj"], proj.Files.Order().ToArray());
        Assert.Equal(2, proj.Total);

        var t = await Call<GlobTool.Input, GlobTool.Output>(tool, new() { Pattern = "Lib/**/Tool*.cs" });
        Assert.Equal(["Lib/Deep/Tool1.cs"], t.Files.ToArray());

        var top = await Call<GlobTool.Input, GlobTool.Output>(tool, new() { Pattern = "*.csproj" });
        Assert.Equal(["App.csproj"], top.Files.ToArray());
    }

    // ---- git ----

    private async Task GitInit()
    {
        foreach (var args in new[]
        {
            new[] { "init" }, new[] { "config", "user.name", "Test" }, new[] { "config", "user.email", "t@example.com" },
            new[] { "config", "commit.gpgsign", "false" },
        })
        {
            var r = await GitProcess.RunAsync(_root, args, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(0, r.ExitCode);
        }
    }

    [GitFact]
    public async Task Git_status_diff_commit_roundtrip()
    {
        await GitInit();
        Write("a.txt", "hello\n");

        var status = new GitStatusTool(_sandbox).Descriptor;
        var s1 = await Call<GitStatusTool.Input, GitStatusTool.Output>(status, new());
        Assert.Null(s1.Error);
        Assert.False(s1.IsClean);
        Assert.Contains(s1.Entries, e => e.Status == "??" && e.Path == "a.txt");
        Assert.False(string.IsNullOrEmpty(s1.Branch));

        var commit = new GitCommitTool(_sandbox).Descriptor;
        var c1 = await Call<GitCommitTool.Input, GitCommitTool.Output>(commit, new() { Message = "first commit", Paths = ["a.txt"] });
        Assert.Null(c1.Error);
        Assert.Equal(40, c1.CommitSha!.Length);
        Assert.Contains("first commit", c1.Summary);

        var s2 = await Call<GitStatusTool.Input, GitStatusTool.Output>(status, new());
        Assert.True(s2.IsClean);

        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello\nworld\n");
        var diff = new GitDiffTool(_sandbox).Descriptor;
        var d1 = await Call<GitDiffTool.Input, GitDiffTool.Output>(diff, new());
        Assert.Null(d1.Error);
        Assert.Contains("+world", d1.Diff);
        var stat = await Call<GitDiffTool.Input, GitDiffTool.Output>(diff, new() { Stat = true });
        Assert.Contains("a.txt", stat.Diff);
        Assert.DoesNotContain("+world", stat.Diff);
        var small = await Call<GitDiffTool.Input, GitDiffTool.Output>(diff, new() { MaxBytes = 20 });
        Assert.True(small.Truncated);

        var c2 = await Call<GitCommitTool.Input, GitCommitTool.Output>(commit,
            new() { Message = "second", Paths = ["a.txt"] });
        Assert.Null(c2.Error);
        Assert.NotEqual(c1.CommitSha, c2.CommitSha);
        var d2 = await Call<GitDiffTool.Input, GitDiffTool.Output>(diff, new() { Ref = "HEAD~1" });
        Assert.Contains("+world", d2.Diff);

        var nothing = await Call<GitCommitTool.Input, GitCommitTool.Output>(commit, new() { Message = "empty" });
        Assert.NotNull(nothing.Error);
    }

    [GitFact]
    public async Task Git_log_lists_commits_newest_first()
    {
        await GitInit();
        var commit = new GitCommitTool(_sandbox).Descriptor;

        Write("a.txt", "one\n");
        var c1 = await Call<GitCommitTool.Input, GitCommitTool.Output>(commit, new() { Message = "first commit", Paths = ["a.txt"] });
        Assert.Null(c1.Error);
        Write("b.txt", "two\n");
        var c2 = await Call<GitCommitTool.Input, GitCommitTool.Output>(commit, new() { Message = "second commit", Paths = ["b.txt"] });
        Assert.Null(c2.Error);

        var log = new GitLogTool(_sandbox).Descriptor;
        var r = await Call<GitLogTool.Input, GitLogTool.Output>(log, new());
        Assert.Null(r.Error);
        Assert.True(r.Succeeded);
        Assert.Equal(2, r.Total);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal("second commit", r.Entries[0].Subject);
        Assert.Equal("first commit", r.Entries[1].Subject);
        Assert.Equal(c2.CommitSha, r.Entries[0].Sha);
        Assert.Equal(c1.CommitSha, r.Entries[1].Sha);
        Assert.Equal(40, r.Entries[0].Sha.Length);
        Assert.StartsWith(r.Entries[0].ShortSha, r.Entries[0].Sha);
        Assert.Equal("Test", r.Entries[0].AuthorName);
        Assert.True(DateTimeOffset.TryParse(r.Entries[0].Date, null, System.Globalization.DateTimeStyles.RoundtripKind, out _));

        var one = await Call<GitLogTool.Input, GitLogTool.Output>(log, new() { MaxCount = 1 });
        Assert.Equal("second commit", Assert.Single(one.Entries).Subject);

        var onlyB = await Call<GitLogTool.Input, GitLogTool.Output>(log, new() { Paths = ["b.txt"] });
        Assert.Equal("second commit", Assert.Single(onlyB.Entries).Subject);

        var byAuthor = await Call<GitLogTool.Input, GitLogTool.Output>(log, new() { Author = "nobody-matches" });
        Assert.Null(byAuthor.Error);
        Assert.Empty(byAuthor.Entries);

        var bad = await Call<GitLogTool.Input, GitLogTool.Output>(log, new() { Ref = "--output=x" });
        Assert.NotNull(bad.Error);
        Assert.False(bad.Succeeded);
    }

    [GitFact]
    public async Task Git_status_outside_repo_returns_error_not_throw()
    {
        // Bound the repo search so a temp dir inside some parent repo cannot satisfy it.
        var previousCeiling = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", Path.GetDirectoryName(_root));
        try
        {
            var s = await Call<GitStatusTool.Input, GitStatusTool.Output>(new GitStatusTool(_sandbox).Descriptor, new());
            Assert.NotNull(s.Error);
        }
        finally { Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", previousCeiling); }
    }
}
