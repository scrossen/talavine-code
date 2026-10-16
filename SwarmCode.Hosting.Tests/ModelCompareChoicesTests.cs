using SwarmCode.Hosting;
using SwarmCode.Ui;
using SwarmCore.Coding.Review;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCode.Hosting.Tests;

/// <summary>/compare choices: recent models pinned first, last selection and judge remembered, stale lanes dropped; REPL options.</summary>
public sealed class ModelCompareChoicesTests
{
    private static readonly IReadOnlyList<CodingLane> Lanes =
    [
        new("claude", "ClaudeCodeNative", "claude-code:default"),
        new("openrouter", "OpenRouter", "anthropic/claude-x"),
        new("deepseek", "DeepSeek", "deepseek-chat"),
    ];

    [Fact]
    public void Remember_puts_the_latest_run_first_without_duplicates_and_keeps_the_selection_and_judge()
    {
        var prefs = new AppPreferences { RecentCompareModels = [new("deepseek", "deepseek-chat"), new("openrouter", "old/model")] };
        var models = new List<ModelPanelPick> { new("openrouter", "anthropic/claude-x"), new("openrouter", "old/model") };
        var judge = new ModelPanelPick("deepseek", "deepseek-chat");

        ModelCompareChoices.Remember(prefs, models, judge);

        Assert.Equal([new ModelPanelPick("openrouter", "anthropic/claude-x"), new ModelPanelPick("openrouter", "old/model"), new ModelPanelPick("deepseek", "deepseek-chat")],
            prefs.RecentCompareModels);
        Assert.Equal(models, prefs.LastCompareModels);
        Assert.Equal(judge, prefs.CompareJudge);

        for (var i = 0; i < 20; i++) ModelCompareChoices.Remember(prefs, [new("openrouter", "m" + i), new("deepseek", "deepseek-chat")], judge);
        Assert.Equal(ModelCompareChoices.MaxRecent, prefs.RecentCompareModels.Count);
        Assert.Equal(new ModelPanelPick("openrouter", "m19"), prefs.RecentCompareModels[0]);
    }

    [Fact]
    public void Available_drops_removed_and_native_lanes_and_fills_in_the_lane_model()
    {
        var stored = new List<ModelPanelPick> { new("gone", "x"), new("claude", "claude-code:default"), new("OpenRouter", null), new("openrouter", "anthropic/claude-x") };
        var available = ModelCompareChoices.Available(stored, Lanes);
        Assert.Equal([new ModelPanelPick("openrouter", "anthropic/claude-x")], available);
    }

    [Fact]
    public void A_lane_own_model_is_sent_as_the_lane_name()
    {
        Assert.Equal(new ModelPanelPick("deepseek"), ModelCompareChoices.ForRequest(new("deepseek", "deepseek-chat"), Lanes));
        Assert.Equal(new ModelPanelPick("deepseek", "deepseek-reasoner"), ModelCompareChoices.ForRequest(new("deepseek", "deepseek-reasoner"), Lanes));
    }

    [Theory]
    [InlineData("--models a,b:x/y --judge c Which is faster?", "Which is faster?", "a|b:x/y", "c")]
    [InlineData("--judge c:m  explain this", "explain this", null, "c:m")]
    [InlineData("plain prompt --judge c", "plain prompt --judge c", null, null)]
    [InlineData("--verbose prompt", "--verbose prompt", null, null)]
    [InlineData("--models a,b", "", "a|b", null)]
    public void Repl_compare_reads_leading_options_and_keeps_the_rest_as_the_prompt(string arguments, string question, string? models, string? judge)
    {
        var parsed = Repl.ParseCompare(arguments);
        Assert.Equal(question, parsed.Question);
        Assert.Equal(models, parsed.Models is null ? null : string.Join("|", parsed.Models));
        Assert.Equal(judge, parsed.Judge?.ToString());
    }
}
