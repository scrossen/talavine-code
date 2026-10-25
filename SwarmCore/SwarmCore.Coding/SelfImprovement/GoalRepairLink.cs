using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>A stopped goal's exact baseline. A code fix is never evidence that an external action did or did not happen.</summary>
public sealed record GoalRepairLink(Guid GoalId, long GoalVersion, string IntentDigest, string FailureDigest,
    ContinualObjectiveStatus Status, DateTimeOffset ObservedAtUtc)
{
    public string? WorkIntentDigest { get; init; }
    public static GoalRepairLink From(ContinualObjectiveSnapshot goal, DateTimeOffset now)
        => new(goal.Id, goal.Version, Intent(goal), Failure(goal), goal.Status, now) { WorkIntentDigest = Intent(goal, ignoreUserControl: true) };

    public static string Intent(ContinualObjectiveSnapshot goal, bool ignoreUserControl = false)
    {
        var payload = goal.ExecutorPayloadJson is null ? null : JsonNode.Parse(goal.ExecutorPayloadJson)?.AsObject();
        payload?.Remove(nameof(CodingGoalPayload.RepairContinuation));
        if (ignoreUserControl) payload?.Remove(nameof(CodingGoalPayload.LastUserControl));
        return Digest(JsonSerializer.Serialize(new { goal.ObjectiveText, goal.Authority, goal.ExecutorKey,
            goal.ConversationId, ExecutorPayloadJson = payload?.ToJsonString(), goal.Provider, goal.Model }));
    }

    public static string Failure(ContinualObjectiveSnapshot goal)
        => Digest(JsonSerializer.Serialize(new { goal.LastFailure, goal.Status, goal.AttemptCount,
            Effects = goal.Receipts.Where(MissionTransitions.NeedsInspection)
                .Select(r => new { r.Attempt, r.ToolCallId, Fingerprint = ContinualReceiptResolutionPolicy.Fingerprint(r) }) }));

    public bool CanResume(ContinualObjectiveSnapshot goal, DateTimeOffset now)
        => goal.Id == GoalId && goal.Version == GoalVersion && goal.Status == Status
            && goal.Status is (ContinualObjectiveStatus.Uncertain or ContinualObjectiveStatus.Failed
                or ContinualObjectiveStatus.AttentionRequired)
            && goal.Mission is null && goal.PartitionKey == CodingGoalOrchestrator.Partition
            && !(goal.LeaseExpiresAtUtc > now) && goal.LeaseToken is null
            && goal.PausedByParentId is null && goal.ExternalDependency is null && goal.TargetClarification is null
            && goal.ConstraintConflict is null && goal.IntegrationConflict is null
            && !goal.VerificationCandidatePending && !goal.CompletionVerificationPending
            && goal.VerificationCandidate is null
            && Intent(goal) == IntentDigest && Failure(goal) == FailureDigest;

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
