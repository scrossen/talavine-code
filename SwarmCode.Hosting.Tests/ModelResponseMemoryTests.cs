using SwarmCode.Hosting.Memory;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class ModelResponseMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-memory-" + Guid.NewGuid().ToString("N"));
    private static ModelResponseObservation Row(string id, string scope = "profile-a") => new(id, scope, "Provider", "family/model",
        ModelResponseLearning.Fingerprint("private user request"), ModelResponseOutcome.Declined,
        new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero), "host-classifier-v2");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public async Task Independent_writers_retain_each_observation_replays_are_idempotent_conflicts_are_rejected_and_profiles_are_isolated()
    {
        var paths = new SwarmCodeDataPaths(_root);
        var a = new SqliteModelResponseMemory(paths);
        var b = new SqliteModelResponseMemory(paths);
        Assert.Empty(await a.ReadAsync("profile-a"));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? a : b).AppendAsync(Row("event-" + i))));
        await Task.WhenAll(a.AppendAsync(Row("event-0")), b.AppendAsync(Row("event-0")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.AppendAsync(Row("event-0") with { Outcome = ModelResponseOutcome.Verified }));
        await b.AppendAsync(Row("event-other", "profile-b"));
        var reopened = new SqliteModelResponseMemory(new SwarmCodeDataPaths(_root));
        Assert.Equal(20, (await reopened.ReadAsync("profile-a")).Count);
        Assert.Single(await reopened.ReadAsync("profile-b"));
        Assert.Empty(await reopened.ReadAsync("missing-profile"));
        Assert.All(await reopened.ReadAsync("profile-a"), row => Assert.Equal(ModelResponseOutcome.Declined, row.Outcome));
        Assert.DoesNotContain("private user request", File.ReadAllText(paths.ModelResponseMemoryPath));
    }

    [Fact]
    public async Task Reviewed_corrections_retain_originals_replace_ranking_and_reject_competing_or_cross_scope_revisions()
    {
        var store = new SqliteModelResponseMemory(new SwarmCodeDataPaths(_root));
        var original = Row("original") with { Outcome = ModelResponseOutcome.AnsweredUnverified };
        await store.AppendAsync(original);
        var correction = Row("reviewed") with { SupersedesId = original.Id, EvidenceKind = "reviewed-receipt" };
        await store.AppendAsync(correction);
        await store.AppendAsync(correction);
        var rows = await store.ReadAsync(original.ScopeId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(correction, Assert.Single(ModelResponseLearning.Current(rows)));
        Assert.Equal(0, Assert.Single(ModelResponseLearning.Summarize(rows)).Answered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(correction with { Id = "competing" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(correction with { Id = "foreign", ScopeId = "profile-b" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(correction with { Id = "other-model", Model = "family/other" }));
    }

    [Fact]
    public async Task Cancelled_write_and_unresolved_alias_do_not_create_evidence()
    {
        var store = new SqliteModelResponseMemory(new SwarmCodeDataPaths(_root));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AppendAsync(Row("cancelled"), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(Row("alias") with { Model = "openrouter/auto" }));
        Assert.Empty(await store.ReadAsync("profile-a"));
    }
}
