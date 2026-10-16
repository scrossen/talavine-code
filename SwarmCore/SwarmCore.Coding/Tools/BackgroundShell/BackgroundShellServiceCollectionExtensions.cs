using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Coding.Tools.BackgroundShell;

public static class BackgroundShellServiceCollectionExtensions
{
    /// <summary>
    /// Opt-in background shells (<c>shell.background.*</c>): the <see cref="BackgroundShellManager"/> (disposed with the
    /// container, which kills every shell) and catalog publication for API lanes. These tools execute commands, so a
    /// governed host decides whether to call this; they are not part of <see cref="CodingToolRegistrar.CodingToolIds"/>.
    /// Native lanes reach the same catalog tools through the host tool bridge (<see cref="Native.HostToolBridgeBroker"/>).
    /// </summary>
    public static IServiceCollection AddSwarmCoreBackgroundShell(this IServiceCollection services, Action<BackgroundShellOptions>? configure = null)
    {
        services.AddOptions<BackgroundShellOptions>();
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<BackgroundShellOptions>>();
            return new BackgroundShellManager(() => options.CurrentValue, () => sp.GetService<ICredentialStore>(),
                sp.GetService<ILogger<BackgroundShellManager>>());
        });
        if (!services.Any(d => d.ImplementationType == typeof(BackgroundShellToolRegistration)))
            services.AddHostedService<BackgroundShellToolRegistration>();
        return services;
    }
}
