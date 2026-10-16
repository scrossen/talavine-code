using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding;
using SwarmCore.Coding.Extensions;
using SwarmCore.Missions;

namespace SwarmCode.Hosting.Extensions;

/// <summary>Everything a SwarmCode surface shows about extensions for one repository.</summary>
public sealed record SwarmCodeExtensionInventory(
    IReadOnlyList<CustomCommand> Commands,
    IReadOnlyList<CodingSkill> Skills,
    IReadOnlyList<CodingHook> UserHooks,
    RepoHookInspection? RepositoryHooks);

/// <summary>
/// SwarmCode's extensions: custom slash commands, skills and hooks. The person's own folder is the SwarmCode data
/// directory (<c>commands/</c>, <c>skills/</c>); user hooks come from <c>settings.json</c> <c>Coding:Hooks</c>; repository
/// hooks (<c>.swarmcode/hooks.json</c>) run only after the person approves that exact content
/// (<see cref="FileCodingHookApprovalStore"/>).
/// </summary>
public static class SwarmCodeExtensions
{
    /// <summary>Registers the durable approval stores for repository hooks and repository permission settings (before the harness so its defaults stay unused).</summary>
    public static IServiceCollection AddSwarmCodeExtensionStores(this IServiceCollection services, SwarmCodeDataPaths paths)
    {
        services.TryAddSingleton(_ => new FileCodingHookApprovalStore(paths));
        services.TryAddSingleton<ICodingHookApprovalStore>(sp => sp.GetRequiredService<FileCodingHookApprovalStore>());
        services.TryAddSingleton(_ => new FileCodingRepositorySettingsApprovalStore(paths));
        services.TryAddSingleton<SwarmCore.Coding.Permissions.ICodingRepositorySettingsApprovalStore>(sp => sp.GetRequiredService<FileCodingRepositorySettingsApprovalStore>());
        return services;
    }

    /// <summary>
    /// After the harness: the user extension folder, the skills index for API lanes, <c>skills.read</c>, and the hook
    /// decorators on the engine's guard and profile resolver. Call last, after every guard/resolver decoration.
    /// </summary>
    public static IServiceCollection AddSwarmCodeExtensions(this IServiceCollection services, SwarmCodeDataPaths paths)
    {
        services.PostConfigure<CodingOptions>(coding =>
        {
            coding.UserExtensionsDirectory ??= paths.RootDirectory;
            if (!coding.AdditionalInjectorNames.Contains(CodingSkillsInjector.InjectorName, StringComparer.OrdinalIgnoreCase))
                coding.AdditionalInjectorNames.Add(CodingSkillsInjector.InjectorName);
        });
        services.AddHostedService<SkillsToolRegistration>();
        services.AddCodingHooks();
        return services;
    }

    /// <summary>Discovers commands, skills and hooks for a repository without running anything.</summary>
    public static async Task<SwarmCodeExtensionInventory> InventoryAsync(string? repositoryRoot, SwarmCodeDataPaths paths,
        CodingHooksOptions? userHooks, ICodingHookApprovalStore approvals, IEnumerable<string>? builtInCommands = null, CancellationToken ct = default)
    {
        var options = Options.Create(new CodingOptions { Hooks = userHooks ?? new() });
        var source = new CodingHookSource(options, approvals);
        var commands = await Task.Run(() => CustomCommandCatalog.Discover(repositoryRoot, paths.RootDirectory, builtInCommands), ct).ConfigureAwait(false);
        var skills = await Task.Run(() => CodingSkillCatalog.Discover(repositoryRoot, paths.RootDirectory), ct).ConfigureAwait(false);
        var user = CodingHook.From((userHooks ?? new()).All(), ExtensionSource.User, "settings.json Coding:Hooks");
        var repo = await source.InspectRepositoryAsync(repositoryRoot, ct).ConfigureAwait(false);
        return new SwarmCodeExtensionInventory(commands, skills, user, repo);
    }

    /// <summary>The prompt shown before repository hooks may run.</summary>
    public static string DescribeApprovalRequest(RepoHookInspection inspection)
        => "This repository wants to run hooks:\n" + string.Join("\n", inspection.Hooks.Select(h =>
               $"  {h.Event}{(h.Matcher is null ? "" : $" [{h.Matcher}]")}: {h.Command}"))
           + "\nThey run on this computer with your permissions whenever their event fires. Allow?";

    private sealed class SkillsToolRegistration(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var coding = services.GetRequiredService<IOptions<CodingOptions>>();
            var sandbox = services.GetService<ISandboxPathResolver>();
            services.GetRequiredService<ICapabilityCatalog>().Register(CodingSkillTools.CreateRead(
                () => sandbox?.Root ?? coding.Value.RepositoryRoot,
                () => coding.Value.UserExtensionsDirectory));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
