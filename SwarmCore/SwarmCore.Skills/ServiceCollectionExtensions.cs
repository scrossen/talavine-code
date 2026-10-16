using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Core;
using SwarmCore.Skills.FileSkills;
using SwarmCore.Skills.Models;
using SwarmCore.Skills.Security;
using SwarmCore.Skills.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace SwarmCore.Skills;

/// <summary>
/// Extension methods for registering SwarmCore.Skills services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add SwarmCore.Skills services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSwarmCoreSkills(
        this IServiceCollection services,
        Action<SkillRegistryConfiguration>? configureOptions = null)
    {
        // Idempotent: AddAgentSkills calls this, and a host that also calls it directly
        // used to end up with two configurations, two registries, and two executors —
        // the enabled toggle the user flipped on one registry was invisible to the other.
        // A second call configures the instance the first call registered.
        var config = GetOrAddSingletonInstance(services, () => new SkillRegistryConfiguration());
        configureOptions?.Invoke(config);

        // Core services
        services.TryAddSingleton<ISkillSecurityPolicy, SkillSecurityPolicy>();

        // The consent service needs a grant store to construct at all, so ship a working default
        // and let a host that wants durable grants register its own instead.
        services.TryAddSingleton<IPermissionGrantStore, InMemoryPermissionGrantStore>();
        services.TryAddSingleton<ISkillPermissionConsentService, SkillPermissionConsentService>();
        services.TryAddSingleton<ISkillRegistry, SkillRegistry>();
        services.TryAddSingleton<ISkillExecutor, SkillExecutor>();

        // Auto-discover skills on startup if configured
        if (config.AutoDiscoverSkills && !HasHostedService<SkillDiscoveryHostedService>(services))
        {
            services.AddHostedService<SkillDiscoveryHostedService>();
        }

        return services;
    }

    /// <summary>
    /// Return the already-registered singleton instance of <typeparamref name="T"/>, or
    /// create, register, and return a new one. Lets repeated registration calls layer
    /// configuration onto one instance instead of shadowing it.
    /// </summary>
    private static T GetOrAddSingletonInstance<T>(IServiceCollection services, Func<T> factory)
        where T : class
    {
        var existing = services
            .LastOrDefault(d => d.ServiceType == typeof(T) && d.ImplementationInstance is T)
            ?.ImplementationInstance as T;
        if (existing is not null)
            return existing;

        var created = factory();
        services.AddSingleton(created);
        return created;
    }

    private static bool HasHostedService<THosted>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(THosted));

    /// <summary>
    /// Register a skill type with the container.
    /// The skill will be automatically registered with the registry on startup.
    /// </summary>
    /// <typeparam name="TSkill">The skill type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSkill<TSkill>(this IServiceCollection services)
        where TSkill : class, ISkill
    {
        services.AddSingleton<ISkill, TSkill>();
        return services;
    }

    /// <summary>
    /// Register a skill instance with the container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="skill">The skill instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSkill(this IServiceCollection services, ISkill skill)
    {
        services.AddSingleton(skill);
        return services;
    }

    /// <summary>
    /// Register a skill using a factory function.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Factory to create the skill.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSkill(
        this IServiceCollection services,
        Func<IServiceProvider, ISkill> factory)
    {
        services.AddSingleton<ISkill>(factory);
        return services;
    }

    /// <summary>
    /// Add file-based skill loading from SKILL.md manifest files.
    /// Skills are loaded from the configured directories on startup and registered with the skill registry.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Configure skill directory options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFileBasedSkills(
        this IServiceCollection services,
        Action<SkillDirectoryOptions>? configureOptions = null)
    {
        // Idempotent for the same reason as AddSwarmCoreSkills: a second call adds its
        // directories to the options the first call registered.
        var options = GetOrAddSingletonInstance(services, () => new SkillDirectoryOptions());
        configureOptions?.Invoke(options);

        services.TryAddSingleton<ISkillLoader, SkillDirectoryLoader>();
        services.TryAddSingleton<ISkillInstructionProvider, SkillInstructionProvider>();
        if (!HasHostedService<FileBasedSkillDiscoveryService>(services))
        {
            services.AddHostedService<FileBasedSkillDiscoveryService>();
        }

        return services;
    }

    /// <summary>
    /// Add file-based skills from one or more directories.
    /// Convenience overload for the common case of specifying directories directly.
    ///
    /// Directories added this way load as <see cref="SkillSource.Unknown"/>, which keeps the
    /// flat skill-id namespace. Prefer <see cref="AddAgentSkills"/> for any host that also
    /// accepts user-installed packages, so trusted and untrusted skills cannot share an id.
    /// </summary>
    public static IServiceCollection AddFileBasedSkills(
        this IServiceCollection services,
        params string[] skillDirectories)
    {
        return services.AddFileBasedSkills(options =>
        {
            options.SkillDirectories.AddRange(skillDirectories);
        });
    }

    /// <summary>
    /// Wire up Agent Skills (https://agentskills.io) for a host application with the standard
    /// four-tier layout: skills shipped in the binary, playbooks the user authored, skills the
    /// agent drafted, and packages the user installed.
    ///
    /// Every host that supports skills should go through here rather than assembling directory
    /// lists by hand. The origin tagging is what keeps an installed package from claiming the
    /// id of a reviewed built-in, and getting that wrong in one app is a security bug in that
    /// app rather than a cosmetic inconsistency.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="builtInSkillsDirectory">Reviewed skills shipped with the app. Usually under AppContext.BaseDirectory.</param>
    /// <param name="userSkillsRoot">
    /// User-visible root that will hold <c>authored/</c>, <c>imported/</c>, and <c>suggested/</c> subfolders.
    /// Pass null for a host that ships skills but does not accept user-installed ones.
    /// </param>
    /// <param name="configureRegistry">Optional registry configuration (trust policy, always-enabled ids).</param>
    public static IServiceCollection AddAgentSkills(
        this IServiceCollection services,
        string builtInSkillsDirectory,
        string? userSkillsRoot = null,
        Action<SkillRegistryConfiguration>? configureRegistry = null)
    {
        services.AddSwarmCoreSkills(configureRegistry);

        return services.AddFileBasedSkills(options =>
        {
            options.AddDirectory(builtInSkillsDirectory, SkillSource.BuiltIn);

            if (string.IsNullOrWhiteSpace(userSkillsRoot)) return;

            options.AddDirectory(Path.Combine(userSkillsRoot, "suggested"), SkillSource.Suggested);
            options.AddDirectory(Path.Combine(userSkillsRoot, "imported"), SkillSource.Imported);
            options.AddDirectory(Path.Combine(userSkillsRoot, "authored"), SkillSource.Authored);
        });
    }
}

/// <summary>
/// Hosted service that discovers and registers skills on startup.
/// </summary>
internal class SkillDiscoveryHostedService : IHostedService
{
    private readonly ISkillRegistry _registry;
    private readonly IEnumerable<ISkill> _skills;
    private readonly SkillRegistryConfiguration _configuration;
    private readonly ILogger<SkillDiscoveryHostedService> _logger;

    public SkillDiscoveryHostedService(
        ISkillRegistry registry,
        IEnumerable<ISkill> skills,
        SkillRegistryConfiguration configuration,
        ILogger<SkillDiscoveryHostedService> logger)
    {
        _registry = registry;
        _skills = skills;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting skill discovery...");

        // Register explicitly added skills
        foreach (var skill in _skills)
        {
            try
            {
                await _registry.RegisterAsync(skill, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register skill {SkillId}", skill.SkillId);
            }
        }

        // Discover skills from configured assemblies
        if (_configuration.AssembliesToScan.Count > 0)
        {
            var assemblies = _configuration.AssembliesToScan
                .Select(name =>
                {
                    try
                    {
                        return Assembly.Load(name);
                    }
                    catch
                    {
                        _logger.LogWarning("Could not load assembly {Assembly}", name);
                        return null;
                    }
                })
                .Where(a => a is not null)
                .Cast<Assembly>();

            await _registry.DiscoverSkillsAsync(assemblies, cancellationToken);
        }
        else
        {
            // Scan entry assembly by default
            await _registry.DiscoverSkillsAsync(null, cancellationToken);
        }

        _logger.LogInformation(
            "Skill discovery complete. {Count} skills registered.",
            _registry.GetAllSkills().Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Hosted service that discovers and registers file-based skills from SKILL.md files on startup.
/// </summary>
internal class FileBasedSkillDiscoveryService : IHostedService
{
    private readonly ISkillLoader _loader;
    private readonly ISkillRegistry _registry;
    private readonly ILogger<FileBasedSkillDiscoveryService> _logger;

    public FileBasedSkillDiscoveryService(
        ISkillLoader loader,
        ISkillRegistry registry,
        ILogger<FileBasedSkillDiscoveryService> logger)
    {
        _loader = loader;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting file-based skill discovery...");

        try
        {
            var skills = await _loader.LoadSkillsAsync(cancellationToken);

            foreach (var skill in skills)
            {
                try
                {
                    await _registry.RegisterAsync(skill, cancellationToken);
                    _logger.LogInformation(
                        "Registered file-based skill: {SkillId} ({Name}) [{Category}]",
                        skill.SkillId, skill.Name, skill.Category);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to register file-based skill {SkillId}", skill.SkillId);
                }
            }

            _logger.LogInformation(
                "File-based skill discovery complete. {Count} skills loaded.",
                skills.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File-based skill discovery failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
