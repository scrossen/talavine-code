using System.Text.RegularExpressions;

namespace SwarmCore.Skills.FileSkills;

/// <summary>How badly a manifest departs from the Agent Skills specification.</summary>
public enum SkillValidationSeverity
{
    /// <summary>Portability or hygiene problem. The skill still loads.</summary>
    Warning = 0,

    /// <summary>Spec violation that makes the skill unsafe or unloadable.</summary>
    Error = 1,
}

/// <param name="Severity">Whether this blocks loading.</param>
/// <param name="Field">The frontmatter field or file the finding is about.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record SkillValidationFinding(
    SkillValidationSeverity Severity,
    string Field,
    string Message);

/// <summary>
/// Validates a parsed <see cref="SkillManifest"/> against the Agent Skills specification
/// (https://agentskills.io/specification).
///
/// The name rules are not cosmetic. "name must match the parent directory" is what keeps a
/// package's identity tied to where it was installed — without it a manifest can claim any
/// identity it likes regardless of the folder the user reviewed and dropped it into.
/// </summary>
public static partial class SkillManifestValidator
{
    /// <summary>Spec: max 64 characters.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Spec: max 1024 characters.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>Spec: max 500 characters.</summary>
    public const int MaxCompatibilityLength = 500;

    /// <summary>
    /// Spec recommends keeping the activated body under ~5k tokens. Warn past roughly that,
    /// measured in characters so we don't need a tokenizer — long reference material belongs
    /// in <c>references/</c> where it loads on demand instead of on every activation.
    /// </summary>
    public const int RecommendedInstructionChars = 20_000;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NameShapeRegex();

    /// <summary>
    /// Validate a manifest. When <paramref name="skillDirectoryName"/> is supplied, the spec's
    /// name-matches-directory rule is enforced.
    /// </summary>
    public static IReadOnlyList<SkillValidationFinding> Validate(
        SkillManifest manifest,
        string? skillDirectoryName = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var findings = new List<SkillValidationFinding>();

        ValidateName(manifest.Name, skillDirectoryName, findings);
        ValidateDescription(manifest.Description, findings);

        if (manifest.Compatibility.Length > MaxCompatibilityLength)
        {
            findings.Add(new(SkillValidationSeverity.Warning, "compatibility",
                $"compatibility is {manifest.Compatibility.Length} characters; the specification allows {MaxCompatibilityLength}."));
        }

        if (manifest.Instructions.Length > RecommendedInstructionChars)
        {
            findings.Add(new(SkillValidationSeverity.Warning, "instructions",
                $"The instruction body is {manifest.Instructions.Length:N0} characters and loads in full every time this skill runs. "
                + "Move detailed material into references/ so it is read only when needed."));
        }

        foreach (var key in manifest.UnknownKeys)
        {
            findings.Add(new(SkillValidationSeverity.Warning, key,
                $"Frontmatter key '{key}' is not part of the Agent Skills specification and is not understood by Talavine. "
                + "Put author-defined values under 'metadata:' so other agents preserve them."));
        }

        // Parser diagnostics (e.g. an unrecognized permission id) are advisory: the skill
        // still loads, but the author and the import review should see them.
        foreach (var diagnostic in manifest.Diagnostics)
        {
            findings.Add(new(SkillValidationSeverity.Warning, diagnostic.Field, diagnostic.Message));
        }

        return findings;
    }

    /// <summary>True when nothing in the manifest blocks loading.</summary>
    public static bool IsLoadable(IEnumerable<SkillValidationFinding> findings) =>
        !findings.Any(f => f.Severity == SkillValidationSeverity.Error);

    private static void ValidateName(
        string name,
        string? skillDirectoryName,
        List<SkillValidationFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            findings.Add(new(SkillValidationSeverity.Error, "name",
                "SKILL.md must declare a name in its YAML frontmatter."));
            return;
        }

        if (name.Length > MaxNameLength)
        {
            findings.Add(new(SkillValidationSeverity.Error, "name",
                $"name is {name.Length} characters; the specification allows {MaxNameLength}."));
        }

        if (!NameShapeRegex().IsMatch(name))
        {
            findings.Add(new(SkillValidationSeverity.Error, "name",
                $"name '{name}' must be lowercase letters, digits, and single hyphens only, "
                + "and may not start or end with a hyphen."));
        }

        if (!string.IsNullOrWhiteSpace(skillDirectoryName)
            && !string.Equals(name, skillDirectoryName, StringComparison.Ordinal))
        {
            // Mismatch is an error, not a warning: the folder is what the user reviewed and
            // chose to install, so the manifest must not be able to claim a different identity.
            findings.Add(new(SkillValidationSeverity.Error, "name",
                $"name '{name}' does not match its directory '{skillDirectoryName}'. "
                + "The specification requires them to be identical."));
        }
    }

    private static void ValidateDescription(string description, List<SkillValidationFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            findings.Add(new(SkillValidationSeverity.Error, "description",
                "SKILL.md must declare a description — it is what the agent matches against to decide when to use the skill."));
            return;
        }

        if (description.Length > MaxDescriptionLength)
        {
            findings.Add(new(SkillValidationSeverity.Error, "description",
                $"description is {description.Length} characters; the specification allows {MaxDescriptionLength}."));
        }
    }
}
