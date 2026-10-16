using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Continual;

public enum CriterionValidationMethod { Receipt, JsonPostcondition, Semantic }
public enum CriterionOutcome { Pass, Fail, Inconclusive }
public enum MissionEvidenceKind { ExecutionReceipt, Artifact, StateObservation, Source, PersonalState }

/// <summary>Immutable host assignment. Tools may propose a plan, never replace these criteria.</summary>
public sealed record MissionAcceptanceCriterion(string Id, string Description, CriterionValidationMethod Method)
{
    public IReadOnlyList<MissionEvidenceKind> RequiredEvidenceKinds { get; init; } = [MissionEvidenceKind.ExecutionReceipt];
    public MissionEvidenceRequirement? Receipt { get; init; }
    public string? JsonProperty { get; init; }
    public string? ExpectedJson { get; init; }
    public int? ExpectedExitCode { get; init; }
}

public sealed record CriterionEvaluation(string CriterionId, CriterionOutcome Outcome, string Reason,
    IReadOnlyList<string> EvidenceReferences);

public sealed record MissionVerificationRun(Guid ObjectiveId, int Attempt, int ConstraintVersion,
    string CriteriaDigest, string EvidenceDigest, string OutputDigest, DateTimeOffset AtUtc,
    IReadOnlyList<CriterionEvaluation> Criteria);

/// <summary>Metadata describes host observations, not facts asserted by a source or learned procedures.</summary>
public sealed record MissionReceiptEvidence(string Digest, string Source, DateTimeOffset ObservedAtUtc,
    string AccessPartition, string Retention, string? RepositoryRevision = null,
    string? ArchiveReference = null);

public sealed record MissionObservedState(MissionEvidenceKind Kind, string Target, string VersionDigest, string? AccountId = null)
{
    public IReadOnlyList<Guid> ClaimIds { get; init; } = [];
    public DateTimeOffset? EffectiveAtUtc { get; init; }
    public DateTimeOffset? KnownAtUtc { get; init; }
    public string? StateSummary { get; init; }
}

public static class MissionVerification
{
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Reference(ContinualToolReceipt receipt) => $"receipt:{receipt.ObjectiveId:N}:{receipt.Attempt}:{receipt.ToolCallId}";
    public static string OutputReference(string output) => "output:" + Digest(output);
    public static string EvidenceDigest(ContinualObjectiveSnapshot objective) => Digest(JsonSerializer.Serialize(objective.Receipts));
    public static string CriteriaDigest(ContinualObjectiveSnapshot objective) => Digest(JsonSerializer.Serialize(new
        { objective.ObjectiveText, objective.Mission!.ConstraintVersion, objective.Mission.Constraints,
            objective.Mission.EvidenceEffectiveAtUtc, objective.Mission.EvidenceKnownAtUtc,
            objective.Mission.ReminderDueAtUtc, objective.Mission.ReminderDueDate, objective.Mission.ReminderTimeZoneId,
            TaskCorrections = objective.SteeringHistory.Where(s => s.Kind == SwarmCore.Agent.Runtime.AgentSteeringKind.TaskCorrection),
            Criteria = Criteria(objective) }));

    public static IReadOnlyList<MissionAcceptanceCriterion> Criteria(ContinualObjectiveSnapshot objective)
    {
        var mission = objective.Mission!;
        if (mission.AcceptanceCriteria.Count > 0 && !mission.PlanIsModelAuthored) return mission.AcceptanceCriteria;
        // Old contracts acquire deterministic stable IDs without rewriting their stored assignment.
        var criteria = mission.EvidenceRequirements.Select((r, i) => new MissionAcceptanceCriterion(
            $"evidence-{i}-{Digest(JsonSerializer.Serialize(r))[..12]}", $"Observe successful {r.ToolName}", CriterionValidationMethod.Receipt)
            { Receipt = r }).ToList();
        criteria.Add(new("objective", objective.ObjectiveText, CriterionValidationMethod.Semantic)
            { RequiredEvidenceKinds = mission.EvidenceRequirements.Count == 0 ? [] : [MissionEvidenceKind.ExecutionReceipt] });
        criteria.AddRange(mission.SuccessCriteria.Select((s, i) => new MissionAcceptanceCriterion(
            $"success-{i}-{Digest(s)[..12]}", s, CriterionValidationMethod.Semantic)
            { RequiredEvidenceKinds = mission.EvidenceRequirements.Count == 0 ? [] : [MissionEvidenceKind.ExecutionReceipt] }));
        return criteria;
    }

    public static CriterionEvaluation Check(ContinualObjectiveSnapshot objective, MissionAcceptanceCriterion criterion)
    {
        CriterionEvaluation Result(CriterionOutcome outcome, string reason, ContinualToolReceipt? r = null)
            => new(criterion.Id, outcome, reason, r is null ? [] : [Reference(r)]);
        if (criterion.Receipt is not { } required)
            return Result(CriterionOutcome.Inconclusive, "The host criterion has no observation selector.");
        var candidates = objective.Receipts.Where(r => r.ToolName == required.ToolName
            && MissionSteeringPolicy.CanUseReceiptContent(objective, r)
            && (required.InputJson is null || MissionTransitions.EquivalentInput(required.InputJson, r.InputJson))).ToArray();
        var receipt = candidates.MaxBy(r => r.Sequence);
        if (receipt is null) return Result(CriterionOutcome.Inconclusive, $"Run {required.ToolName} to obtain the required observation.");
        if (receipt.ObjectiveId != objective.Id || receipt.Attempt > objective.AttemptCount || receipt.CompletedAtUtc is null
            || receipt.CompletedAtUtc < receipt.StartedAtUtc || receipt.State != ContinualToolReceiptState.Completed || !receipt.WasDispatched)
            return Result(CriterionOutcome.Inconclusive, "The observation is incomplete or belongs to another mission/attempt.", receipt);
        if (receipt.IsError) return Result(CriterionOutcome.Fail, "The executed observation failed; repair the reported error and rerun this check.", receipt);
        if (receipt.ArtifactEvidenceError is not null)
            return Result(CriterionOutcome.Inconclusive, "The latest observation could not be preserved or independently captured; inspect and reverify it.", receipt);
        if (required.MustBeReadOnly && receipt.SideEffect != ContinualSideEffectClass.ReadOnly
            || string.IsNullOrWhiteSpace(receipt.Result) || required.ResultContains is not null
                && !receipt.Result.Contains(required.ResultContains, StringComparison.Ordinal))
            return Result(CriterionOutcome.Fail, "The latest observation does not satisfy the assigned selector.", receipt);
        var isolated = objective with { Mission = objective.Mission! with { EvidenceRequirements = [required] } };
        if (MissionTransitions.MissingEvidence(isolated) is { } missing)
            return Result(CriterionOutcome.Inconclusive, missing, receipt);
        if (criterion.RequiredEvidenceKinds.Contains(MissionEvidenceKind.Artifact)
            && (receipt.ArtifactHashes is not { Count: > 0 } || receipt.ArtifactHashes.Values.All(v => v == "missing")))
            return Result(CriterionOutcome.Inconclusive, "No host artifact digest supports this criterion.", receipt);
        if (criterion.RequiredEvidenceKinds.Any(k => k is MissionEvidenceKind.Source or MissionEvidenceKind.StateObservation or MissionEvidenceKind.PersonalState
            && !receipt.ObservedStates.Any(s => s.Kind == k)))
            return Result(CriterionOutcome.Inconclusive, "The host has not captured the required source or durable-state observation.", receipt);
        if (criterion.Method == CriterionValidationMethod.JsonPostcondition || criterion.ExpectedExitCode is not null)
        {
            try
            {
                var value = JsonNode.Parse(receipt.Result!);
                if (criterion.ExpectedExitCode is { } code)
                {
                    var exit = value?["ExitCode"] ?? value?["exitCode"];
                    if (exit is null || exit.GetValue<int>() != code || (value?["TimedOut"] ?? value?["timedOut"])?.GetValue<bool>() == true)
                        return Result(CriterionOutcome.Fail, "The host-observed command exit code does not satisfy the assigned check.", receipt);
                }
                if (criterion.Method == CriterionValidationMethod.JsonPostcondition)
                {
                    if (string.IsNullOrWhiteSpace(criterion.JsonProperty) || criterion.ExpectedJson is null)
                        return Result(CriterionOutcome.Inconclusive, "The host postcondition is incomplete.", receipt);
                    foreach (var segment in criterion.JsonProperty.Split('.'))
                    {
                        if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment, out value))
                            return Result(CriterionOutcome.Inconclusive, "The observed state is missing the required property.", receipt);
                    }
                    if (!JsonNode.DeepEquals(value, JsonNode.Parse(criterion.ExpectedJson)))
                        return Result(CriterionOutcome.Fail, "The observed durable state differs from the assigned postcondition.", receipt);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            { return Result(CriterionOutcome.Inconclusive, "The observation is not a valid typed result.", receipt); }
        }
        return Result(CriterionOutcome.Pass, "The host observation satisfies the assigned check.", receipt);
    }

    public static string? ValidateCompletion(ContinualObjectiveSnapshot objective, MissionVerificationRun? run, string? output)
    {
        if (objective.Mission is null) return null;
        if (run is null || run.ObjectiveId != objective.Id || run.Attempt != objective.AttemptCount
            || run.ConstraintVersion != objective.Mission.ConstraintVersion || run.CriteriaDigest != CriteriaDigest(objective)
            || run.EvidenceDigest != EvidenceDigest(objective) || run.OutputDigest != Digest(output ?? ""))
            return "Current host verification is missing or stale.";
        var criteria = Criteria(objective);
        if (run.Criteria.Count != criteria.Count || run.Criteria.Select(c => c.CriterionId).Distinct().Count() != criteria.Count)
            return "The verifier did not evaluate every assigned criterion exactly once.";
        foreach (var criterion in criteria)
        {
            var verdict = run.Criteria.SingleOrDefault(c => c.CriterionId == criterion.Id);
            if (verdict is null || verdict.Outcome != CriterionOutcome.Pass
                || !Supports(criterion, verdict.EvidenceReferences, objective, output ?? "")) return $"Criterion {criterion.Id} lacks a supported pass.";
            if (criterion.Method != CriterionValidationMethod.Semantic && Check(objective, criterion).Outcome != CriterionOutcome.Pass)
                return $"Criterion {criterion.Id} no longer passes its deterministic check.";
        }
        return null;
    }

    public static bool Supports(MissionAcceptanceCriterion criterion, IReadOnlyList<string> references,
        ContinualObjectiveSnapshot objective, string output)
    {
        if (references.Count == 0) return false;
        var receipts = objective.Receipts.Where(r => r.ObjectiveId == objective.Id && r.Attempt <= objective.AttemptCount
            && MissionSteeringPolicy.CanUseReceiptContent(objective, r)
            && r.WasDispatched && !r.IsError && r.ArtifactEvidenceError is null && r.State == ContinualToolReceiptState.Completed
            && r.CompletedAtUtc is not null && r.CompletedAtUtc >= r.StartedAtUtc
            && references.Contains(Reference(r), StringComparer.Ordinal)).ToArray();
        if (references.Any(r => !receipts.Any(receipt => Reference(receipt) == r)
            && !(criterion.RequiredEvidenceKinds.Count == 0 && r == OutputReference(output)))) return false;
        return criterion.RequiredEvidenceKinds.All(kind => kind switch
        {
            MissionEvidenceKind.ExecutionReceipt => receipts.Any(r => r.WasDispatched && !r.IsError && r.State == ContinualToolReceiptState.Completed),
            MissionEvidenceKind.Artifact => receipts.Any(r => r.ArtifactEvidenceError is null && r.ArtifactHashes is { Count: > 0 }
                && r.ArtifactHashes.Values.Any(v => v != "missing")),
            MissionEvidenceKind.Source or MissionEvidenceKind.StateObservation or MissionEvidenceKind.PersonalState => receipts.Any(r => r.ArtifactEvidenceError is null && r.ObservedStates.Any(s => s.Kind == kind)),
            _ => false,
        });
    }
}
