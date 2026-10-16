using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Permissions;

namespace SwarmCore.Coding.Tests;

public class CodingPermissionTests
{
    private static CodingPermissionPolicy Policy(CodingPermissionMode mode, string[]? allow = null, string[]? deny = null)
        => CodingPermissionPolicy.From(mode, new CodingPermissionRules { Allow = [.. allow ?? []], Deny = [.. deny ?? []] });

    // Mode x tool matrix without rules.
    [Theory]
    // Auto: everything runs.
    [InlineData(CodingPermissionMode.Auto, "grep", ToolSideEffect.ReadOnly, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.Auto, "code.edit_file", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.Auto, "shell.exec", ToolSideEffect.Exec, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.Auto, "git.commit", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.Auto, "apps.call_tool", ToolSideEffect.ExternalSend, CodingPermissionVerdict.Run)]
    // Ask risky: reads and local edits run; exec, sends, destructive, git commit/push, computer/browser ask.
    [InlineData(CodingPermissionMode.AskRisky, "grep", ToolSideEffect.ReadOnly, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.AskRisky, "code.edit_file", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.AskRisky, "fs.write_file", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.AskRisky, "shell.exec", ToolSideEffect.Exec, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "code.run_csharp", ToolSideEffect.Exec, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "mail.send", ToolSideEffect.ExternalSend, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "fs.delete", ToolSideEffect.Destructive, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "git.commit", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "git.push", ToolSideEffect.ExternalSend, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "browser.click", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "computer.type", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.AskRisky, "agents.spawn", ToolSideEffect.Exec, CodingPermissionVerdict.Run)] // agents inherit the mode
    // Ask: every side effect asks; reads run.
    [InlineData(CodingPermissionMode.Ask, "grep", ToolSideEffect.ReadOnly, CodingPermissionVerdict.Run)]
    [InlineData(CodingPermissionMode.Ask, "code.edit_file", ToolSideEffect.WritesLocal, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.Ask, "agents.spawn", ToolSideEffect.Exec, CodingPermissionVerdict.Ask)]
    [InlineData(CodingPermissionMode.Ask, "shell.exec", ToolSideEffect.Exec, CodingPermissionVerdict.Ask)]
    public void Mode_matrix(CodingPermissionMode mode, string tool, ToolSideEffect effect, CodingPermissionVerdict expected)
        => Assert.Equal(expected, Policy(mode).Decide(tool, effect).Verdict);

    [Theory]
    [InlineData(CodingPermissionMode.Auto)]
    [InlineData(CodingPermissionMode.AskRisky)]
    [InlineData(CodingPermissionMode.Ask)]
    public void Deny_wins_over_allow_mode_and_read_only(CodingPermissionMode mode)
    {
        var policy = Policy(mode, allow: ["shell.*", "grep"], deny: ["shell.exec", "GREP"]);
        var shell = policy.Decide("shell.exec", ToolSideEffect.Exec);
        Assert.Equal(CodingPermissionVerdict.Deny, shell.Verdict);
        Assert.Contains("Coding:Permissions:Deny", shell.Reason);
        Assert.Contains("shell.exec", shell.Reason);
        Assert.Equal(CodingPermissionVerdict.Deny, policy.Decide("grep", ToolSideEffect.ReadOnly).Verdict);
    }

    [Fact]
    public void Allow_skips_the_prompt_in_asking_modes()
    {
        var policy = Policy(CodingPermissionMode.Ask, allow: ["code.edit_file", "git.*"]);
        Assert.Equal(CodingPermissionVerdict.Run, policy.Decide("code.edit_file", ToolSideEffect.WritesLocal).Verdict);
        Assert.Equal(CodingPermissionVerdict.Run, policy.Decide("git.commit", ToolSideEffect.WritesLocal).Verdict);
        Assert.Equal(CodingPermissionVerdict.Ask, policy.Decide("shell.exec", ToolSideEffect.Exec).Verdict);
    }

    [Theory]
    [InlineData("shell.*", "shell.exec", true)]
    [InlineData("shell.*", "shellx.exec", false)]
    [InlineData("*", "anything", true)]
    [InlineData("git.commit", "GIT.COMMIT", true)]
    [InlineData("git.commit", "git.commit_all", false)]
    [InlineData("mcp.*.delete*", "mcp.github.delete_repo", true)]
    [InlineData("a.b", "aXb", false)] // '.' is literal
    [InlineData("", "x", false)]
    public void Globs_match_tool_ids(string pattern, string tool, bool expected)
        => Assert.Equal(expected, CodingPermissionPolicy.Matches(pattern, tool));

    [Fact]
    public void Native_lanes_are_blocked_only_when_the_policy_asks_or_denies()
    {
        Assert.Null(Policy(CodingPermissionMode.Auto).NativeLaneBlock);
        Assert.Null(Policy(CodingPermissionMode.Auto, allow: ["shell.*"]).NativeLaneBlock);
        Assert.Contains("approval prompts", Policy(CodingPermissionMode.AskRisky).NativeLaneBlock);
        Assert.Contains("approval prompts", Policy(CodingPermissionMode.Ask).NativeLaneBlock);
        Assert.Contains("deny rules", Policy(CodingPermissionMode.Auto, deny: ["shell.exec"]).NativeLaneBlock);
    }

    [Theory]
    [InlineData("auto", CodingPermissionMode.Auto)]
    [InlineData("ask-risky", CodingPermissionMode.AskRisky)]
    [InlineData("Ask_Risky", CodingPermissionMode.AskRisky)]
    [InlineData("ask", CodingPermissionMode.Ask)]
    public void Mode_names_round_trip(string text, CodingPermissionMode mode)
    {
        Assert.True(CodingPermissionPolicy.TryParse(text, out var parsed));
        Assert.Equal(mode, parsed);
        Assert.True(CodingPermissionPolicy.TryParse(CodingPermissionPolicy.Name(mode), out var again));
        Assert.Equal(mode, again);
    }

    [Fact]
    public void Stricter_never_loosens()
    {
        Assert.Equal(CodingPermissionMode.Ask, CodingPermissionPolicy.Stricter(CodingPermissionMode.Auto, CodingPermissionMode.Ask));
        Assert.Equal(CodingPermissionMode.AskRisky, CodingPermissionPolicy.Stricter(CodingPermissionMode.AskRisky, CodingPermissionMode.Auto));
        Assert.False(CodingPermissionPolicy.TryParse("whatever", out _));
    }

    [Fact]
    public async Task Guard_applies_only_the_running_turns_policy()
    {
        var active = new ActiveCodingPermissions();
        var guard = new CodingPermissionGuard(active);
        ToolInvocationRequest Call(string tool, ToolSideEffect effect, string conversation = "c1") => new(tool, effect, new object(), conversation);

        // No policy for the conversation: the guard has no opinion.
        Assert.True((await guard.InspectAsync(Call("shell.exec", ToolSideEffect.Exec))).Allowed);

        using (active.Enter("c1", Policy(CodingPermissionMode.AskRisky, deny: ["git.push"])))
        {
            var denied = await guard.InspectAsync(Call("git.push", ToolSideEffect.ExternalSend));
            Assert.False(denied.Allowed);
            Assert.Contains("git.push", denied.BlockReason);
            var ask = await guard.InspectAsync(Call("shell.exec", ToolSideEffect.Exec));
            Assert.True(ask.Allowed);
            Assert.True(ask.RequiresApproval);
            var edit = await guard.InspectAsync(Call("code.edit_file", ToolSideEffect.WritesLocal));
            Assert.True(edit.Allowed);
            Assert.False(edit.RequiresApproval);
            // Another conversation is not governed by c1's policy.
            Assert.False((await guard.InspectAsync(Call("shell.exec", ToolSideEffect.Exec, "c2"))).RequiresApproval);
        }
        Assert.False((await guard.InspectAsync(Call("shell.exec", ToolSideEffect.Exec))).RequiresApproval);
    }

    [Fact]
    public async Task Deny_wins_through_the_composed_guard_chain()
    {
        var active = new ActiveCodingPermissions();
        var chain = new CompositeToolInvocationGuard(new CodingPermissionGuard(active), new EscalateEverything());
        using var _ = active.Enter("c", Policy(CodingPermissionMode.Auto, allow: ["shell.exec"], deny: ["shell.*"]));
        var decision = await chain.InspectAsync(new ToolInvocationRequest("shell.exec", ToolSideEffect.Exec, new object(), "c"));
        Assert.False(decision.Allowed);
    }

    private sealed class EscalateEverything : IToolInvocationGuard
    {
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
            => Task.FromResult(ToolGuardDecision.Escalate("later guard"));
    }

    [Fact]
    public async Task Profile_withholds_denied_read_only_tools_and_names_the_rules()
    {
        var catalog = new InMemoryCapabilityCatalog();
        catalog.Register(Tool("grep", ToolSideEffect.ReadOnly));
        catalog.Register(Tool("shell.exec", ToolSideEffect.Exec));
        catalog.Register(Tool("glob", ToolSideEffect.ReadOnly));
        var options = new CodingOptions { Permissions = new CodingPermissionRules { Deny = ["grep", "shell.exec"] } };
        var profile = await new CodingProfileResolver(catalog, Options.Create(options)).ResolveAsync("c");
        var ids = profile.Tools.Select(t => t.ToolId).ToList();
        Assert.DoesNotContain("grep", ids); // read-only: the guard would never see it, so it is not offered
        Assert.Contains("shell.exec", ids); // side effect: offered, and the guard refuses it with a reason
        Assert.Contains("glob", ids);
        Assert.Contains("Coding:Permissions:Deny", profile.Snapshot.SystemPrompt);
    }

    private static ToolDescriptor Tool(string id, ToolSideEffect effect)
        => ToolDescriptor.Create<object, object>(id, "test", "test tool", ToolCostClass.Trivial, effect, (_, _) => Task.FromResult<object>(new()));
}
