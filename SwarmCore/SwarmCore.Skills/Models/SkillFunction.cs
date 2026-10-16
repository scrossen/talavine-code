namespace SwarmCore.Skills.Models;

/// <summary>
/// Describes a function provided by a skill.
/// Compatible with Semantic Kernel function patterns.
/// </summary>
public class SkillFunction
{
    /// <summary>
    /// Function name (unique within the skill).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what this function does.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Parameters accepted by this function.
    /// </summary>
    public List<SkillParameter> Parameters { get; init; } = [];

    /// <summary>
    /// Return type description.
    /// </summary>
    public string ReturnType { get; init; } = "string";

    /// <summary>
    /// Whether this function is async.
    /// </summary>
    public bool IsAsync { get; init; } = true;

    /// <summary>
    /// Whether this function supports streaming output.
    /// </summary>
    public bool SupportsStreaming { get; init; } = false;
}

/// <summary>
/// Describes a parameter for a skill function.
/// </summary>
public class SkillParameter
{
    /// <summary>
    /// Parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Parameter type (string, int, bool, etc.).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Description of the parameter.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Whether this parameter is required.
    /// </summary>
    public bool IsRequired { get; init; } = true;

    /// <summary>
    /// Default value if not provided.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Valid values for enum-like parameters.
    /// </summary>
    public List<string>? ValidValues { get; init; }

    /// <summary>
    /// Optional host-registered source for choices that must be discovered at runtime.
    /// Source names are declarative identifiers only; skills cannot provide resolver code.
    /// </summary>
    public string? OptionsSource { get; init; }

    /// <summary>
    /// Other parameter names whose current values are required to resolve this parameter's
    /// choices. Changing a dependency invalidates this parameter's current selection.
    /// </summary>
    public List<string> DependsOn { get; init; } = [];
}
