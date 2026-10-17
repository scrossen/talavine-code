using SwarmCore.Coding.SelfImprovement;

namespace SwarmCore.Coding.Tests;

public sealed class UserBugSubmissionTests
{
    [Fact]
    public async Task Explicit_reports_are_durable_with_automatic_capture_disabled_and_keep_closed_stage()
    {
        var root = Path.Combine(Path.GetTempPath(), "bug-submission-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var backlog = new JsonFileSelfImprovementBacklog(root);
            var reporter = new SelfImprovementReporter(backlog, () => new() { Enabled = false, CaptureDefects = false });
            Assert.Null(await reporter.ReportAsync(new(SelfImprovementKind.Defect, "Automatic", "Ignored")));
            var item = await reporter.SubmitBugAsync("Dictation does not reach the message box", "session-1");
            Assert.Equal(SelfImprovementKind.Defect, item.Kind);
            Assert.Equal("user-bug-report", item.Source);
            Assert.Equal("session-1", item.ConversationId);
            Assert.Equal(SelfImprovementStage.Queued, item.Stage);
            var secondStore = new JsonFileSelfImprovementBacklog(root);
            Assert.Equal(item.Details, (await secondStore.GetAsync(item.Id))!.Details);
            await secondStore.UpdateAsync(item.Id, fresh => fresh with { Stage = SelfImprovementStage.Dismissed, Note = "Keep closed" }, item.Revision);
            var repeat = await reporter.SubmitBugAsync(item.Details, "session-2");
            Assert.Equal(item.Id, repeat.Id);
            Assert.Equal(2, repeat.Occurrences);
            Assert.Equal(SelfImprovementStage.Dismissed, repeat.Stage);
            Assert.Equal("Keep closed", repeat.Note);
            Assert.Single(await secondStore.ListAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Concurrent_explicit_reports_fold_without_losing_sightings()
    {
        var backlog = new InMemorySelfImprovementBacklog();
        var reporter = new SelfImprovementReporter(backlog, () => new() { Enabled = false });
        var reports = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => reporter.SubmitBugAsync("Send button does nothing")));
        Assert.Single(reports.Select(r => r.Id).Distinct());
        Assert.Equal(12, (await backlog.ListAsync()).Single().Occurrences);
    }

    [Fact]
    public async Task Distinct_quoted_values_are_not_collapsed_as_exception_fingerprints()
    {
        var reporter = new SelfImprovementReporter(new InMemorySelfImprovementBacklog(), () => new());
        var a = await reporter.SubmitBugAsync("Theme 'Light' is broken");
        var b = await reporter.SubmitBugAsync("Theme 'Dark' is broken");
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public async Task Full_backlog_and_failed_writes_do_not_produce_success_receipts()
    {
        var reporter = new SelfImprovementReporter(new InMemorySelfImprovementBacklog(), () => new() { MaxQueued = 1 });
        var first = await reporter.SubmitBugAsync("First problem");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reporter.SubmitBugAsync("Second problem"));
        Assert.Equal(first.Id, (await reporter.SubmitBugAsync("First problem")).Id);
        var path = Path.GetTempFileName();
        try
        {
            var unavailable = new SelfImprovementReporter(new JsonFileSelfImprovementBacklog(path), () => new());
            await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.SubmitBugAsync("Cannot save"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Empty_reports_are_rejected(string description)
    {
        var reporter = new SelfImprovementReporter(new InMemorySelfImprovementBacklog(), () => new());
        await Assert.ThrowsAsync<ArgumentException>(() => reporter.SubmitBugAsync(description));
        Assert.Empty(await reporter.Backlog.ListAsync());
    }

    [Fact]
    public async Task Long_reports_are_rejected_instead_of_silently_truncated()
    {
        var reporter = new SelfImprovementReporter(new InMemorySelfImprovementBacklog(), () => new() { MaxDetailCharacters = 200 });
        await Assert.ThrowsAsync<ArgumentException>(() => reporter.SubmitBugAsync(new string('x', 201)));
        Assert.Empty(await reporter.Backlog.ListAsync());
    }
}
