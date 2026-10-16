using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Business;

/// <summary>
/// Exact independently exported historical local simulation receipt. NativeId is
/// opaque sink identity, never an RFC Message-ID or current remote authority.
/// The host admits the immutable receipt file hash after owning sink readback.
/// This shape alone proves neither provenance nor customer intent or consent.
/// </summary>
public sealed record BusinessHistoricalSimulatedArtifactReference(string ScopeId, string OperationId,
    string NativeId, string ContentSha256, DateTimeOffset RecordedAtUtc, string Disposition,
    string ReceiptFileSha256);

public static class BusinessHistoricalSimulatedArtifactPolicy
{
    public static bool Valid(BusinessHistoricalSimulatedArtifactReference? value, DateTimeOffset cutoff)
        => value is not null && BusinessStageResultContract.Bounded(value.ScopeId, 200)
            && BusinessStageResultContract.Bounded(value.OperationId, 320)
            && BusinessStageResultContract.Bounded(value.NativeId, 320)
            && !value.NativeId.Any(char.IsWhiteSpace) && !value.NativeId.Contains('<') && !value.NativeId.Contains('>')
            && ContinualSavedReportCorrectionPolicy.IsSha256(value.ContentSha256)
            && ContinualSavedReportCorrectionPolicy.IsSha256(value.ReceiptFileSha256)
            && value.Disposition == "simulated" && value.RecordedAtUtc.Offset == TimeSpan.Zero
            && value.RecordedAtUtc > DateTimeOffset.UnixEpoch && value.RecordedAtUtc <= cutoff;
}
