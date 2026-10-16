namespace SwarmCore.Skills.Models;

/// <summary>
/// Configuration options for the skill registry.
/// </summary>
public class SkillRegistryConfiguration
{
    /// <summary>
    /// Whether to auto-discover skills on startup.
    /// </summary>
    public bool AutoDiscoverSkills { get; set; } = false;

    /// <summary>
    /// Default trust level for skills without explicit trust level.
    /// </summary>
    public SkillTrustLevel DefaultTrustLevel { get; set; } = SkillTrustLevel.Verified;

    /// <summary>
    /// Whether to auto-enable skills that can be auto-enabled.
    /// </summary>
    public bool AutoEnableSkills { get; set; } = true;

    /// <summary>
    /// Assemblies to scan for skill discovery (empty = entry assembly only).
    /// </summary>
    public List<string> AssembliesToScan { get; set; } = [];

    /// <summary>
    /// Skills that should always be enabled.
    /// </summary>
    public List<string> AlwaysEnabledSkills { get; set; } = [];

    /// <summary>
    /// Skills that should always be disabled.
    /// </summary>
    public List<string> AlwaysDisabledSkills { get; set; } = [];

    /// <summary>
    /// Default timeout for skill execution. Sized to the tool-use engine's
    /// per-call turn budget so a legitimate multi-step LLM skill is not
    /// truncated mid-run; a genuinely hung skill is still cancelled.
    /// </summary>
    public TimeSpan DefaultExecutionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Whether to require sandbox for untrusted skills.
    /// </summary>
    public bool RequireSandboxForUntrusted { get; set; } = true;

    /// <summary>
    /// Whether to log skill executions.
    /// </summary>
    public bool LogExecutions { get; set; } = true;

    /// <summary>
    /// Maximum concurrent skill executions.
    /// </summary>
    public int MaxConcurrentExecutions { get; set; } = 10;
}
