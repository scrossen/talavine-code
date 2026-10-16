using SwarmCore.Skills.Models;

namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Resolves a bounded, host-approved dynamic choice source for skill parameters.
/// Implementations must treat <see cref="SkillParameterOptionsRequest.OptionsSource"/>
/// as an allowlisted identifier rather than executable input.
/// </summary>
public interface ISkillParameterOptionsProvider
{
    /// <summary>
    /// Whether this provider owns the given declarative option-source identifier.
    /// </summary>
    bool CanResolve(string optionsSource);

    /// <summary>
    /// Resolve the currently available choices using already-selected dependency values.
    /// </summary>
    Task<IReadOnlyList<SkillParameterOption>> ResolveAsync(
        SkillParameterOptionsRequest request,
        CancellationToken cancellationToken = default);
}
