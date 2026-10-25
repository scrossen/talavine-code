using System.Collections.Concurrent;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Extensions;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class AgentDefinitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-agentdefs-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private string User => Path.Combine(_root, "user");

    public AgentDefinitionTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".swarmcode", "agents"));
        Directory.CreateDirectory(Path.Combine(Repo, ".claude", "agents"));
        Directory.CreateDirectory(Path.Combine(User, "agents"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Parse_ReadsFrontmatterFieldsListsAndBody()
    {
        var definition = AgentDefinitionCatalog.Parse("""
            ---
            name: Reviewer
            description: "Reviews a diff for bugs"
            lane: deepseek
            model: deepseek-chat
            isolation: worktree
            tools:
              - fs.read_file
              - code.grep
              - Bash(git:*)
            ---
            Review carefully.
            Report findings only.
            """, "reviewer.md", ExtensionSource.Repository)!;

        Assert.Equal("reviewer", definition.Name);
        Assert.Equal("Reviews a diff for bugs", definition.Description);
        Assert.Equal("deepseek", definition.Lane);
        Assert.Equal("deepseek-chat", definition.Model);
        Assert.Equal("worktree", definition.Isolation);
        Assert.Equal(["fs.read_file", "code.grep", "shell.exec"], definition.Tools);
        Assert.Equal("Review carefully.\nReport findings only.", definition.Instructions);
    }

    [Fact]
    public void ParseTools_AcceptsInlineListsAndClaudeCodeNames_AndStarMeansEverything()
    {
        Assert.Equal(["fs.read_file", "code.edit_file", "code.glob", "web.search"], AgentDefinitionCatalog.ParseTools("[Read, Edit, Glob, WebSearch]"));
        Assert.Equal(["shell.*", "git.commit"], AgentDefinitionCatalog.ParseTools("shell.*, git.commit"));
        Assert.Empty(AgentDefinitionCatalog.ParseTools("Read, *"));
        Assert.Empty(AgentDefinitionCatalog.ParseTools(null));
    }

    [Fact]
    public void Parse_RequiresDescription_AndFallsBackToTheFileNameForTheName()
    {
        Assert.Null(AgentDefinitionCatalog.Parse("---\nname: x\n---\nbody", "x.md", ExtensionSource.User));
        var named = AgentDefinitionCatalog.Parse("---\ndescription: From the file name\n---\nbody", Path.Combine("dir", "Doc-Writer.md"), ExtensionSource.User)!;
        Assert.Equal("doc-writer", named.Name);
    }

    [Fact]
    public void Discover_UserThenSwarmCodeThenClaude_FirstNameWins()
    {
        Write("repo/.swarmcode/agents/reviewer.md", "---\ndescription: Repo reviewer\n---\nrepo");
        Write("repo/.claude/agents/reviewer.md", "---\ndescription: Claude reviewer\n---\nclaude");
        Write("repo/.claude/agents/tester.md", "---\nname: tester\ndescription: Runs tests\ntools: Bash\n---\ntest");
        Write("user/agents/tester.md", "---\ndescription: My tester\n---\nmine");
        Write("repo/.swarmcode/agents/nodesc.md", "no frontmatter at all");

        var definitions = AgentDefinitionCatalog.Discover(Repo, User);

        Assert.Equal(["reviewer", "tester"], definitions.Select(d => d.Name));
        Assert.Equal(ExtensionSource.Repository, AgentDefinitionCatalog.Find(definitions, "REVIEWER")!.Source);
        Assert.Equal("Repo reviewer", AgentDefinitionCatalog.Find(definitions, "reviewer")!.Description);
        Assert.Equal(ExtensionSource.User, AgentDefinitionCatalog.Find(definitions, "tester")!.Source);
        Assert.Null(AgentDefinitionCatalog.Find(definitions, "missing"));
        Assert.Contains("- reviewer (repository): Repo reviewer", AgentDefinitionCatalog.FormatIndex(definitions)!);
        Assert.Null(AgentDefinitionCatalog.FormatIndex([]));
    }

    [Fact]
    public void AgentTools_MetadataRoundTrips_AndAdmitsAlwaysAllowedTools()
    {
        var metadata = CodingAgentTools.Metadata(new Dictionary<string, string> { ["x"] = "y" }, ["fs.*", "code.grep"]);
        Assert.Equal("y", metadata["x"]);
        Assert.Equal(["fs.*", "code.grep"], CodingAgentTools.Patterns(metadata));
        Assert.Empty(CodingAgentTools.Patterns(null));
        Assert.Empty(CodingAgentTools.Patterns(CodingAgentTools.Metadata(metadata, [])));

        var patterns = CodingAgentTools.Patterns(metadata);
        Assert.True(CodingAgentTools.Admits(patterns, "fs.read_file"));
        Assert.True(CodingAgentTools.Admits(patterns, "code.grep"));
        Assert.True(CodingAgentTools.Admits(patterns, "plan.update"));
        Assert.True(CodingAgentTools.Admits(patterns, "harness.decline"));
        Assert.False(CodingAgentTools.Admits(patterns, "shell.exec"));
        Assert.True(CodingAgentTools.Admits([], "shell.exec"));
    }

    [Fact]
    public async Task ToolFilter_KeepsOnlyAllowedTools_OnARoleTurn()
    {
        var tools = new[] { Tool("fs.read_file"), Tool("shell.exec"), Tool("plan.update") };
        var filter = new CodingAgentToolFilter();
        var request = new AgentTurnRequest { ConversationId = "c", UserMessage = "m", CommunicationScope = AgentCommunicationScope.None,
            Metadata = CodingAgentTools.Metadata(null, ["fs.*"]) };
        var filtered = await filter.FilterAsync(request, null!, tools);
        Assert.Equal(["fs.read_file", "plan.update"], filtered.Select(t => t.ToolId));

        var unrestricted = await filter.FilterAsync(request with { Metadata = null }, null!, tools);
        Assert.Equal(3, unrestricted.Count);
    }

    [Fact]
    public void ActiveWorkspaces_CarryTheAllowListForTheTurn()
    {
        var active = new ActiveCodingWorkspaces();
        Assert.True(active.AdmitsTool("c", "shell.exec"));
        using (active.Enter("c", new CodingWorkspace { RepositoryRoot = "repo" }, allowedTools: ["code.*"]))
        {
            Assert.Equal(["code.*"], active.AllowedTools("c"));
            Assert.True(active.AdmitsTool("c", "code.grep"));
            Assert.False(active.AdmitsTool("c", "shell.exec"));
        }
        Assert.Empty(active.AllowedTools("c"));
    }

    private sealed class FakeHost : IAgentTeamHost
    {
        private int _next;
        public ConcurrentQueue<(string Id, string Message)> Started { get; } = new();
        public AgentConversationRequest? LastCreate { get; private set; }
        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
            => Task.FromResult($"agent{Interlocked.Increment(ref _next)}");
        public Task<AgentConversation> CreateAgentConversationAsync(AgentConversationRequest request, CancellationToken ct)
        {
            LastCreate = request;
            return Task.FromResult(new AgentConversation($"agent{Interlocked.Increment(ref _next)}"));
        }
        public Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
        { Started.Enqueue((conversationId, message)); return Task.FromResult(true); }
        public void Cancel(string conversationId) { }
        public bool IsBusy(string conversationId) => false;
        public Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class ListSource(params AgentDefinition[] definitions) : IAgentDefinitionSource
    {
        public Task<IReadOnlyList<AgentDefinition>> ListAsync(string conversationId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentDefinition>>(definitions);
    }

    [Fact]
    public async Task Spawn_WithARole_AppliesItsDefaultsAndBrief()
    {
        var role = new AgentDefinition("reviewer", "Reviews", "Only report findings.", "deepseek", "deepseek-chat", ["fs.read_file"], null, "p", ExtensionSource.User);
        var options = new AgentTeamOptions();
        var team = new AgentTeam(new InMemoryAgentTeamStore(), () => options, definitions: new ListSource(role));
        var host = new FakeHost();
        team.AttachHost(host);

        var agent = await team.SpawnAsync("root", new AgentSpawnRequest("Review PR 12", Agent: "Reviewer"));

        Assert.Equal("reviewer", agent.Agent);
        Assert.Equal(["fs.read_file"], agent.AllowedTools);
        Assert.Equal("deepseek", agent.Lane);
        Assert.Equal("deepseek-chat", agent.Model);
        Assert.StartsWith("reviewer: ", agent.Title);
        Assert.Equal("deepseek", host.LastCreate!.Lane);
        Assert.True(host.LastCreate.ExplicitModelSelection);
        var (_, brief) = Assert.Single(host.Started);
        Assert.Contains("\"reviewer\" role", brief);
        Assert.Contains("Only report findings.", brief);
        Assert.Contains("limited to: fs.read_file", brief);
        Assert.Contains("Review PR 12", brief);

        // An explicit lane in the request beats the role's default.
        var explicitLane = await team.SpawnAsync("root", new AgentSpawnRequest("Again", Lane: "claude", Agent: "reviewer"));
        Assert.Equal("claude", explicitLane.Lane);
    }

    [Fact]
    public async Task Spawn_WithAnUnknownRole_NamesTheKnownOnes()
    {
        var role = new AgentDefinition("reviewer", "Reviews", "", null, null, [], null, "p", ExtensionSource.User);
        var team = new AgentTeam(new InMemoryAgentTeamStore(), () => new AgentTeamOptions(), definitions: new ListSource(role));
        team.AttachHost(new FakeHost());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => team.SpawnAsync("root", new AgentSpawnRequest("x", Agent: "tester")));
        Assert.Contains("Known roles: reviewer", error.Message);

        var none = new AgentTeam(new InMemoryAgentTeamStore(), () => new AgentTeamOptions());
        none.AttachHost(new FakeHost());
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => none.SpawnAsync("root", new AgentSpawnRequest("x", Agent: "tester")));
        Assert.Contains("agents/<name>.md", missing.Message);
    }

    private static ToolDescriptor Tool(string id) => ToolDescriptor.Create<object, object>(id, "t", "d", ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (_, _) => Task.FromResult<object>(""));
}
