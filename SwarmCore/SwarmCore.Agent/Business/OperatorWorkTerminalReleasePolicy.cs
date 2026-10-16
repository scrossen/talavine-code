using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Business;

/// <summary>Eligibility only; a host must prove current authority, result and atomic ownership.</summary>
public static class OperatorWorkTerminalReleasePolicy
{
    public const int Version = 1;
    public static string PolicySha256 { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("operator-work-terminal-release-v1:completed;confirmed;no-pending;no-lease;nonempty-known-successful-completed-receipts;no-authority-grant"))).ToLowerInvariant();
    public static bool CanRelease(ContinualObjectiveStatus status, bool confirmed, bool completionPending,
        bool activeLease, IReadOnlyList<ContinualToolReceipt> receipts)
        => status == ContinualObjectiveStatus.Completed && confirmed && !completionPending && !activeLease
            && receipts is { Count: > 0 } && receipts.All(r => Enum.IsDefined(r.SideEffect)
                && r.SideEffect != ContinualSideEffectClass.Unknown && r.State == ContinualToolReceiptState.Completed
                && !r.IsError && r.CompletedAtUtc is not null && r.CompletedAtUtc >= r.StartedAtUtc);
}
