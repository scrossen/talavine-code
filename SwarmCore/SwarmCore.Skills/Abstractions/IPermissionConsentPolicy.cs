using SwarmCore.Skills.Models;

namespace SwarmCore.Skills.Abstractions;

/// <summary>Host execution authority is evaluated before creating any UI/ledger request.
/// Null leaves the existing standing/session consent behavior in charge.</summary>
public interface IPermissionConsentPolicy
{
    Task<PermissionConsentResult?> EvaluateAsync(PermissionConsentRequest request, CancellationToken ct);
}
