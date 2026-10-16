using SwarmCore.Skills.Models;

namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Security policy for skill execution.
/// </summary>
public interface ISkillSecurityPolicy
{
    /// <summary>
    /// Check if a skill can be executed in the given context.
    /// </summary>
    /// <param name="skill">The skill to check.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SecurityCheckResult> CheckExecutionAllowedAsync(
        ISkill skill,
        SkillExecutionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Determine if a skill should run in sandbox mode.
    /// </summary>
    /// <param name="skill">The skill to check.</param>
    bool RequiresSandbox(ISkill skill);

    /// <summary>
    /// Determine if a skill can be made available without a capability grant.
    /// </summary>
    /// <param name="skill">The skill to check.</param>
    bool CanAutoEnable(ISkill skill);

    /// <summary>
    /// Check if a user has permission to use a skill.
    /// </summary>
    /// <param name="skill">The skill to check.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanUserAccessSkillAsync(
        ISkill skill,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// Get the effective permissions for a skill execution.
    /// </summary>
    /// <param name="skill">The skill being executed.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SkillPermission>> GetEffectivePermissionsAsync(
        ISkill skill,
        SkillExecutionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a security check.
/// </summary>
public class SecurityCheckResult
{
    /// <summary>
    /// Whether execution is allowed.
    /// </summary>
    public bool IsAllowed { get; init; }

    /// <summary>
    /// Reason for denial (if not allowed).
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// Whether sandbox execution is required.
    /// </summary>
    public bool RequiresSandbox { get; init; }

    /// <summary>
    /// Permissions that are missing.
    /// </summary>
    public List<SkillPermission> MissingPermissions { get; init; } = [];

    /// <summary>
    /// Warnings about the execution.
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Risk level of the operation.
    /// </summary>
    public SecurityRiskLevel RiskLevel { get; init; } = SecurityRiskLevel.Low;

    public static SecurityCheckResult Allowed(bool requiresSandbox = false, SecurityRiskLevel riskLevel = SecurityRiskLevel.Low) => new()
    {
        IsAllowed = true,
        RequiresSandbox = requiresSandbox,
        RiskLevel = riskLevel
    };

    public static SecurityCheckResult Denied(string reason, List<SkillPermission>? missingPermissions = null) => new()
    {
        IsAllowed = false,
        DenialReason = reason,
        MissingPermissions = missingPermissions ?? []
    };
}

/// <summary>
/// Security risk levels.
/// </summary>
public enum SecurityRiskLevel
{
    /// <summary>
    /// No risk - read-only or safe operations.
    /// </summary>
    None = 0,

    /// <summary>
    /// Low risk - generally safe operations.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium risk - should be monitored.
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High risk - requires careful consideration.
    /// </summary>
    High = 3,

    /// <summary>
    /// Critical risk - could cause significant harm.
    /// </summary>
    Critical = 4
}
