using System.Text;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Permissions;

namespace SwarmCore.Coding.Tests;

/// <summary>Project-scoped permission rules: <c>.swarmcode/settings.json</c> and <c>.claude/settings.json</c>.</summary>
public sealed class RepositoryPermissionRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-repo-permissions-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");

    public RepositoryPermissionRulesTests() => Directory.CreateDirectory(Repo);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string folder, string json)
    {
        Directory.CreateDirectory(Path.Combine(Repo, folder));
        File.WriteAllText(Path.Combine(Repo, folder, "settings.json"), json);
    }

    [Fact]
    public void Parse_AcceptsClaudeCodeAndSwarmCodeShapes_CaseInsensitive_AndIgnoresOtherContent()
    {
        var claude = RepositoryPermissionRules.Parse(Encoding.UTF8.GetBytes("""{ "model": "x", "permissions": { "allow": ["git.commit", " shell.* ", "", "git.commit"], "deny": ["browser.*", 5] } }"""));
        Assert.Equal(["git.commit", "shell.*"], claude.Allow);
        Assert.Equal(["browser.*"], claude.Deny);

        var swarm = RepositoryPermissionRules.Parse(Encoding.UTF8.GetBytes("""{ "Coding": { "Permissions": { "Allow": ["fs.write_file"], "Deny": ["computer.*"] } } }"""));
        Assert.Equal(["fs.write_file"], swarm.Allow);
        Assert.Equal(["computer.*"], swarm.Deny);

        var nothing = RepositoryPermissionRules.Parse(Encoding.UTF8.GetBytes("""{ "hooks": {} }"""));
        Assert.Empty(nothing.Allow);
        Assert.Empty(nothing.Deny);
    }

    [Fact]
    public void Inspect_ReadsBothLocations_WhenBothExist_AndNoneWithoutFiles()
    {
        Assert.False(RepositoryPermissionRules.Inspect(Repo).Exists);
        Assert.Same(RepositoryPermissionInspection.None, RepositoryPermissionRules.Inspect(null));
        Assert.Same(RepositoryPermissionInspection.None, RepositoryPermissionRules.Inspect("  "));

        Write(".swarmcode", """{ "permissions": { "allow": ["git.commit"], "deny": ["shell.*"] } }""");
        Write(".claude", """{ "permissions": { "allow": ["fs.write_file"], "deny": ["browser.*"] } }""");
        var inspection = RepositoryPermissionRules.Inspect(Repo);
        Assert.True(inspection.Exists);
        Assert.Equal(2, inspection.Files.Count);
        Assert.EndsWith(Path.Combine(".swarmcode", "settings.json"), inspection.Files[0].Path);
        Assert.EndsWith(Path.Combine(".claude", "settings.json"), inspection.Files[1].Path);
        Assert.Equal(["git.commit", "fs.write_file"], inspection.Allow);
        Assert.Equal(["shell.*", "browser.*"], inspection.Deny);
        Assert.NotNull(inspection.Sha256);
        Assert.Equal(64, inspection.Sha256!.Length);
        Assert.False(inspection.Approved);
        Assert.True(inspection.NeedsApproval);
        Assert.Empty(inspection.Problems);

        // The digest covers every file: a change to either asks again.
        var before = inspection.Sha256;
        Write(".claude", """{ "permissions": { "allow": ["fs.write_file", "git.push"] } }""");
        Assert.NotEqual(before, RepositoryPermissionRules.Inspect(Repo).Sha256);
    }

    [Fact]
    public void Inspect_MalformedOrOversizedFile_ContributesNothing_WithAProblem_AndNeverThrows()
    {
        Write(".swarmcode", "{ not json");
        var malformed = RepositoryPermissionRules.Inspect(Repo);
        Assert.True(malformed.Exists);
        var file = Assert.Single(malformed.Files);
        Assert.Contains("not valid JSON", file.Problem);
        Assert.Empty(malformed.Allow);
        Assert.Empty(malformed.Deny);
        Assert.False(malformed.NeedsApproval);
        Assert.Single(malformed.Problems);

        Write(".swarmcode", """[ "an array, not an object" ]""");
        Assert.Contains("top level", Assert.Single(RepositoryPermissionRules.Inspect(Repo).Files).Problem);

        var big = new string(' ', (int)RepositoryPermissionRules.MaxFileBytes + 1) + "{}";
        Write(".claude", big);
        Write(".swarmcode", """{ "permissions": { "deny": ["shell.*"] } }""");
        var oversized = RepositoryPermissionRules.Inspect(Repo);
        Assert.Equal(2, oversized.Files.Count);
        Assert.Contains("larger than", oversized.Files[1].Problem);
        Assert.Null(oversized.Files[1].Sha256);
        Assert.Equal(["shell.*"], oversized.Deny); // the good file still applies
        Assert.NotNull(oversized.Sha256);
    }

    [Fact]
    public async Task InspectAsync_ApprovalIsKeyedByRepositoryAndDigest()
    {
        Write(".swarmcode", """{ "permissions": { "allow": ["git.commit"], "deny": ["shell.*"] } }""");
        var approvals = new InMemoryCodingRepositorySettingsApprovalStore();
        var first = await RepositoryPermissionRules.InspectAsync(Repo, approvals);
        Assert.True(first.NeedsApproval);

        await approvals.ApproveAsync(Repo, first.Sha256!);
        var approved = await RepositoryPermissionRules.InspectAsync(Repo + Path.DirectorySeparatorChar, approvals);
        Assert.True(approved.Approved);
        Assert.False(approved.NeedsApproval);

        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(other, ".swarmcode"));
        File.Copy(Path.Combine(Repo, ".swarmcode", "settings.json"), Path.Combine(other, ".swarmcode", "settings.json"));
        Assert.False((await RepositoryPermissionRules.InspectAsync(other, approvals)).Approved); // same content, other repository

        Write(".swarmcode", """{ "permissions": { "allow": ["git.commit", "git.push"], "deny": ["shell.*"] } }""");
        Assert.False((await RepositoryPermissionRules.InspectAsync(Repo, approvals)).Approved); // changed content

        await approvals.RevokeAsync(Repo);
        Assert.False((await RepositoryPermissionRules.InspectAsync(Repo, approvals)).Approved);

        // Deny-only files need no approval and never ask.
        Write(".swarmcode", """{ "permissions": { "deny": ["shell.*"] } }""");
        var denyOnly = await RepositoryPermissionRules.InspectAsync(Repo, approvals);
        Assert.False(denyOnly.NeedsApproval);
        Assert.Equal(["shell.*"], denyOnly.Deny);

        // A store that fails approves nothing.
        Write(".swarmcode", """{ "permissions": { "allow": ["git.commit"] } }""");
        Assert.False((await RepositoryPermissionRules.InspectAsync(Repo, new ThrowingApprovals())).Approved);
        Assert.False((await RepositoryPermissionRules.InspectAsync(Repo, null)).Approved);
    }

    private sealed class ThrowingApprovals : ICodingRepositorySettingsApprovalStore
    {
        public Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => throw new IOException("store gone");
        public Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => throw new IOException("store gone");
        public Task RevokeAsync(string repositoryRoot, CancellationToken ct = default) => throw new IOException("store gone");
    }

    [Fact]
    public void Merge_DenyAlways_AllowOnlyWhenApproved_UserRulesPreserved_InputsUntouched()
    {
        var user = new CodingPermissionRules { Allow = ["git.commit"], Deny = ["computer.*"] };
        var repo = new RepositoryPermissionInspection(Repo,
            [new RepositoryPermissionFile("p", "s", ["shell.exec", "GIT.COMMIT"], ["browser.*", ""], null)], "digest", Approved: false);

        var unapproved = RepositoryPermissionRules.Merge(user, repo);
        Assert.Equal(["git.commit"], unapproved.Allow);
        Assert.Equal(["computer.*", "browser.*"], unapproved.Deny);

        var approved = RepositoryPermissionRules.Merge(user, repo with { Approved = true });
        Assert.Equal(["git.commit", "shell.exec"], approved.Allow); // duplicates collapse case-insensitively
        Assert.Equal(["computer.*", "browser.*"], approved.Deny);

        Assert.Equal(["git.commit"], user.Allow);
        Assert.Equal(["computer.*"], user.Deny);
        Assert.Equal(["git.commit"], RepositoryPermissionRules.Merge(user, null).Allow);
        Assert.Empty(RepositoryPermissionRules.Merge(null, RepositoryPermissionInspection.None).Deny);

        // Through the policy: a repository deny refuses even an allowed, auto-mode tool; an approved allow runs under Ask.
        var policy = CodingPermissionPolicy.From(CodingPermissionMode.Ask, approved);
        Assert.Equal(CodingPermissionVerdict.Deny, policy.Decide("browser.open", ToolSideEffect.Exec).Verdict);
        Assert.Equal(CodingPermissionVerdict.Run, policy.Decide("shell.exec", ToolSideEffect.Exec).Verdict);
        Assert.Equal(CodingPermissionVerdict.Ask, policy.Decide("git.push", ToolSideEffect.ExternalSend).Verdict);
    }

    [Fact]
    public async Task ProfileResolver_NamesTheRunningTurnsMergedDenyRules()
    {
        var catalog = new InMemoryCapabilityCatalog();
        catalog.Register(ToolDescriptor.Create<object, object>("grep", "test", "test tool", ToolCostClass.Trivial, ToolSideEffect.ReadOnly, (_, _) => Task.FromResult<object>(new())));
        catalog.Register(ToolDescriptor.Create<object, object>("shell.exec", "test", "test tool", ToolCostClass.Trivial, ToolSideEffect.Exec, (_, _) => Task.FromResult<object>(new())));
        var options = new CodingOptions { Permissions = new CodingPermissionRules { Deny = ["shell.exec"] } };
        var active = new ActiveCodingPermissions();
        var resolver = new CodingProfileResolver(catalog, Options.Create(options), active);

        // Outside a turn: the configured rules.
        var configured = await resolver.ResolveAsync("c");
        Assert.Contains("shell.exec", configured.Snapshot.SystemPrompt);
        Assert.DoesNotContain("grep", configured.Snapshot.SystemPrompt.Split("Coding:Permissions:Deny").Last());

        // During a turn: the request's merged policy (user plus repository deny) is what the model is told.
        var merged = CodingPermissionPolicy.From(CodingPermissionMode.Auto, new CodingPermissionRules { Deny = ["shell.exec", "grep"] });
        using (active.Enter("c", merged))
        {
            var during = await resolver.ResolveAsync("c");
            Assert.DoesNotContain("grep", during.Tools.Select(t => t.ToolId));
            Assert.Contains("grep", during.Snapshot.SystemPrompt.Split("Coding:Permissions:Deny").Last());
        }
        Assert.Contains("grep", (await resolver.ResolveAsync("c")).Tools.Select(t => t.ToolId));
    }
}
