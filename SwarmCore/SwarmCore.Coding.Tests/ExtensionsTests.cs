using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Extensions;

namespace SwarmCore.Coding.Tests;

public sealed class CustomCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-ext-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private string User => Path.Combine(_root, "user");

    public CustomCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".swarmcode", "commands"));
        Directory.CreateDirectory(Path.Combine(Repo, ".claude", "commands"));
        Directory.CreateDirectory(Path.Combine(User, "commands"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Discover_ReadsAllRoots_WithUserThenSwarmCodeThenClaudePrecedence_AndBuiltInsWin()
    {
        Write("repo/.swarmcode/commands/review.md", "---\ndescription: Review the diff\n---\nReview $ARGUMENTS carefully.");
        Write("repo/.claude/commands/review.md", "Claude review body");
        Write("repo/.claude/commands/fix.md", "# Fix an issue\nFix issue $1.");
        Write("user/commands/fix.md", "---\ndescription: \"My fix\"\n---\nUser fix $1");
        Write("repo/.swarmcode/commands/help.md", "shadow a built-in");
        Write("repo/.swarmcode/commands/bad name.md", "invalid name");
        Write("repo/.swarmcode/commands/empty.md", "---\ndescription: nothing\n---\n");
        Write("repo/.swarmcode/commands/huge.md", new string('x', (int)CustomCommandCatalog.MaxFileBytes + 10));

        var commands = CustomCommandCatalog.Discover(Repo, User, ["help", "clear"]);

        Assert.Equal(["fix", "review"], commands.Select(c => c.Name));
        var fix = CustomCommandCatalog.Find(commands, "/FIX")!;
        Assert.Equal(ExtensionSource.User, fix.Source);
        Assert.Equal("My fix", fix.Description);
        var review = CustomCommandCatalog.Find(commands, "review")!;
        Assert.Equal(ExtensionSource.Repository, review.Source);
        Assert.Equal("Review the diff", review.Description);
        Assert.Null(CustomCommandCatalog.Find(commands, "help"));
    }

    [Fact]
    public void Discover_DescriptionFallsBackToFirstLine()
    {
        Write("repo/.claude/commands/fix.md", "# Fix an issue\nFix issue $1.");
        var fix = Assert.Single(CustomCommandCatalog.Discover(Repo, null));
        Assert.Equal("Fix an issue", fix.Description);
        Assert.Equal(ExtensionSource.RepositoryClaude, fix.Source);
    }

    [Theory]
    [InlineData("Review $ARGUMENTS now", "a b  c", "Review a b  c now")]
    [InlineData("Fix $1 then $2 ($3)", "one \"two words\"", "Fix one then two words ()")]
    [InlineData("$1$ARGUMENTS", "x", "xx")]
    [InlineData("Price $10 stays", "a", "Price $10 stays\n\na")]
    [InlineData("No placeholders", "extra text", "No placeholders\n\nextra text")]
    [InlineData("No placeholders", "", "No placeholders")]
    public void Expand_SubstitutesArguments(string body, string args, string expected)
        => Assert.Equal(expected, CustomCommandExpander.Expand(body, args));

    [Fact]
    public void TryParseInvocation_SplitsNameAndArguments()
    {
        Assert.True(CustomCommandCatalog.TryParseInvocation("/Review  the  thing\nmore", out var name, out var args));
        Assert.Equal("review", name);
        Assert.Equal("the  thing\nmore", args);
        Assert.False(CustomCommandCatalog.TryParseInvocation("review", out _, out _));
    }
}

public sealed class CodingSkillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-skills-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private string User => Path.Combine(_root, "user");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Skill(string folder, string name, string description, string body = "Do the thing.")
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n{body}");
    }

    [Fact]
    public void Discover_FindsSkillsInAllRoots_FirstNameWins_AndSkipsSkillsWithoutDescription()
    {
        Skill("user/skills/release", "release", "Cut a release (mine)");
        Skill("repo/.swarmcode/skills/release", "release", "Cut a release (repo)");
        Skill("repo/.swarmcode/skills/db", "db-migrate", "Write a migration");
        Skill("repo/.claude/skills/pdf", "pdf", "Fill PDF forms");
        Directory.CreateDirectory(Path.Combine(Repo, ".claude", "skills", "nofile"));
        Skill("repo/.claude/skills/nodesc", "nodesc", "");

        var skills = CodingSkillCatalog.Discover(Repo, User);

        Assert.Equal(["release", "db-migrate", "pdf"], skills.Select(s => s.Name));
        Assert.Equal("Cut a release (mine)", skills[0].Description);
        Assert.Equal(ExtensionSource.User, skills[0].Source);
        Assert.Equal(ExtensionSource.RepositoryClaude, skills[2].Source);
    }

    [Fact]
    public void Discover_IsBounded()
    {
        for (var i = 0; i < CodingSkillCatalog.MaxSkills + 10; i++)
            Skill($"repo/.swarmcode/skills/s{i:000}", $"s{i:000}", new string('d', 1000));
        var skills = CodingSkillCatalog.Discover(Repo, null);
        Assert.Equal(CodingSkillCatalog.MaxSkills, skills.Count);
        Assert.All(skills, s => Assert.True(s.Description.Length <= CodingSkillCatalog.MaxDescriptionCharacters));
        var index = CodingSkillCatalog.FormatIndex(skills, 2_000)!;
        Assert.True(index.Length <= 2_100);
        Assert.Contains("more skill(s) not listed", index);
        Assert.Contains("skills.read", index);
        Assert.Contains("come from the repository", index);
    }

    [Fact]
    public void Read_TruncatesLargeSkill_AndBoundsFileListing()
    {
        Skill("repo/.swarmcode/skills/big", "big", "Large skill", new string('y', CodingSkillCatalog.MaxSkillCharacters + 500));
        var dir = Path.Combine(Repo, ".swarmcode", "skills", "big");
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        for (var i = 0; i < CodingSkillCatalog.MaxListedFiles + 5; i++) File.WriteAllText(Path.Combine(dir, "scripts", $"f{i:000}.sh"), "echo");
        var content = CodingSkillCatalog.Read(CodingSkillCatalog.Discover(Repo, null), "BIG")!;
        Assert.True(content.Truncated);
        Assert.Equal(CodingSkillCatalog.MaxSkillCharacters, content.Content.Length);
        Assert.True(content.FilesTruncated);
        Assert.Equal(CodingSkillCatalog.MaxListedFiles, content.Files.Count);
        Assert.StartsWith("scripts/", content.Files[0]);
        Assert.Null(CodingSkillCatalog.Read(CodingSkillCatalog.Discover(Repo, null), "missing"));
    }

    [Fact]
    public async Task SkillsReadTool_ReturnsInstructions_AndRelativeFolder()
    {
        Skill("repo/.swarmcode/skills/db", "db-migrate", "Write a migration", "Step one: read schema.md");
        File.WriteAllText(Path.Combine(Repo, ".swarmcode", "skills", "db", "schema.md"), "tables");
        var tool = CodingSkillTools.CreateRead(() => Repo, () => null);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
        var output = (SkillsReadOutput)(await tool.Invoke(new SkillsReadInput { Name = "db-migrate" }, default))!;
        Assert.True(output.Found);
        Assert.Contains("Step one", output.Content);
        Assert.Equal(".swarmcode/skills/db", output.Path);
        Assert.Equal(["schema.md"], output.Files);
        var missing = (SkillsReadOutput)(await tool.Invoke(new SkillsReadInput { Name = "nope" }, default))!;
        Assert.False(missing.Found);
        Assert.Contains("db-migrate", missing.Message);
    }

    [Fact]
    public async Task Injector_ListsSkillsForTheTurnWorkspace_AndNothingWithoutSkills()
    {
        var options = Options.Create(new CodingOptions { UserExtensionsDirectory = User });
        var injector = new CodingSkillsInjector(options);
        Assert.Null(await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.Empty) { WorkspaceRoot = Repo }));
        Skill("repo/.claude/skills/pdf", "pdf", "Fill PDF forms");
        var text = await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.Empty) { WorkspaceRoot = Repo });
        Assert.Contains("- pdf (repository (.claude)): Fill PDF forms", text);
    }
}

public sealed class CodingHookTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-hooks-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");

    public CodingHookTests() => Directory.CreateDirectory(Repo);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static bool Win => OperatingSystem.IsWindows();
    private static string Exit2WithReason(string reason) => Win ? $"echo {reason} 1>&2 & exit /b 2" : $"echo '{reason}' 1>&2; exit 2";
    private static string EchoJson(string json) => Win ? $"echo {json}" : $"echo '{json}'";
    private static string CatStdin => Win ? "findstr \"^\"" : "cat";
    private static string Flood => Win ? "for /l %i in (1,1,3000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" : "yes xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx | head -n 3000";
    private string TouchMarker(string marker) => Win ? $"type nul > \"{marker}\"" : $"touch '{marker}'";

    private static CodingHook Hook(CodingHookEvent evt, string command, string? matcher = null, int seconds = 30, ExtensionSource source = ExtensionSource.User)
        => new(evt, matcher, command, TimeSpan.FromSeconds(seconds), source, "test");

    [Theory]
    [InlineData(null, "shell.exec", true)]
    [InlineData("*", "fs.write_file", true)]
    [InlineData("shell.*", "shell.exec", true)]
    [InlineData("SHELL.EXEC", "shell.exec", true)]
    [InlineData("code.edit_file|fs.write_file", "fs.write_file", true)]
    [InlineData("code.edit_file|fs.write_file", "shell.exec", false)]
    [InlineData("git.?it", "git.commit", false)]
    [InlineData("fs.read_fil?", "fs.read_file", true)]
    [InlineData("shell", "shell.exec", false)]
    public void Matcher_IsAToolIdGlob(string? matcher, string tool, bool expected)
        => Assert.Equal(expected, CodingHookMatcher.Matches(matcher, tool));

    [Fact]
    public void ParseHooksFile_AcceptsFlatAndNestedShapes()
    {
        var json = """
            {
              // comments allowed
              "hooks": {
                "PreToolUse": [
                  { "matcher": "shell.*", "command": "check-shell", "timeout": 5 },
                  { "matcher": "fs.*", "hooks": [ { "type": "command", "command": "check-fs" }, { "command": "" } ] }
                ],
                "Stop": [ { "command": "notify", "timeoutSeconds": 99999 } ],
                "Unknown": [ { "command": "ignored" } ]
              }
            }
            """;
        var hooks = CodingHookSource.ParseHooksFile(Encoding.UTF8.GetBytes(json), "hooks.json");
        Assert.Equal(3, hooks.Count);
        Assert.Equal(("shell.*", "check-shell", TimeSpan.FromSeconds(5)), (hooks[0].Matcher, hooks[0].Command, hooks[0].Timeout));
        Assert.Equal(("fs.*", "check-fs"), (hooks[1].Matcher, hooks[1].Command));
        Assert.Equal(CodingHook.MaxTimeout, hooks[2].Timeout);
        Assert.All(hooks, h => Assert.Equal(ExtensionSource.Repository, h.Source));
    }

    [Fact]
    public async Task Runner_Exit2_Blocks_WithStderrReason()
    {
        var outcome = await new CodingHookRunner().RunAsync([Hook(CodingHookEvent.PreToolUse, Exit2WithReason("no-pushing"))],
            CodingHookEvent.PreToolUse, "shell.exec", new JsonObject(), Repo);
        Assert.True(outcome.Blocked);
        Assert.Contains("no-pushing", outcome.Reason);
    }

    [Fact]
    public async Task Runner_JsonDecisionBlock_Blocks_AndLaterHooksDoNotRun()
    {
        var marker = Path.Combine(_root, "later.txt");
        var outcome = await new CodingHookRunner().RunAsync(
            [Hook(CodingHookEvent.PreToolUse, EchoJson("{\"decision\":\"block\",\"reason\":\"tests first\"}")), Hook(CodingHookEvent.PreToolUse, TouchMarker(marker))],
            CodingHookEvent.PreToolUse, "shell.exec", new JsonObject(), Repo);
        Assert.True(outcome.Blocked);
        Assert.Equal("tests first", outcome.Reason);
        Assert.Single(outcome.Executions);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Runner_Allows_AddsContext_AndIgnoresNonMatchingTools()
    {
        var runner = new CodingHookRunner();
        var hooks = new[]
        {
            Hook(CodingHookEvent.PostToolUse, EchoJson("{\"additionalContext\":\"lint: 0 warnings\"}"), "code.*"),
            Hook(CodingHookEvent.PostToolUse, Exit2WithReason("never"), "shell.*"),
        };
        var outcome = await runner.RunAsync(hooks, CodingHookEvent.PostToolUse, "code.edit_file", new JsonObject(), Repo);
        Assert.False(outcome.Blocked);
        Assert.Equal(["lint: 0 warnings"], outcome.Context);
        Assert.Single(outcome.Executions);
    }

    [Fact]
    public async Task Runner_OtherExitCodes_AreWarningsNotBlocks_AndPromptStdoutIsContext()
    {
        var runner = new CodingHookRunner();
        var failed = await runner.RunAsync([Hook(CodingHookEvent.PreToolUse, Win ? "exit /b 1" : "exit 1")], CodingHookEvent.PreToolUse, "x", new JsonObject(), Repo);
        Assert.False(failed.Blocked);
        Assert.Contains("exited with code 1", Assert.Single(failed.Warnings));

        var prompt = await runner.RunAsync([Hook(CodingHookEvent.UserPromptSubmit, Win ? "echo branch is main" : "echo 'branch is main'")], CodingHookEvent.UserPromptSubmit, null, new JsonObject(), Repo);
        Assert.Equal(["branch is main"], prompt.Context);
        Assert.Equal("hi\n\n[Context added by a UserPromptSubmit hook]\nbranch is main", CodingHooks.WithContext("hi", prompt));
    }

    [Fact]
    public async Task Runner_PassesPayloadOnStdin_NotOnTheCommandLine()
    {
        var hook = Hook(CodingHookEvent.PreToolUse, CatStdin);
        var payload = new JsonObject { ["conversationId"] = "c1", ["tool"] = "shell.exec", ["input"] = new JsonObject { ["command"] = "rm -rf / & echo pwned" } };
        var outcome = await new CodingHookRunner().RunAsync([hook], CodingHookEvent.PreToolUse, "shell.exec", payload, Repo);
        var stdout = Assert.Single(outcome.Executions).Stdout;
        var echoed = JsonNode.Parse(stdout.Trim())!.AsObject();
        Assert.Equal("c1", (string?)echoed["conversationId"]);
        Assert.Equal("PreToolUse", (string?)echoed["event"]);
        Assert.Equal("rm -rf / & echo pwned", (string?)echoed["input"]!["command"]);
        Assert.False(File.Exists(Path.Combine(Repo, "pwned")));
    }

    [Fact]
    public async Task Runner_TimesOut_KillsTheProcess_AndFailsOpen()
    {
        // The hook writes a marker once its sleep ends; a runner that abandoned the child instead of killing it
        // would let the marker appear after the timeout.
        var marker = Path.Combine(_root, "hook-outlived-its-timeout.txt");
        var command = Win
            ? $"ping -n 4 127.0.0.1 > nul & echo survived> \"{marker}\""
            : $"sleep 3; echo survived > '{marker}'";
        var watch = Stopwatch.StartNew();
        var outcome = await new CodingHookRunner().RunAsync([Hook(CodingHookEvent.PreToolUse, command, seconds: 1)], CodingHookEvent.PreToolUse, "x", new JsonObject(), Repo);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
        Assert.False(outcome.Blocked);
        Assert.True(Assert.Single(outcome.Executions).TimedOut);
        Assert.Contains("timed out", Assert.Single(outcome.Warnings));

        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(marker), "The timed-out hook kept running after the runner returned.");
    }

    [Fact]
    public async Task Runner_BoundsOutput()
    {
        var execution = await new CodingHookRunner().RunOneAsync(Hook(CodingHookEvent.Stop, Flood), "{}", Repo);
        Assert.True(execution.OutputTruncated);
        Assert.True(execution.Stdout.Length <= CodingHookRunner.MaxOutputCharacters);
        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public async Task Runner_MissingShellCommand_DoesNotThrow()
    {
        var outcome = await new CodingHookRunner().RunAsync([Hook(CodingHookEvent.Stop, "definitely-not-a-command-" + Guid.NewGuid().ToString("N"))],
            CodingHookEvent.Stop, null, new JsonObject(), Repo);
        Assert.False(outcome.Blocked);
        Assert.Single(outcome.Warnings);
    }

    private void WriteRepoHooks(string command)
    {
        var dir = Path.Combine(Repo, ".swarmcode");
        Directory.CreateDirectory(dir);
        var json = new JsonObject { ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(new JsonObject { ["matcher"] = "shell.*", ["command"] = command }) } };
        File.WriteAllText(Path.Combine(dir, "hooks.json"), json.ToJsonString());
    }

    private CodingHooks Hooks(ICodingHookApprovalStore approvals, CodingOptions? options = null)
        => new(new CodingHookSource(Options.Create(options ?? new CodingOptions()), approvals), new CodingHookRunner(),
            new SwarmCore.Missions.SandboxPathResolver(Options.Create(new SwarmCore.Missions.SandboxOptions { Root = Repo })));

    [Fact]
    public async Task RepositoryHooks_NeverRunUnapproved_RunAfterApproval_AndNeedReapprovalWhenChanged()
    {
        var marker = Path.Combine(_root, "ran.txt");
        WriteRepoHooks(TouchMarker(marker) + (Win ? " & exit /b 2" : "; exit 2"));
        var approvals = new InMemoryCodingHookApprovalStore();
        var hooks = Hooks(approvals);
        var guard = new HookedToolInvocationGuard(null, hooks);
        var request = new ToolInvocationRequest("shell.exec", ToolSideEffect.ExternalSend, new { command = "git push" }, "conv");

        {
            var inspection = (await hooks.Source.InspectRepositoryAsync(Repo))!;
            Assert.True(inspection.NeedsApproval);
            Assert.True((await guard.InspectAsync(request)).Allowed);
            Assert.False(File.Exists(marker));

            await approvals.ApproveAsync(Repo, inspection.Sha256!);
            var blocked = await guard.InspectAsync(request);
            Assert.False(blocked.Allowed);
            Assert.StartsWith("PreToolUse hook:", blocked.BlockReason);
            Assert.True(File.Exists(marker));
            File.Delete(marker);

            WriteRepoHooks(TouchMarker(marker) + (Win ? " & exit /b 2 " : "; exit 2 "));
            Assert.True((await hooks.Source.InspectRepositoryAsync(Repo))!.NeedsApproval);
            Assert.True((await guard.InspectAsync(request)).Allowed);
            Assert.False(File.Exists(marker));
        }
    }

    [Fact]
    public async Task NoApprovalStore_RepositoryHooksStayOff_UserHooksRun()
    {
        WriteRepoHooks(Win ? "exit /b 2" : "exit 2");
        var options = new CodingOptions();
        options.Hooks.PreToolUse.Add(new CodingHookDefinition { Matcher = "git.*", Command = Exit2WithReason("user-policy") });
        var hooks = Hooks(new NoCodingHookApprovals(), options);
        var guard = new HookedToolInvocationGuard(null, hooks);
        {
            Assert.True((await guard.InspectAsync(new ToolInvocationRequest("shell.exec", ToolSideEffect.ExternalSend, new { }, "c"))).Allowed);
            var blocked = await guard.InspectAsync(new ToolInvocationRequest("git.commit", ToolSideEffect.WritesLocal, new { }, "c"));
            Assert.Contains("user-policy", blocked.BlockReason);
        }
        options.Hooks.Enabled = false;
        Assert.True((await guard.InspectAsync(new ToolInvocationRequest("git.commit", ToolSideEffect.WritesLocal, new { }, "c"))).Allowed);
    }

    [Fact]
    public async Task Guard_KeepsTheInnerGuardsBlock()
    {
        var options = new CodingOptions();
        options.Hooks.PreToolUse.Add(new CodingHookDefinition { Command = Win ? "exit /b 0" : "exit 0" });
        var guard = new HookedToolInvocationGuard(new BlockAll(), Hooks(new NoCodingHookApprovals(), options));
        var decision = await guard.InspectAsync(new ToolInvocationRequest("shell.exec", ToolSideEffect.ExternalSend, new { }, "c"));
        Assert.Equal("inner", decision.BlockReason);
    }

    [Fact]
    public async Task ProfileResolver_AddsPostToolUseNotes_BlocksReadOnlyCalls_AndLeavesToolsAloneWithoutHooks()
    {
        var tool = ToolDescriptor.Create<Echo, Echo>("code.grep", "coding", "grep", ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (i, _) => Task.FromResult(i));
        var write = ToolDescriptor.Create<Echo, Echo>("fs.write_file", "coding", "write", ToolCostClass.Cheap, ToolSideEffect.WritesLocal, (i, _) => Task.FromResult(i));
        var inner = new FixedResolver(tool, write);

        var none = await new HookedProfileResolver(inner, Hooks(new NoCodingHookApprovals())).ResolveAsync("c");
        Assert.Same(tool, none.Tools[0]);

        var options = new CodingOptions();
        options.Hooks.PostToolUse.Add(new CodingHookDefinition { Matcher = "fs.*", Command = EchoJson("{\"additionalContext\":\"formatted\"}") });
        options.Hooks.PreToolUse.Add(new CodingHookDefinition { Matcher = "code.grep", Command = Exit2WithReason("no-grep") });
        var resolved = await new HookedProfileResolver(inner, Hooks(new NoCodingHookApprovals(), options)).ResolveAsync("c");

        var annotated = Assert.IsType<HookAnnotatedToolResult>(await resolved.Tools[1].Invoke(new Echo { Text = "x" }, default));
        Assert.Equal(["formatted"], annotated.HookNotes);
        Assert.Equal("x", ((Echo)annotated.Result!).Text);
        var ex = await Assert.ThrowsAsync<ToolCallNotDispatchedException>(() => resolved.Tools[0].Invoke(new Echo(), default));
        Assert.Contains("no-grep", ex.Message);
    }

    public sealed class Echo { public string? Text { get; set; } }

    private sealed class BlockAll : IToolInvocationGuard
    {
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default) => Task.FromResult(ToolGuardDecision.Block("inner"));
    }

    private sealed class FixedResolver(params ToolDescriptor[] tools) : IProfileResolver
    {
        public Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
            => Task.FromResult(new ResolvedProfile(new AgentProfileSnapshot { Id = Guid.NewGuid(), Name = "t", SystemPrompt = "", EnabledToolIds = [] }, tools));
    }
}
