using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.PersonalMemory;

public static class PersonalMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared personal-memory layer. The host must also register:
    /// <list type="bullet">
    /// <item><see cref="IPersonalMemoryStore"/> (singleton-safe: the recall injector is a singleton),</item>
    /// <item><see cref="IPersonalMemoryTurnContextProvider"/> (or rely on the default ambient provider),</item>
    /// <item>optionally <see cref="IPersonalMemoryAvailability"/> (per-user / tenant switch) and <see cref="ILlmRouter"/>.</item>
    /// </list>
    /// <see cref="PersonalMemoryService"/> and <see cref="MemoryConsolidationService"/> are scoped because hosts
    /// commonly register <see cref="ILlmRouter"/> scoped; the write gate is a singleton so per-scope writes
    /// serialize across DI scopes in one process (stores still enforce versions across processes).
    /// </summary>
    public static IServiceCollection AddSwarmPersonalMemory(this IServiceCollection services, Action<PersonalMemoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new PersonalMemoryOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<PersonalMemoryWriteGate>();
        services.TryAddSingleton<MemoryConversationSummarizer>();
        services.TryAddSingleton<IPersonalMemoryAvailability, AlwaysEnabledPersonalMemoryAvailability>();
        services.TryAddSingleton<IPersonalMemoryTurnContextProvider>(_ => new AmbientPersonalMemoryTurnContextProvider());
        services.TryAddScoped(sp => new MemoryConsolidationService(
            sp.GetRequiredService<IPersonalMemoryStore>(),
            sp.GetRequiredService<PersonalMemoryOptions>(),
            sp.GetService<IPersonalMemoryAvailability>(),
            sp.GetService<ILlmRouter>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<MemoryConsolidationService>>(),
            sp.GetRequiredService<PersonalMemoryWriteGate>()));
        services.TryAddScoped(sp => new PersonalMemoryService(
            sp.GetRequiredService<IPersonalMemoryStore>(),
            sp.GetRequiredService<MemoryConsolidationService>(),
            sp.GetRequiredService<PersonalMemoryOptions>(),
            sp.GetService<IPersonalMemoryAvailability>(),
            sp.GetService<ILlmRouter>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<PersonalMemoryService>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContextInjector, PersonalMemoryContextInjector>(sp => new PersonalMemoryContextInjector(
            sp.GetRequiredService<IPersonalMemoryStore>(),
            sp.GetRequiredService<IPersonalMemoryTurnContextProvider>(),
            sp.GetRequiredService<PersonalMemoryOptions>(),
            sp.GetService<IPersonalMemoryAvailability>(),
            sp.GetService<TimeProvider>())));
        return services;
    }
}
