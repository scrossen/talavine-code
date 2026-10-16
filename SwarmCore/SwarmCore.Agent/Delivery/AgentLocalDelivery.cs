using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Delivery;

/// <summary>Host-owned attempt, persisted before an immutable local artifact is appended.</summary>
public sealed record AgentLocalDeliveryCheckpoint(Guid DeliveryId, string ContentFingerprint);
public sealed record AgentLocalDeliveryRecoveryReceipt(Guid GoalId, Guid ThreadId, string ObjectiveKey,
    AgentLocalDeliveryCheckpoint Delivery, string PendingPlanJson);

/// <summary>The complete operator-reviewed snapshot. A later row is never a new authority baseline.</summary>
public sealed record AgentPendingPlanResumeExpectation(Guid GoalId, Guid ThreadId, string ObjectiveKey,
    string PendingPlanSha256, string BossTaskSubjectSha256, string BossTaskBodySha256)
{
    public static AgentPendingPlanResumeExpectation Create(Guid goalId, Guid threadId, string objectiveKey,
        string pendingPlanJson, string? bossTaskSubject, string? bossTaskBody)
        => new(goalId, threadId, objectiveKey, HashText(pendingPlanJson), HashNullableText(bossTaskSubject), HashNullableText(bossTaskBody));

    public void Validate()
    {
        if (GoalId == Guid.Empty || ThreadId == Guid.Empty || string.IsNullOrWhiteSpace(ObjectiveKey)
            || !IsHash(PendingPlanSha256) || !IsHash(BossTaskSubjectSha256) || !IsHash(BossTaskBodySha256))
            throw new InvalidOperationException("Pending-plan recovery requires a complete immutable expectation.");
    }
    public bool Matches(Guid goalId, Guid threadId, string objectiveKey, string? pendingPlanJson,
        string? bossTaskSubject, string? bossTaskBody)
        => goalId == GoalId && threadId == ThreadId && objectiveKey == ObjectiveKey && pendingPlanJson is not null
            && SameHash(PendingPlanSha256, HashText(pendingPlanJson))
            && SameHash(BossTaskSubjectSha256, HashNullableText(bossTaskSubject))
            && SameHash(BossTaskBodySha256, HashNullableText(bossTaskBody));
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string HashNullableText(string? text) => HashText(JsonSerializer.Serialize(text));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameHash(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public static class AgentLocalDeliveryIdentity
{
    public static AgentLocalDeliveryCheckpoint Create(Guid profileId, Guid goalId, string kind, string canonicalContent)
    {
        if (profileId == Guid.Empty || goalId == Guid.Empty || string.IsNullOrWhiteSpace(kind))
            throw new InvalidOperationException("Local delivery requires a durable worker and objective identity.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalContent)));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"local-boss-artifact-v1\n{profileId:D}\n{goalId:D}\n{kind}\n{fingerprint}"));
        return new(new Guid(bytes.AsSpan(0, 16)), fingerprint);
    }
}

/// <summary>Never create a replacement artifact or repeat business work to obtain acknowledgement.</summary>
public sealed class AgentLocalDeliveryOutcomeUnknownException(Guid deliveryId, string? reason = null)
    : InvalidOperationException(reason ?? "Local boss-report delivery outcome unknown; reconcile its saved identity before continuing.")
{
    public Guid DeliveryId { get; } = deliveryId;
}
