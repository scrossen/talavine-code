namespace SwarmCore.Skills.Models;

/// <summary>
/// A request for user consent to grant elevated permissions to a skill execution.
/// </summary>
public record PermissionConsentRequest
{
    /// <summary>
    /// Unique identifier for this consent request.
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The skill that needs elevated permissions.
    /// </summary>
    public required string SkillId { get; init; }

    /// <summary>
    /// Human-readable skill name for display.
    /// </summary>
    public required string SkillName { get; init; }

    /// <summary>
    /// The function being invoked.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// Session/conversation ID for scoping grants.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The permissions that were not granted and require user consent.
    /// </summary>
    public required IReadOnlyList<SkillPermission> MissingPermissions { get; init; }
}

/// <summary>
/// Result of a permission consent request.
/// </summary>
public class PermissionConsentResult
{
    /// <summary>An existing revocable conversation or persistent grant satisfied this request.</summary>
    public bool AlreadyGranted { get; init; }
    /// <summary>
    /// Whether the user approved the permission request.
    /// </summary>
    public bool Approved { get; init; }

    /// <summary>
    /// The scope of the grant — how long the permission stays active.
    /// </summary>
    public PermissionGrantScope Scope { get; init; } = PermissionGrantScope.Once;
}

/// <summary>
/// How long a permission grant remains active.
/// </summary>
public enum PermissionGrantScope
{
    /// <summary>
    /// Grant applies to this single skill execution only.
    /// </summary>
    Once,

    /// <summary>
    /// Grant applies for the duration of the current conversation/session.
    /// </summary>
    Conversation,

    /// <summary>
    /// Grant is persisted and applies to all future executions.
    /// </summary>
    Always
}
