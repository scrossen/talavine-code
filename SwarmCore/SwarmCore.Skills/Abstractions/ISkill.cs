using SwarmCore.Skills.Models;
using Microsoft.Extensions.DependencyInjection;

namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// A skill is a discrete capability that can be invoked by agents or users.
/// Built on Semantic Kernel function patterns for compatibility.
/// </summary>
public interface ISkill
{
    /// <summary>
    /// Unique identifier for this skill.
    /// </summary>
    string SkillId { get; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Detailed description of what this skill does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Version of this skill.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Category for organization and search.
    /// </summary>
    SkillCategory Category { get; }

    /// <summary>
    /// Trust level of this skill.
    /// </summary>
    SkillTrustLevel TrustLevel { get; }

    /// <summary>
    /// Permissions required to execute this skill.
    /// </summary>
    IReadOnlyList<SkillPermission> RequiredPermissions { get; }

    /// <summary>
    /// Keywords for discovery and search.
    /// </summary>
    IReadOnlyList<string> Keywords { get; }

    /// <summary>
    /// Get the functions provided by this skill.
    /// </summary>
    IEnumerable<SkillFunction> GetFunctions();

    /// <summary>
    /// Execute a function within this skill.
    /// </summary>
    /// <param name="functionName">The function to execute.</param>
    /// <param name="context">Execution context with parameters and security info.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SkillResult> ExecuteAsync(
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Check if this skill is available and healthy.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Initialize the skill with the service provider.
    /// Called once when the skill is registered.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency resolution.</param>
    Task InitializeAsync(IServiceProvider serviceProvider);
}

/// <summary>
/// Base class for implementing skills with common functionality.
/// </summary>
public abstract class SkillBase : ISkill
{
    public abstract string SkillId { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Version => "1.0.0";
    public abstract SkillCategory Category { get; }
    public virtual SkillTrustLevel TrustLevel => SkillTrustLevel.Verified;
    public virtual IReadOnlyList<SkillPermission> RequiredPermissions => [];
    public virtual IReadOnlyList<string> Keywords => [];

    protected IServiceProvider? ServiceProvider { get; private set; }

    public abstract IEnumerable<SkillFunction> GetFunctions();

    public abstract Task<SkillResult> ExecuteAsync(
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default);

    public virtual Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(true);

    public virtual Task InitializeAsync(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Helper to get a required service from the provider.
    /// </summary>
    protected T GetRequiredService<T>() where T : notnull
    {
        if (ServiceProvider is null)
            throw new InvalidOperationException("Skill has not been initialized");

        return ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Helper to get an optional service from the provider.
    /// </summary>
    protected T? GetService<T>() => ServiceProvider is null ? default : ServiceProvider.GetService<T>();
}
