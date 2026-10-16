using SwarmCore.Coding.Routing;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Coding.Tests;

public sealed class ModelResponseLearningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);
    private static ModelResponseObservation Row(string model, string task, ModelResponseOutcome outcome, int ageDays = 0)
        => new(Guid.NewGuid().ToString("N"), "profile", "Provider", model, task, outcome, Now.AddDays(-ageDays), "harness-decline");

    [Fact]
    public void Exact_request_recent_evidence_beats_general_history_and_old_or_runtime_failures_do_not_poison_ranking()
    {
        var task = ModelResponseLearning.Fingerprint("original request");
        var other = ModelResponseLearning.Fingerprint("another request");
        var history = Enumerable.Range(0, 10).Select(_ => Row("family/a", other, ModelResponseOutcome.Verified)).ToList();
        history.Add(Row("family/a", task, ModelResponseOutcome.Declined));
        Assert.True(ModelResponseLearning.Score("Provider", "family/a", task, history, Now) < 0);
        Assert.Equal(0, ModelResponseLearning.Score("Provider", "unknown/model", task, history, Now));
        Assert.Equal(0, ModelResponseLearning.Score("Provider", "family/a", task,
            [Row("family/a", task, ModelResponseOutcome.RuntimeFailed), Row("family/a", task, ModelResponseOutcome.Declined, 91)], Now));
        Assert.True(ModelResponseLearning.Score("Provider", "family/a", task, [Row("family/a", task, ModelResponseOutcome.Verified)], Now)
            > ModelResponseLearning.Score("Provider", "family/a", task, [Row("family/a", task, ModelResponseOutcome.AnsweredUnverified)], Now));
    }

    [Fact]
    public void Response_history_moves_unknown_alternatives_ahead_of_a_recent_refuser_without_relaxing_catalog_filters()
    {
        var task = ModelResponseLearning.Fingerprint("original request");
        LlmCatalogModel[] models = [new("family/cheap", 65536, true, .01m, .02m, false),
            new("family/alternative", 65536, true, .1m, .2m, false), new("family/expensive", 65536, true, 20m, 20m, false)];
        var result = CatalogCodingFallbackDiscovery.SelectCandidates("Provider", models, [], new() { MaxCandidates = 2 },
            [Row("family/cheap", task, ModelResponseOutcome.Declined), Row("family/expensive", task, ModelResponseOutcome.Verified)], task, Now);
        Assert.Equal(["family/alternative", "family/cheap"], result.Select(r => r.Model));
    }

    [Fact]
    public void Request_fingerprints_are_normalized_and_aliases_are_not_treated_as_exact_model_evidence()
    {
        Assert.Equal(ModelResponseLearning.Fingerprint("Find   a File"), ModelResponseLearning.Fingerprint(" find a file "));
        Assert.DoesNotContain("file", ModelResponseLearning.Fingerprint("find a file"));
        Assert.False(ModelResponseLearning.IsExactModel("openrouter/auto"));
        Assert.False(ModelResponseLearning.IsExactModel("configured:codex-cli:default"));
        Assert.False(ModelResponseLearning.IsExactModel("~owner/latest"));
        Assert.True(ModelResponseLearning.IsExactModel("family/model-v1"));
    }
}
