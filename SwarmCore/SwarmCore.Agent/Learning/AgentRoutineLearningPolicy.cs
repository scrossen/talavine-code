using System.Text.RegularExpressions;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.Agent.Learning;

/// <summary>Validation and state transitions for host-verified, bounded routine learning.</summary>
public static partial class AgentRoutineLearningPolicy
{
    private static readonly Regex Sha256 = Sha256Pattern();

    public static void ValidateContext(AgentRoutineLearningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Key.ProfileId)
            || string.IsNullOrWhiteSpace(context.Key.AgentId)
            || string.IsNullOrWhiteSpace(context.Key.TaskId))
            throw new ArgumentException("Learning scope must be fully host-bound.", nameof(context));
        RequireHash(context.BindingFingerprint, nameof(context.BindingFingerprint));
        RequireHash(context.ToolFingerprint, nameof(context.ToolFingerprint));
        RequireHash(context.PreconditionsFingerprint, nameof(context.PreconditionsFingerprint));
        if (context.ProfileRouteFingerprint is not null)
            RequireHash(context.ProfileRouteFingerprint, nameof(context.ProfileRouteFingerprint));
        ValidateSafety(context.Safety);
    }

    public static void ValidateSafety(AgentRoutineSafetyFingerprint safety)
    {
        ArgumentNullException.ThrowIfNull(safety);
        RequireHash(safety.Authority, nameof(safety.Authority));
        RequireHash(safety.Privacy, nameof(safety.Privacy));
        RequireHash(safety.Delivery, nameof(safety.Delivery));
        RequireHash(safety.NoSend, nameof(safety.NoSend));
    }

    public static string SafetyFingerprint(AgentRoutineSafetyFingerprint safety)
    {
        ValidateSafety(safety);
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', "agent-routine-safety-v1",
            safety.Authority.ToLowerInvariant(), safety.Privacy.ToLowerInvariant(),
            safety.Delivery.ToLowerInvariant(), safety.NoSend.ToLowerInvariant()));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static AgentRoutineLearningHistory Stage(
        AgentRoutineLearningHistory? current,
        AgentRoutineLearningContext context,
        AgentLearnedPractice practice,
        DateTimeOffset now,
        AgentProcedureHistory? procedureHistory = null)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(practice);
        var normalized = AgentLearnedInstructionPolicy.Normalize(practice.Kind, practice.Trigger, practice.Procedure, practice.Reason);
        if (normalized != practice)
            throw new AgentRoutineLearningConflictException("Candidate practice was not normalized by the shared policy.");

        var history = current ?? new AgentRoutineLearningHistory(context.Key, 0, null, []);
        EnsureKey(context.Key, history.Key);
        if (procedureHistory is not null) EnsureKey(context.Key, procedureHistory.Key);
        ValidateHistory(history);
        if (history.Revisions.LastOrDefault(item => item.Practice.ContentKey == practice.ContentKey
            && (item.State == AgentRoutineLearningRevisionState.Staged
                    && item.ParentRevisionId == history.ActiveRevisionId && MatchesContext(item, context)
                || item.State == AgentRoutineLearningRevisionState.Active
                    && item.Id == history.ActiveRevisionId
                    && HasCurrentAcceptedProof(item, procedureHistory, context))) is not null)
            return history;

        var revisions = history.Revisions.Select(item => item.State == AgentRoutineLearningRevisionState.Staged
            ? item with { State = AgentRoutineLearningRevisionState.Rejected }
            : item).ToList();
        var staged = new AgentRoutineLearningRevision(Guid.NewGuid(), history.ActiveRevisionId,
            checked(revisions.Count + 1), practice, context.BindingFingerprint, context.ToolFingerprint,
            context.PreconditionsFingerprint, context.Safety, now, AgentRoutineLearningRevisionState.Staged);
        var replacement = history with
        {
            Version = checked(history.Version + 1),
            Revisions = revisions.Append(staged).ToArray(),
        };
        ValidateTransition(history, history.Version, replacement);
        return replacement;
    }

    /// <summary>
    /// Activates a staged proposal only from a currently retained, host-verified procedure revision.
    /// The caller must obtain procedureHistory from the normal durable procedure store, not tool input.
    /// </summary>
    public static AgentRoutineLearningHistory Adopt(
        AgentRoutineLearningHistory current,
        Guid candidateId,
        AgentRoutineLearningContext context,
        AgentProcedureHistory procedureHistory,
        DateTimeOffset now)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(procedureHistory);
        EnsureKey(context.Key, current.Key);
        EnsureKey(context.Key, procedureHistory.Key);
        ValidateHistory(current);
        var candidate = current.Revisions.SingleOrDefault(item => item.Id == candidateId
            && item.State == AgentRoutineLearningRevisionState.Staged)
            ?? throw new AgentRoutineLearningConflictException("Staged learning candidate was not found.");
        if (candidate.ParentRevisionId != current.ActiveRevisionId
            || candidate.BindingFingerprint != context.BindingFingerprint
            || candidate.ToolFingerprint != context.ToolFingerprint
            || candidate.PreconditionsFingerprint != context.PreconditionsFingerprint
            || candidate.Safety != context.Safety)
            throw new AgentRoutineLearningConflictException("Learning candidate parent, preconditions, or safety boundary changed.");

        var proof = procedureHistory.Revisions.SingleOrDefault(item => item.Id == procedureHistory.ActiveRevisionId);
        if (procedureHistory.Version <= 0 || procedureHistory.ActiveRevisionId is null
            || proof is not { State: AgentProcedureRevisionState.Active, Verification: not null }
            || proof.BindingFingerprint != context.BindingFingerprint
            || proof.ToolFingerprint != context.ToolFingerprint
            || string.IsNullOrWhiteSpace(proof.Verification.VerifierId)
            || string.IsNullOrWhiteSpace(proof.Verification.EvidenceHash)
            || proof.Verification.VerifiedAt == default
            || proof.Verification.PreconditionsFingerprint != context.PreconditionsFingerprint
            || proof.Verification.SafetyFingerprint != SafetyFingerprint(context.Safety))
            throw new AgentRoutineLearningConflictException("No matching accepted host procedure receipt is available.");

        var revisions = current.Revisions.ToList();
        var candidateIndex = revisions.FindIndex(item => item.Id == candidate.Id);
        revisions[candidateIndex] = candidate with { State = AgentRoutineLearningRevisionState.Adopted };
        if (current.ActiveRevisionId is { } previousId)
        {
            var index = revisions.FindIndex(item => item.Id == previousId);
            revisions[index] = revisions[index] with { State = AgentRoutineLearningRevisionState.Superseded };
        }
        var accepted = candidate with
        {
            Id = Guid.NewGuid(),
            ParentRevisionId = current.ActiveRevisionId,
            Sequence = checked(revisions.Count + 1),
            State = AgentRoutineLearningRevisionState.Active,
            CandidateRevisionId = candidate.Id,
            AcceptedProcedureRevisionId = proof.Id,
            AcceptedProcedureHistoryVersion = procedureHistory.Version,
            Verification = proof.Verification,
            CreatedAtUtc = now,
        };
        revisions.Add(accepted);
        var replacement = current with
        {
            Version = checked(current.Version + 1),
            ActiveRevisionId = accepted.Id,
            Revisions = revisions,
        };
        ValidateTransition(current, current.Version, replacement);
        return replacement;
    }

    /// <summary>Reverts by appending a new active revision copied from retained history.</summary>
    public static AgentRoutineLearningHistory Revert(
        AgentRoutineLearningHistory current,
        Guid targetRevisionId,
        AgentRoutineLearningContext context,
        AgentProcedureHistory procedureHistory,
        DateTimeOffset now)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(procedureHistory);
        EnsureKey(context.Key, current.Key);
        EnsureKey(context.Key, procedureHistory.Key);
        ValidateHistory(current);
        if (current.ActiveRevisionId is not { } activeId || targetRevisionId == activeId)
            throw new AgentRoutineLearningConflictException("A distinct active revision and rollback target are required.");
        var active = current.Revisions.Single(item => item.Id == activeId);
        var target = current.Revisions.SingleOrDefault(item => item.Id == targetRevisionId
            && item.State == AgentRoutineLearningRevisionState.Superseded)
            ?? throw new AgentRoutineLearningConflictException("Rollback target is not retained superseded history.");
        if (active.BindingFingerprint != context.BindingFingerprint || active.ToolFingerprint != context.ToolFingerprint
            || active.PreconditionsFingerprint != context.PreconditionsFingerprint || active.Safety != context.Safety
            || target.BindingFingerprint != context.BindingFingerprint || target.ToolFingerprint != context.ToolFingerprint
            || target.PreconditionsFingerprint != context.PreconditionsFingerprint || target.Safety != context.Safety)
            throw new AgentRoutineLearningConflictException("Rollback is not valid under the current procedure and safety fingerprints.");
        var proof = procedureHistory.Revisions.SingleOrDefault(item => item.Id == procedureHistory.ActiveRevisionId);
        if (target.AcceptedProcedureRevisionId != procedureHistory.ActiveRevisionId
            || target.AcceptedProcedureHistoryVersion is null
            || target.AcceptedProcedureHistoryVersion != procedureHistory.Version
            || proof is not { State: AgentProcedureRevisionState.Active, Verification: not null }
            || proof.Verification != target.Verification
            || proof.Verification.PreconditionsFingerprint != context.PreconditionsFingerprint
            || proof.Verification.SafetyFingerprint != SafetyFingerprint(context.Safety))
            throw new AgentRoutineLearningConflictException("Rollback target no longer has its accepted procedure receipt at the active head.");

        var revisions = current.Revisions.ToList();
        revisions[revisions.FindIndex(item => item.Id == activeId)] = active with { State = AgentRoutineLearningRevisionState.Superseded };
        var restored = target with
        {
            Id = Guid.NewGuid(),
            ParentRevisionId = activeId,
            Sequence = checked(revisions.Count + 1),
            State = AgentRoutineLearningRevisionState.Active,
            CreatedAtUtc = now,
            RevertsRevisionId = target.Id,
        };
        revisions.Add(restored);
        var replacement = current with
        {
            Version = checked(current.Version + 1),
            ActiveRevisionId = restored.Id,
            Revisions = revisions,
        };
        ValidateTransition(current, current.Version, replacement);
        return replacement;
    }

    public static string RenderCurrent(AgentRoutineLearningHistory? history, AgentProcedureHistory? procedureHistory,
        AgentRoutineLearningContext context)
    {
        ValidateContext(context);
        if (history is null || procedureHistory is null || history.ActiveRevisionId is not { } activeId) return string.Empty;
        ValidateHistory(history);
        EnsureKey(context.Key, history.Key);
        EnsureKey(context.Key, procedureHistory.Key);
        var active = history.Revisions.SingleOrDefault(item => item.Id == activeId
            && item.State == AgentRoutineLearningRevisionState.Active);
        if (active is null || !HasCurrentAcceptedProof(active, procedureHistory, context))
            return string.Empty;

        return AgentLearnedInstructionPolicy.RenderPromptSection([
            new AgentLearnedPracticeView(active.Sequence, string.Empty, string.Empty, null,
                "routine:" + active.Id.ToString("N"), active.Practice.Kind),
        ]);
    }

    /// <summary>Shared state-machine guard used by every durable adapter before committing CAS.</summary>
    public static void ValidateTransition(AgentRoutineLearningHistory? current, long expectedVersion,
        AgentRoutineLearningHistory replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedVersion < 0 || replacement.Version != expectedVersion + 1)
            throw new AgentRoutineLearningConflictException("Learning history CAS must advance exactly one version.");
        ValidateHistory(replacement);
        if (current is null)
        {
            if (expectedVersion != 0 || replacement.Version != 1 || replacement.ActiveRevisionId is not null
                || replacement.Revisions.Count != 1 || replacement.Revisions[0].State != AgentRoutineLearningRevisionState.Staged
                || replacement.Revisions[0].ParentRevisionId is not null)
                throw new AgentRoutineLearningConflictException("Initial learning history may contain only one staged candidate.");
            return;
        }

        EnsureKey(current.Key, replacement.Key);
        ValidateHistory(current);
        if (current.Version != expectedVersion || replacement.Revisions.Count < current.Revisions.Count)
            throw new AgentRoutineLearningConflictException("Learning history changed or lost retained revisions.");
        foreach (var prior in current.Revisions)
        {
            var next = replacement.Revisions.SingleOrDefault(item => item.Id == prior.Id)
                ?? throw new AgentRoutineLearningConflictException("Learning history is append-only; revisions cannot be removed.");
            if (prior with { State = next.State } != next)
                throw new AgentRoutineLearningConflictException("Existing learning revision content is immutable.");
            if (prior.State != next.State && !AllowedStateTransition(prior.State, next.State))
                throw new AgentRoutineLearningConflictException("Invalid learning revision state transition.");
        }
        var appended = replacement.Revisions.Skip(current.Revisions.Count).ToArray();
        if (appended.Length != 1 || appended[0].Sequence != current.Revisions.Count + 1
            || appended[0].Id == Guid.Empty)
            throw new AgentRoutineLearningConflictException("A history write must append exactly one ordered revision.");
        ValidateRevision(appended[0]);
        ValidateActivePointer(replacement);
        if (replacement.ActiveRevisionId == current.ActiveRevisionId)
        {
            if (appended[0].State is not (AgentRoutineLearningRevisionState.Staged or AgentRoutineLearningRevisionState.Rejected)
                || appended[0].ParentRevisionId != current.ActiveRevisionId)
                throw new AgentRoutineLearningConflictException("Only a staged candidate may leave the active head unchanged.");
        }
        else if (appended[0].State == AgentRoutineLearningRevisionState.Active)
        {
            if (replacement.ActiveRevisionId != appended[0].Id
                || appended[0].ParentRevisionId != current.ActiveRevisionId
                || appended[0].Verification is null || appended[0].AcceptedProcedureRevisionId is null
                || appended[0].AcceptedProcedureHistoryVersion is null)
                throw new AgentRoutineLearningConflictException("Active learning requires a child revision and accepted host proof.");

            if (appended[0].RevertsRevisionId is { } rollbackId)
            {
                var rollbackTarget = current.Revisions.SingleOrDefault(item => item.Id == rollbackId
                    && item.State == AgentRoutineLearningRevisionState.Superseded);
                if (rollbackTarget is null || rollbackTarget.Practice != appended[0].Practice
                    || rollbackTarget.BindingFingerprint != appended[0].BindingFingerprint
                    || rollbackTarget.ToolFingerprint != appended[0].ToolFingerprint
                    || rollbackTarget.PreconditionsFingerprint != appended[0].PreconditionsFingerprint
                    || rollbackTarget.Safety != appended[0].Safety
                    || rollbackTarget.Verification != appended[0].Verification
                    || rollbackTarget.AcceptedProcedureRevisionId != appended[0].AcceptedProcedureRevisionId
                    || rollbackTarget.AcceptedProcedureHistoryVersion != appended[0].AcceptedProcedureHistoryVersion
                    || rollbackTarget.CandidateRevisionId != appended[0].CandidateRevisionId)
                    throw new AgentRoutineLearningConflictException("Rollback must restore an unchanged retained revision with its original receipt.");
            }
            else if (appended[0].CandidateRevisionId is { } candidateId)
            {
                var candidateBefore = current.Revisions.SingleOrDefault(item => item.Id == candidateId
                    && item.State == AgentRoutineLearningRevisionState.Staged);
                var candidateAfter = replacement.Revisions.SingleOrDefault(item => item.Id == candidateId
                    && item.State == AgentRoutineLearningRevisionState.Adopted);
                if (appended[0].RevertsRevisionId is not null || candidateBefore is null || candidateAfter is null
                    || candidateBefore.ParentRevisionId != current.ActiveRevisionId
                    || candidateBefore.Practice != appended[0].Practice
                    || candidateBefore.BindingFingerprint != appended[0].BindingFingerprint
                    || candidateBefore.ToolFingerprint != appended[0].ToolFingerprint
                    || candidateBefore.PreconditionsFingerprint != appended[0].PreconditionsFingerprint
                    || candidateBefore.Safety != appended[0].Safety
                    || candidateAfter with { State = candidateBefore.State } != candidateBefore)
                    throw new AgentRoutineLearningConflictException("Adoption must activate exactly the staged behavior under the same fingerprints.");
            }
            else
                throw new AgentRoutineLearningConflictException("An active revision must retain adoption or rollback provenance.");
        }
        else
            throw new AgentRoutineLearningConflictException("A learning write cannot clear or redirect the active head without a verified child.");
    }

    public static void ValidateHistory(AgentRoutineLearningHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(history.Revisions);
        if (history.Version < 0 || history.Revisions.Count != history.Version)
            throw new AgentRoutineLearningConflictException("Learning history version does not match its append-only revision count.");
        var ids = new HashSet<Guid>();
        for (var index = 0; index < history.Revisions.Count; index++)
        {
            var revision = history.Revisions[index];
            ValidateRevision(revision);
            if (revision.Sequence != index + 1 || revision.Id == Guid.Empty || ids.Contains(revision.Id))
                throw new AgentRoutineLearningConflictException("Learning history has duplicate or unordered revisions.");
            if (revision.ParentRevisionId is { } parent && !ids.Contains(parent))
                throw new AgentRoutineLearningConflictException("Learning history parent must precede its child.");
            if (revision.CandidateRevisionId is { } candidateId
                && !history.Revisions.Take(index).Any(item => item.Id == candidateId
                    && item.State == AgentRoutineLearningRevisionState.Adopted))
                throw new AgentRoutineLearningConflictException("Adopted learning must retain its adopted candidate provenance.");
            ids.Add(revision.Id);
        }
        ValidateActivePointer(history);
        var activeCount = history.Revisions.Count(item => item.State == AgentRoutineLearningRevisionState.Active);
        if (activeCount != (history.ActiveRevisionId is null ? 0 : 1))
            throw new AgentRoutineLearningConflictException("Learning history active pointer is inconsistent.");
    }

    private static void ValidateRevision(AgentRoutineLearningRevision revision)
    {
        if (revision.Id == Guid.Empty || revision.Sequence <= 0 || !Enum.IsDefined(revision.State))
            throw new AgentRoutineLearningConflictException("Learning revision identity is invalid.");
        ArgumentNullException.ThrowIfNull(revision.Practice);
        var normalized = AgentLearnedInstructionPolicy.Normalize(revision.Practice.Kind, revision.Practice.Trigger,
            revision.Practice.Procedure, revision.Practice.Reason);
        if (normalized != revision.Practice) throw new AgentRoutineLearningConflictException("Stored practice was not normalized.");
        RequireHash(revision.BindingFingerprint, nameof(revision.BindingFingerprint));
        RequireHash(revision.ToolFingerprint, nameof(revision.ToolFingerprint));
        RequireHash(revision.PreconditionsFingerprint, nameof(revision.PreconditionsFingerprint));
        ValidateSafety(revision.Safety);
        if (revision.State == AgentRoutineLearningRevisionState.Active
            && (revision.Verification is null || revision.AcceptedProcedureRevisionId is null
                || revision.AcceptedProcedureHistoryVersion is null
                || revision.AcceptedProcedureHistoryVersion <= 0
                || revision.AcceptedProcedureRevisionId == Guid.Empty
                || revision.Verification.PreconditionsFingerprint is null
                || revision.Verification.SafetyFingerprint is null))
            throw new AgentRoutineLearningConflictException("An active learned practice requires durable host procedure proof.");
        if (revision.Verification is { } proof)
        {
            if (string.IsNullOrWhiteSpace(proof.VerifierId) || proof.VerifiedAt == default)
                throw new AgentRoutineLearningConflictException("A stored procedure proof receipt is incomplete.");
            if (proof.EvidenceHash is null || !Sha256.IsMatch(proof.EvidenceHash))
                throw new AgentRoutineLearningConflictException("A stored procedure proof must include a SHA-256 evidence hash.");
            if (proof.PreconditionsFingerprint is not null)
                RequireHash(proof.PreconditionsFingerprint, nameof(proof.PreconditionsFingerprint));
            if (proof.SafetyFingerprint is not null)
                RequireHash(proof.SafetyFingerprint, nameof(proof.SafetyFingerprint));
        }
    }

    private static void ValidateActivePointer(AgentRoutineLearningHistory history)
    {
        var active = history.Revisions.Where(item => item.State == AgentRoutineLearningRevisionState.Active).ToArray();
        if (history.ActiveRevisionId is null ? active.Length != 0
            : active.Length != 1 || active[0].Id != history.ActiveRevisionId)
            throw new AgentRoutineLearningConflictException("Learning active head is inconsistent.");
    }

    private static bool AllowedStateTransition(AgentRoutineLearningRevisionState oldState,
        AgentRoutineLearningRevisionState newState)
        => (oldState, newState) is
            (AgentRoutineLearningRevisionState.Active, AgentRoutineLearningRevisionState.Superseded)
            or (AgentRoutineLearningRevisionState.Staged, AgentRoutineLearningRevisionState.Rejected)
            or (AgentRoutineLearningRevisionState.Staged, AgentRoutineLearningRevisionState.Adopted);

    private static void EnsureKey(AgentProcedureKey expected, AgentProcedureKey actual)
    {
        if (expected != actual) throw new AgentRoutineLearningConflictException("Learning scope does not match the host-bound profile, agent, and task.");
    }

    private static bool MatchesContext(AgentRoutineLearningRevision item, AgentRoutineLearningContext context)
        => item.BindingFingerprint == context.BindingFingerprint
            && item.ToolFingerprint == context.ToolFingerprint
            && item.PreconditionsFingerprint == context.PreconditionsFingerprint
            && item.Safety == context.Safety;

    // A refined procedure can keep the same context fingerprints while replacing
    // its accepted head. Do not let an obsolete active practice suppress staging
    // a freshly verified revision. Missing host-read proof also cannot dedupe it.
    private static bool HasCurrentAcceptedProof(AgentRoutineLearningRevision active,
        AgentProcedureHistory? procedureHistory, AgentRoutineLearningContext context)
    {
        if (procedureHistory is null || active.State != AgentRoutineLearningRevisionState.Active
            || !MatchesContext(active, context) || active.Verification is null
            || active.AcceptedProcedureRevisionId is null || active.AcceptedProcedureHistoryVersion is null
            || active.AcceptedProcedureHistoryVersion != procedureHistory.Version
            || active.AcceptedProcedureRevisionId != procedureHistory.ActiveRevisionId)
            return false;
        var procedure = procedureHistory.Revisions.SingleOrDefault(item => item.Id == procedureHistory.ActiveRevisionId);
        return procedure is { State: AgentProcedureRevisionState.Active, Verification: not null }
            && procedure.Verification == active.Verification
            && procedure.Verification.PreconditionsFingerprint == context.PreconditionsFingerprint
            && procedure.Verification.SafetyFingerprint == SafetyFingerprint(context.Safety)
            && procedure.BindingFingerprint == context.BindingFingerprint
            && procedure.ToolFingerprint == context.ToolFingerprint;
    }

    private static void RequireHash(string? value, string field)
    {
        if (value is null || !Sha256.IsMatch(value))
            throw new ArgumentException($"{field} must be a SHA-256 fingerprint; raw values are not accepted.", field);
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Pattern();
}
