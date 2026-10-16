namespace SwarmCore.Agent.Marketing;

public sealed record SocialPerson(string Id, string Handle, string Name, string Bio, bool Protected = false)
{
    public string Url => "https://x.com/" + Handle;
}
public sealed record SocialProspectEvidence(SocialPerson Person, string Query, string[] MatchedPhrases, DateTimeOffset ObservedAt);
public sealed record SocialFollowing(IReadOnlySet<string> Ids, bool Complete);
public enum SocialFollowState { Candidate, Dispatching, Following, Pending, Uncertain, NotDispatched, Suppressed }
public sealed record SocialFollowOutcome(SocialFollowState State, string Detail);
public sealed record SocialProspectWork(Guid Id, string TargetId, SocialFollowState State);
public sealed record SocialCycleResult(int Researched, int Matched, int Following, int Pending, int Uncertain, string Detail);

/// <summary>Owner-authored standing contract, never instructions extracted from a biography.</summary>
public sealed record SocialProspectingPlan(string AccountId, string Query, string[] RequiredPhrases,
    string[] ExcludedHandles, bool FollowEnabled, int MaxFollows = 3)
{
    public void Validate()
    {
        if (!ValidSubjectId(AccountId)) throw new ArgumentException("Connect an identified X account first.");
        if (string.IsNullOrWhiteSpace(Query) || Query.Length > 200)
            throw new ArgumentException("Supply a search query of at most 200 characters.");
        if (RequiredPhrases is null || RequiredPhrases.Length is < 1 or > 8
            || RequiredPhrases.Any(x => string.IsNullOrWhiteSpace(x) || x.Trim().Length is < 3 or > 100))
            throw new ArgumentException("Supply 1–8 required bio phrases, each 3–100 characters.");
        if (ExcludedHandles is null || ExcludedHandles.Length > 100 || ExcludedHandles.Any(x => !ValidHandle(x)))
            throw new ArgumentException("Excluded handles must be X handles without @.");
        if (MaxFollows is < 1 or > 3) throw new ArgumentException("This small-batch pilot allows 1–3 follows per campaign, not bulk growth.");
    }
    public static bool ValidId(string? value) => value is { Length: >= 1 and <= 19 } && value.All(c => c is >= '0' and <= '9');
    // Browser observations expose a handle, not X's numeric API id. Never fabricate an API id.
    public static bool ValidSubjectId(string? value) => ValidId(value)
        || (value?.StartsWith("handle:", StringComparison.Ordinal) == true && ValidHandle(value[7..]));
    public static bool ValidHandle(string? value) => value is { Length: >= 1 and <= 15 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>The host supplies an authenticated, account-bound provider; this is not a browser grant.</summary>
public interface ISocialProspectingChannel
{
    Task<SocialPerson> GetSelfAsync(CancellationToken ct);
    Task<IReadOnlyList<SocialPerson>> SearchAsync(string query, CancellationToken ct);
    async IAsyncEnumerable<SocialPerson> SearchStreamAsync(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var person in await SearchAsync(query, ct).ConfigureAwait(false)) yield return person;
    }
    Task<SocialFollowing> GetFollowingAsync(string accountId, CancellationToken ct);
    Task<SocialFollowOutcome> FollowAsync(string accountId, string targetId, CancellationToken ct);
}

/// <summary>
/// Host persistence must be tenant/account scoped and serialized across campaigns for an account.
/// ReserveDispatch atomically rechecks standing authority, suppression, daily and lifetime caps,
/// and commits Dispatching BEFORE the network call. Dispatching/Uncertain never become retryable.
/// </summary>
public interface ISocialProspectingJournal
{
    Task SaveCandidateAsync(SocialProspectEvidence evidence, CancellationToken ct);
    Task<IReadOnlyList<SocialProspectWork>> ReadWorkAsync(CancellationToken ct);
    Task<bool> ReserveDispatchAsync(Guid id, CancellationToken ct);
    Task RecordOutcomeAsync(Guid id, SocialFollowOutcome outcome, CancellationToken ct);
}

/// <summary>Deterministic, resumable research → evidence → bounded follow → verification. Zero model calls.</summary>
public sealed class SocialProspectingCycle(ISocialProspectingChannel channel, ISocialProspectingJournal journal, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<SocialCycleResult> RunAsync(SocialProspectingPlan plan, CancellationToken ct = default)
    {
        plan.Validate();
        var self = await channel.GetSelfAsync(ct).ConfigureAwait(false);
        if (self.Id != plan.AccountId) throw new InvalidOperationException("The connected X account changed. No research or follow was dispatched.");
        var work = await journal.ReadWorkAsync(ct).ConfigureAwait(false);
        var found = 0; var matched = 0; var following = 0; var pending = 0; var uncertain = 0;
        // Complete/reconcile persisted work before another search. A full batch never buys another search.
        if (work.Count == 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var person in channel.SearchStreamAsync(plan.Query, ct).ConfigureAwait(false))
            {
                if (!seen.Add(person.Id)) continue;
                if (found >= 10) break;
                found++;
                if (!SocialProspectingPlan.ValidSubjectId(person.Id) || !SocialProspectingPlan.ValidHandle(person.Handle)
                    || person.Id == self.Id || person.Protected
                    || plan.ExcludedHandles.Contains(person.Handle, StringComparer.OrdinalIgnoreCase)) continue;
                var bio = LeadDiscovery.Normalize(person.Bio);
                var phrases = plan.RequiredPhrases.Select(LeadDiscovery.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!phrases.All(p => bio.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                // The bio is evidence only. It never controls query, limits, identity, or authority.
                await journal.SaveCandidateAsync(new(person with { Bio = bio }, plan.Query, phrases, _clock.GetUtcNow()), ct).ConfigureAwait(false);
                matched++;
            }
            work = await journal.ReadWorkAsync(ct).ConfigureAwait(false);
        }
        if (work.Count == 0 || (!plan.FollowEnabled && !work.Any(x => x.State is SocialFollowState.Dispatching or SocialFollowState.Uncertain or SocialFollowState.Pending)))
            return new(found, matched, 0, 0, 0, "Research saved. No follows authorized or no matching public bios found.");
        var actual = await channel.GetFollowingAsync(self.Id, ct).ConfigureAwait(false);
        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();
            if (item.State is SocialFollowState.Suppressed or SocialFollowState.Following or SocialFollowState.NotDispatched) continue;
            if (actual.Ids.Contains(item.TargetId))
            {
                await journal.RecordOutcomeAsync(item.Id, new(SocialFollowState.Following, "Verified in X following list."), ct).ConfigureAwait(false);
                following++; continue;
            }
            if (item.State is SocialFollowState.Dispatching or SocialFollowState.Uncertain or SocialFollowState.Pending)
            {
                // Absence, even in a complete list, does not prove an earlier request failed.
                var state = item.State == SocialFollowState.Pending ? SocialFollowState.Pending : SocialFollowState.Uncertain;
                await journal.RecordOutcomeAsync(item.Id, new(state, "Not yet verified. No repeat follow will be sent."), ct).ConfigureAwait(false);
                if (state == SocialFollowState.Pending) pending++; else uncertain++;
                continue;
            }
            if (!plan.FollowEnabled || !actual.Complete || !await journal.ReserveDispatchAsync(item.Id, ct).ConfigureAwait(false)) continue;
            // The reservation survives cancellation/process death; recover by reading, never re-sending.
            var outcome = await channel.FollowAsync(self.Id, item.TargetId, ct).ConfigureAwait(false);
            await journal.RecordOutcomeAsync(item.Id, outcome, ct).ConfigureAwait(false);
            if (outcome.State == SocialFollowState.Following) following++;
            if (outcome.State == SocialFollowState.Pending) pending++;
            if (outcome.State == SocialFollowState.Uncertain) uncertain++;
            if (outcome.State is SocialFollowState.Uncertain or SocialFollowState.NotDispatched) break;
        }
        return new(found, matched, following, pending, uncertain,
            actual.Complete ? "Cycle recorded. A follow is not a qualified lead or permission to message."
                : "Following lookup was incomplete; no new follows sent. Existing uncertain actions remain retained.");
    }
}
