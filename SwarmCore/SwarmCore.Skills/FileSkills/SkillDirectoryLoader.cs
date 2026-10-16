using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwarmCore.Skills.Packaging;

namespace SwarmCore.Skills.FileSkills;

/// <summary>
/// Scans configured directories for SKILL.md files and creates FileBasedSkill instances.
/// Follows the emerging standard where each skill is a folder containing a SKILL.md manifest.
///
/// Expected directory structure:
///   skills/
///     email-triage/
///       SKILL.md
///       (optional assets)
///     email-summary/
///       SKILL.md
/// </summary>
public class SkillDirectoryLoader : ISkillLoader
{
    /// <summary>Ceiling on bundled files listed per skill, so a pathological package can't flood the inventory.</summary>
    private const int MaxResourcesPerSkill = 200;

    private readonly SkillDirectoryOptions _options;
    private readonly ILogger<SkillDirectoryLoader> _logger;
    private readonly List<FileBasedSkill> _loadedSkills = [];
    private readonly object _lock = new();

    public SkillDirectoryLoader(SkillDirectoryOptions options, ILogger<SkillDirectoryLoader> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FileBasedSkill>> LoadSkillsAsync(CancellationToken ct = default)
    {
        var skills = new List<FileBasedSkill>();

        foreach (var directory in _options.SkillDirectories)
        {
            if (!Directory.Exists(directory))
            {
                _logger.LogDebug("Skill directory does not exist, skipping: {Directory}", directory);
                continue;
            }

            var loaded = await ScanDirectoryAsync(directory, _options.SourceFor(directory), ct);
            skills.AddRange(loaded);
        }

        lock (_lock)
        {
            _loadedSkills.Clear();
            _loadedSkills.AddRange(skills);
        }

        _logger.LogInformation("Loaded {Count} file-based skills from {DirCount} directories",
            skills.Count, _options.SkillDirectories.Count);

        return skills;
    }

    public Task<FileBasedSkill?> LoadSkillAsync(string skillMdPath, CancellationToken ct = default)
        => LoadSkillAsync(skillMdPath, SkillSource.Unknown, ct);

    /// <summary>
    /// Load one SKILL.md, tagging it with the origin of the directory it came from.
    /// </summary>
    public async Task<FileBasedSkill?> LoadSkillAsync(
        string skillMdPath,
        SkillSource source,
        CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(skillMdPath))
            {
                _logger.LogWarning("SKILL.md not found at {Path}", skillMdPath);
                return null;
            }

            // The manifest itself gets the same reparse-point check the loader already
            // applies to skill directories and to bundled resources: a SKILL.md that is a
            // symlink or junction could make arbitrary filesystem content parse as a skill.
            if (IsReparsePoint(skillMdPath))
            {
                _logger.LogWarning("Refusing to read SKILL.md that is a link/reparse point: {Path}", skillMdPath);
                return null;
            }

            var manifest = await SkillManifestParser.ParseFileAsync(skillMdPath, ct);
            manifest.Source = source;

            var skillDirectory = Path.GetDirectoryName(Path.GetFullPath(skillMdPath));
            var directoryName = string.IsNullOrEmpty(skillDirectory) ? null : Path.GetFileName(skillDirectory);

            if (!TryApplySpecValidation(manifest, directoryName, skillMdPath))
                return null;

            if (!TryVerifyPublisherSignature(manifest, skillDirectory, skillMdPath))
                return null;

            manifest.Resources = DiscoverResources(skillDirectory);

            var skill = new FileBasedSkill(manifest);
            _logger.LogDebug("Loaded file-based skill: {SkillId} ({Name}) from {Path} [{Source}]",
                skill.SkillId, skill.Name, skillMdPath, source);

            return skill;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load skill from {Path}", skillMdPath);
            return null;
        }
    }

    /// <summary>
    /// Apply the Agent Skills specification rules, returning false when the skill must not load.
    ///
    /// The directory is authoritative for identity. A locally authored skill whose only fault
    /// is a mismatched or badly formed name is repaired in memory against its folder — the user
    /// wrote both, and silently dropping their skill for a cosmetic frontmatter slip is worse
    /// than adopting the name they already chose. Packages from untrusted origins get no such
    /// courtesy: for them the mismatch is exactly the spoofing signal the rule exists to catch.
    /// </summary>
    private bool TryApplySpecValidation(SkillManifest manifest, string? directoryName, string skillMdPath)
    {
        var findings = SkillManifestValidator.Validate(manifest, directoryName);

        if (!SkillManifestValidator.IsLoadable(findings) && IsLocallyAuthored(manifest.Source)
            && !string.IsNullOrWhiteSpace(directoryName))
        {
            var repaired = SkillManifestValidator.Validate(
                CloneWithName(manifest, directoryName),
                directoryName);

            if (SkillManifestValidator.IsLoadable(repaired))
            {
                _logger.LogWarning(
                    "Skill at {Path} declares name '{DeclaredName}'; adopting its directory name '{DirectoryName}' to satisfy the Agent Skills specification.",
                    skillMdPath, manifest.Name, directoryName);
                manifest.Name = directoryName;
                findings = repaired;
            }
        }

        foreach (var finding in findings)
        {
            if (finding.Severity == SkillValidationSeverity.Error)
                _logger.LogError("SKILL.md at {Path} — {Field}: {Message}", skillMdPath, finding.Field, finding.Message);
            else
                _logger.LogWarning("SKILL.md at {Path} — {Field}: {Message}", skillMdPath, finding.Field, finding.Message);
        }

        if (!SkillManifestValidator.IsLoadable(findings))
        {
            _logger.LogError("Skipping skill at {Path}: it does not satisfy the Agent Skills specification.", skillMdPath);
            return false;
        }

        return true;
    }

    // Unknown means the host never said where the package came from (the ISkillLoader path
    // overload, legacy undeclared directories), so it gets the untrusted treatment.
    private static bool IsLocallyAuthored(SkillSource source) =>
        source is SkillSource.BuiltIn or SkillSource.Authored;

    /// <summary>
    /// Re-run publisher signature verification at load time, not only at import.
    ///
    /// Three outcomes. No <c>SIGNATURE.json</c>: the package loads exactly as before, with
    /// <see cref="SkillManifest.VerifiedPublisher"/> false. A signature that verifies against
    /// the pinned key: loads with the flag set. A signature that is present but malformed,
    /// empty, or does not match the bytes on disk: refused — something claimed first-party
    /// provenance and the contents no longer back it up, which is exactly the post-import
    /// tampering the import-time check cannot see.
    /// </summary>
    private bool TryVerifyPublisherSignature(SkillManifest manifest, string? skillDirectory, string skillMdPath)
    {
        manifest.VerifiedPublisher = false;
        if (string.IsNullOrEmpty(skillDirectory))
            return true;

        var signaturePath = Path.Combine(skillDirectory, SkillPackageSignatureFile.FileName);
        if (!File.Exists(signaturePath))
            return true;

        SkillPackageSignatureFile? signature;
        try
        {
            if (new FileInfo(signaturePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _logger.LogError("Refusing skill at {Path}: its SIGNATURE.json is a link/reparse point.", skillMdPath);
                return false;
            }

            signature = JsonSerializer.Deserialize<SkillPackageSignatureFile>(File.ReadAllText(signaturePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refusing skill at {Path}: its SIGNATURE.json could not be read.", skillMdPath);
            return false;
        }

        if (signature is null || string.IsNullOrWhiteSpace(signature.Signature))
        {
            _logger.LogError("Refusing skill at {Path}: its SIGNATURE.json carries no signature.", skillMdPath);
            return false;
        }

        var publicKey = string.IsNullOrWhiteSpace(_options.PublisherPublicKeyPem)
            ? SkillCatalogDefaults.PublisherPublicKeyPem
            : _options.PublisherPublicKeyPem;
        var signedName = string.IsNullOrWhiteSpace(signature.Name) ? manifest.Name : signature.Name;

        if (!SkillPackageSigning.Verify(skillDirectory, signedName, signature.Signature, publicKey))
        {
            _logger.LogError(
                "Refusing skill at {Path}: it carries a publisher signature that does not match its contents. "
                + "The package was modified after it was signed, or the signature is forged.",
                skillMdPath);
            return false;
        }

        manifest.VerifiedPublisher = true;
        _logger.LogDebug("Skill at {Path} verified against publisher {Publisher}.", skillMdPath, signature.Publisher);
        return true;
    }

    private static bool IsReparsePoint(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.LinkTarget is not null) return true;
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // Conservative: if we can't tell, treat as unsafe.
            return true;
        }
    }

    private static SkillManifest CloneWithName(SkillManifest manifest, string name) => new()
    {
        Name = name,
        Description = manifest.Description,
        Compatibility = manifest.Compatibility,
        Instructions = manifest.Instructions,
        UnknownKeys = manifest.UnknownKeys,
        Diagnostics = manifest.Diagnostics,
    };

    /// <summary>
    /// Inventory the specification's optional directories. Paths and sizes only — content is
    /// read on demand so a skill's reference material costs nothing until the task needs it.
    /// </summary>
    private List<SkillResource> DiscoverResources(string? skillDirectory)
    {
        var resources = new List<SkillResource>();
        if (string.IsNullOrEmpty(skillDirectory) || !Directory.Exists(skillDirectory))
            return resources;

        var root = Path.GetFullPath(skillDirectory);

        try
        {
            // Walk the tree by hand rather than with SearchOption.AllDirectories: that
            // enumerator follows directory symlinks and junctions, and a file reached through
            // one carries no ReparsePoint attribute of its own, so checking only the file let
            // a junction dropped into references/ list (and later serve) arbitrary content
            // from elsewhere on disk under an in-package-looking path. A reparse point of
            // either kind is never followed — the same rule LoadSkillsAsync applies to
            // symlinked skill directories.
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            var truncated = false;

            while (pending.Count > 0 && !truncated)
            {
                foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                        continue;

                    if (entry is DirectoryInfo subdirectory)
                    {
                        pending.Push(subdirectory);
                        continue;
                    }

                    if (entry is not FileInfo info || !IsInsideRoot(info.FullName, root))
                        continue;

                    var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                    if (relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;

                    if (resources.Count >= MaxResourcesPerSkill)
                    {
                        _logger.LogWarning(
                            "Skill at {Directory} bundles more than {Max} files; the remainder is not listed.",
                            root, MaxResourcesPerSkill);
                        truncated = true;
                        break;
                    }

                    resources.Add(new SkillResource(relative, ClassifyResource(relative), info.Length));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate bundled files for skill at {Directory}", root);
        }

        return resources
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SkillResourceKind ClassifyResource(string relativePath)
    {
        if (relativePath.StartsWith("references/", StringComparison.OrdinalIgnoreCase))
            return SkillResourceKind.Reference;
        if (relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            return SkillResourceKind.Asset;
        if (relativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
            return SkillResourceKind.Script;
        return SkillResourceKind.Other;
    }

    public async Task<IReadOnlyList<FileBasedSkill>> ReloadSkillsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading file-based skills...");
        return await LoadSkillsAsync(ct);
    }

    private async Task<List<FileBasedSkill>> ScanDirectoryAsync(
        string directory,
        SkillSource source,
        CancellationToken ct)
    {
        var skills = new List<FileBasedSkill>();
        var manifestName = _options.ManifestFileName;
        var rootFullPath = Path.GetFullPath(directory);

        // Scan root directory if configured
        if (_options.ScanRootDirectories)
        {
            var rootManifest = FindManifest(directory, manifestName);
            if (rootManifest is not null && IsInsideRoot(rootManifest, rootFullPath))
            {
                var skill = await LoadSkillAsync(rootManifest, source, ct);
                if (skill is not null)
                    skills.Add(skill);
            }
        }

        // Scan subdirectories (each subdirectory is a potential skill)
        try
        {
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();

                // Refuse to follow symlinked subdirectories or anything whose resolved path
                // escapes the configured skills root. Without this, a symlink dropped under
                // the skills folder could make arbitrary filesystem content masquerade as a
                // skill — the same shape of bug as the path-restriction bypass in CVE-2025-54794.
                if (IsSymlink(subDir))
                {
                    _logger.LogWarning("Refusing to follow symlinked skill directory: {SubDir}", subDir);
                    continue;
                }

                if (!IsInsideRoot(subDir, rootFullPath))
                {
                    _logger.LogWarning("Skill directory resolved outside skills root, skipping: {SubDir}", subDir);
                    continue;
                }

                var manifest = FindManifest(subDir, manifestName);
                if (manifest is null) continue;

                if (!IsInsideRoot(manifest, rootFullPath))
                {
                    _logger.LogWarning("SKILL.md resolved outside skills root, skipping: {Manifest}", manifest);
                    continue;
                }

                var skill = await LoadSkillAsync(manifest, source, ct);
                if (skill is not null)
                    skills.Add(skill);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning skill directory: {Directory}", directory);
        }

        return skills;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null) return true;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch
        {
            // Conservative: if we can't tell, treat as unsafe.
            return true;
        }
    }

    private static bool IsInsideRoot(string candidate, string rootFullPath)
    {
        try
        {
            var resolved = Path.GetFullPath(candidate);
            var rootWithSep = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
                ? rootFullPath
                : rootFullPath + Path.DirectorySeparatorChar;
            return resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolved, rootFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find SKILL.md in a directory (case-insensitive).
    /// </summary>
    private static string? FindManifest(string directory, string manifestName)
    {
        try
        {
            var files = Directory.GetFiles(directory, "*.md");
            return files.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(manifestName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
