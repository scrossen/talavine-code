namespace SwarmCore.Skills.Models;

/// <summary>
/// Context for skill execution, containing parameters and security information.
/// </summary>
public class SkillExecutionContext
{
    /// <summary>
    /// User ID for the current execution (optional).
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Session ID for tracking related operations.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Parameters for the function being executed.
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = [];

    /// <summary>
    /// Permissions granted for this execution.
    /// </summary>
    public IReadOnlyList<string> GrantedPermissions { get; init; } = [];

    /// <summary>
    /// Whether this execution is allowed to escape sandbox restrictions.
    /// <b>Reserved / advisory.</b> The default executor never reads this flag: a
    /// sandboxed call always runs with a narrowed grant list and this value forced to
    /// false. It exists for hosts that layer their own isolation on top.
    /// </summary>
    public bool AllowSandboxEscape { get; init; } = false;

    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Additional metadata for the execution.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = default;

    /// <summary>
    /// Get a typed parameter value.
    /// </summary>
    public T? GetParameter<T>(string name, T? defaultValue = default)
    {
        if (!Parameters.TryGetValue(name, out var value))
            return defaultValue;

        if (value is T typedValue)
            return typedValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Check if a permission is granted.
    /// </summary>
    public bool HasPermission(string permissionId) =>
        GrantedPermissions.Contains(permissionId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a permission is granted.
    /// </summary>
    public bool HasPermission(SkillPermission permission) =>
        HasPermission(permission.PermissionId);
}
