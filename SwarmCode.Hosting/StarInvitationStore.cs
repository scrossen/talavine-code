using SwarmCore.Coding.Engagement;

namespace SwarmCode.Hosting;

public sealed record StarInvitationState(bool Eligible, bool Dismissed)
{
    public bool ShouldShow(bool busy) => OptionalInvitationPolicy.ShouldShow(Eligible, Dismissed, busy);
}

/// <summary>Profile-local, field-owned invitation writes using the existing cross-process preferences lease.</summary>
public sealed class StarInvitationStore(AppPreferencesStore preferences)
{
    public const string RepositoryUrl = "https://github.com/scrossen/talavine-code";

    public Task<StarInvitationState> ReadAsync() => Task.Run(() =>
    {
        try
        {
            var current = preferences.Current;
            return new StarInvitationState(current.GitHubStarInvitationEligible, current.GitHubStarInvitationDismissed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Optional promotion stays quiet when the person's preference cannot be established.
            return new StarInvitationState(false, true);
        }
    });

    public Task RecordAsync(CodingCompletionEvidence? evidence, CancellationToken ct = default)
        => RecordAsync(evidence, ct, beforeCommit: null);

    internal Task RecordAsync(CodingCompletionEvidence? evidence, CancellationToken ct, Action? beforeCommit)
    {
        if (!OptionalInvitationPolicy.Qualifies(evidence)) return Task.CompletedTask;
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var current = preferences.Current;
            if (current.GitHubStarInvitationEligible || current.GitHubStarInvitationDismissed) return;
            // Never copy a detached preferences object. A concurrent dismissal wins under the same lease.
            preferences.Update(p =>
            {
                // The file lease may have waited while the person stopped the still-finishing request.
                ct.ThrowIfCancellationRequested();
                // Claim the host's terminal completion only after the lease is held. A stop before
                // this boundary cancels; a stop after it cannot undo this durable success.
                beforeCommit?.Invoke();
                if (!p.GitHubStarInvitationDismissed) p.GitHubStarInvitationEligible = true;
            });
        }, ct);
    }

    public Task DismissAsync() => Task.Run(() => preferences.Update(p => p.GitHubStarInvitationDismissed = true));
}
