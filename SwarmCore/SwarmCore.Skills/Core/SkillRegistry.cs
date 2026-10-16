using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.FileSkills;
using SwarmCore.Skills.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Skills.Core;

/// <summary>
/// Default implementation of ISkillRegistry.
/// </summary>
public class SkillRegistry : ISkillRegistry
{
    private readonly ConcurrentDictionary<string, ISkill> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _enabledState = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _skillSignatures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards the read-modify-write across the three dictionaries above. Each is
    /// individually thread-safe, but the registration decision reads all three and
    /// then writes all three, and two concurrent registrations of one id could
    /// interleave so that a later write reset the user's enabled toggle.
    /// </summary>
    private readonly object _registrationLock = new();
    private readonly SkillRegistryConfiguration _configuration;
    private readonly ISkillSecurityPolicy _securityPolicy;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SkillRegistry> _logger;

    public SkillRegistry(
        SkillRegistryConfiguration configuration,
        ISkillSecurityPolicy securityPolicy,
        IServiceProvider serviceProvider,
        ILogger<SkillRegistry> logger)
    {
        _configuration = configuration;
        _securityPolicy = securityPolicy;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task RegisterAsync(ISkill skill, CancellationToken ct = default)
    {
        try
        {
            // Initialize the skill
            await skill.InitializeAsync(_serviceProvider);

            // Check if skill is available
            var isAvailable = await skill.IsAvailableAsync(ct);
            if (!isAvailable)
            {
                _logger.LogWarning("Skill {SkillId} is not available, skipping registration", skill.SkillId);
                return;
            }

            // Re-registration safety: if this skill id already exists, detect drift in
            // its security-relevant surface (permissions, manifest body, description).
            // A change that GROWS permissions must force re-consent — that is the real
            // capability-escalation risk. A body/description edit that adds no new
            // permission is preserved as-is: a user editing their own local SKILL.md
            // prose should not have the skill silently reset to disabled on every
            // reload. Permission growth still fails closed.
            var signature = ComputeSignature(skill);

            // Everything from here on is synchronous and must be atomic per registry:
            // the decision below reads all three maps and then writes all three.
            lock (_registrationLock)
            {
                var wasAlreadyRegistered = _skills.TryGetValue(skill.SkillId, out var prior);

                // Two different files claiming one skill id is never a legitimate reload — it is
                // one package displacing another. Skill ids are namespaced by origin so this
                // should be unreachable, but if it ever happens the first registration wins:
                // built-in directories are scanned before user-writable ones, so the reviewed
                // skill keeps the id and the newcomer is refused rather than silently inheriting
                // the incumbent's enabled state.
                if (wasAlreadyRegistered && IsDisplacement(prior, skill, out var priorOrigin, out var incomingOrigin))
                {
                    _logger.LogError(
                        "Refusing to register skill {SkillId} from {IncomingOrigin}: id already held by {PriorOrigin}. "
                        + "Rename the incoming skill — a skill may not take over another's identity.",
                        skill.SkillId, incomingOrigin, priorOrigin);
                    return;
                }

                var priorSignature = _skillSignatures.TryGetValue(skill.SkillId, out var sig) ? sig : null;
                var driftDetected = wasAlreadyRegistered && priorSignature is not null && priorSignature != signature;
                // Compared by id: a re-instantiated code skill builds new SkillPermission
                // objects for the same permissions, which is not growth.
                var permissionsGrew = wasAlreadyRegistered && prior is not null
                    && skill.RequiredPermissions.Any(p => !prior.RequiredPermissions.Any(
                        held => string.Equals(held.PermissionId, p.PermissionId, StringComparison.OrdinalIgnoreCase)));
                // A skill that already holds an Elevated+ permission can be
                // repurposed by an instruction-body swap within its existing
                // capability envelope, so body drift on such a skill still forces
                // re-consent. Standard-only (read-only) skills are safe to keep
                // enabled through a prose edit the user likely made themselves.
                var holdsElevatedPermission = skill.RequiredPermissions
                    .Any(p => p.Level >= PermissionLevel.Elevated);

                // Register the skill
                _skills[skill.SkillId] = skill;
                _skillSignatures[skill.SkillId] = signature;

                // Determine enabled state
                bool shouldEnable;
                if (permissionsGrew)
                {
                    _logger.LogWarning(
                        "Skill {SkillId} added a required permission on re-register; forcing disabled until the user re-consents",
                        skill.SkillId);
                    shouldEnable = false;
                }
                else if (driftDetected && holdsElevatedPermission)
                {
                    _logger.LogWarning(
                        "Skill {SkillId} content changed on re-register and holds an elevated permission; forcing disabled until the user re-consents",
                        skill.SkillId);
                    shouldEnable = false;
                }
                else if (driftDetected && wasAlreadyRegistered && _enabledState.TryGetValue(skill.SkillId, out var priorEnabledOnDrift))
                {
                    // Instructions/description changed, no permission grew, and the
                    // skill is Standard-only. Preserve the user's prior decision
                    // rather than silently disabling an edit they likely made.
                    _logger.LogInformation(
                        "Skill {SkillId} content changed on re-register with no permission growth; preserving prior enabled state ({Enabled})",
                        skill.SkillId, priorEnabledOnDrift);
                    shouldEnable = priorEnabledOnDrift;
                }
                else if (wasAlreadyRegistered && _enabledState.TryGetValue(skill.SkillId, out var priorEnabled))
                {
                    // Unchanged surface — preserve the user's prior enable decision rather than
                    // recomputing from DetermineInitialEnabledState (which can flip it back on).
                    shouldEnable = priorEnabled;
                }
                else
                {
                    shouldEnable = DetermineInitialEnabledState(skill);
                }

                _enabledState[skill.SkillId] = shouldEnable;

                _logger.LogInformation(
                    "Registered skill {SkillId} ({Name}) - TrustLevel: {TrustLevel}, Enabled: {Enabled}",
                    skill.SkillId, skill.Name, skill.TrustLevel, shouldEnable);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register skill {SkillId}", skill.SkillId);
            throw;
        }
    }

    /// <summary>
    /// True when an incoming skill would take over an id currently held by a different
    /// skill. Two cases:
    /// <list type="bullet">
    /// <item>The concrete type changes — a C# skill claiming a file skill's id, or a file
    /// skill claiming a C# skill's id, or one C# implementation replacing another. A
    /// registry only ever legitimately re-registers the <em>same</em> implementation
    /// (restart, rescan), so a type change is a takeover.</item>
    /// <item>Both are file-based and come from different files. Reloading the same file
    /// is normal and handled by the drift logic; arriving from a different path is a
    /// takeover.</item>
    /// </list>
    /// </summary>
    private static bool IsDisplacement(
        ISkill? prior,
        ISkill incoming,
        out string priorOrigin,
        out string incomingOrigin)
    {
        priorOrigin = "";
        incomingOrigin = "";

        if (prior is null)
            return false;

        priorOrigin = DescribeOrigin(prior);
        incomingOrigin = DescribeOrigin(incoming);

        if (prior.GetType() != incoming.GetType())
            return true;

        if (prior is not FileBasedSkill priorFile || incoming is not FileBasedSkill incomingFile)
            return false;

        var priorPath = priorFile.SourcePath;
        var incomingPath = incomingFile.SourcePath;

        if (string.IsNullOrEmpty(priorPath) || string.IsNullOrEmpty(incomingPath))
            return false;

        return !string.Equals(priorPath, incomingPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeOrigin(ISkill skill) =>
        skill is FileBasedSkill file && !string.IsNullOrEmpty(file.SourcePath)
            ? file.SourcePath
            : skill.GetType().FullName ?? skill.GetType().Name;

    // Stable hash over the skill's security-relevant surface: concrete type, version,
    // description, sorted required permissions, and — for file-based skills — the
    // SKILL.md instruction body. A change here means a user who previously consented
    // may no longer consent, so we force a re-consent loop.
    private static string ComputeSignature(ISkill skill)
    {
        var sb = new StringBuilder();
        sb.Append(skill.GetType().FullName).Append('|');
        sb.Append(skill.Version).Append('|');
        sb.Append(skill.TrustLevel).Append('|');
        sb.Append(skill.Description).Append('|');
        foreach (var p in skill.RequiredPermissions.Select(p => p.PermissionId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(p).Append(',');
        }
        if (skill is FileBasedSkill fileSkill)
        {
            sb.Append('|').Append(fileSkill.Instructions);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    public Task UnregisterAsync(string skillId, CancellationToken ct = default)
    {
        if (_skills.TryRemove(skillId, out var skill))
        {
            _enabledState.TryRemove(skillId, out _);
            _skillSignatures.TryRemove(skillId, out _);
            _logger.LogInformation("Unregistered skill {SkillId}", skillId);
        }
        return Task.CompletedTask;
    }

    public ISkill? GetSkill(string skillId) =>
        _skills.TryGetValue(skillId, out var skill) ? skill : null;

    public IReadOnlyList<ISkill> GetAllSkills() =>
        _skills.Values.ToList();

    public async Task<IReadOnlyList<SkillSearchResult>> SearchAsync(
        string query,
        SkillSearchOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SkillSearchOptions();
        var queryLower = query.ToLowerInvariant();
        // Split on punctuation too, not just spaces: otherwise "ollama?" keeps
        // its '?' and never matches a skill name.
        var queryTerms = queryLower.Split(
            [' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '[', ']', '/'],
            StringSplitOptions.RemoveEmptyEntries);

        var results = new List<SkillSearchResult>();

        foreach (var skill in _skills.Values)
        {
            // Filter by enabled state
            if (options.OnlyEnabled && !IsEnabled(skill.SkillId))
                continue;

            // Filter by category
            if (options.Category is not null && !skill.Category.Equals(options.Category))
                continue;

            // Filter by trust level
            if (options.MinTrustLevel.HasValue && skill.TrustLevel < options.MinTrustLevel.Value)
                continue;

            // Calculate relevance
            var (relevance, matchingKeywords) = CalculateRelevance(skill, queryTerms);

            if (relevance >= options.MinRelevance)
            {
                results.Add(new SkillSearchResult
                {
                    SkillId = skill.SkillId,
                    Name = skill.Name,
                    Description = skill.Description,
                    Relevance = relevance,
                    MatchingKeywords = matchingKeywords,
                    Category = skill.Category,
                    TrustLevel = skill.TrustLevel
                });
            }
        }

        return results
            .OrderByDescending(r => r.Relevance)
            .Take(options.Limit)
            .ToList();
    }

    public IReadOnlyList<ISkill> GetByCategory(SkillCategory category) =>
        _skills.Values.Where(s => s.Category.Equals(category)).ToList();

    public IReadOnlyList<ISkill> GetByTrustLevel(SkillTrustLevel trustLevel) =>
        _skills.Values.Where(s => s.TrustLevel == trustLevel).ToList();

    public bool IsEnabled(string skillId)
    {
        if (_configuration.AlwaysDisabledSkills.Contains(skillId, StringComparer.OrdinalIgnoreCase))
            return false;

        if (_configuration.AlwaysEnabledSkills.Contains(skillId, StringComparer.OrdinalIgnoreCase))
            return true;

        return _enabledState.TryGetValue(skillId, out var enabled) && enabled;
    }

    public Task<bool> SetEnabledAsync(string skillId, bool enabled, CancellationToken ct = default)
    {
        if (_configuration.AlwaysEnabledSkills.Contains(skillId, StringComparer.OrdinalIgnoreCase) ||
            _configuration.AlwaysDisabledSkills.Contains(skillId, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Cannot change enabled state for skill {SkillId} - it is in always enabled/disabled list",
                skillId);
            // Report the no-op so callers do not tell the user the toggle
            // succeeded when a deployment policy pinned the skill.
            return Task.FromResult(false);
        }

        _enabledState[skillId] = enabled;
        _logger.LogInformation("Skill {SkillId} enabled state set to {Enabled}", skillId, enabled);
        return Task.FromResult(true);
    }

    public async Task<int> DiscoverSkillsAsync(
        IEnumerable<Assembly>? assemblies = null,
        CancellationToken ct = default)
    {
        assemblies ??= new[] { Assembly.GetEntryAssembly()! };
        var discovered = 0;

        foreach (var assembly in assemblies.Where(a => a is not null))
        {
            try
            {
                var skillTypes = assembly.GetTypes()
                    .Where(t => typeof(ISkill).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract
                        && t.GetCustomAttribute<SkillDiscoveryIgnoredAttribute>() is null);

                foreach (var skillType in skillTypes)
                {
                    try
                    {
                        var skill = (ISkill?)ActivatorUtilities.CreateInstance(_serviceProvider, skillType);
                        if (skill is not null)
                        {
                            await RegisterAsync(skill, ct);
                            discovered++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to instantiate skill type {SkillType}", skillType.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan assembly {Assembly} for skills", assembly.FullName);
            }
        }

        _logger.LogInformation("Discovered {Count} skills from assemblies", discovered);
        return discovered;
    }

    public SkillFunction? GetFunction(string skillId, string functionName)
    {
        var skill = GetSkill(skillId);
        return skill?.GetFunctions().FirstOrDefault(f =>
            f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));
    }

    private bool DetermineInitialEnabledState(ISkill skill)
    {
        // Check configuration overrides
        if (_configuration.AlwaysEnabledSkills.Contains(skill.SkillId, StringComparer.OrdinalIgnoreCase))
            return true;

        if (_configuration.AlwaysDisabledSkills.Contains(skill.SkillId, StringComparer.OrdinalIgnoreCase))
            return false;

        // System skills are always enabled
        if (skill.TrustLevel == SkillTrustLevel.System)
            return true;

        // Check if can auto-enable
        if (_configuration.AutoEnableSkills && _securityPolicy.CanAutoEnable(skill))
            return true;

        // Community skills with capabilities and all untrusted skills start disabled.
        return false;
    }

    private static (double relevance, List<string> matchingKeywords) CalculateRelevance(
        ISkill skill,
        string[] queryTerms)
    {
        var matchingKeywords = new List<string>();
        var score = 0.0;

        var skillNameLower = skill.Name.ToLowerInvariant();
        var skillDescLower = skill.Description.ToLowerInvariant();
        var skillKeywords = skill.Keywords.Select(k => k.ToLowerInvariant()).ToList();

        foreach (var term in queryTerms)
        {
            // Exact name match is highest
            if (skillNameLower.Contains(term))
            {
                score += 0.4;
                matchingKeywords.Add(term);
            }

            // Keyword match
            var keywordMatch = skillKeywords.FirstOrDefault(k => k.Contains(term) || term.Contains(k));
            if (keywordMatch is not null)
            {
                score += 0.3;
                if (!matchingKeywords.Contains(keywordMatch))
                    matchingKeywords.Add(keywordMatch);
            }

            // Description match
            if (skillDescLower.Contains(term))
            {
                score += 0.2;
            }

            // Category match
            if (skill.Category.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.1;
            }
        }

        // Do NOT divide by queryTerms.Length. That averaged the score over EVERY
        // word in the query, including stopwords that can never match, so a
        // longer question was mathematically penalised: against MinRelevance=0.1
        // a keyword-only hit (0.3) failed at 4+ words and even a perfect 1.0 on
        // one term failed at 11+. Callers are told to search with goal-shaped
        // natural-language phrases (5-12 words), i.e. exactly the queries this
        // guaranteed to zero out — which is why search returned "0 matches out
        // of 17 registered" while the registry was fully populated.
        // Capping the accumulated score keeps ranking intact (more and better
        // matches still score higher) without punishing query length.
        var normalizedScore = Math.Min(1.0, score);
        return (normalizedScore, matchingKeywords);
    }
}
