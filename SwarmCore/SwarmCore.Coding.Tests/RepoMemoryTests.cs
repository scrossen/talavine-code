using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Memory;
using SwarmCore.Coding.Prompting;

namespace SwarmCore.Coding.Tests;

public sealed class RepoMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-repomemory-" + Guid.NewGuid().ToString("N"));
    private readonly RepoMemoryOptions _options = new() { MaxIndexLines = 40, MaxIndexCharacters = 1_500 };

    public RepoMemoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "memory", "deep"));
        var index = new StringBuilder("# Project memory\n\n");
        for (var i = 1; i <= 400; i++) index.Append($"- Topic {i}: see memory/topic-{i}.md for the persistence writer rule number {i}.\n");
        File.WriteAllText(Path.Combine(_root, "MEMORY.md"), index.ToString());
        File.WriteAllText(Path.Combine(_root, "memory", "persistence.md"),
            "# Persistence writers\n\nEvery writer owns explicit fields or carries a checked version.\nNever replace a row from a detached snapshot.\nKeep row and sync journal atomic.\n");
        File.WriteAllText(Path.Combine(_root, "memory", "deep", "ui.md"), "# UI\n\nKeep blocking data work off the dispatcher.\n");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "not memory");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("memory/../notes.md")]
    [InlineData("notes.md")]
    [InlineData("memory/persistence.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("memory//x.md")]
    [InlineData("memory/./x.md")]
    [InlineData("memory/x.md:stream")]
    [InlineData("")]
    public void Paths_outside_repository_memory_are_refused(string path)
    {
        Assert.Null(RepoMemoryFiles.ResolvePath(_root, path, _options, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("MEMORY.md", "MEMORY.md")]
    [InlineData("./memory/persistence.md", "memory/persistence.md")]
    [InlineData("memory\\deep\\ui.md", "memory/deep/ui.md")]
    [InlineData("memory/new-topic.md", "memory/new-topic.md")]
    public void Index_and_topic_paths_resolve_inside_the_repository(string path, string expected)
    {
        var full = RepoMemoryFiles.ResolvePath(_root, path, _options, out var normalized, out _);
        Assert.NotNull(full);
        Assert.Equal(expected, normalized);
        Assert.StartsWith(Path.GetFullPath(_root), full);
    }

    [Fact]
    public void Topic_reached_through_a_link_is_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "swarmcore-repomemory-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "outside");
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_root, "memory", "linked"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; } // no symlink privilege on this machine
            Assert.Null(RepoMemoryFiles.ResolvePath(_root, "memory/linked/secret.md", _options, out _, out var problem));
            Assert.Contains("link", problem);
            Assert.DoesNotContain(RepoMemoryFiles.ListTopics(_root, _options), t => t.Path.Contains("linked"));
        }
        finally { try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void Index_is_bounded_by_lines_and_characters_and_marked_truncated()
    {
        var index = RepoMemoryFiles.LoadIndex(_root, _options)!;
        Assert.True(index.Truncated);
        Assert.Equal(402, index.TotalLines);
        Assert.True(index.IncludedLines <= _options.MaxIndexLines);
        Assert.True(index.Content.Length <= _options.MaxIndexCharacters);
        Assert.Equal(2, index.TopicFileCount);
        var text = RepoMemoryIndexInjector.Format(index, _options);
        Assert.Contains("TRUNCATED", text);
        Assert.Contains("repo-memory.search", text);
        Assert.Contains("never written or committed automatically", text);
        Assert.DoesNotContain("Topic 400", text);
    }

    [Fact]
    public void Small_index_is_not_marked_truncated()
    {
        File.WriteAllText(Path.Combine(_root, "MEMORY.md"), "# Memory\n- one\n- two\n");
        var index = RepoMemoryFiles.LoadIndex(_root, _options)!;
        Assert.False(index.Truncated);
        Assert.DoesNotContain("TRUNCATED", RepoMemoryIndexInjector.Format(index, _options));
    }

    [Fact]
    public void Repository_without_memory_contributes_nothing()
    {
        var empty = Path.Combine(_root, "empty-repo");
        Directory.CreateDirectory(empty);
        Assert.Null(RepoMemoryFiles.LoadIndex(empty, _options));
    }

    [Fact]
    public async Task Injector_uses_the_turn_workspace_and_respects_the_switch()
    {
        var options = new CodingOptions();
        var injector = new RepoMemoryIndexInjector(Options.Create(options));
        var text = await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.Empty, WorkspaceRoot: _root));
        Assert.Contains("Repository memory", text);
        options.RepoMemory.Enabled = false;
        Assert.Null(await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.Empty, WorkspaceRoot: _root)));
        Assert.Null(await new RepoMemoryIndexInjector(Options.Create(new CodingOptions())).BuildAsync(new ContextInjectionRequest("c", "hi", Guid.Empty)));
    }

    [Fact]
    public void Search_finds_topic_lines_and_requires_multiword_matches()
    {
        var hits = RepoMemoryFiles.Search(_root, "detached snapshot", _options);
        Assert.Contains(hits, h => h.Path == "memory/persistence.md" && h.Line == 4);
        Assert.Equal("memory/persistence.md", hits[0].Path);
        Assert.Empty(RepoMemoryFiles.Search(_root, "zebra unicorn", _options));
        Assert.Empty(RepoMemoryFiles.Search(_root, "  ", _options));
        Assert.Contains(RepoMemoryFiles.Search(_root, "dispatcher", _options), h => h.Path == "memory/deep/ui.md");
        Assert.True(RepoMemoryFiles.Search(_root, "writer rule", _options, limit: 5).Count <= 5);
    }

    [Fact]
    public void Read_returns_the_requested_range_within_budget()
    {
        var read = RepoMemoryFiles.Read(_root, "memory/persistence.md", _options, startLine: 3, maxLines: 2);
        Assert.Equal(3, read.StartLine);
        Assert.Equal(4, read.EndLine);
        Assert.True(read.Truncated);
        Assert.StartsWith("Every writer", read.Content);
        Assert.Throws<ArgumentException>(() => RepoMemoryFiles.Read(_root, "../x.md", _options));
        Assert.Throws<FileNotFoundException>(() => RepoMemoryFiles.Read(_root, "memory/missing.md", _options));
        var budget = new RepoMemoryOptions { MaxReadCharacters = 500 };
        var bounded = RepoMemoryFiles.Read(_root, "MEMORY.md", budget, maxLines: 5000);
        Assert.True(bounded.Content.Length <= 600);
        Assert.True(bounded.Truncated);
    }

    [Fact]
    public async Task Propose_replaces_an_anchor_without_writing_the_repository()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var before = File.ReadAllText(Path.Combine(_root, "memory", "persistence.md"));
        var result = await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/persistence.md",
            "Keep row and sync journal atomic.", "Keep row and sync journal atomic in one transaction.", "Learned in this turn: journal writes must share the row transaction.");
        Assert.True(result.Ok, result.Message);
        Assert.Equal(RepoMemoryProposalKind.Replace, result.Proposal!.Kind);
        Assert.Contains("-Keep row and sync journal atomic.", result.Proposal.Diff);
        Assert.Contains("+Keep row and sync journal atomic in one transaction.", result.Proposal.Diff);
        Assert.Contains("--- a/memory/persistence.md", result.Proposal.Diff);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "memory", "persistence.md"))); // nothing written
        Assert.Single(await store.ListAsync(_root, includeClosed: false));
    }

    [Fact]
    public async Task Propose_rejects_unusable_edits()
    {
        const string path = "memory/persistence.md", oldText = "Never", newText = "x", rationale = "why";
        var store = new InMemoryRepoMemoryProposalStore();
        File.AppendAllText(Path.Combine(_root, "memory", "persistence.md"), "Never forget.\n");
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, path, oldText, newText, rationale)).Ok); // now ambiguous
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, path, "no such passage", newText, rationale)).Ok);
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, "src/Program.cs", null, newText, rationale)).Ok);
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, path, null, "  ", rationale)).Ok);
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, path, null, "Add a rule.", "")).Ok);
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, path, "Keep row and sync journal atomic.", "Keep row and sync journal atomic.", rationale)).Ok);
        Assert.False((await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/new.md", "anchor", "text", rationale)).Ok);
        var secret = await RepoMemoryProposals.ProposeAsync(store, _root, _options, path, null, "Deploy with api_key=sk-live-1234567890abcdefghijklmnop", rationale); // gitleaks:allow -- synthetic test fixture, never a live credential
        Assert.False(secret.Ok);
        Assert.Contains("never contain", secret.Message);
        Assert.Empty(await store.ListAsync(null, includeClosed: true));
    }

    [Fact]
    public async Task Apply_writes_only_the_working_tree_and_only_once()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var created = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/builds.md", null, "# Builds\n\nBuild one project at a time.", "New durable convention.")).Proposal!;
        Assert.Equal(RepoMemoryProposalKind.Create, created.Kind);
        Assert.Contains("--- /dev/null", created.Diff);
        Assert.False(File.Exists(Path.Combine(_root, "memory", "builds.md")));

        var applied = await RepoMemoryProposals.ApplyAsync(store, created.Id, created.Version, _options);
        Assert.True(applied.Ok, applied.Message);
        Assert.Equal("# Builds\n\nBuild one project at a time.\n", File.ReadAllText(Path.Combine(_root, "memory", "builds.md")));
        Assert.Equal(RepoMemoryProposalStatus.Applied, (await store.GetAsync(created.Id))!.Status);

        var again = await RepoMemoryProposals.ApplyAsync(store, created.Id, created.Version, _options);
        Assert.False(again.Ok); // stale version: a second click never writes twice
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "memory"), "*.tmp"));
    }

    [Fact]
    public async Task Apply_after_the_file_changed_marks_the_proposal_stale_and_writes_nothing()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var path = Path.Combine(_root, "memory", "persistence.md");
        var proposal = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/persistence.md",
            "Never replace a row from a detached snapshot.", "Never replace a current row from an unchecked detached snapshot.", "Sharper rule.")).Proposal!;
        File.WriteAllText(path, "# Persistence writers\n\nRewritten by someone else.\n");
        var result = await RepoMemoryProposals.ApplyAsync(store, proposal.Id, proposal.Version, _options);
        Assert.False(result.Ok);
        Assert.Equal(RepoMemoryProposalStatus.Stale, (await store.GetAsync(proposal.Id))!.Status);
        Assert.Equal("# Persistence writers\n\nRewritten by someone else.\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Two_proposals_for_one_file_applied_at_once_keep_both_edits()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var path = Path.Combine(_root, "memory", "persistence.md");
        var p1 = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/persistence.md",
            "Never replace a row from a detached snapshot.", "Never replace a current row from an unchecked detached snapshot.", "Sharper rule.")).Proposal!;
        var p2 = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/persistence.md",
            "Keep row and sync journal atomic.", "Keep the row and its sync journal entry in one transaction.", "Clearer rule.")).Proposal!;

        var results = await Task.WhenAll(
            Task.Run(() => RepoMemoryProposals.ApplyAsync(store, p1.Id, p1.Version, _options)),
            Task.Run(() => RepoMemoryProposals.ApplyAsync(store, p2.Id, p2.Version, _options)));

        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        var text = File.ReadAllText(path);
        Assert.Contains("Never replace a current row from an unchecked detached snapshot.", text);
        Assert.Contains("Keep the row and its sync journal entry in one transaction.", text);
    }

    [Fact]
    public async Task An_edit_saved_while_applying_is_never_overwritten()
    {
        var path = Path.Combine(_root, "memory", "persistence.md");
        var inner = new InMemoryRepoMemoryProposalStore();
        // The person saves the file in an editor right after the proposal is claimed (between read and write).
        var store = new EditDuringClaimStore(inner, () => File.AppendAllText(path, "Edited in an editor.\n"));
        var proposal = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/persistence.md",
            "Never replace a row from a detached snapshot.", "Never replace a current row from an unchecked detached snapshot.", "Sharper rule.")).Proposal!;

        var result = await RepoMemoryProposals.ApplyAsync(store, proposal.Id, proposal.Version, _options);

        Assert.False(result.Ok);
        var text = File.ReadAllText(path);
        Assert.Contains("Edited in an editor.", text);
        Assert.Contains("Never replace a row from a detached snapshot.", text);
        Assert.Equal(RepoMemoryProposalStatus.Pending, (await inner.GetAsync(proposal.Id))!.Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "memory"), "*.tmp"));
    }

    private sealed class EditDuringClaimStore(IRepoMemoryProposalStore inner, Action onApplyClaim) : IRepoMemoryProposalStore
    {
        public Task<RepoMemoryProposal> AddAsync(RepoMemoryProposal proposal, CancellationToken ct = default) => inner.AddAsync(proposal, ct);
        public Task<RepoMemoryProposal?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<RepoMemoryProposal>> ListAsync(string? repositoryRoot, bool includeClosed, CancellationToken ct = default) => inner.ListAsync(repositoryRoot, includeClosed, ct);

        public async Task<RepoMemoryProposal?> SetStatusAsync(Guid id, long expectedVersion, RepoMemoryProposalStatus status, string? reason, CancellationToken ct = default)
        {
            var result = await inner.SetStatusAsync(id, expectedVersion, status, reason, ct);
            if (status == RepoMemoryProposalStatus.Applied) onApplyClaim();
            return result;
        }
    }

    [Fact]
    public async Task Append_preserves_crlf_files_and_discard_closes_the_proposal()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var path = Path.Combine(_root, "memory", "deep", "ui.md");
        File.WriteAllText(path, "# UI\r\n\r\nKeep blocking data work off the dispatcher.\r\n");
        var proposal = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "memory/deep/ui.md", null, "Coalesce rendering.", "UI convention.")).Proposal!;
        Assert.Equal(RepoMemoryProposalKind.Append, proposal.Kind);
        Assert.True((await RepoMemoryProposals.ApplyAsync(store, proposal.Id, proposal.Version, _options)).Ok);
        Assert.Equal("# UI\r\n\r\nKeep blocking data work off the dispatcher.\r\n\r\nCoalesce rendering.\r\n", File.ReadAllText(path));

        var other = (await RepoMemoryProposals.ProposeAsync(store, _root, _options, "MEMORY.md", null, "- New index line.", "Index entry.")).Proposal!;
        Assert.True((await RepoMemoryProposals.DiscardAsync(store, other.Id, other.Version)).Ok);
        Assert.False((await RepoMemoryProposals.ApplyAsync(store, other.Id, other.Version + 1, _options)).Ok);
        Assert.Empty(await store.ListAsync(_root, includeClosed: false));
        Assert.Equal(2, (await store.ListAsync(_root, includeClosed: true)).Count);
    }

    [Fact]
    public async Task Tools_search_read_and_propose_through_descriptors()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var tools = RepoMemoryTools.Create(() => _root, () => _options, store);
        Assert.Equal(RepoMemoryTools.ToolIds, tools.Select(t => t.ToolId));
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly, tools[0].SideEffect);
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly, tools[1].SideEffect);
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.WritesLocal, tools[2].SideEffect);

        var search = (RepoMemorySearchOutput)(await tools[0].Invoke(new RepoMemorySearchInput { Query = "sync journal" }, default))!;
        Assert.NotEmpty(search.Hits);
        var list = (RepoMemorySearchOutput)(await tools[0].Invoke(new RepoMemorySearchInput(), default))!;
        Assert.Equal(2, list.Topics.Count);
        var read = (RepoMemoryReadResult)(await tools[1].Invoke(new RepoMemoryReadInput { Path = "memory/persistence.md" }, default))!;
        Assert.Contains("detached snapshot", read.Content);
        var proposed = (RepoMemoryProposeOutput)(await tools[2].Invoke(new RepoMemoryProposeInput { Path = "MEMORY.md", NewText = "- Builds: one project at a time.", Rationale = "convention" }, default))!;
        Assert.True(proposed.Ok, proposed.Message);
        Assert.NotNull(proposed.Diff);
        Assert.Contains("not written", tools[2].FormatResultForUser!(proposed));
    }

    [Fact]
    public async Task A_proposal_records_the_conversation_that_made_it()
    {
        var store = new InMemoryRepoMemoryProposalStore();
        var tools = RepoMemoryTools.Create(() => _root, () => _options, store, conversationId: () => "session-7");
        var proposed = (RepoMemoryProposeOutput)(await tools[2].Invoke(new RepoMemoryProposeInput { Path = "MEMORY.md", NewText = "- Linked.", Rationale = "convention" }, default))!;

        Assert.True(proposed.Ok, proposed.Message);
        Assert.Equal("session-7", Assert.Single(await store.ListAsync(_root, includeClosed: false)).ConversationId);
    }

    [Fact]
    public void Coding_injector_list_adds_configured_injectors_after_repository_instructions()
    {
        var options = new CodingOptions { AdditionalInjectorNames = ["repoMemory", "personal-memory", "REPOMEMORY", " "] };
        var names = new CodingInjectorListProvider(Options.Create(options)).GetForProfile(Guid.Empty);
        Assert.Equal(["repoInstructions", "repoMemory", "personal-memory"], names);
        Assert.Equal(["repoInstructions"], new CodingInjectorListProvider().GetForProfile(Guid.Empty));
    }

    [Fact]
    public void Harness_registers_the_index_injector_and_a_default_proposal_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCoreCodingHarness();
        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IContextInjector>(), i => i.Name == RepoMemoryIndexInjector.InjectorName);
        Assert.IsType<InMemoryRepoMemoryProposalStore>(provider.GetRequiredService<IRepoMemoryProposalStore>());
    }
}
