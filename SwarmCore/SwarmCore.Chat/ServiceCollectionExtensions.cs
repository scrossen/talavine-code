using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SwarmCore.Chat;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared chat building blocks. The host must already have an
    /// <c>ILlmRouter</c>/<c>ILlmSettingsProvider</c>/<c>ILlmCapabilitiesService</c> registered
    /// (e.g. via <c>AddAllLlmProviders</c>) — these services are scoped so they can depend on
    /// a scoped router without a captive dependency.
    /// </summary>
    public static IServiceCollection AddSwarmCoreChat(this IServiceCollection services)
    {
        services.TryAddScoped<IVisionDescriptionService, VisionDescriptionService>();
        services.TryAddScoped<IChatModelPickerService, ChatModelPickerService>();
        return services;
    }
}
