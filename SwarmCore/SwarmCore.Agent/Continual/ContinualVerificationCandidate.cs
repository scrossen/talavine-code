using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Agent.Continual;

/// <summary>Retained output provenance, not business acceptance or permission to replay tools.</summary>
public sealed record ContinualVerificationCandidate(int ExecutionAttempt, string OutputSha256,
    string AuthoritySha256, DateTimeOffset CapturedAtUtc, string Provenance,
    Guid? TerminalTurnId = null, string? TerminalEventSha256 = null, long? ThreadVersion = null)
{
    public string? RecoveryOperatorId { get; init; }
    public long? RecoveryObjectiveVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrectionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectionParentSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectionIntentSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrectionPhysicalOperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectionReferencesSha256 { get; init; }
}

public static class ContinualVerificationCandidatePolicy
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string AuthorityHash(ContinualObjectiveSnapshot objective) => Hash(JsonSerializer.Serialize(new
    {
        objective.Id, objective.Surface, objective.PartitionKey, objective.ConversationId, objective.ExecutorKey,
        objective.ObjectiveText, objective.Provider, objective.Model, objective.Authority, objective.ExecutorPayloadJson, objective.Mission,
    }));
    public static ContinualVerificationCandidate Create(ContinualObjectiveSnapshot objective, string sanitizedOutput, DateTimeOffset atUtc)
        => new(objective.AttemptCount, Hash(sanitizedOutput), AuthorityHash(objective), atUtc, "execution-checkpoint");
    public static ContinualVerificationCandidate CreateCorrected(ContinualObjectiveSnapshot parent, string output,
        Guid correctionId, string parentIdentitySha, string intentSha, Guid physicalOperationId, string refsSha, DateTimeOffset atUtc)
    {
        if (parent.VerificationCandidate is not { } candidate || !Matches(parent, parent.LastOutput ?? string.Empty)
            || candidate.Provenance == ContinualSavedReportCorrectionPolicy.Provenance
            || !ContinualSavedReportCorrectionPolicy.IsSha256(intentSha) || !ContinualSavedReportCorrectionPolicy.IsSha256(refsSha)
            || parentIdentitySha != Identity(candidate) || correctionId == Guid.Empty || physicalOperationId == Guid.Empty
            || Hash(output) == candidate.OutputSha256 || string.IsNullOrWhiteSpace(output)
            || string.Equals(output.Trim(), parent.LastOutput?.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Correction requires the exact retained parent and positive consumed intent provenance.");
        return new(candidate.ExecutionAttempt, Hash(output), AuthorityHash(parent), atUtc, "saved-report-correction")
        {
            CorrectionId = correctionId, CorrectionParentSha256 = parentIdentitySha, CorrectionIntentSha256 = intentSha,
            CorrectionPhysicalOperationId = physicalOperationId, CorrectionReferencesSha256 = refsSha,
        };
    }
    /// <summary>Checks candidate identity and provenance shape only. Hosts must independently prove a correction's committed intent, consumed physical pair, archived parent and atomic linkage before permitting audit.</summary>
    public static bool Matches(ContinualObjectiveSnapshot objective, string sanitizedOutput)
        => objective.VerificationCandidate is { ExecutionAttempt: > 0 } candidate
            && (candidate.Provenance == ContinualSavedReportCorrectionPolicy.Provenance || !HasCorrectionFields(candidate))
            && candidate.ExecutionAttempt <= objective.AttemptCount
            && candidate.OutputSha256 == Hash(sanitizedOutput) && candidate.AuthoritySha256 == AuthorityHash(objective)
            && (candidate.Provenance == "execution-checkpoint" || candidate.Provenance == "terminal-recovery"
                && candidate.TerminalTurnId is { } turn && turn != Guid.Empty
                && candidate.TerminalEventSha256 is { Length: 64 } && candidate.ThreadVersion is > 0
                && !string.IsNullOrWhiteSpace(candidate.RecoveryOperatorId) && candidate.RecoveryObjectiveVersion is > 0
                || candidate.Provenance == "saved-report-correction"
                && candidate.CorrectionId is { } correction && correction != Guid.Empty
                && candidate.CorrectionPhysicalOperationId is { } physical && physical != Guid.Empty
                && ContinualSavedReportCorrectionPolicy.IsSha256(candidate.CorrectionParentSha256)
                && ContinualSavedReportCorrectionPolicy.IsSha256(candidate.CorrectionIntentSha256)
                && ContinualSavedReportCorrectionPolicy.IsSha256(candidate.CorrectionReferencesSha256));

    public static bool HasCorrectionFields(ContinualVerificationCandidate candidate)
        => candidate.CorrectionId is not null || candidate.CorrectionParentSha256 is not null
            || candidate.CorrectionIntentSha256 is not null || candidate.CorrectionPhysicalOperationId is not null
            || candidate.CorrectionReferencesSha256 is not null;
    public static bool RequiresCorrectionProvenance(ContinualVerificationCandidate? candidate)
        => candidate is not null && (candidate.Provenance == ContinualSavedReportCorrectionPolicy.Provenance || HasCorrectionFields(candidate));

    // Semantic canonical identity: host JSON casing/formatting cannot change what was audited.
    public static string Identity(ContinualVerificationCandidate candidate) => Hash(JsonSerializer.Serialize(candidate));
    public static ContinualVerificationExpectation Expect(ContinualObjectiveSnapshot objective) => new(
        objective.VerificationCandidate is null ? null : Identity(objective.VerificationCandidate),
        Hash(objective.LastOutput ?? string.Empty), AuthorityHash(objective));
    public static bool MatchesExpectation(ContinualObjectiveSnapshot objective, ContinualVerificationExpectation expected)
        => Expect(objective) == expected;
}

/// <summary>Exact retained audit baseline, not an authorization supplied by a model.</summary>
public sealed record ContinualVerificationExpectation(string? CandidateSha256, string OutputSha256, string AuthoritySha256);

/// <summary>Host verification persistence crossed a commit boundary without an exact acknowledgment. Never treat this as executor failure or permission to replay.</summary>
public class ContinualVerificationCommitUnknownException(Guid objectiveId)
    : InvalidOperationException("Verification commit outcome is unknown. Inspect retained proof without replaying business work.")
{
    public Guid ObjectiveId { get; } = objectiveId;
}
