namespace SwarmCore.Skills.Models;

/// <summary>
/// One user-facing choice for a skill parameter.
/// </summary>
public sealed record SkillParameterOption(
    string Value,
    string Label,
    string? Description = null,
    bool IsDefault = false);

/// <summary>
/// Context supplied to a host-registered dynamic parameter option provider.
/// </summary>
public sealed record SkillParameterOptionsRequest(
    string OptionsSource,
    string SkillId,
    string FunctionName,
    string ParameterName,
    IReadOnlyList<string> Dependencies,
    IReadOnlyDictionary<string, string> CurrentValues);
