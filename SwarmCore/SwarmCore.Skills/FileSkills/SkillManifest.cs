namespace SwarmCore.Skills.FileSkills;

/// <summary>
/// Where a file-based skill was loaded from. Determines the skill-id namespace so a
/// package dropped into the imported folder can never collide with — and silently
/// replace — a reviewed skill that ships in the binary. See <see cref="SkillManifest.GetSkillId"/>.
/// </summary>
public enum SkillSource
{
    /// <summary>Origin not declared by the host. Keeps the legacy flat <c>file.&lt;name&gt;</c> id.</summary>
    Unknown = 0,

    /// <summary>Reviewed skills shipped with the application.</summary>
    BuiltIn = 1,

    /// <summary>Auto-drafted skill suggestions written by the reflection agent.</summary>
    Suggested = 2,

    /// <summary>Third-party packages installed through the Skill Vetter.</summary>
    Imported = 3,

    /// <summary>Playbooks explicitly authored by the user in Talavine.</summary>
    Authored = 4,
}

/// <summary>
/// A supporting file bundled alongside SKILL.md, discovered at load time but
/// <em>not</em> read into memory. Progressive disclosure: the agent sees the
/// inventory when the skill activates and pulls a file's content only when the
/// task actually needs it.
/// </summary>
/// <param name="RelativePath">Path relative to the skill root, forward-slashed.</param>
/// <param name="Kind">Which standard directory the file came from.</param>
/// <param name="SizeBytes">On-disk size, used to keep oversized files out of context.</param>
public sealed record SkillResource(string RelativePath, SkillResourceKind Kind, long SizeBytes);

/// <summary>
/// The three optional directories the Agent Skills specification defines, plus a
/// catch-all for anything else that ships in the package root.
/// </summary>
public enum SkillResourceKind
{
    /// <summary>Documentation the agent may read on demand (<c>references/</c>).</summary>
    Reference = 0,

    /// <summary>Templates, schemas, and data files (<c>assets/</c>).</summary>
    Asset = 1,

    /// <summary>Executable code (<c>scripts/</c>). Never executed by Talavine — see the Skill Vetter.</summary>
    Script = 2,

    /// <summary>Any other bundled file.</summary>
    Other = 3,
}

/// <summary>
/// Something the parser understood well enough to keep loading but that the author should
/// see: a declaration that will be ignored, or a value that was silently coerced.
/// </summary>
/// <param name="Field">The frontmatter field the note is about.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record SkillManifestDiagnostic(string Field, string Message);

/// <summary>
/// Parsed representation of a SKILL.md manifest file.
/// Follows the Agent Skills open standard (https://agentskills.io/specification):
/// <c>name</c> and <c>description</c> are required, <c>license</c>, <c>compatibility</c>,
/// <c>metadata</c>, and <c>allowed-tools</c> are optional.
///
/// Talavine's own fields (category, trust level, function signatures, permissions,
/// sampling overrides) are client extensions. The spec reserves <c>metadata</c> for exactly
/// this, so the parser accepts them either at the top level (how Talavine has always
/// written them) or nested under <c>metadata:</c> (how a portable skill should write them).
/// </summary>
public class SkillManifest
{
    /// <summary>
    /// Unique skill name (from frontmatter). Used to generate SkillId.
    /// Per spec: 1-64 chars, lowercase alphanumeric and hyphens, matching the parent directory.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Human-readable description of what this skill does and when to use it.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Semantic version of this skill. Talavine extension; the spec carries this in metadata.
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Category for organization (maps to SkillCategory). Talavine extension.
    /// </summary>
    public string Category { get; set; } = "External";

    /// <summary>
    /// Trust level (maps to SkillTrustLevel enum). Talavine extension — user-writable
    /// packages are clamped on load; only host-classified built-ins may declare Verified.
    /// See <c>FileBasedSkill.MapTrustLevel</c>.
    /// </summary>
    public string TrustLevel { get; set; } = "Community";

    /// <summary>
    /// The LLM function type to use when executing this skill (maps to FunctionTypes constants).
    /// Talavine extension.
    /// </summary>
    public string FunctionType { get; set; } = "default";

    /// <summary>
    /// Spec field: the license this skill is published under.
    /// </summary>
    public string License { get; set; } = "";

    /// <summary>
    /// Spec field: environment requirements (intended product, system packages, network access).
    /// Surfaced to the user during import review rather than acted on.
    /// </summary>
    public string Compatibility { get; set; } = "";

    /// <summary>
    /// Spec field: tools the skill author pre-approved for this skill, as written in the
    /// manifest. Advisory only — Talavine grants capability through
    /// <see cref="Permissions"/> and the consent store, never from an untrusted manifest.
    /// Captured so the Skill Vetter can show the user what the package expects to call.
    /// </summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>
    /// Spec field: arbitrary author metadata. Keys Talavine understands are lifted into
    /// the typed properties above; everything else is preserved here for display.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Frontmatter keys the parser did not recognize. Previously these were dropped
    /// silently, which hid security-relevant declarations from the import review.
    /// </summary>
    public List<string> UnknownKeys { get; set; } = [];

    /// <summary>
    /// Non-blocking findings the parser raised while reading the frontmatter — for example
    /// a <c>permissions:</c> entry that names no known permission and will therefore grant
    /// nothing. These never stop the skill loading; the validator lifts them into warning
    /// findings so the loader logs them and the import review can show them.
    /// </summary>
    public List<SkillManifestDiagnostic> Diagnostics { get; set; } = [];

    /// <summary>
    /// Keywords for discovery and search.
    /// </summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// Functions exposed by this skill. If empty, a default "Execute" function is generated.
    /// </summary>
    public List<SkillManifestFunction> Functions { get; set; } = [];

    /// <summary>
    /// Permission IDs required to execute this skill.
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// The markdown body of the SKILL.md file — LLM instructions for this skill.
    /// </summary>
    public string Instructions { get; set; } = "";

    /// <summary>
    /// Supporting files discovered next to SKILL.md. Paths only — content is read on demand.
    /// </summary>
    public List<SkillResource> Resources { get; set; } = [];

    /// <summary>
    /// Where this skill was loaded from. Set by the loader, never by the manifest author.
    /// </summary>
    public SkillSource Source { get; set; } = SkillSource.Unknown;

    /// <summary>
    /// True when the package's <c>SIGNATURE.json</c> verified against the pinned publisher
    /// key when the loader read it. Set by the loader, never by the manifest author.
    /// </summary>
    public bool VerifiedPublisher { get; set; }

    /// <summary>
    /// Absolute path to the SKILL.md file this manifest was loaded from.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// Directory containing the SKILL.md file (for resolving relative asset paths).
    /// </summary>
    public string SourceDirectory => string.IsNullOrEmpty(SourcePath)
        ? ""
        : Path.GetDirectoryName(SourcePath) ?? "";

    /// <summary>
    /// Optional temperature override for LLM calls.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Optional max tokens override for LLM calls.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Generate a slug-style SkillId from the name, namespaced by origin.
    ///
    /// Built-in (and source-less) skills keep the historical flat <c>file.&lt;name&gt;</c> id so
    /// persisted enable-state and always-enabled allow-lists survive. Untrusted origins get
    /// their own prefix: without it, an imported package named <c>email-triage</c> produced the
    /// same id as the shipped built-in, and because the imported directory is scanned last it
    /// replaced the built-in in the registry and inherited its enabled state.
    /// </summary>
    public string GetSkillId()
    {
        var slug = Slugify(Name);
        return Source switch
        {
            SkillSource.Imported => $"file.imported.{slug}",
            SkillSource.Suggested => $"file.suggested.{slug}",
            SkillSource.Authored => $"file.authored.{slug}",
            _ => $"file.{slug}",
        };
    }

    private static string Slugify(string name) =>
        name.ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
}

/// <summary>
/// A function defined in the SKILL.md frontmatter.
/// </summary>
public class SkillManifestFunction
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ReturnType { get; set; } = "string";
    public List<SkillManifestParameter> Parameters { get; set; } = [];
}

/// <summary>
/// A parameter for a skill manifest function.
/// </summary>
public class SkillManifestParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? DefaultValue { get; set; }
    public List<string> ValidValues { get; set; } = [];
    public string? OptionsSource { get; set; }
    public List<string> DependsOn { get; set; } = [];
}
