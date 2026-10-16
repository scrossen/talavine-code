namespace SwarmCore.Skills.Models;

/// <summary>
/// Categories for organizing skills.
/// </summary>
public class SkillCategory
{
    /// <summary>
    /// Category name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional parent category for hierarchical organization.
    /// </summary>
    public string? ParentCategory { get; init; }

    /// <summary>
    /// Description of what skills in this category do.
    /// </summary>
    public string Description { get; init; } = "";

    // Predefined categories
    public static readonly SkillCategory Email = new() { Name = "Email", Description = "Email processing and analysis" };
    public static readonly SkillCategory Data = new() { Name = "Data", Description = "Data analysis and querying" };
    public static readonly SkillCategory Communication = new() { Name = "Communication", Description = "External communications" };
    public static readonly SkillCategory Automation = new() { Name = "Automation", Description = "Task automation" };
    public static readonly SkillCategory Security = new() { Name = "Security", Description = "Security and threat detection" };
    public static readonly SkillCategory Knowledge = new() { Name = "Knowledge", Description = "Knowledge management and retrieval" };
    public static readonly SkillCategory Development = new() { Name = "Development", Description = "Software development tools" };
    public static readonly SkillCategory External = new() { Name = "External", Description = "External tools from MCP connectors" };
    public static readonly SkillCategory Diagnostics = new() { Name = "Diagnostics", Description = "Self-healing, troubleshooting, and routing-anomaly recovery skills" };

    public override bool Equals(object? obj) =>
        obj is SkillCategory other && Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => Name.ToLowerInvariant().GetHashCode();

    public override string ToString() => Name;
}

/// <summary>
/// Trust levels for skills, determining security behavior.
/// </summary>
public enum SkillTrustLevel
{
    /// <summary>
    /// Untrusted skill - requires sandbox execution and manual enable.
    /// </summary>
    Untrusted = 0,

    /// <summary>
    /// Community skill - runs sandboxed. Read-only file skills may be available immediately;
    /// any declared capability still uses the normal permission broker.
    /// </summary>
    Community = 1,

    /// <summary>
    /// Verified skill - no sandbox required, can be auto-enabled.
    /// </summary>
    Verified = 2,

    /// <summary>
    /// Trusted skill - full trust, no restrictions, auto-enabled.
    /// </summary>
    Trusted = 3,

    /// <summary>
    /// System skill - built-in, always enabled, cannot be disabled.
    /// </summary>
    System = 4
}
