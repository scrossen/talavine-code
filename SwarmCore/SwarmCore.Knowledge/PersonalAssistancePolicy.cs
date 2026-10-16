using System.Security.Cryptography;
using System.Text;
using SwarmCore.Abstractions.Context;

namespace SwarmCore.Knowledge;

public static class PersonalAssistancePolicy
{
    public static bool IsTerminal(PersonalActionState state)
        => state is PersonalActionState.Verified or PersonalActionState.Retired or PersonalActionState.Cancelled or PersonalActionState.Changed;

    public static PersonalActionOperation Lease(PersonalActionOperation operation, DateTimeOffset now, Guid owner)
        => operation with { State = PersonalActionState.Executing, Revision = operation.Revision + 1,
            LeaseOwner = owner, LeaseUntil = now.AddMinutes(2), UpdatedAt = now };

    public static PersonalActionOperation Complete(PersonalActionOperation operation, PersonalActionState state,
        string outcome, string? targetFingerprint, DateTimeOffset now, int? uncertainLimit)
    {
        var removed = state is PersonalActionState.Retired or PersonalActionState.Cancelled;
        var failures = state == PersonalActionState.Uncertain ? Math.Min(operation.ConsecutiveUncertainOutcomes, 1000) + 1 : 0;
        var exhausted = uncertainLimit is { } assignedLimit && failures >= Math.Max(1, assignedLimit);
        return operation with { State = state, Outcome = exhausted ? outcome + " Automatic retries stopped after repeated unchanged failures; inspect the local target before resuming." : outcome,
            TargetFingerprint = targetFingerprint ?? operation.TargetFingerprint, Revision = operation.Revision + 1,
            LeaseOwner = null, LeaseUntil = failures == 0 ? null : exhausted ? DateTimeOffset.MaxValue : now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(6, failures)))),
            ConsecutiveUncertainOutcomes = failures, UpdatedAt = now, Body = removed ? null : operation.Body,
            Why = removed ? "Source unavailable or assignment withdrawn." : operation.Why,
            Title = removed ? "Source-backed action" : operation.Title, Recipient = removed ? null : operation.Recipient };
    }

    public static bool IsAssignment(string? text, PersonalActionKind kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim();
        var flags = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var prohibited = kind == PersonalActionKind.ReplyDraft ? @"\b(do\s+not|don't|never)\s+(draft|prepare|save)\b" : @"\b(do\s+not|don't|never)\s+(create|add|save|set|make|remind)\b";
        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, prohibited, flags)) return false;
        // Evaluate separate user clauses rather than treating the first word as
        // the intent of an entire compound request. No affirmative command
        // vocabulary is required for the host-authorized assignment clause.
        var clauses = System.Text.RegularExpressions.Regex.Split(normalized,
            @"(?:[.!?;]\s+|\s+(?:and\s+then|and|then|but)\s+)", flags);
        if (clauses.All(clause => System.Text.RegularExpressions.Regex.IsMatch(clause.Trim(),
            @"^(?:(?:they\s+said|the\s+(?:message|email|source)\s+(?:says|said)|quote:)|(?:what|why|when|who|where|explain|show|find|summarize)\b)", flags))) return false;
        // Positive authority comes from the trusted host assignment, not a magic
        // verb grammar. These exclusions only preserve explicit no-action intent.
        return true;
    }
    public static Guid OperationId(Guid profile, string assignment, string obligation, PersonalActionKind kind)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"personal-action-v1:{profile:N}:{assignment}:{obligation}:{kind}"))[..16]);

    public static bool CanDispatch(PersonalActionOperation operation, PersonalActionAuthority current)
        => !current.Revoked && current.ProfileId == operation.Authority.ProfileId
            && current.AssignmentId == operation.Authority.AssignmentId
            && current.Revision == operation.Authority.Revision
            && current.AccountIds.Contains(operation.SourceAccountId)
            && operation.State is PersonalActionState.Assigned or PersonalActionState.Uncertain;

    public static bool IsQuiet(PersonalAssistanceRule rule, DateTimeOffset now)
    {
        if (!rule.Enabled || rule.Authority.Revoked || rule.SnoozedUntil > now) return true;
        if (rule.QuietStartUtc is not { } start || rule.QuietEndUtc is not { } end || start == end) return false;
        var time = TimeOnly.FromDateTime(now.UtcDateTime);
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    public static DateTimeOffset NextEligibleAt(PersonalAssistanceRule rule, DateTimeOffset now)
    {
        var next = rule.SnoozedUntil > now ? rule.SnoozedUntil.Value : now;
        if (rule.QuietEndUtc is { } end && IsQuiet(rule with { Enabled = true, SnoozedUntil = null, Authority = rule.Authority with { Revoked = false } }, next))
        {
            var candidate = new DateTimeOffset(next.UtcDateTime.Date.Add(end.ToTimeSpan()), TimeSpan.Zero);
            next = candidate > next ? candidate : candidate.AddDays(1);
        }
        return next;
    }

    public static PersonalActionState AfterSourceChange(string status, bool targetChanged)
        => targetChanged ? PersonalActionState.Changed
            : status is "settled" or "cancelled" or "superseded" or "removed" ? PersonalActionState.Retired
            : PersonalActionState.Changed;
}
