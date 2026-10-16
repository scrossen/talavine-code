using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.LlmRouting.Models;

namespace SwarmCore.Agent.Continual;

public static class MissionAdaptiveReasoning
{
    public static AdaptiveAgentContext Create(ContinualObjectiveSnapshot mission)
    {
        var personal = MissionVerification.Criteria(mission).Any(c => c.RequiredEvidenceKinds.Any(k => k is MissionEvidenceKind.Source or MissionEvidenceKind.PersonalState));
        var observations = mission.VerificationHistory.Where(v => v.ConstraintVersion == mission.Mission!.ConstraintVersion)
            .OrderBy(v => v.Attempt).ThenBy(v => v.AtUtc).Select(v => new AdaptiveReasoningObservation
            {
                WorkId = "host-bound-at-runner", Sequence = v.Attempt,
                EvidenceId = "verification:" + v.Attempt + ":" + v.EvidenceDigest + ":" + v.OutputDigest,
                Failure = v.Criteria.Any(c => c.Outcome == CriterionOutcome.Fail) ? AdaptiveFailure.Acceptance : AdaptiveFailure.None,
                Evidence = v.Criteria.Any(c => c.Outcome == CriterionOutcome.Inconclusive) ? AdaptiveEvidence.MissingCoverage
                    : personal ? AdaptiveEvidence.Unverified : AdaptiveEvidence.NotRequired,
                MadeProgress = v.Criteria.Any(c => c.Outcome == CriterionOutcome.Pass),
                AcceptancePassed = v.Criteria.Count > 0 && v.Criteria.All(c => c.Outcome == CriterionOutcome.Pass)
            }).ToArray();
        return new() { PriorObservedToolIds = mission.Receipts.Select(r => r.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), InitialObservations = observations, RequiresPersonalEvidence = personal, BudgetReference = mission.Mission?.BudgetReference };
    }
}
