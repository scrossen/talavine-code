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

    [Fact]
    public async Task Mac_plan_rebuilds_only_the_native_debug_bundle_in_the_source_checkout()
    {
        var repo = await CheckoutAsync();
        var exe = Path.Combine(repo, "SwarmCode.App", "bin", "Debug", "net11.0-macos", "osx-arm64", "Talavine Code.app", "Contents", "MacOS", "SwarmCode.App");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        await File.WriteAllTextAsync(exe, "");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(repo, "scripts", "build-swarmcode-macos.sh"), "exit 0");
        var rebuild = new SwarmCodeRebuild(Paths, () => new() { RepositoryRoot = repo }, new InMemorySelfImprovementBacklog(),
            executablePath: exe, baseDirectory: Path.Combine(Path.GetDirectoryName(exe)!, "..", "MonoBundle"));
        var plan = rebuild.Plan(isWindows: false);
        Assert.True(plan.Possible, plan.Message);
        Assert.Equal("macos-appkit", plan.Layout);
        Assert.Equal("/bin/bash", plan.BuildCommand);
        Assert.Equal([Path.Combine(repo, "scripts", "build-swarmcode-macos.sh")], plan.BuildArguments);
        var path = rebuild.WriteRelaunchScript(plan, 4242, []);
        Assert.EndsWith("relaunch.sh", path);
        Assert.Contains(Paths.RootDirectory, await File.ReadAllTextAsync(path));
        File.Delete(Path.Combine(repo, "scripts", "build-swarmcode-macos.sh"));
        Assert.False(rebuild.Plan(isWindows: false).Possible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Native_helper_executes_the_build_and_only_relaunches_after_success(int buildExit)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Tmp, "native path with ' quote");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "relaunched.txt");
        var exe = Path.Combine(root, "native-app");
        await File.WriteAllTextAsync(exe, "#!/bin/bash\nprintf '%s' \"$SWARMCODE_HOME\" > \"$(dirname \"$0\")/relaunched.txt\"\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var build = Path.Combine(root, "build.sh");
        await File.WriteAllTextAsync(build, $"exit {buildExit}\n");
        using var departed = Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { "-c", "exit 0" } })!;
        await departed.WaitForExitAsync();
        var rebuild = new SwarmCodeRebuild(Paths, () => new(), new InMemorySelfImprovementBacklog());
        var plan = new RebuildPlan(true, "fixture", root, exe, "macos-appkit", "/bin/bash", [build]);
        var script = rebuild.WriteRelaunchScript(plan, departed.Id, [], waitSeconds: 1);
        using var helper = Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { script } })!;
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(buildExit, helper.ExitCode);
        Assert.Equal(buildExit, rebuild.ReadStatus()!.BuildExitCode);
        Assert.False(rebuild.ReadStatus()!.InProgress);
        if (buildExit == 0)
        {
            for (var attempt = 0; attempt < 20 && !File.Exists(marker); attempt++) await Task.Delay(50);
            Assert.Equal(Paths.RootDirectory, await File.ReadAllTextAsync(marker));
        }
        else Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Installed_mac_plan_requires_explicit_source_and_preserves_exact_bundle_target_with_isolated_staging()
    {
        var repo = await CheckoutAsync();
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(repo, "scripts", "build-swarmcode-macos.sh"), "exit 0");
        var bundle = Path.Combine(Tmp, "installed", "Talavine Code.app");
        var exe = Path.Combine(bundle, "Contents", "MacOS", "SwarmCode.App");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        await File.WriteAllTextAsync(exe, "fixture");
        await File.WriteAllTextAsync(Path.Combine(bundle, "Contents", "Info.plist"), "fixture");
        var rebuild = new SwarmCodeRebuild(Paths, () => new() { RepositoryRoot = repo }, new InMemorySelfImprovementBacklog(),
            executablePath: exe, baseDirectory: Path.Combine(bundle, "Contents", "MonoBundle"));
        var plan = rebuild.Plan(isWindows: false);
        Assert.True(plan.Possible, plan.Message); Assert.Equal(bundle, plan.InstallBundlePath);
        Assert.StartsWith(Paths.SelfImprovementDirectory, plan.StagingArtifactsPath);
        var script = await File.ReadAllTextAsync(rebuild.WriteRelaunchScript(plan, 4242, []));
        Assert.Contains("codesign --verify --deep --strict", script);
        Assert.Contains("Signed bundle identity changed; installation refused", script);
        Assert.Contains("mv \"$backup\" \"$target\"", script);
        Assert.Contains("SWARMCODE_ARTIFACTS_PATH", script);
        Assert.Contains("CFBundleShortVersionString", script);
        Assert.Contains("export VERSION=", script);
        Assert.Contains("Installed version changed or candidate version differs", script);
        Assert.Contains("artifactHashes", script);
        Assert.False(new SwarmCodeRebuild(Paths, () => new(), new InMemorySelfImprovementBacklog(),
            executablePath: exe, baseDirectory: Path.Combine(bundle, "Contents", "MonoBundle")).Plan(isWindows: false).Possible);
        File.Delete(Path.Combine(bundle, "Contents", "Info.plist"));
        Assert.False(rebuild.Plan(isWindows: false).Possible);
    }

    [Fact]
    public async Task Automatic_installation_selects_only_requested_repairs_and_reuses_already_merged_exact_commits()
    {
        var repo = await CheckoutAsync(); var backlog = new InMemorySelfImprovementBacklog();
        var requested = await VerifiedAsync(backlog, repo, "requested", "a.txt", "a fixed\n");
        var unrelated = await VerifiedAsync(backlog, repo, "unrelated", "b.txt", "b fixed\n");
        var rebuild = new SwarmCodeRebuild(Paths, () => new() { RepositoryRoot = repo }, backlog);
        var selected = new HashSet<string> { requested.Id };
        var merged = Assert.Single(await rebuild.MergeVerifiedAsync(repo, itemIds: selected));
        Assert.True(merged.Merged);
        Assert.Equal(SelfImprovementStage.Verified, (await backlog.GetAsync(unrelated.Id))!.Stage);
        Assert.Equal("b", (await File.ReadAllTextAsync(Path.Combine(repo, "b.txt"))).Trim());
        var again = Assert.Single(await rebuild.MergeVerifiedAsync(repo, itemIds: selected));
        Assert.True(again.Merged); Assert.Contains("already merged", again.Message);
        await File.WriteAllTextAsync(Path.Combine(repo, "b.txt"), "unrelated private work");
        Assert.False(Assert.Single(await rebuild.MergeVerifiedAsync(repo, itemIds: selected)).Merged);
        Assert.Equal("unrelated private work", await File.ReadAllTextAsync(Path.Combine(repo, "b.txt")));
    }

    [Fact]
    public async Task Automatic_installation_refuses_a_changed_source_head_or_a_branch_changed_after_verification()
    {
        var repo = await CheckoutAsync(); var backlog = new InMemorySelfImprovementBacklog();
        var verified = await VerifiedAsync(backlog, repo, "repair", "a.txt", "a fixed\n");
        var rebuild = new SwarmCodeRebuild(Paths, () => new() { RepositoryRoot = repo }, backlog);
        await File.WriteAllTextAsync(Path.Combine(repo, "unrelated.txt"), "new unrelated source");
        await Git(repo, "add", "unrelated.txt"); await Git(repo, "commit", "-q", "-m", "Unrelated newer work");
        var before = await Git(repo, "rev-parse", "HEAD");
        var result = Assert.Single(await rebuild.MergeVerifiedAsync(repo, itemIds: new HashSet<string> { verified.Id }));
        Assert.False(result.Merged); Assert.Equal(before, await Git(repo, "rev-parse", "HEAD"));
        Assert.Equal("a", (await File.ReadAllTextAsync(Path.Combine(repo, "a.txt"))).Trim());
        await File.WriteAllTextAsync(Path.Combine(verified.WorktreePath!, "extra.txt"), "unverified change");
        await Git(verified.WorktreePath!, "add", "extra.txt"); await Git(verified.WorktreePath!, "commit", "-q", "-m", "Unverified branch drift");
        result = Assert.Single(await rebuild.MergeVerifiedAsync(repo, itemIds: new HashSet<string> { verified.Id }));
        Assert.False(result.Merged); Assert.Contains("changed after verification", result.Message);
        Assert.Equal(before, await Git(repo, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Automatic_helpers_bind_the_exact_verified_source_and_native_helper_refuses_later_source_drift()
    {
        var repo = await CheckoutAsync();
        var build = Path.Combine(repo, "build.sh");
        await File.WriteAllTextAsync(build, "touch built-marker\n");
        await Git(repo, "add", "build.sh"); await Git(repo, "commit", "-q", "-m", "Fixture build command");
        var backlog = new InMemorySelfImprovementBacklog();
        var item = await VerifiedAsync(backlog, repo, "exact-helper", "a.txt", "a fixed\n");
        await backlog.UpdateAsync(item.Id, fresh => fresh with { Evidence = "Independent regression checks passed.", LastAttemptUtc = DateTime.UtcNow });
        var rebuild = new SwarmCodeRebuild(Paths, () => new() { RepositoryRoot = repo }, backlog);
        Assert.True(Assert.Single(await rebuild.MergeVerifiedAsync(repo, itemIds: new HashSet<string> { item.Id })).Merged);
        var exe = Path.Combine(Tmp, "not-launched");
        var windows = rebuild.WriteRelaunchScript(new(true, "fixture", repo, exe, "bin", "dotnet", ["build"]), 4242, []);
        var ps = await File.ReadAllTextAsync(windows);
        Assert.Contains("$repairHead -ne '" + item.Commit + "'", ps);
        Assert.Contains("$postBuildHead.Trim() -ne $repairHead", ps);
        Assert.Contains("--porcelain --untracked-files=all", ps);
        Assert.Contains("Stop-RepairSource", ps);
        if (OperatingSystem.IsWindows()) return;
        using var departed = Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { "-c", "exit 0" } })!;
        await departed.WaitForExitAsync();
        var native = rebuild.WriteRelaunchScript(new(true, "fixture", repo, exe, "macos-appkit", "/bin/bash", [build]), departed.Id, [], waitSeconds: 1);
        var sh = await File.ReadAllTextAsync(native);
        Assert.Contains("\"$repair_head\" == '" + item.Commit + "'", sh);
        await File.WriteAllTextAsync(Path.Combine(repo, "b.txt"), "A newer clean committed tree");
        await Git(repo, "add", "b.txt"); await Git(repo, "commit", "-q", "-m", "New source after helper preparation");
        using var helper = rebuild.StartRelaunchTracked(native);
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(124, helper.ExitCode); Assert.Equal(124, rebuild.ReadStatus()!.BuildExitCode);
        Assert.False(rebuild.ReadStatus()!.InProgress); Assert.False(File.Exists(Path.Combine(repo, "built-marker")));
        if (File.Exists(SwarmCodeRebuild.FindPowerShell()))
        {
            windows = rebuild.WriteRelaunchScript(new(true, "fixture", repo, exe, "bin", "dotnet", ["build"]), departed.Id, [], waitSeconds: 1);
            using var windowsHelper = rebuild.StartRelaunchTracked(windows);
            await windowsHelper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(124, windowsHelper.ExitCode); Assert.Equal(124, rebuild.ReadStatus()!.BuildExitCode);
            Assert.False(rebuild.ReadStatus()!.InProgress); Assert.False(File.Exists(Path.Combine(repo, "built-marker")));
        }
    }

    [Fact]
    public async Task Native_helper_refuses_to_rebuild_a_still_running_app()
    {
        if (OperatingSystem.IsWindows()) return;
        var rebuild = new SwarmCodeRebuild(Paths, () => new(), new InMemorySelfImprovementBacklog());
        var plan = new RebuildPlan(true, "fixture", Tmp, Path.Combine(Tmp, "missing"), "macos-appkit", "/bin/bash", ["-c", "exit 0"]);
        var script = rebuild.WriteRelaunchScript(plan, Environment.ProcessId, [], waitSeconds: 1);
        using var helper = Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { script } })!;
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(125, helper.ExitCode);
        Assert.Equal(125, rebuild.ReadStatus()!.BuildExitCode);
        Assert.False(rebuild.ReadStatus()!.InProgress);
    }
}
