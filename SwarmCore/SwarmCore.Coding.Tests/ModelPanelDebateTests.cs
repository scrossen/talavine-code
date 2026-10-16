using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Review;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>The judged comparison: picked models answer, review each other and may concede; the judge decides only a disagreement.</summary>
public sealed class ModelPanelDebateTests
{
    private static readonly CodingLane Alpha = new("alpha", "OpenRouter", "a/one") { Priority = 10 };
    private static readonly CodingLane Beta = new("beta", "DeepSeek", "b-one") { Priority = 11 };
    private static readonly CodingLane Gamma = new("gamma", "Venice", "g-one") { Priority = 12 };
    private static readonly CodingLane Delta = new("delta", "OpenAI", "d-one") { Priority = 13 };
    private static readonly CodingLane Native = new("claude", "ClaudeCodeNative", "claude-code:default") { Priority = 0 };

    /// <summary>
    /// Answers are "answer from {lane}"; a prompt with "(YOURS)" is a deliberation (the script gets the prompt and the
    /// contender's round), any other prompt with answer fences is the judge's.
    /// </summary>
    private sealed class Script : IModelPanelCompletion
    {
        public ConcurrentDictionary<string, Func<string, int, string>> Stances { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, Func<string, string>> Judges { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<(string Lane, string Model, string Prompt)> Calls { get; } = new();
        private readonly ConcurrentDictionary<string, int> _rounds = new(StringComparer.OrdinalIgnoreCase);

        public Task<string> CompleteAsync(CodingLane lane, string prompt, int maxTokens, CancellationToken ct)
        {
            Calls.Enqueue((lane.Name, lane.Model, prompt));
            if (prompt.Contains("(YOURS)", StringComparison.Ordinal))
            {
                var round = _rounds.AddOrUpdate(lane.Name, 1, (_, r) => r + 1);
                return Task.FromResult(Stances.TryGetValue(lane.Name, out var stance) ? stance(prompt, round) : Stand);
            }
            if (prompt.Contains("ANSWER A", StringComparison.Ordinal))
                return Task.FromResult(Judges.TryGetValue(lane.Name, out var judge) ? judge(prompt) : "{\"ranking\":[]}");
            return Task.FromResult("answer from " + lane.Name);
        }
    }

    private const string Stand = """{"decision":"stand","reason":"mine is correct"}""";

    private static string ConcedeTo(string prompt, string lane, string reason = "it is more complete")
        => $$"""{"decision":"concede","better":"{{LabelOf(prompt, lane)}}","reason":"{{reason}}"}""";

    private static string LabelOf(string prompt, string lane)
    {
        var at = prompt.IndexOf("answer from " + lane + "\n", StringComparison.Ordinal);
        Assert.True(at >= 0, $"the prompt should contain {lane}'s answer");
        var head = prompt[..at];
        var tag = head.LastIndexOf("ANSWER ", StringComparison.Ordinal);
        return head.Substring(tag + 7, 1);
    }

    private static ModelPanelReviewService Build(Script script, params CodingLane[] lanes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = lanes.ToList() })));
        var provider = services.BuildServiceProvider();
        return new ModelPanelReviewService(script, provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ModelPanelReviewOptions()));
    }

    private static ModelPanelReviewRequest Compare(string judge, params string[] models)
        => new("Which sort is stable?", Models: models.Select(m => ModelPanelPick.Parse(m)!).ToList(), Judge: ModelPanelPick.Parse(judge));

    [Fact]
    public async Task When_the_others_concede_the_models_agree_and_the_judge_is_never_called()
    {
        var script = new Script();
        script.Stances["alpha"] = (p, _) => ConcedeTo(p, "beta");
        script.Stances["gamma"] = (p, _) => ConcedeTo(p, "beta", "beta handles the edge case");
        var service = Build(script, Alpha, Beta, Gamma, Delta);

        var result = await service.ReviewAsync(Compare("delta", "alpha", "beta", "gamma"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("beta", result.Winner);
        Assert.NotNull(result.Debate);
        Assert.True(result.Debate!.Consensus);
        Assert.Equal("consensus", result.Debate.DecidedBy);
        Assert.Equal(1, result.Debate.Rounds);
        Assert.Equal(3, result.Scores["beta"]);
        Assert.Empty(result.Judgments);
        Assert.DoesNotContain(script.Calls, c => c.Lane == "delta");
        // Everyone sees the same labels, so a contender's own answer is not always "A".
        var own = script.Calls.Where(c => c.Prompt.Contains("(YOURS)")).Select(c => c.Prompt[c.Prompt.IndexOf("Your answer is labelled ", StringComparison.Ordinal) + 24]).ToList();
        Assert.Equal(3, own.Distinct().Count());
        Assert.Contains("The models agreed: beta", result.Summary);
        Assert.Contains("gamma conceded to beta: beta handles the edge case", result.Summary);
    }

    [Fact]
    public async Task A_disagreement_goes_to_the_judge_who_sees_the_standing_answers_and_the_debate_blind()
    {
        var script = new Script();
        script.Stances["gamma"] = (p, _) => ConcedeTo(p, "alpha");
        script.Judges["delta"] = p => $$"""{"ranking":["{{LabelOf(p, "beta")}}","{{LabelOf(p, "alpha")}}"],"verdict":"beta is correct"}""";
        var service = Build(script, Alpha, Beta, Gamma, Delta);

        var result = await service.ReviewAsync(Compare("delta", "alpha", "beta", "gamma"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("beta", result.Winner);
        Assert.Equal("judge", result.Debate!.DecidedBy);
        Assert.False(result.Debate.Consensus);
        Assert.Equal(["alpha", "beta"], result.Debate.Standing);
        Assert.Equal(2, result.Debate.Rounds); // round 2: alpha and beta both stood, so the debate stopped
        Assert.Equal(2, result.Scores["alpha"]); // alpha plus gamma's concession
        var judgment = Assert.Single(result.Judgments);
        Assert.Equal("delta", judgment.Judge);
        var judgePrompt = Assert.Single(script.Calls, c => c.Lane == "delta").Prompt;
        Assert.Contains("DEBATE", judgePrompt);
        Assert.Contains("conceded to answer " + LabelOf(judgePrompt, "alpha"), judgePrompt);
        Assert.DoesNotContain("answer from gamma", judgePrompt); // a withdrawn answer is not a candidate
        foreach (var hidden in new[] { "alpha", "beta", "gamma", "OpenRouter", "DeepSeek", "Venice" })
            Assert.DoesNotContain(hidden, judgePrompt.Replace("answer from alpha", "").Replace("answer from beta", ""), StringComparison.Ordinal);
        Assert.Contains("The judge picked beta", result.Summary);
    }

    [Fact]
    public async Task Concession_chains_resolve_across_rounds_and_later_rounds_see_the_earlier_reasons()
    {
        var script = new Script();
        script.Stances["gamma"] = (p, _) => ConcedeTo(p, "alpha", "alpha is tighter");
        script.Stances["alpha"] = (p, round) => round == 1 ? Stand : ConcedeTo(p, "beta");
        var service = Build(script, Alpha, Beta, Gamma, Delta);

        var result = await service.ReviewAsync(Compare("delta", "alpha", "beta", "gamma"));

        Assert.Equal("beta", result.Winner);
        Assert.True(result.Debate!.Consensus);
        Assert.Equal(2, result.Debate.Rounds);
        Assert.Equal(3, result.Scores["beta"]); // beta, alpha, and gamma through alpha
        var secondRound = script.Calls.Where(c => c.Lane == "alpha" && c.Prompt.Contains("(YOURS)")).Skip(1).Single().Prompt;
        Assert.Contains("alpha is tighter", secondRound);
        Assert.DoesNotContain(script.Calls, c => c.Lane == "delta");
        // gamma conceded in round 1, so it is not asked again.
        Assert.Single(script.Calls, c => c.Lane == "gamma" && c.Prompt.Contains("(YOURS)"));
    }

    [Fact]
    public async Task A_ring_of_mutual_concessions_cancels_and_the_judge_decides()
    {
        var script = new Script();
        script.Stances["alpha"] = (p, _) => ConcedeTo(p, "beta");
        script.Stances["beta"] = (p, _) => ConcedeTo(p, "alpha");
        script.Judges["gamma"] = p => $$"""{"ranking":["{{LabelOf(p, "alpha")}}","{{LabelOf(p, "beta")}}"],"verdict":"alpha"}""";
        var service = Build(script, Alpha, Beta, Gamma);

        var result = await service.ReviewAsync(Compare("gamma", "alpha", "beta"));

        Assert.Equal("alpha", result.Winner);
        Assert.Equal("judge", result.Debate!.DecidedBy);
        Assert.Equal(["alpha", "beta"], result.Debate.Standing);
    }

    [Fact]
    public async Task A_failed_judge_falls_back_to_the_most_backed_standing_answer()
    {
        var script = new Script();
        script.Stances["gamma"] = (p, _) => ConcedeTo(p, "beta");
        script.Judges["delta"] = _ => "I cannot decide.";
        var service = Build(script, Alpha, Beta, Gamma, Delta);

        var result = await service.ReviewAsync(Compare("delta", "alpha", "beta", "gamma"));

        Assert.True(result.Success);
        Assert.Equal("beta", result.Winner);
        Assert.Equal("support", result.Debate!.DecidedBy);
        Assert.Contains("did not return a usable decision", result.Message);
    }

    [Fact]
    public async Task Picks_can_name_another_model_on_a_lane_and_bad_picks_fail_before_any_call()
    {
        var script = new Script();
        script.Stances["alpha"] = (p, _) => ConcedeTo(p, "alpha:a/two");
        script.Stances["beta"] = (p, _) => ConcedeTo(p, "alpha:a/two");
        var service = Build(script, Native, Alpha, Beta);

        var result = await service.ReviewAsync(Compare("beta", "alpha", "alpha:a/two", "beta", "alpha:a/one"));

        Assert.True(result.Success, result.Message);
        Assert.Equal(["alpha", "alpha:a/two", "beta"], result.Answers.Select(a => a.Lane)); // alpha:a/one is alpha's own route
        Assert.Contains(script.Calls, c => c.Lane == "alpha:a/two" && c.Model == "a/two");
        Assert.Equal("alpha:a/two", result.Winner);
        Assert.Contains("OpenRouter/a/two", result.Summary);

        var calls = script.Calls.Count;
        Assert.Contains("Unknown lane 'nope'", (await service.ReviewAsync(Compare("nope", "alpha", "beta"))).Message);
        Assert.Contains("native CLI lane", (await service.ReviewAsync(Compare("alpha", "alpha", "claude"))).Message);
        Assert.Contains("at least two different models", (await service.ReviewAsync(Compare("beta", "alpha", "alpha:a/one"))).Message);
        Assert.Equal(calls, script.Calls.Count);
    }

    [Fact]
    public void Positions_count_as_standing_unless_they_clearly_concede_to_another_answer()
    {
        var labels = new List<(string Label, string Lane)> { ("A", "alpha"), ("B", "beta") };
        var concede = ModelPanelReviewService.ParsePosition("alpha", 1, """ok {"decision":"Concede","better":"Answer B","reason":"r"}""", labels);
        Assert.True(concede.Conceded);
        Assert.Equal("beta", concede.ConcededTo);
        Assert.False(ModelPanelReviewService.ParsePosition("alpha", 1, """{"decision":"concede","better":"A"}""", labels).Conceded); // its own
        Assert.False(ModelPanelReviewService.ParsePosition("alpha", 1, """{"decision":"concede","better":"Z"}""", labels).Conceded);
        Assert.False(ModelPanelReviewService.ParsePosition("alpha", 1, "B is better", labels).Conceded);
        Assert.False(ModelPanelReviewService.ParsePosition("alpha", 1, """{"decision":"stand","better":"B"}""", labels).Conceded);
    }

    [Fact]
    public void Concession_rings_cancel_and_chains_resolve_to_the_standing_answer()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "b", ["b"] = "c", ["c"] = "b", ["d"] = "a" };
        ModelPanelReviewService.ResolveConcessions(map);
        Assert.False(map.ContainsKey("b"));
        Assert.False(map.ContainsKey("c"));
        Assert.Equal("b", ModelPanelReviewService.Backs("d", map));
        Assert.Equal("c", ModelPanelReviewService.Backs("c", map));
    }

    [Theory]
    [InlineData("openrouter", "openrouter", null)]
    [InlineData("openrouter:anthropic/claude-x", "openrouter", "anthropic/claude-x")]
    [InlineData(" local : llama3:8b ", "local", "llama3:8b")]
    [InlineData("lane:", "lane", null)]
    public void Picks_parse_lane_and_optional_model(string text, string lane, string? model)
    {
        var pick = ModelPanelPick.Parse(text)!;
        Assert.Equal(lane, pick.Lane);
        Assert.Equal(model, pick.Model);
    }

    [Fact]
    public void The_tool_switches_to_explicit_picks_only_when_a_model_is_named()
    {
        var plain = ModelPanelReviewTools.ToRequest(new ModelPanelReviewInput { Question = "q", Lanes = ["alpha", "beta"], Judge = "gamma" }, "c1");
        Assert.Equal(["alpha", "beta"], plain.LaneNames);
        Assert.Null(plain.Models);
        Assert.Equal(new ModelPanelPick("gamma"), plain.Judge);
        var picked = ModelPanelReviewTools.ToRequest(new ModelPanelReviewInput { Question = "q", Lanes = ["alpha", "alpha:a/two"] }, "c1");
        Assert.Null(picked.LaneNames);
        Assert.Equal([new ModelPanelPick("alpha"), new ModelPanelPick("alpha", "a/two")], picked.Models);
        Assert.Null(picked.Judge);
    }
}
