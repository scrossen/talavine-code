using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Agent.Procedures;

public enum AgentProcedureAcceptanceCandidateKind { PromoteRevision, ReconcileEffects }

/// <summary>
/// Immutable, host-created acceptance intent. It contains parameterized plans and verification
/// fingerprints, never invocation receipts, results or a saved answer. Export is not authority to
/// promote: a durable host must independently prove committed terminal/output and current acceptance.
/// </summary>
public sealed record AgentProcedureAcceptanceCandidate(
    int FormatVersion,
    AgentProcedureAcceptanceCandidateKind Kind,
    AgentProcedureKey Key,
    long ExpectedVersion,
    Guid? ExpectedActiveRevisionId,
    string ExpectedHistoryDigest,
    Guid? TargetRevisionId,
    string BindingFingerprint,
    string ToolFingerprint,
    AgentProcedureVerification Verification,
    string ReplacementJson,
    string ContentDigest)
{
    private const int MaximumSnapshotBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static AgentProcedureAcceptanceCandidate Freeze(
        AgentProcedureAcceptanceCandidateKind kind, AgentProcedureHistory? expected,
        AgentProcedureHistory replacement, string binding, string tools, AgentProcedureVerification verification)
    {
        var baseline = expected ?? new(replacement.Key, 0, null, []);
        var candidate = new AgentProcedureAcceptanceCandidate(1, kind, replacement.Key, baseline.Version,
            baseline.ActiveRevisionId, HistoryDigest(baseline), replacement.ActiveRevisionId, binding, tools,
            verification, JsonSerializer.Serialize(replacement, SnapshotOptions), string.Empty);
        candidate = candidate with { ContentDigest = candidate.ComputeDigest() };
        _ = candidate.ReadReplacement();
        return candidate;
    }

    /// <summary>Returns a fresh detached snapshot; mutation cannot change the exported intent.</summary>
    public AgentProcedureHistory ReadReplacement()
    {
        if (FormatVersion != 1 || !Enum.IsDefined(Kind) || ExpectedVersion < 0 || ExpectedVersion == long.MaxValue
            || Key is null || Verification is null || ReplacementJson is null
            || ExpectedHistoryDigest is not { Length: 64 } || ExpectedHistoryDigest.Any(c => !Uri.IsHexDigit(c))
            || ContentDigest is not { Length: 64 } || ContentDigest.Any(c => !Uri.IsHexDigit(c))
            || TargetRevisionId == Guid.Empty || ExpectedActiveRevisionId == Guid.Empty
            || string.IsNullOrWhiteSpace(Key.ProfileId) || string.IsNullOrWhiteSpace(Key.AgentId)
            || string.IsNullOrWhiteSpace(Key.TaskId) || string.IsNullOrWhiteSpace(BindingFingerprint)
            || string.IsNullOrWhiteSpace(ToolFingerprint) || string.IsNullOrWhiteSpace(Verification.VerifierId)
            || string.IsNullOrWhiteSpace(Verification.EvidenceHash)
            || Encoding.UTF8.GetByteCount(ReplacementJson) > MaximumSnapshotBytes
            || !string.Equals(ContentDigest, ComputeDigest(), StringComparison.Ordinal))
            throw new InvalidDataException("Invalid frozen procedure acceptance intent.");
        var replacement = JsonSerializer.Deserialize<AgentProcedureHistory>(ReplacementJson, SnapshotOptions)
            ?? throw new InvalidDataException("Missing procedure replacement snapshot.");
        if (replacement.Key != Key || replacement.Version != checked(ExpectedVersion + 1)
            || replacement.ActiveRevisionId != TargetRevisionId
            || replacement.Revisions.Select(r => r.Id).Distinct().Count() != replacement.Revisions.Count)
            throw new InvalidDataException("Procedure replacement identity does not match its frozen intent.");
        var target = TargetRevisionId is { } id ? replacement.Revisions.SingleOrDefault(r => r.Id == id) : null;
        if (Kind == AgentProcedureAcceptanceCandidateKind.PromoteRevision && TargetRevisionId is null
            || TargetRevisionId is not null && (target is not { State: AgentProcedureRevisionState.Active, Verification: not null }
                || target.BindingFingerprint != BindingFingerprint || target.ToolFingerprint != ToolFingerprint))
            throw new InvalidDataException("Procedure acceptance target is not the verified bound revision.");
        if (Kind == AgentProcedureAcceptanceCandidateKind.PromoteRevision && target!.Verification != Verification)
            throw new InvalidDataException("Procedure promotion proof differs from its frozen verification.");
        return replacement;
    }

    public bool MatchesExpectedHistory(AgentProcedureHistory? current)
        => (current?.Key ?? Key) == Key && (current?.Version ?? 0) == ExpectedVersion
            && (current?.ActiveRevisionId) == ExpectedActiveRevisionId
            && string.Equals(HistoryDigest(current ?? new(Key, 0, null, [])), ExpectedHistoryDigest, StringComparison.Ordinal);

    internal async Task<bool> ApplyAsync(IAgentProcedureStore store, CancellationToken ct)
    {
        var replacement = ReadReplacement();
        var current = await store.GetAsync(Key, ct).ConfigureAwait(false);
        if (!MatchesExpectedHistory(current)) return false;
        return await store.CompareExchangeAsync(Key, ExpectedVersion, replacement, ct).ConfigureAwait(false);
    }

    private string ComputeDigest() => Digest(JsonSerializer.Serialize(new
    {
        FormatVersion, Kind, Key, ExpectedVersion, ExpectedActiveRevisionId, ExpectedHistoryDigest,
        TargetRevisionId, BindingFingerprint, ToolFingerprint, Verification, ReplacementJson,
    }, SnapshotOptions));

    private static string HistoryDigest(AgentProcedureHistory history)
        => Digest(JsonSerializer.Serialize(history, SnapshotOptions));
    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
