namespace SwarmCore.Agent.Communication;

/// <summary>What kind of outcome a goal delivered. Results are the owner's view of progress, not tool receipts.</summary>
public enum GoalResultKind { Post, Message, Email, Submission, Commit, Release, Document, Other }

/// <summary>Where a result stands. Pending means sent or submitted but not yet accepted; Verified means its public form was read back.</summary>
public enum GoalResultState { Done, Pending, Verified, Failed, Withdrawn }

/// <summary>A result as the worker reports it. The host supplies identity, attempt and time.</summary>
public sealed record GoalResultDraft(GoalResultKind Kind, string Title)
{
    public string? Url { get; init; }
    /// <summary>Who received it or where it appeared: a channel, forum, list, person or publication.</summary>
    public string? Audience { get; init; }
    public string? Summary { get; init; }
    public GoalResultState State { get; init; } = GoalResultState.Done;
    /// <summary>A stable key the worker reuses to update the same result later (a publication id, message id, PR number).</summary>
    public string? ExternalId { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
    /// <summary>Where the proof lives (a readback file, a receipt, an API record). Never credentials.</summary>
    public string? Evidence { get; init; }
}

/// <summary>A durable result record. <see cref="Source"/> is "agent" when the bound worker recorded it.</summary>
public sealed record GoalResult(string Id, Guid GoalId, int Attempt, long Sequence, DateTimeOffset RecordedUtc, DateTimeOffset OccurredAtUtc,
    GoalResultKind Kind, GoalResultState State, string Title, string? Url, string? Audience, string? Summary, string Source,
    string? ExternalId, string? Evidence)
{
    public long Revision { get; init; } = 1;
}

public sealed record GoalResultPage(IReadOnlyList<GoalResult> Results, int Total, int Offset, bool HasMore);

/// <summary>
/// The owner-facing results ledger of a goal. Hosts own persistence and admission: only a goal's bound worker
/// may record, a later record with the same <see cref="GoalResultDraft.ExternalId"/> (or the same URL) updates
/// the earlier one, and nothing here resumes, pauses or replays work.
/// </summary>
public interface IGoalResultLedger
{
    Task<GoalResult> RecordAsync(Guid goalId, string conversationId, string leaseToken, GoalResultDraft draft, CancellationToken ct = default);
    Task<GoalResultPage> ListAsync(Guid goalId, int offset = 0, int take = 50, CancellationToken ct = default);
}

public static class GoalResults
{
    public const int MaximumTitleCharacters = 200;
    public const int MaximumSummaryCharacters = 1000;
    public const int MaximumAudienceCharacters = 200;
    public const int MaximumEvidenceCharacters = 500;
    public const int MaximumUrlCharacters = 2048;
    public const int MaximumExternalIdCharacters = 200;

    public const string WorkerGuidance = "Record every concrete result of this goal the moment it is real with goal_results.record: "
        + "a post or comment published, a message or email sent, a submission or pull request opened, a commit or release shipped, a document delivered. "
        + "Give the kind, a short plain title, the public URL when one exists, the audience (channel, forum, list or person), a one-line summary, "
        + "the state (Done, Pending when awaiting acceptance, Verified after reading back the public form, Failed, Withdrawn) and a stable externalId "
        + "so later updates replace the earlier record. This ledger is what the owner reads as progress; tool receipts are not shown to them. "
        + "Never record something that did not happen, never record credentials, and never redo an action to obtain a record.";

    /// <summary>Normalizes and validates a draft; returns the validated draft or throws <see cref="ArgumentException"/>.</summary>
    public static GoalResultDraft Validate(GoalResultDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Enum.IsDefined(draft.Kind) || !Enum.IsDefined(draft.State)) throw new ArgumentException("Use a known result kind and state.");
        var title = Clean(draft.Title, MaximumTitleCharacters, "title", required: true)!;
        var url = Clean(draft.Url, MaximumUrlCharacters, "url");
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("url must be an absolute http(s) link.");
        url = url is null ? null : new Uri(url).AbsoluteUri;   // one spelling per link, so a later update finds the earlier record
        return draft with
        {
            Title = title, Url = url,
            Audience = Clean(draft.Audience, MaximumAudienceCharacters, "audience"),
            Summary = Clean(draft.Summary, MaximumSummaryCharacters, "summary"),
            ExternalId = Clean(draft.ExternalId, MaximumExternalIdCharacters, "externalId"),
            Evidence = Clean(draft.Evidence, MaximumEvidenceCharacters, "evidence"),
        };
    }

    /// <summary>The key a later record uses to update an earlier one: the external id when given, else the URL.</summary>
    public static string? IdentityKey(GoalResultDraft draft)
        => draft.ExternalId is { Length: > 0 } id ? "id:" + id : draft.Url is { Length: > 0 } url ? "url:" + url : null;

    private static string? Clean(string? value, int maximum, string name, bool required = false)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (required) throw new ArgumentException(name + " is required.");
            return null;
        }
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new ArgumentException(name + " contains control characters.");
        text = string.Join('\n', text.Split('\n').Select(line => line.Replace("\r", "").Replace("\t", " ").Trim()));
        if (text.Length > maximum) throw new ArgumentException(name + " is longer than " + maximum + " characters.");
        return text;
    }
}
