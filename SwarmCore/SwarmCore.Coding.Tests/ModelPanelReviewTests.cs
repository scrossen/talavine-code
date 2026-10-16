using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Review;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>The model panel: independent answers, blind judging, Borda tally, refusal handling, and the request framing.</summary>
public sealed class ModelPanelReviewTests
{
    private static readonly CodingLane Alpha = new("alpha", "OpenRouter", "a/one") { Priority = 10 };
    private static readonly CodingLane Beta = new("beta", "DeepSeek", "b-one") { Priority = 11 };
    private static readonly CodingLane Gamma = new("gamma", "Venice", "g-one") { Priority = 12 };
    private static readonly CodingLane Native = new("claude", "ClaudeCodeNative", "claude-code:default") { Priority = 0 };

    /// <summary>Scripted completions: the first call per lane is its answer, later calls are its judgments.</summary>
    private sealed class ScriptedCompletion : IModelPanelCompletion
    {
        public ConcurrentDictionary<string, string> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, Func<string, string>> Judgments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<(string Lane, string Prompt)> Calls { get; } = new();
        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string> CompleteAsync(CodingLane lane, string prompt, int maxTokens, CancellationToken ct)
        {
            Calls.Enqueue((lane.Name, prompt));
            if (Failing.Contains(lane.Name)) throw new InvalidOperationException("HTTP 500 from " + lane.Name);
            if (prompt.Contains("ANSWER A", StringComparison.Ordinal))
                return Task.FromResult(Judgments.TryGetValue(lane.Name, out var judge) ? judge(prompt) : "{\"ranking\":[]}");
            return Task.FromResult(Answers.GetValueOrDefault(lane.Name, "answer from " + lane.Name));
        }
    }

    private static ModelPanelReviewService Build(ScriptedCompletion completion, params CodingLane[] lanes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = lanes.ToList() })));
        var provider = services.BuildServiceProvider();
        return new ModelPanelReviewService(completion, provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ModelPanelReviewOptions()));
    }

    /// <summary>Finds the label the judge saw for the answer text of <paramref name="lane"/>.</summary>
    private static string LabelOf(string prompt, string lane)
    {
        var marker = "answer from " + lane;
        var at = prompt.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the judge prompt should contain {lane}'s answer");
        var head = prompt[..at];
        var tag = head.LastIndexOf("ANSWER ", StringComparison.Ordinal);
        return head.Substring(tag + 7, 1);
    }

    [Fact]
    public async Task Every_api_lane_answers_then_judges_blind_and_the_borda_winner_is_reported()
    {
        var completion = new ScriptedCompletion();
        // Every judge prefers beta, then alpha, then gamma, whatever label each answer got in its own view.
        foreach (var judge in new[] { "alpha", "beta", "gamma" })
            completion.Judgments[judge] = prompt =>
                $$"""{"ranking":["{{LabelOf(prompt, "beta")}}","{{LabelOf(prompt, "alpha")}}","{{LabelOf(prompt, "gamma")}}"],"assessments":[{"answer":"{{LabelOf(prompt, "beta")}}","strengths":"complete","weaknesses":"long"}],"verdict":"{{judge}} prefers the thorough one"}""";
        var service = Build(completion, Native, Alpha, Beta, Gamma);

        var result = await service.ReviewAsync(new ModelPanelReviewRequest("Which sort is stable?", "some code"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("beta", result.Winner);
        Assert.Equal(3, result.Answers.Count); // the native lane is not a completion
        Assert.All(result.Answers, a => Assert.True(a.Usable));
        Assert.Equal(3, result.Judgments.Count);
        Assert.Equal(9, result.Scores["beta"]);   // 3 judges x 3 points
        Assert.Equal(6, result.Scores["alpha"]);
        Assert.Equal(3, result.Scores["gamma"]);
        Assert.Equal(3, result.FirstPlaceVotes["beta"]);
        Assert.All(result.Judgments, j => Assert.Equal(["beta", "alpha", "gamma"], j.Ranking));
        Assert.Contains(result.Judgments, j => j.Assessments.Any(a => a.Lane == "beta" && a.Strengths == "complete"));
        Assert.Contains("Winner: beta (DeepSeek/b-one)", result.Summary);
        Assert.Contains("alpha prefers the thorough one", result.Summary);

        // Blind: a judge's prompt names no lane, provider or model, and the answers rotate so "A" is not always the same lane.
        var judgePrompts = completion.Calls.Where(c => c.Prompt.Contains("ANSWER A", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, judgePrompts.Count);
        foreach (var (_, prompt) in judgePrompts)
            foreach (var hidden in new[] { "alpha", "beta", "gamma", "OpenRouter", "DeepSeek", "Venice" })
                Assert.DoesNotContain(hidden, prompt.Replace("answer from alpha", "").Replace("answer from beta", "").Replace("answer from gamma", ""), StringComparison.Ordinal);
        Assert.True(judgePrompts.Select(p => LabelOf(p.Prompt, "alpha")).Distinct().Count() > 1, "answers should rotate between judges");
        // The material is fenced as untrusted content in both prompts.
        Assert.All(completion.Calls, c => Assert.Contains("MATERIAL UNDER REVIEW", c.Prompt));
    }

    [Fact]
    public async Task A_declining_model_is_excluded_from_the_vote_but_named_in_the_report()
    {
        var completion = new ScriptedCompletion();
        completion.Answers["alpha"] = "I'm sorry, but I can't help with that request.";
        completion.Judgments["beta"] = prompt => $$"""{"ranking":["{{LabelOf(prompt, "gamma")}}","{{LabelOf(prompt, "beta")}}"],"verdict":"gamma wins"}""";
        completion.Judgments["gamma"] = prompt => $$"""{"ranking":["{{LabelOf(prompt, "gamma")}}","{{LabelOf(prompt, "beta")}}"],"verdict":"gamma wins"}""";
        var service = Build(completion, Alpha, Beta, Gamma);

        var result = await service.ReviewAsync(new ModelPanelReviewRequest("Write the thing"));

        Assert.True(result.Success);
        Assert.Equal("gamma", result.Winner);
        var declined = Assert.Single(result.Answers, a => a.Refused);
        Assert.Equal("alpha", declined.Lane);
        Assert.Equal(2, result.Judgments.Count); // only models that answered judge
        Assert.DoesNotContain(result.Judgments, j => j.Judge == "alpha");
        Assert.Contains("alpha (OpenRouter/a/one", result.Summary);
        Assert.Contains("(declined)", result.Summary);
        // The judges never saw the refusal as a candidate.
        Assert.All(completion.Calls.Where(c => c.Prompt.Contains("ANSWER A", StringComparison.Ordinal)), c => Assert.DoesNotContain("can't help", c.Prompt));
    }

    [Fact]
    public async Task Failures_and_unparseable_judgments_are_reported_not_fatal()
    {
        var completion = new ScriptedCompletion();
        completion.Failing.Add("gamma");
        completion.Judgments["alpha"] = _ => "I think B is best, honestly."; // no JSON
        completion.Judgments["beta"] = prompt => $$"""Sure! {"ranking":["{{LabelOf(prompt, "alpha")}}","{{LabelOf(prompt, "beta")}}"],"verdict":"alpha"} hope that helps""";
        var service = Build(completion, Alpha, Beta, Gamma);

        var result = await service.ReviewAsync(new ModelPanelReviewRequest("q"));

        Assert.True(result.Success);
        var failed = Assert.Single(result.Answers, a => a.Error is not null);
        Assert.Equal("gamma", failed.Lane);
        Assert.Contains("HTTP 500", failed.Error);
        var broken = Assert.Single(result.Judgments, j => j.Judge == "alpha");
        Assert.NotNull(broken.Error);
        Assert.Empty(broken.Ranking);
        var fine = Assert.Single(result.Judgments, j => j.Judge == "beta");
        Assert.Null(fine.Error);
        Assert.Equal(["alpha", "beta"], fine.Ranking);
        Assert.Equal("alpha", result.Winner);
    }

    [Fact]
    public async Task Fewer_than_two_api_lanes_or_an_unknown_lane_is_a_clear_failure()
    {
        var completion = new ScriptedCompletion();
        var one = await Build(completion, Native, Alpha).ReviewAsync(new ModelPanelReviewRequest("q"));
        Assert.False(one.Success);
        Assert.Contains("at least two API model lanes", one.Message);
        Assert.Empty(completion.Calls);

        var unknown = await Build(completion, Alpha, Beta).ReviewAsync(new ModelPanelReviewRequest("q", LaneNames: ["alpha", "nope"]));
        Assert.False(unknown.Success);
        Assert.Contains("nope", unknown.Message);
        Assert.Empty(completion.Calls);

        var empty = await Build(completion, Alpha, Beta).ReviewAsync(new ModelPanelReviewRequest("   "));
        Assert.False(empty.Success);
    }

    [Fact]
    public async Task Named_lanes_select_the_panel_and_duplicate_routes_collapse()
    {
        var completion = new ScriptedCompletion();
        completion.Judgments["alpha"] = prompt => $$"""{"ranking":["{{LabelOf(prompt, "alpha")}}","{{LabelOf(prompt, "gamma")}}"]}""";
        completion.Judgments["gamma"] = prompt => $$"""{"ranking":["{{LabelOf(prompt, "alpha")}}","{{LabelOf(prompt, "gamma")}}"]}""";
        var alias = new CodingLane("alpha-alias", "openrouter", "a/one") { Priority = 15 }; // same route as alpha, later in lane order
        var service = Build(completion, Alpha, alias, Beta, Gamma);

        var result = await service.ReviewAsync(new ModelPanelReviewRequest("q", LaneNames: ["gamma", "alpha", "alpha-alias"]));

        Assert.True(result.Success, result.Message);
        Assert.Equal(["alpha", "gamma"], result.Answers.Select(a => a.Lane).Order().ToArray());
        Assert.Equal("alpha", result.Winner);
    }

    [Fact]
    public void Tally_breaks_ties_by_first_place_votes_then_lane_order()
    {
        var lanes = new[] { "a", "b", "c" };
        // a: 3+1 = 4 (one first place); b: 2+3 = 5 (one first place); c: 1+2 = 3.
        var (scores, firsts, winner) = ModelPanelReviewService.Tally(lanes, [
            new("j1", ["a", "b", "c"], null, [], null),
            new("j2", ["b", "c", "a"], null, [], null),
            new("broken", [], null, [], "no json"),
        ]);
        Assert.Equal("b", winner);
        Assert.Equal(4, scores["a"]);
        Assert.Equal(5, scores["b"]);
        Assert.Equal(1, firsts["a"]);
        // Equal points and firsts: the earlier lane wins.
        var (_, _, tie) = ModelPanelReviewService.Tally(["x", "y"], [new("j", ["x", "y"], null, [], null), new("k", ["y", "x"], null, [], null)]);
        Assert.Equal("x", tie);
    }

    [Fact]
    public void Judgment_parsing_accepts_labels_with_or_without_the_word_answer_and_ignores_unknown_labels()
    {
        var labels = new List<(string Label, string Lane)> { ("A", "alpha"), ("B", "beta") };
        var parsed = ModelPanelReviewService.ParseJudgment("judge",
            """Here you go: {"ranking":["Answer B","a","Z"],"assessments":[{"answer":"answer: b","strengths":"s","weaknesses":"w"}],"verdict":"B"} done""", labels);
        Assert.Null(parsed.Error);
        Assert.Equal(["beta", "alpha"], parsed.Ranking);
        Assert.Equal("beta", Assert.Single(parsed.Assessments).Lane);
        Assert.Equal("B", parsed.Verdict);
        Assert.NotNull(ModelPanelReviewService.ParseJudgment("judge", "{\"ranking\":[\"Q\"]}", labels).Error);
    }

    [Theory]
    [InlineData("Please review this design for the cache layer", true)]
    [InlineData("Can I get a second opinion on this approach?", true)]
    [InlineData("Which approach is better, A or B?", true)]
    [InlineData("Sanity-check my migration plan", true)]
    [InlineData("Can you review the auth module before I merge?", true)]
    [InlineData("Here is my plan for the cache. Please critique it.", true)]
    [InlineData("What do you think of this schema?", true)]
    [InlineData("Add a unit test for the parser", false)]
    [InlineData("Fix the failing build", false)]
    [InlineData("Fix the bug on the code review page", false)]
    [InlineData("Add a PR review webhook to the bot", false)]
    [InlineData("Write adversarial tests for the parser", false)]
    [InlineData("", false)]
    public void Review_requests_are_recognised_and_framed_without_losing_the_original(string message, bool expected)
    {
        Assert.Equal(expected, ModelPanelReviewPrompts.RequestsReview(message));
        if (!expected) return;
        var framed = ModelPanelReviewPrompts.FrameRequest(message);
        Assert.StartsWith("[Review requested]", framed);
        Assert.EndsWith(message, framed);
        Assert.Contains("review.panel", framed);
    }

    [Fact]
    public void Candidates_skip_native_lanes_and_cap_the_panel_in_lane_order()
    {
        var lanes = new[] { Native, Alpha, Beta, Gamma, new CodingLane("delta", "OpenAI", "d") };
        Assert.Equal(["alpha", "beta", "gamma"], ModelPanelReviewService.Candidates(lanes, null, 3).Select(l => l.Name));
        Assert.Equal(["beta", "delta"], ModelPanelReviewService.Candidates(lanes, ["delta", "beta"], 1).Select(l => l.Name));
        Assert.Equal(["alpha", "beta"], ModelPanelReviewService.Candidates(lanes, null, 1).Select(l => l.Name)); // never fewer than two
    }
}
