namespace SwarmCore.Skills.FileSkills;

/// <summary>
/// Loads file-based skills from SKILL.md manifest files.
/// </summary>
public interface ISkillLoader
{
    /// <summary>
    /// Load all skills from configured directories.
    /// </summary>
    Task<IReadOnlyList<FileBasedSkill>> LoadSkillsAsync(CancellationToken ct = default);

    /// <summary>
    /// Load a single skill from a SKILL.md file path.
    /// </summary>
    Task<FileBasedSkill?> LoadSkillAsync(string skillMdPath, CancellationToken ct = default);

    /// <summary>
    /// Reload all skills (e.g., after directory changes).
    /// </summary>
    Task<IReadOnlyList<FileBasedSkill>> ReloadSkillsAsync(CancellationToken ct = default);
}

/// <summary>
/// Configuration for the skill directory loader.
/// </summary>
public class SkillDirectoryOptions
{
    /// <summary>
    /// Directories to scan for skill folders containing SKILL.md files.
    /// Each directory is scanned recursively for subdirectories with a SKILL.md.
    /// </summary>
    public List<string> SkillDirectories { get; set; } = [];

    /// <summary>
    /// Origin of each configured directory, keyed by path. Directories added without a
    /// declared source load as <see cref="SkillSource.Unknown"/>, which keeps the legacy
    /// flat skill-id namespace. Use <see cref="AddDirectory"/> to declare one.
    /// </summary>
    public Dictionary<string, SkillSource> DirectorySources { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a skill directory along with where its contents come from. The source
    /// determines the skill-id namespace, so an untrusted package cannot claim the id of
    /// a reviewed built-in.
    /// </summary>
    public SkillDirectoryOptions AddDirectory(string directory, SkillSource source)
    {
        SkillDirectories.Add(directory);
        DirectorySources[directory] = source;
        return this;
    }

    /// <summary>Resolve the declared source for a configured directory.</summary>
    public SkillSource SourceFor(string directory) =>
        DirectorySources.TryGetValue(directory, out var source) ? source : SkillSource.Unknown;

    /// <summary>
    /// If true, also scan for SKILL.md files directly in the skill directories
    /// (not just in subdirectories).
    /// </summary>
    public bool ScanRootDirectories { get; set; } = true;

    /// <summary>
    /// File name pattern for skill manifests (case-insensitive).
    /// Default: "SKILL.md"
    /// </summary>
    public string ManifestFileName { get; set; } = "SKILL.md";

    /// <summary>
    /// Whether to watch directories for changes and auto-reload.
    /// </summary>
    public bool WatchForChanges { get; set; } = false;

    /// <summary>
    /// Publisher public key (PEM) that a package's <c>SIGNATURE.json</c> is verified
    /// against at load time. Null or empty uses the pinned first-party key in
    /// <see cref="Packaging.SkillCatalogDefaults.PublisherPublicKeyPem"/>. Override only for
    /// tests or a host with its own publisher.
    /// </summary>
    public string? PublisherPublicKeyPem { get; set; }
}
