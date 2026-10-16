using System.Diagnostics;
using SwarmCode.Hosting.Improvement;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Tests;

public class SwarmCodeRebuildTests : TempDirTest
{
    private static async Task<string> Git(string repo, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com" }.Concat(args)) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}{stdout}");
        return stdout.Trim();
    }

    private async Task<string> CheckoutAsync()
    {
        var repo = Path.Combine(Tmp, "source");
        Directory.CreateDirectory(Path.Combine(repo, "SwarmCode.Hosting"));
        Directory.CreateDirectory(Path.Combine(repo, "SwarmCore", "SwarmCore.Coding"));
        await File.WriteAllTextAsync(Path.Combine(repo, "SwarmCode.Hosting", "SwarmCode.Hosting.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(repo, "SwarmCore", "SwarmCore.Coding", "SwarmCore.Coding.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "b.txt"), "b\n");
        await Git(repo, "init", "-q", "-b", "main");
        // MergeVerifiedAsync may create a merge commit; the fixture must not
        // depend on the developer's global Git identity being configured.
        await Git(repo, "config", "user.name", "Test");
        await Git(repo, "config", "user.email", "test@example.com");
        await Git(repo, "config", "core.autocrlf", "false");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "initial");
        return repo;
    }

    /// <summary>A verified item whose branch (from HEAD) changes one file; the worktree lives under the data directory like a cycle's.</summary>
    private async Task<SelfImprovementItem> VerifiedAsync(ISelfImprovementBacklog backlog, string repo, string name, string file, string content)
    {
        var worktree = Path.Combine(Paths.WorktreesDirectory, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        var branch = "swarmcode/self-improvement-" + name;
        await Git(repo, "worktree", "add", "-q", "-b", branch, worktree, "HEAD");
        await File.WriteAllTextAsync(Path.Combine(worktree, file), content);
        await Git(worktree, "add", "-A");
        await Git(worktree, "commit", "-q", "-m", "fix " + name);
        var commit = await Git(worktree, "rev-parse", "HEAD");
        var item = new SelfImprovementItem
        {
            Id = name, Kind = SelfImprovementKind.Defect, Title = "Fix " + name, Fingerprint = "fp-" + name, Details = "details",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow, Stage = SelfImprovementStage.Verified, Attempts = 1,
            LastAttemptUtc = DateTime.UtcNow, Branch = branch, Commit = commit, WorktreePath = worktree,
        };
        await backlog.UpsertByFingerprintAsync(item.Fingerprint, () => item, _ => null);
        return item;
    }

    [Fact]
    public async Task Verified_branches_merge_in_turn_and_a_conflicting_one_is_left_for_the_person()
    {
        var repo = await CheckoutAsync();
        var backlog = new InMemorySelfImprovementBacklog();
        var first = await VerifiedAsync(backlog, repo, "one", "a.txt", "a fixed\n");
        var second = await VerifiedAsync(backlog, repo, "two", "b.txt", "b fixed\n");
        var conflicting = await VerifiedAsync(backlog, repo, "three", "a.txt", "a conflicting\n");
        var rebuild = new SwarmCodeRebuild(Paths, () => new SelfImprovementOptions { RepositoryRoot = repo }, backlog);

        Assert.Equal(3, (await rebuild.ShippableAsync()).Count);
        var outcomes = await rebuild.MergeVerifiedAsync(repo);
        Assert.Equal([true, true, false], outcomes.Select(o => o.Merged));
        // Trimmed: git's autocrlf may rewrite line endings on checkout.
        Assert.Equal("a fixed", (await File.ReadAllTextAsync(Path.Combine(repo, "a.txt"))).Trim());
        Assert.Equal("b fixed", (await File.ReadAllTextAsync(Path.Combine(repo, "b.txt"))).Trim());
        Assert.Equal("", await Git(repo, "status", "--porcelain")); // the aborted merge left the checkout clean
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));

        Assert.Equal(SelfImprovementStage.Merged, (await backlog.GetAsync(first.Id))!.Stage);
        Assert.Equal(SelfImprovementStage.Merged, (await backlog.GetAsync(second.Id))!.Stage);
        var kept = (await backlog.GetAsync(conflicting.Id))!;
        Assert.Equal(SelfImprovementStage.Verified, kept.Stage);
        Assert.Contains("Not merged", kept.Note);
        // Merged worktrees and branches are gone; the conflicting one stays for the person.
        Assert.False(Directory.Exists(first.WorktreePath));
        Assert.True(Directory.Exists(conflicting.WorktreePath));
        var branches = await Git(repo, "branch", "--list", "swarmcode/*");
        Assert.DoesNotContain("self-improvement-one", branches);
        Assert.Contains("self-improvement-three", branches);
        Assert.Single(await rebuild.ShippableAsync());
    }

    [Fact]
    public async Task The_plan_follows_where_SwarmCode_runs_from_and_the_helper_script_waits_builds_and_restarts()
    {
        var repo = await CheckoutAsync();
        var exe = Path.Combine(repo, "SwarmCode.App", "bin", "Debug", "net11.0-windows10.0.19041.0", "win-x64", "SwarmCode.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        await File.WriteAllTextAsync(exe, "");
        var backlog = new InMemorySelfImprovementBacklog();
        SelfImprovementOptions Options() => new() { RepositoryRoot = repo };

        var bin = new SwarmCodeRebuild(Paths, Options, backlog, executablePath: exe, baseDirectory: Path.GetDirectoryName(exe)).Plan();
        Assert.True(bin.Possible, bin.Message);
        Assert.Equal("bin", bin.Layout);
        Assert.Equal("dotnet", bin.BuildCommand);
        Assert.Contains("-c", bin.BuildArguments);
        Assert.Contains(Path.Combine(repo, "SwarmCode.App", "SwarmCode.App.csproj"), bin.BuildArguments);

        var scriptExe = Path.Combine(repo, ".build", "swarmcode-debug", "Debug", "net11.0-windows10.0.19041.0", "win-arm64", "SwarmCode.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptExe)!);
        await File.WriteAllTextAsync(scriptExe, "");
        var script = new SwarmCodeRebuild(Paths, Options, backlog, executablePath: scriptExe, baseDirectory: Path.GetDirectoryName(scriptExe)).Plan();
        Assert.True(script.Possible, script.Message);
        Assert.Equal("script", script.Layout);
        Assert.Equal("pwsh", script.BuildCommand);
        Assert.Equal("win-arm64", script.BuildArguments[^1]);

        var elsewhere = Path.Combine(Tmp, "installed", "SwarmCode.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        await File.WriteAllTextAsync(elsewhere, "");
        var outside = new SwarmCodeRebuild(Paths, Options, backlog, executablePath: elsewhere, baseDirectory: Path.GetDirectoryName(elsewhere)).Plan();
        Assert.False(outside.Possible);
        Assert.Contains("outside the source checkout", outside.Message);
        Assert.False(new SwarmCodeRebuild(Paths, Options, backlog, executablePath: exe, baseDirectory: Path.GetDirectoryName(exe)).Plan(isWindows: false).Possible);
        Assert.False(new SwarmCodeRebuild(Paths, () => new SelfImprovementOptions { RepositoryRoot = Path.Combine(Tmp, "missing") }, backlog, executablePath: exe, baseDirectory: Path.GetDirectoryName(exe)).Plan().Possible);

        var rebuild = new SwarmCodeRebuild(Paths, Options, backlog, executablePath: exe, baseDirectory: Path.GetDirectoryName(exe));
        var path = rebuild.WriteRelaunchScript(bin, 4242, ["Fix one"]);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("Wait-Process -Id 4242", text);
        Assert.Contains("'dotnet' 'build'", text);
        Assert.Contains("Start-Process -FilePath '" + exe + "'", text);
        Assert.Contains(repo, text);
        var status = rebuild.ReadStatus()!;
        Assert.True(status.InProgress);
        Assert.Equal(["Fix one"], status.Merged);
        Assert.Contains("dotnet build", status.Command);
        Assert.Equal(rebuild.LogPath, status.LogPath);
    }
}
