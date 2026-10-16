using System.Text.Json;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Business;

public sealed record BusinessDraftIdentity(Guid BriefId, int BriefRevision, string BusinessName);

/// <summary>Local simulation identities are tied to original scoped work, never generated acknowledgement retries.</summary>
public static class BusinessSimulationDraftPolicy
{
    public static BusinessDraftIdentity? BusinessIdentity(JsonElement parameters, bool required = false)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("Work parameters must be an object.");
        if (!parameters.TryGetProperty("businessIdentity", out var value))
        {
            if (required) throw new ArgumentException("New drafts require an owner-bound businessIdentity (briefId, briefRevision, businessName).");
            return null; // Historical work remains readable; this is never permission for a new write.
        }
        if (value.ValueKind != JsonValueKind.Object
            || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "briefId", "briefRevision", "businessName" })
            || value.GetProperty("briefId").ValueKind != JsonValueKind.String || !value.GetProperty("briefId").TryGetGuid(out var id) || id == Guid.Empty
            || value.GetProperty("briefRevision").ValueKind != JsonValueKind.Number || !value.GetProperty("briefRevision").TryGetInt32(out var revision) || revision <= 0
            || value.GetProperty("businessName").ValueKind != JsonValueKind.String)
            throw new ArgumentException("A precise business brief identity and revision are required.");
        var name = value.GetProperty("businessName").GetString()!;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != name.Trim() || name.Any(char.IsControl))
            throw new ArgumentException("The business signature must be a bounded single-line canonical business name.");
        return new(id, revision, name);
    }

    public static bool HasBusinessSignature(string body, BusinessDraftIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
        return last > 0 && lines[last] == identity.BusinessName && lines.Take(last).Any(l => !string.IsNullOrWhiteSpace(l));
    }
    public static string JobIdentity(string scope, string employee, string originalJob, string sourceIdentity)
    {
        if (new[] { scope, employee, originalJob, sourceIdentity }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Exact scoped employee, original job and source identities are required.");
        return ContinualVerificationCandidatePolicy.Hash(JsonSerializer.Serialize(new { scope, employee, originalJob, sourceIdentity }));
    }
    public static string ProjectionOperation(string jobIdentity)
    {
        if (!ContinualSavedReportCorrectionPolicy.IsSha256(jobIdentity)) throw new ArgumentException("A canonical original job digest is required.");
        return "business:simulated-draft:" + jobIdentity;
    }
}
