using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding;
using SwarmCore.Coding.History;

namespace SwarmCode.Hosting.Tests;

/// <summary>The history source over the session index and turns journals, the file store's search, and the host composition.</summary>
public class ConversationHistoryHostingTests : TempDirTest
{
    private string Repo(string name)
    {
        var p = Path.Combine(Tmp, name);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public async Task Source_joins_the_index_with_turns_across_repositories_and_skips_agent_sessions()
    {
        var shop = Repo("shop");
        var billing = Repo("billing");
        var catalog = new WorkspaceSessionCatalog(Paths);
        var store = new FileConversationStore(Paths);

        var auth = await catalog.CreateAsync(shop, laneName: "alpha");
        await catalog.TouchAsync(auth.Id, titleHint: "Fix the login redirect");
        var t1 = await store.AppendUserTurnAsync(auth.Id, "Fix the login redirect");
        await store.CompleteAssistantTurnAsync(t1.Id, "Fixed AuthController.", null, TimeSpan.FromSeconds(3));
        var t2 = await store.AppendContinuationTurnAsync(auth.Id, "Continue.");
        await store.CompleteAssistantTurnAsync(t2.Id, "Added the test.", null, TimeSpan.FromSeconds(2));

        var invoices = await catalog.CreateAsync(billing, title: "Invoice PDF export");
        var t3 = await store.AppendUserTurnAsync(invoices.Id, "Add a PDF export for invoices");
        await store.CompleteAssistantTurnAsync(t3.Id, "Added InvoicePdfExporter using QuestPDF.", null, TimeSpan.FromSeconds(5));

        var agent = await catalog.CreateAsync(shop, title: "agent: check tests", parentSessionId: auth.Id);
        await store.AppendUserTurnAsync(agent.Id, "Run the tests");

        // A different store instance wrote the turns; the source must still see them (no stale cache).
        var source = new ConversationHistorySource(Paths, catalog);
        var all = await source.LoadAsync(HistoryScope.Everything);

        Assert.Equal(2, all.Count);
        var authDoc = Assert.Single(all, d => d.Id == auth.Id);
        Assert.Equal("Fix the login redirect", authDoc.Title);
        Assert.Equal(Path.GetFullPath(shop), authDoc.Project);
        Assert.Equal("shop", authDoc.ProjectName);
        Assert.Equal("alpha", authDoc.Lane);
        Assert.Equal(2, authDoc.Turns.Count);
        Assert.False(authDoc.Turns[0].IsContinuation);
        Assert.True(authDoc.Turns[1].IsContinuation);
        Assert.Equal("Added the test.", authDoc.Turns[1].AssistantText);
        Assert.NotNull(authDoc.Turns[1].CompletedUtc);

        var byProject = await source.LoadAsync(new HistoryScope(Project: "billing"));
        Assert.Equal(invoices.Id, Assert.Single(byProject).Id);
        Assert.Empty(await source.LoadAsync(new HistoryScope(SinceUtc: DateTime.UtcNow.AddMinutes(5))));
        Assert.Equal(3, (await source.LoadAsync(new HistoryScope(IncludeAgentSessions: true))).Count);

        // Search across what the source returns.
        var hits = ConversationSearch.Search(all, new HistorySearchOptions("questpdf") { IncludeResponses = true });
        Assert.Equal(invoices.Id, Assert.Single(hits).Session.Id);
        Assert.Empty(ConversationSearch.Search(all, new HistorySearchOptions("continue")));
    }

    [Fact]
    public async Task File_store_search_covers_titles_requests_and_replies_with_previews()
    {
        var store = new FileConversationStore(Paths);
        await store.GetOrCreateAsync("a", "Fix the login redirect");
        var t = await store.AppendUserTurnAsync("a", "The login page loops after OAuth returns");
        await store.CompleteAssistantTurnAsync(t.Id, "The callback dropped the state cookie.", null, TimeSpan.FromSeconds(1));
        await store.GetOrCreateAsync("b", "Invoice PDF export");
        await store.AppendUserTurnAsync("b", "Add a PDF export for invoices");

        var reloaded = new FileConversationStore(Paths);
        var byTitle = await reloaded.SearchAsync("invoice");
        Assert.Equal("b", Assert.Single(byTitle).Conversation.Id);

        var byReply = await reloaded.SearchAsync("state cookie");
        var hit = Assert.Single(byReply);
        Assert.Equal("a", hit.Conversation.Id);
        Assert.Equal(t.Id, hit.TurnId);
        Assert.Contains("state cookie", hit.Preview);

        var recent = await reloaded.SearchAsync("");
        Assert.Equal(2, recent.Count);
        Assert.Empty(await reloaded.SearchAsync("nothing matches this"));
    }

    [Fact]
    public async Task Host_registers_the_source_the_summary_service_and_the_history_tools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = Repo("repo"), DataPaths = Paths });
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.IsType<ConversationHistorySource>(sp.GetRequiredService<IConversationHistorySource>());
        Assert.NotNull(sp.GetRequiredService<WorkSummaryService>());
        foreach (var hosted in sp.GetServices<IHostedService>().OfType<ConversationHistoryToolRegistration>())
            await hosted.StartAsync(CancellationToken.None);
        var ids = sp.GetRequiredService<ICapabilityCatalog>().All().Select(t => t.ToolId).ToHashSet();
        Assert.Contains(ConversationHistoryTools.SearchToolId, ids);
        Assert.Contains(ConversationHistoryTools.SummaryToolId, ids);
        Assert.Contains("history.summary", sp.GetRequiredService<IOptions<CodingOptions>>().Value.AdditionalInstructions ?? "");
    }
}
