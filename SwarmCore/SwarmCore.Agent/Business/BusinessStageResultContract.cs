using System.Globalization;
using System.Text.Json;
using SwarmCore.Abstractions.AgentWork;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Business;

public enum BusinessRecordIdentityFormat { PositiveInt64, GuidD, Sha256, ExactOpaque }
public sealed record BusinessStageReferenceRule(string Kind, BusinessRecordIdentityFormat IdentityFormat,
    int MinimumCount = 1, int MaximumCount = 1);

/// <summary>Registered host metadata. Creates neither capabilities nor evidence of a database change.</summary>
public sealed class BusinessStageResultContract
{
    public BusinessStageResultContract(string canonicalProofKind, string resultKind,
        IEnumerable<BusinessStageReferenceRule> referenceRules, IEnumerable<string> allowedToolIds,
        IEnumerable<string>? requiredToolIds = null)
    {
        if (!Bounded(canonicalProofKind, 100) || !Bounded(resultKind, 200)) throw new ArgumentException("Bounded registered result metadata required.");
        var rules = referenceRules.Take(33).Select(r => r with { }).ToArray();
        var tools = allowedToolIds.Take(33).ToArray();
        var required = requiredToolIds?.Take(33).ToArray() ?? tools;
        if (rules.Length is < 1 or > 32 || rules.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Count() != rules.Length
            || rules.Any(r => !Bounded(r.Kind, 64) || !Enum.IsDefined(r.IdentityFormat) || r.MinimumCount < 1
                || r.MaximumCount < r.MinimumCount || r.MaximumCount > 32) || rules.Sum(r => r.MaximumCount) > 32
            || tools.Length is < 1 or > 32 || tools.Any(t => !Bounded(t, 200)) || tools.Distinct(StringComparer.Ordinal).Count() != tools.Length
            || required.Length is < 1 or > 32 || required.Distinct(StringComparer.Ordinal).Count() != required.Length
            || required.Any(t => !tools.Contains(t, StringComparer.Ordinal)))
            throw new ArgumentException("Unique bounded reference roles and registered tools required.");
        CanonicalProofKind = canonicalProofKind; ResultKind = resultKind;
        ReferenceRules = Array.AsReadOnly(rules); AllowedToolIds = Array.AsReadOnly(tools);
        RequiredToolIds = Array.AsReadOnly(required.ToArray());
    }
    public string CanonicalProofKind { get; }
    public string ResultKind { get; }
    public IReadOnlyList<BusinessStageReferenceRule> ReferenceRules { get; }
    public IReadOnlyList<string> AllowedToolIds { get; }
    public IReadOnlyList<string> RequiredToolIds { get; }
    internal static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && value.Trim() == value && !value.Any(char.IsControl);
}

/// <summary>Content-free snapshot from an identified host result reader, never from model output.</summary>
public sealed record BusinessStageResultReadback(string CanonicalProofKind, string ResultKind,
    OperatorWorkOccupancyIdentity Identity, Guid ObjectiveId, string CanonicalResultSha256,
    string ResourceFootprintSha256, IReadOnlyList<ContinualReportReference> References, DateTimeOffset ObservedAtUtc);

/// <summary>Frozen matched shape from a host callback. Not authority, audit acceptance, or terminal release.</summary>
public sealed class BusinessStageMatchedResult
{
    internal BusinessStageMatchedResult(BusinessStageResultReadback readback)
        => Readback = readback with { References = Array.AsReadOnly(readback.References.Select(r => r with { }).ToArray()) };
    public BusinessStageResultReadback Readback { get; }
}

/// <summary>
/// Composes existing source-work, receipt and typed-reference contracts. This checks
/// binding/shape only: hosts must read actual rows under current authority and keep
/// result verification plus terminal release atomic. It never creates an allowance.
/// </summary>
public static class BusinessStageResultPolicy
{
    public static BusinessStageMatchedResult? FreezeMatched(BusinessStageResultContract contract, OperatorWorkContract work,
        OperatorWorkOccupancyIdentity identity, Guid objectiveId, IReadOnlyList<OperatorWorkResource> resources,
        IReadOnlyList<ContinualToolReceipt> receipts, BusinessStageResultReadback retained,
        BusinessStageResultReadback current, DateTimeOffset cutoff)
        => Matches(contract, work, identity, objectiveId, resources, receipts, retained, current, cutoff)
            ? new(current) : null;

    public static bool Matches(BusinessStageResultContract contract, OperatorWorkContract work,
        OperatorWorkOccupancyIdentity identity, Guid objectiveId, IReadOnlyList<OperatorWorkResource> resources,
        IReadOnlyList<ContinualToolReceipt> receipts, BusinessStageResultReadback retained,
        BusinessStageResultReadback current, DateTimeOffset cutoff)
    {
        if (contract is null || work is null || identity is null || resources is null || receipts is null || retained is null || current is null)
            return false;
        if (objectiveId == Guid.Empty || identity.SourceId != work.SourceId || identity.WorkSha256 != work.Sha256
            || work.RequiredLocalResult != contract.ResultKind
            || OperatorWorkOccupancyPolicy.Decide(new(identity, true, resources), []).Kind != OperatorWorkOccupancyDecisionKind.StartDistinct
            || work.AllowedEffects.Count == 0 || work.AllowedEffects.Any(e => !contract.AllowedToolIds.Contains(e.ToolId, StringComparer.Ordinal))
            || contract.RequiredToolIds.Any(id => !work.AllowedEffects.Any(e => e.ToolId == id) || !receipts.Any(r => r.ToolName == id))
            || receipts.Count is < 1 or > 256 || receipts.Any(r => r.ObjectiveId != objectiveId || !r.WasDispatched
                || r.State != ContinualToolReceiptState.Completed || r.IsError || r.CompletedAtUtc is null
                || r.CompletedAtUtc < r.StartedAtUtc || r.CompletedAtUtc > cutoff
                || !Enum.IsDefined(r.SideEffect) || r.SideEffect == ContinualSideEffectClass.Unknown
                || !contract.AllowedToolIds.Contains(r.ToolName, StringComparer.Ordinal)
                || !work.AllowedEffects.Any(e => e.ToolId == r.ToolName && e.SideEffect == r.SideEffect.ToString()))
            || receipts.Select(r => r.ToolCallId).Distinct(StringComparer.Ordinal).Count() != receipts.Count)
            return false;
        var footprint = ResourceFingerprint(resources);
        if (!ValidReadback(retained) || !ValidReadback(current)
            || retained.CanonicalResultSha256 != current.CanonicalResultSha256
            || !ContinualSavedReportCorrectionPolicy.ValidateReferences(retained.References, current.References,
                contract.ReferenceRules.Select(r => r.Kind).ToArray())) return false;
        return true;

        bool ValidReadback(BusinessStageResultReadback readback)
        {
            if (readback.Identity != identity || readback.ObjectiveId != objectiveId
                || readback.CanonicalProofKind != contract.CanonicalProofKind || readback.ResultKind != contract.ResultKind
                || !ContinualSavedReportCorrectionPolicy.IsSha256(readback.CanonicalResultSha256)
                || readback.ResourceFootprintSha256 != footprint || readback.ObservedAtUtc > cutoff
                || readback.ObservedAtUtc < receipts.Max(r => r.CompletedAtUtc!.Value)
                || readback.References is not { Count: > 0 and <= 32 }
                || readback.References.Any(r => r is null)
                || readback.References.Select(r => (r.Kind, r.RecordId)).Distinct().Count() != readback.References.Count) return false;
            foreach (var rule in contract.ReferenceRules)
            {
                var refs = readback.References.Where(r => r.Kind == rule.Kind).ToArray();
                if (refs.Length < rule.MinimumCount || refs.Length > rule.MaximumCount
                    || refs.Any(r => !CanonicalId(r.RecordId, rule.IdentityFormat))) return false;
            }
            return readback.References.All(reference => receipts.Count(r => r.ToolCallId == reference.SourceReceiptId) == 1);
        }
    }

    public static string ResourceFingerprint(IReadOnlyList<OperatorWorkResource> resources)
    {
        if (resources.Count is < 1 or > 64 || resources.Any(r => !BusinessStageResultContract.Bounded(r.Domain, 200)
            || !BusinessStageResultContract.Bounded(r.Target, 500) || r.Access is not (OperatorWorkResourceAccess.Read or OperatorWorkResourceAccess.Write)))
            throw new ArgumentException("Complete scoped host resource identities required.");
        return ContinualVerificationCandidatePolicy.Hash(JsonSerializer.Serialize(resources.OrderBy(r => r.Domain, StringComparer.Ordinal)
            .ThenBy(r => r.Target, StringComparer.Ordinal).ThenBy(r => r.Access).ToArray()));
    }

    public static bool CanonicalId(string value, BusinessRecordIdentityFormat format) => format switch
    {
        BusinessRecordIdentityFormat.PositiveInt64 => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0 && number.ToString(CultureInfo.InvariantCulture) == value,
        BusinessRecordIdentityFormat.GuidD => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value,
        BusinessRecordIdentityFormat.Sha256 => ContinualSavedReportCorrectionPolicy.IsSha256(value) && value == value.ToLowerInvariant(),
        BusinessRecordIdentityFormat.ExactOpaque => BusinessStageResultContract.Bounded(value, 256),
        _ => false,
    };
}
