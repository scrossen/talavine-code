using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.FileSkills;
using SwarmCore.Skills.Models;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Skills.Security;

/// <summary>
/// Default implementation of ISkillSecurityPolicy.
/// </summary>
public class SkillSecurityPolicy : ISkillSecurityPolicy
{
    private readonly SkillRegistryConfiguration _configuration;
    private readonly ILogger<SkillSecurityPolicy> _logger;

    public SkillSecurityPolicy(
        SkillRegistryConfiguration configuration,
        ILogger<SkillSecurityPolicy> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task<SecurityCheckResult> CheckExecutionAllowedAsync(
        ISkill skill,
        SkillExecutionContext context,
        CancellationToken ct = default)
    {
        var missingPermissions = new List<SkillPermission>();
        var warnings = new List<string>();

        // Check required permissions
        foreach (var required in skill.RequiredPermissions)
        {
            if (!context.HasPermission(required))
            {
                missingPermissions.Add(required);
            }
        }

        if (missingPermissions.Count > 0)
        {
            _logger.LogWarning(
                "Skill {SkillId} execution denied - missing permissions: {Permissions}",
                skill.SkillId,
                string.Join(", ", missingPermissions.Select(p => p.PermissionId)));

            return Task.FromResult(SecurityCheckResult.Denied(
                $"Missing required permissions: {string.Join(", ", missingPermissions.Select(p => p.Name))}",
                missingPermissions));
        }

        // Determine risk level based on permissions and trust
        var riskLevel = CalculateRiskLevel(skill, context);

        // Check if sandbox is required
        var requiresSandbox = RequiresSandbox(skill);

        // Add warnings for elevated permissions
        foreach (var perm in skill.RequiredPermissions.Where(p => p.Level >= PermissionLevel.Elevated))
        {
            warnings.Add($"Skill uses elevated permission: {perm.Name}");
        }

        return Task.FromResult(new SecurityCheckResult
        {
            IsAllowed = true,
            RequiresSandbox = requiresSandbox,
            RiskLevel = riskLevel,
            Warnings = warnings
        });
    }

    public bool RequiresSandbox(ISkill skill)
    {
        if (!_configuration.RequireSandboxForUntrusted)
            return false;

        // Untrusted and Community skills require sandbox
        return skill.TrustLevel is SkillTrustLevel.Untrusted or SkillTrustLevel.Community;
    }

    public bool CanAutoEnable(ISkill skill)
    {
        // Built-in trusted skills can always be auto-enabled. A Community
        // SKILL.md whose declared permissions are all Standard-level is also
        // safe to make available immediately: it remains sandboxed, and the
        // executor auto-grants Standard read permissions at run time anyway,
        // so keeping it disabled only adds a toggle chore without withholding
        // any capability. Anything declaring an Elevated+ permission (send,
        // write, network, execute) still waits for the permission broker.
        return skill.TrustLevel >= SkillTrustLevel.Verified
               || (skill is FileBasedSkill { TrustLevel: SkillTrustLevel.Community }
                   && skill.RequiredPermissions.All(static p => p.Level == PermissionLevel.Standard));
    }

    /// <summary>
    /// Per-user access check, consulted by the executor only when the execution context
    /// names a user. Defaults to allow; override for user-specific restrictions.
    /// </summary>
    public virtual Task<bool> CanUserAccessSkillAsync(
        ISkill skill,
        string userId,
        CancellationToken ct = default)
    {
        // By default, all users can access all skills
        // Override this for user-specific restrictions
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<SkillPermission>> GetEffectivePermissionsAsync(
        ISkill skill,
        SkillExecutionContext context,
        CancellationToken ct = default)
    {
        // Return permissions that are both required and granted
        var effective = skill.RequiredPermissions
            .Where(p => context.HasPermission(p))
            .ToList();

        return Task.FromResult<IReadOnlyList<SkillPermission>>(effective);
    }

    private SecurityRiskLevel CalculateRiskLevel(ISkill skill, SkillExecutionContext context)
    {
        // Start with trust-based risk
        var riskLevel = skill.TrustLevel switch
        {
            SkillTrustLevel.System => SecurityRiskLevel.None,
            SkillTrustLevel.Trusted => SecurityRiskLevel.Low,
            SkillTrustLevel.Verified => SecurityRiskLevel.Low,
            SkillTrustLevel.Community => SecurityRiskLevel.Medium,
            SkillTrustLevel.Untrusted => SecurityRiskLevel.High,
            _ => SecurityRiskLevel.Medium
        };

        // Increase risk for elevated permissions
        var hasRestrictedPermission = skill.RequiredPermissions.Any(p => p.Level >= PermissionLevel.Restricted);
        var hasAdminPermission = skill.RequiredPermissions.Any(p => p.Level >= PermissionLevel.Admin);

        if (hasAdminPermission)
            riskLevel = SecurityRiskLevel.Critical;
        else if (hasRestrictedPermission && riskLevel < SecurityRiskLevel.High)
            riskLevel = SecurityRiskLevel.High;

        return riskLevel;
    }
}
