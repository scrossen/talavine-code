using SwarmCode.App.Services;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.App.Tests;

public sealed class UserBugIntakeTests
{
    [Fact]
    public async Task Release_fallback_persists_explicit_intake_without_an_automatic_reporter_or_worker()
    {
        var root = Path.Combine(Path.GetTempPath(), "user-bug-intake", Guid.NewGuid().ToString("N"));
        try
        {
            var backlog = new JsonFileSelfImprovementBacklog(root);
            var intake = new UserBugIntake(backlog, () => new SelfImprovementOptions
            {
                Enabled = false,
                CaptureDefects = false,
                CaptureFeatureRequests = false,
            });

            // No automatic reporter or worker is supplied: this is the Release path.
            var saved = await intake.SubmitAsync(automaticReporter: null, "/bug Release intake does not open a coding turn", "runnerless-session");

            Assert.Equal(UserBugIntakeOutcome.Saved, saved.Outcome);
            Assert.NotNull(saved.Item);
            Assert.Equal("user-bug-report", saved.Item.Source);
            Assert.Equal("runnerless-session", saved.Item.ConversationId);
            var persisted = await new JsonFileSelfImprovementBacklog(root).GetAsync(saved.Item.Id);
            Assert.Equal(saved.Item.Id, persisted!.Id);

            var repeated = await intake.SubmitAsync(null, "/bug Release intake does not open a coding turn", "second-session");
            Assert.Equal(saved.Item.Id, repeated.Item!.Id);
            Assert.Equal(2, repeated.Item.Occurrences);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Runnerless_admission_handles_only_bug_commands_and_preserves_newer_drafts()
    {
        var intake = new UserBugIntake(new InMemorySelfImprovementBacklog(), () => new SelfImprovementOptions { Enabled = false });

        Assert.True(UserBugIntake.IsCommand("/BUG Details"));
        Assert.True(UserBugIntake.IsCommand("/bug"));
        Assert.False(UserBugIntake.IsCommand("/buggy Details"));
        Assert.False(UserBugIntake.IsCommand("/note Details"));
        Assert.True(UserBugIntake.ShouldClearDraft("/bug original", "/bug original"));
        Assert.False(UserBugIntake.ShouldClearDraft("/bug original", "newer draft"));

        var missing = await intake.SubmitAsync(null, "/bug", "runnerless-session");
        Assert.Equal(UserBugIntakeOutcome.MissingDescription, missing.Outcome);
        Assert.Contains("draft was kept", missing.Message!, StringComparison.OrdinalIgnoreCase);
    }
}
