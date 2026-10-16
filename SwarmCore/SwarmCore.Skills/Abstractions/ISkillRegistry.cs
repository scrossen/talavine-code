using System.Reflection;
using SwarmCore.Skills.Models;

namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Registry for managing available skills with discovery and security.
/// </summary>
public interface ISkillRegistry
{
    /// <summary>
    /// Register a skill with the registry.
    /// </summary>
    /// <param name="skill">The skill to register.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RegisterAsync(ISkill skill, CancellationToken ct = default);

    /// <summary>
    /// Unregister a skill.
    /// </summary>
    /// <param name="skillId">ID of the skill to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UnregisterAsync(string skillId, CancellationToken ct = default);

    /// <summary>
    /// Get a skill by ID.
    /// </summary>
    /// <param name="skillId">The skill ID.</param>
    ISkill? GetSkill(string skillId);

    /// <summary>
    /// Get all registered skills.
    /// </summary>
    IReadOnlyList<ISkill> GetAllSkills();

    /// <summary>
    /// Search for skills by keywords or description.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="options">Search options.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SkillSearchResult>> SearchAsync(
        string query,
        SkillSearchOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get skills by category.
    /// </summary>
    /// <param name="category">The category to filter by.</param>
    IReadOnlyList<ISkill> GetByCategory(SkillCategory category);

    /// <summary>
    /// Get skills by trust level.
    /// </summary>
    /// <param name="trustLevel">The trust level to filter by.</param>
    IReadOnlyList<ISkill> GetByTrustLevel(SkillTrustLevel trustLevel);

    /// <summary>
    /// Check if a skill is enabled.
    /// </summary>
    /// <param name="skillId">The skill ID.</param>
    bool IsEnabled(string skillId);

    /// <summary>
    /// Enable or disable a skill.
    /// </summary>
    /// <param name="skillId">The skill ID.</param>
    /// <param name="enabled">Whether to enable or disable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// True if the change was applied; false if the skill's enabled state is
    /// pinned by deployment policy (always-enabled/always-disabled) and could
    /// not be changed. Callers should surface a false result to the user
    /// rather than reporting success.
    /// </returns>
    Task<bool> SetEnabledAsync(string skillId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Discover skills from assemblies.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan (null = entry assembly).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of skills discovered.</returns>
    Task<int> DiscoverSkillsAsync(
        IEnumerable<Assembly>? assemblies = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get a function from a skill by skill and function name.
    /// </summary>
    /// <param name="skillId">The skill ID.</param>
    /// <param name="functionName">The function name.</param>
    SkillFunction? GetFunction(string skillId, string functionName);
}
