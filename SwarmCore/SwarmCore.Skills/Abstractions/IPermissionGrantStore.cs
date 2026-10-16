namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Persistent storage for "Always allow" permission grants.
/// Implementations store grants durably (e.g., in a database) so they survive app restarts.
/// </summary>
public interface IPermissionGrantStore
{
    /// <summary>
    /// Get all persistently granted permission IDs.
    /// </summary>
    Task<IReadOnlySet<string>> GetGrantedPermissionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Persist permission grants.
    /// </summary>
    Task GrantAsync(IEnumerable<string> permissionIds, CancellationToken ct = default);

    /// <summary>
    /// Revoke a persistently granted permission.
    /// </summary>
    Task RevokeAsync(string permissionId, CancellationToken ct = default);
}
