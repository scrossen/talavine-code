using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.App.Services;

/// <summary>
/// Explicit <c>/bug</c> intake is available in installed builds without enabling the
/// Debug-only self-improvement worker, automatic capture, or its host registrations.
/// </summary>
internal sealed class UserBugIntake
{
    private readonly SelfImprovementReporter _releaseReporter;

    public UserBugIntake(ISelfImprovementBacklog backlog, Func<SelfImprovementOptions> options)
        => _releaseReporter = new SelfImprovementReporter(backlog, options);

    public static bool IsCommand(string text)
        => text.StartsWith("/bug", StringComparison.OrdinalIgnoreCase)
            && (text.Length == 4 || char.IsWhiteSpace(text[4]));

    public static bool ShouldClearDraft(string draftAtAdmission, string currentDraft)
        => string.Equals(draftAtAdmission, currentDraft, StringComparison.Ordinal);

    public async Task<UserBugIntakeResult> SubmitAsync(
        SelfImprovementReporter? automaticReporter,
        string text,
        string? conversationId,
        CancellationToken ct = default)
    {
        if (!IsCommand(text)) return UserBugIntakeResult.NotHandled;

        var description = text.Length == 4 ? "" : text[4..].Trim();
        if (description.Length == 0)
            return new(UserBugIntakeOutcome.MissingDescription, null,
                "Describe the problem after /bug. Your draft was kept.");

        try
        {
            // Debug uses the app reporter so its existing Changed/Wake subscription remains intact.
            // Release uses this private reporter over the same durable backlog and starts no worker.
            var item = await (automaticReporter ?? _releaseReporter).SubmitBugAsync(description, conversationId, ct);
            return new(UserBugIntakeOutcome.Saved, item, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new(UserBugIntakeOutcome.Failed, null,
                "Bug report was not saved. Your draft was kept. " + ex.Message);
        }
    }
}

internal enum UserBugIntakeOutcome { NotHandled, MissingDescription, Saved, Failed }

internal sealed record UserBugIntakeResult(UserBugIntakeOutcome Outcome, SelfImprovementItem? Item, string? Message)
{
    public static UserBugIntakeResult NotHandled { get; } = new(UserBugIntakeOutcome.NotHandled, null, null);
}
