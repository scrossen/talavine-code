using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.History;
using SwarmCore.Coding.Review;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>History search, recency buckets, period parsing, the digest, and the work summary service.</summary>
public sealed class ConversationHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 15, 30, 0, DateTimeKind.Utc); // a Wednesday

    private static HistorySessionDocument Session(string id, string title, string project, DateTime lastActivity, params (string Ask, string? Answer)[] turns)
    {
        var list = new List<HistoryTurn>();
        var at = lastActivity.AddMinutes(-10 * turns.Length);
        foreach (var (ask, answer) in turns)
        {
            list.Add(new HistoryTurn(Guid.NewGuid(), ask, answer, at, answer is null ? null : at.AddMinutes(2)));
            at = at.AddMinutes(10);
        }
        return new HistorySessionDocument(id, title, project, lastActivity.AddHours(-1), lastActivity, list) { Lane = "alpha" };
    }

    private static readonly HistorySessionDocument Auth = Session("s1", "Fix the login redirect", @"C:\Code\Shop", Now,
        ("The login page loops after OAuth returns", "The callback dropped the state cookie; I fixed AuthController and added a test."),
        ("Also handle the expired token case", "Done: expired tokens now redirect to /login?expired=1."));

    private static readonly HistorySessionDocument Invoices = Session("s2", "Invoice PDF export", "/home/me/code/billing", Now.AddDays(-3),
        ("Add a PDF export for invoices", "Added InvoicePdfExporter using QuestPDF."));

    private static readonly HistorySessionDocument Continued = new("s3", "New session", @"C:\Code\Shop", Now.AddDays(-9), Now.AddDays(-9),
    [
        new HistoryTurn(Guid.NewGuid(), "Continue the previous work on the cart", "Resumed; the cart total now includes tax.", Now.AddDays(-9), Now.AddDays(-9).AddMinutes(1), IsContinuation: true),
    ]);

    // ---- search ----------------------------------------------------------------

    [Fact]
    public void Search_matches_titles_and_requests_but_not_responses_by_default()
    {
        var all = new[] { Auth, Invoices, Continued };
        var byTitle = ConversationSearch.Search(all, new HistorySearchOptions("invoice"));
        Assert.Equal(["s2"], byTitle.Select(r => r.Session.Id));
        Assert.Equal(HistoryMatchField.Title, byTitle[0].Field);

        var byPrompt = ConversationSearch.Search(all, new HistorySearchOptions("oauth"));
        var hit = Assert.Single(byPrompt);
        Assert.Equal(HistoryMatchField.Prompt, hit.Field);
        Assert.Contains("OAuth", hit.Preview);
        Assert.NotNull(hit.TurnId);

        // "QuestPDF" only appears in a reply.
        Assert.Empty(ConversationSearch.Search(all, new HistorySearchOptions("questpdf")));
        var withResponses = ConversationSearch.Search(all, new HistorySearchOptions("questpdf") { IncludeResponses = true });
        Assert.Equal(HistoryMatchField.Response, Assert.Single(withResponses).Field);
    }

    [Fact]
    public void Search_requires_every_term_and_accepts_quoted_phrases()
    {
        var all = new[] { Auth, Invoices };
        Assert.Equal(["s1"], ConversationSearch.Search(all, new HistorySearchOptions("login token")).Select(r => r.Session.Id));
        Assert.Empty(ConversationSearch.Search(all, new HistorySearchOptions("login invoice")));
        Assert.Equal(["s1"], ConversationSearch.Search(all, new HistorySearchOptions("\"expired token\"")).Select(r => r.Session.Id));
        Assert.Empty(ConversationSearch.Search(all, new HistorySearchOptions("\"token expired\"")));
        Assert.Equal(["expired token", "case"], ConversationSearch.Terms("\"expired token\" case"));
    }

    [Fact]
    public void Search_skips_continuation_prompts_filters_by_project_and_period_and_orders_by_recency()
    {
        var all = new[] { Invoices, Continued, Auth };
        // The continuation's text is host-authored; it is not the person's request.
        Assert.Empty(ConversationSearch.Search(all, new HistorySearchOptions("previous work")));
        // Its reply still counts when responses are searched.
        Assert.Equal(["s3"], ConversationSearch.Search(all, new HistorySearchOptions("tax") { IncludeResponses = true }).Select(r => r.Session.Id));

        var everything = ConversationSearch.Search(all, new HistorySearchOptions(""));
        Assert.Equal(["s1", "s2", "s3"], everything.Select(r => r.Session.Id));
        Assert.Equal(HistoryMatchField.None, everything[0].Field);
        Assert.StartsWith("The login page", everything[0].Preview);
        Assert.Equal("", everything[2].Preview); // only a continuation: nothing the person wrote

        Assert.Equal(["s1", "s3"], ConversationSearch.Search(all, new HistorySearchOptions("") { Project = "Shop" }).Select(r => r.Session.Id));
        Assert.Equal(["s1", "s3"], ConversationSearch.Search(all, new HistorySearchOptions("") { Project = @"c:\code\shop\" }).Select(r => r.Session.Id));
        Assert.Equal(["s1"], ConversationSearch.Search(all, new HistorySearchOptions("") { SinceUtc = Now.AddDays(-1) }).Select(r => r.Session.Id));
        Assert.Equal(["s2", "s3"], ConversationSearch.Search(all, new HistorySearchOptions("") { UntilUtc = Now.AddDays(-2) }).Select(r => r.Session.Id));
        Assert.Single(ConversationSearch.Search(all, new HistorySearchOptions(""), take: 1));
    }

    [Fact]
    public void Snippet_is_bounded_and_marks_cuts()
    {
        var text = string.Concat(Enumerable.Repeat("filler ", 40)) + "the NEEDLE sits here" + string.Concat(Enumerable.Repeat(" tail", 60));
        var snippet = ConversationSearch.Snippet(text, "needle");
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("NEEDLE", snippet);
        Assert.True(snippet.Length < 220, snippet.Length.ToString());
    }

    [Fact]
    public void Project_names_and_recency_buckets()
    {
        Assert.Equal("Shop", HistoryProjects.Name(@"C:\Code\Shop\"));
        Assert.Equal("billing", HistoryProjects.Name("/home/me/code/billing"));
        Assert.True(HistoryProjects.Matches(@"C:\Code\Shop", "shop"));
        Assert.True(HistoryProjects.Matches(@"C:\Code\Shop", "C:/Code/Shop"));
        Assert.False(HistoryProjects.Matches(@"C:\Code\Shop", "billing"));

        var today = new DateTime(2026, 10, 14);
        Assert.Equal(HistoryRecency.Today, HistoryRecency.Bucket(today.AddHours(9), today));
        Assert.Equal(HistoryRecency.Yesterday, HistoryRecency.Bucket(today.AddDays(-1).AddHours(23), today));
        Assert.Equal(HistoryRecency.Week, HistoryRecency.Bucket(today.AddDays(-7), today));
        Assert.Equal(HistoryRecency.Month, HistoryRecency.Bucket(today.AddDays(-8), today));
        Assert.Equal(HistoryRecency.Older, HistoryRecency.Bucket(today.AddDays(-31), today));
    }

    // ---- periods -----------------------------------------------------------------

    [Theory]
    [InlineData("today", "2026-10-14", "2026-10-15", "today")]
    [InlineData("what did I do yesterday", "2026-10-13", "2026-10-14", "yesterday")]
    [InlineData("summarize this week", "2026-10-12", "2026-10-15", "this week")]
    [InlineData("last week", "2026-10-05", "2026-10-12", "last week")]
    [InlineData("the past week", "2026-10-08", "2026-10-15", "the past week")]
    [InlineData("over the last 7 days", "2026-10-08", "2026-10-15", "the past 7 days")]
    [InlineData("past 3 days", "2026-10-12", "2026-10-15", "the past 3 days")]
    [InlineData("past 2 weeks", "2026-10-01", "2026-10-15", "the past 2 weeks")]
    [InlineData("this month", "2026-10-01", "2026-10-15", "this month")]
    [InlineData("last month", "2026-09-01", "2026-10-01", "last month")]
    [InlineData("the past month", "2026-09-15", "2026-10-15", "the past month")]
    [InlineData("2026-10-01", "2026-10-01", "2026-10-02", "Oct 1, 2026")]
    [InlineData("since 2026-10-01", "2026-10-01", "2026-10-15", "since Oct 1, 2026")]
    [InlineData("2026-10-07 to 2026-10-01", "2026-10-01", "2026-10-08", "Oct 1, 2026 to Oct 7, 2026")]
    public void Periods_parse_from_everyday_phrases(string text, string since, string until, string label)
    {
        var now = new DateTime(2026, 10, 14, 15, 30, 0); // Wednesday
        Assert.True(SummaryPeriod.TryParse(text, now, out var period), text);
        Assert.Equal(DateTime.Parse(since), period.SinceLocal);
        Assert.Equal(DateTime.Parse(until), period.UntilLocal);
        Assert.Equal(label, period.Label);
    }

    [Fact]
    public void Periods_all_time_and_unknown_text()
    {
        var now = new DateTime(2026, 10, 14, 15, 30, 0);
        Assert.True(SummaryPeriod.TryParse("everything", now, out var all));
        Assert.Equal(DateTime.MinValue, all.SinceLocal);
        Assert.Null(all.ToScope().SinceUtc);
        Assert.False(SummaryPeriod.TryParse("", now, out _));
        Assert.False(SummaryPeriod.TryParse("the weekend plan", now, out _));
        var week = SummaryPeriod.PastDays(7, now);
        Assert.Equal(new DateTime(2026, 10, 8), week.SinceLocal);
        Assert.Equal("the past 7 days", week.Label);
    }

    // ---- digest --------------------------------------------------------------------

    [Fact]
    public void Digest_groups_by_project_counts_turns_and_chunks_by_budget()
    {
        var period = new SummaryPeriod(DateTime.SpecifyKind(Now.AddDays(-7), DateTimeKind.Utc).ToLocalTime(), DateTime.SpecifyKind(Now.AddDays(1), DateTimeKind.Utc).ToLocalTime(), "the past week");
        var digest = WorkSummaryDigest.Build([Auth, Invoices, Continued], period);
        Assert.Equal(2, digest.SessionCount); // the 9-day-old session is outside the period
        Assert.Equal(3, digest.TurnCount);
        Assert.Equal(["billing", "Shop"], digest.Projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var text = Assert.Single(digest.Chunks);
        Assert.Contains("# Project: Shop", text);
        Assert.Contains("# Project: billing", text);
        Assert.Contains("## Fix the login redirect", text);
        Assert.Contains("- Asked: The login page loops after OAuth returns", text);
        Assert.Contains("- Final answer: Done: expired tokens", text);
        Assert.Contains("2 turns · lane alpha", text);

        var small = new WorkSummaryOptions { MaxChunkCharacters = 400 };
        var chunked = WorkSummaryDigest.Build([Auth, Invoices], period, small);
        Assert.Equal(2, chunked.Chunks.Count);
        Assert.All(chunked.Chunks, c => Assert.StartsWith("# Project: ", c));
        Assert.All(chunked.Chunks, c => Assert.True(c.Length <= 400 + 20, c.Length.ToString()));

        var tiny = new WorkSummaryOptions { MaxRequestCharacters = 12, MaxRequestsPerSession = 1, MaxAnswerCharacters = 10 };
        var clipped = WorkSummaryDigest.Build([Auth], period, tiny).Text;
        Assert.Contains("- Asked: The login p…", clipped);
        Assert.Contains("- ... and 1 more requests", clipped);
        Assert.Contains("- Final answer: Done: exp…", clipped);
    }

    // ---- summary service ------------------------------------------------------------

    private sealed class ListSource(params HistorySessionDocument[] sessions) : IConversationHistorySource
    {
        public List<HistoryScope> Scopes { get; } = [];
        public Task<IReadOnlyList<HistorySessionDocument>> LoadAsync(HistoryScope scope, CancellationToken ct = default)
        {
            Scopes.Add(scope);
            return Task.FromResult<IReadOnlyList<HistorySessionDocument>>(sessions.Where(s => scope.Covers(s.Project, s.CreatedUtc, s.LastActivityUtc)).ToList());
        }
    }

    private sealed class ScriptedCompletion : IModelPanelCompletion
    {
        public ConcurrentQueue<(string Lane, string Prompt)> Calls { get; } = new();
        public Func<string, string> Reply { get; set; } = prompt => "## Report\n" + prompt.Length;
        public Task<string> CompleteAsync(CodingLane lane, string prompt, int maxTokens, CancellationToken ct)
        {
            Calls.Enqueue((lane.Name, prompt));
            return Task.FromResult(Reply(prompt));
        }
    }

    private static readonly CodingLane Native = new("claude", "ClaudeCodeNative", "claude-code:default") { Priority = 0 };
    private static readonly CodingLane Alpha = new("alpha", "OpenRouter", "a/one") { Priority = 10 };
    private static readonly CodingLane Beta = new("beta", "DeepSeek", "b-one") { Priority = 11 };

    private static WorkSummaryService Build(IConversationHistorySource source, ScriptedCompletion completion, WorkSummaryOptions? options = null, params CodingLane[] lanes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = lanes.ToList() })));
        var provider = services.BuildServiceProvider();
        return new WorkSummaryService(source, completion, provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options ?? new WorkSummaryOptions()));
    }

    private static SummaryPeriod Week() => SummaryPeriod.PastDays(7, DateTime.SpecifyKind(Now, DateTimeKind.Utc).ToLocalTime());

    [Fact]
    public async Task Summary_uses_the_first_api_lane_and_fences_the_digest()
    {
        var source = new ListSource(Auth, Invoices, Continued);
        var completion = new ScriptedCompletion { Reply = _ => "You fixed the login redirect and added invoice PDFs." };
        var service = Build(source, completion, null, Native, Alpha, Beta);

        var result = await service.SummarizeAsync(new WorkSummaryRequest(Week()));

        Assert.True(result.Success, result.Message);
        Assert.True(result.ModelWritten);
        Assert.Equal("alpha", result.Lane);
        Assert.Equal("You fixed the login redirect and added invoice PDFs.", result.Summary);
        Assert.Equal(2, result.SessionCount);
        var call = Assert.Single(completion.Calls);
        Assert.Equal("alpha", call.Lane);
        Assert.Contains("SESSION DIGEST", call.Prompt);
        Assert.Contains("Fix the login redirect", call.Prompt);
        Assert.DoesNotContain("Continue the previous work", call.Prompt); // outside the period
        var scope = Assert.Single(source.Scopes);
        Assert.NotNull(scope.SinceUtc);
    }

    [Fact]
    public async Task Summary_honours_a_named_lane_and_project_filter()
    {
        var source = new ListSource(Auth, Invoices);
        var completion = new ScriptedCompletion { Reply = _ => "Billing work." };
        var service = Build(source, completion, null, Alpha, Beta);

        var result = await service.SummarizeAsync(new WorkSummaryRequest(Week(), Project: "billing", LaneName: "beta"));

        Assert.Equal("beta", result.Lane);
        Assert.Equal(["billing"], result.Projects);
        Assert.Equal(1, result.SessionCount);
        Assert.DoesNotContain("login redirect", Assert.Single(completion.Calls).Prompt);
    }

    [Fact]
    public async Task Summary_without_an_api_lane_returns_the_digest_and_explains()
    {
        var completion = new ScriptedCompletion();
        var service = Build(new ListSource(Auth), completion, null, Native);

        var result = await service.SummarizeAsync(new WorkSummaryRequest(Week()));

        Assert.True(result.Success);
        Assert.False(result.ModelWritten);
        Assert.Null(result.Lane);
        Assert.Contains("No API model lane", result.Message);
        Assert.Contains("## Fix the login redirect", result.Summary);
        Assert.Empty(completion.Calls);
    }

    [Fact]
    public async Task Summary_of_an_empty_period_and_digest_only_make_no_model_call()
    {
        var completion = new ScriptedCompletion();
        var service = Build(new ListSource(Continued), completion, null, Alpha);

        var empty = await service.SummarizeAsync(new WorkSummaryRequest(Week()));
        Assert.True(empty.Success);
        Assert.Equal(0, empty.SessionCount);
        Assert.Contains("No sessions had activity the past 7 days", empty.Summary);

        var digest = await Build(new ListSource(Auth), completion, null, Alpha).SummarizeAsync(new WorkSummaryRequest(Week()) { DigestOnly = true });
        Assert.False(digest.ModelWritten);
        Assert.Contains("- Asked: The login page loops", digest.Summary);
        Assert.Empty(completion.Calls);
    }

    [Fact]
    public async Task Long_digests_are_summarized_in_parts_then_combined()
    {
        var completion = new ScriptedCompletion { Reply = prompt => prompt.Contains("PARTIAL REPORT 1", StringComparison.Ordinal) ? "combined report" : "part report" };
        var service = Build(new ListSource(Auth, Invoices), completion, new WorkSummaryOptions { MaxChunkCharacters = 400 }, Alpha);

        var result = await service.SummarizeAsync(new WorkSummaryRequest(Week()));

        Assert.Equal("combined report", result.Summary);
        Assert.Equal(3, completion.Calls.Count);
        var prompts = completion.Calls.Select(c => c.Prompt).ToList();
        Assert.Contains(prompts, p => p.Contains("PART 1 OF 2", StringComparison.Ordinal));
        Assert.Contains(prompts, p => p.Contains("PART 2 OF 2", StringComparison.Ordinal));
        Assert.Contains(prompts, p => p.Contains("PARTIAL REPORT 2 OF 2", StringComparison.Ordinal) && p.Contains("part report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_or_refusing_model_falls_back_to_the_digest()
    {
        var failing = new ScriptedCompletion { Reply = _ => throw new InvalidOperationException("HTTP 500") };
        var failed = await Build(new ListSource(Auth), failing, null, Alpha).SummarizeAsync(new WorkSummaryRequest(Week()));
        Assert.True(failed.Success);
        Assert.False(failed.ModelWritten);
        Assert.Contains("HTTP 500", failed.Message);
        Assert.Contains("## Fix the login redirect", failed.Summary);

        var refusing = new ScriptedCompletion { Reply = _ => "I'm sorry, but I can't help with that request." };
        var refused = await Build(new ListSource(Auth), refusing, null, Alpha).SummarizeAsync(new WorkSummaryRequest(Week()));
        Assert.False(refused.ModelWritten);
        Assert.Contains("did not write a summary", refused.Message);
    }

    // ---- tools and composition --------------------------------------------------------

    [Fact]
    public async Task History_tools_register_only_when_the_host_supplies_a_source()
    {
        var bare = new ServiceCollection();
        bare.AddLogging();
        bare.AddHttpClient();
        bare.AddSwarmCoreCoding(o => o.RepositoryRoot = Path.GetTempPath());
        bare.AddSwarmCoreConversationHistory();
        using (var sp = bare.BuildServiceProvider())
        {
            foreach (var hosted in sp.GetServices<IHostedService>().OfType<ConversationHistoryToolRegistration>()) await hosted.StartAsync(CancellationToken.None);
            Assert.DoesNotContain(sp.GetRequiredService<ICapabilityCatalog>().All(), t => t.ToolId.StartsWith("history.", StringComparison.Ordinal));
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IConversationHistorySource>(new ListSource(Auth));
        services.AddSwarmCoreCoding(o => o.RepositoryRoot = Path.GetTempPath());
        services.AddSwarmCoreConversationHistory();
        using (var sp = services.BuildServiceProvider())
        {
            Assert.Single(sp.GetServices<IHostedService>().OfType<ConversationHistoryToolRegistration>());
            foreach (var hosted in sp.GetServices<IHostedService>().OfType<ConversationHistoryToolRegistration>()) await hosted.StartAsync(CancellationToken.None);
            var ids = sp.GetRequiredService<ICapabilityCatalog>().All().Select(t => t.ToolId).ToHashSet();
            Assert.Contains(ConversationHistoryTools.SearchToolId, ids);
            Assert.Contains(ConversationHistoryTools.SummaryToolId, ids);
            Assert.NotNull(sp.GetRequiredService<WorkSummaryService>());
        }
    }

    [Fact]
    public async Task Search_tool_applies_period_project_and_response_options()
    {
        var tool = ConversationHistoryTools.CreateSearch(new ListSource(Auth, Invoices, Continued));
        var output = Assert.IsType<HistorySearchOutput>(await tool.Invoke(new HistorySearchInput { Query = "questpdf", IncludeResponses = true }, CancellationToken.None));
        var hit = Assert.Single(output.Results);
        Assert.Equal("s2", hit.SessionId);
        Assert.Equal("billing", hit.ProjectName);
        Assert.Equal("response", hit.MatchedIn);

        var none = Assert.IsType<HistorySearchOutput>(await tool.Invoke(new HistorySearchInput { Query = "login", Project = "billing" }, CancellationToken.None));
        Assert.Equal(0, none.Count);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
    }
}
