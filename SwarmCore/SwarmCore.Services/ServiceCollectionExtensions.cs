using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Adapters;
using SwarmCore.Services.Selection;

namespace SwarmCore.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the service catalog machinery (selector, invoker, catalog, and the
    /// Http/CSharp/Llm adapters). The host must additionally register the seams:
    /// <see cref="IServiceStore"/>, <see cref="ICredentialStore"/>,
    /// <see cref="IServiceHttpClient"/>, and (for the Llm adapter) an <c>ILlmRouter</c>.
    /// </summary>
    public static IServiceCollection AddSwarmCoreServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IServiceSelector, KeywordServiceSelector>();

        // Builtin adapter is a singleton so handler registrations are shared across scopes.
        services.TryAddSingleton<BuiltinServiceAdapter>();
        services.AddSingleton<IServiceKindAdapter>(sp => sp.GetRequiredService<BuiltinServiceAdapter>());

        // Scoped so the Llm adapter can depend on a scoped ILlmRouter without a captive dependency.
        services.AddScoped<IServiceKindAdapter, HttpServiceAdapter>();
        services.AddScoped<IServiceKindAdapter, CSharpServiceAdapter>();
        services.AddScoped<IServiceKindAdapter, LlmServiceAdapter>();
        services.AddScoped<IServiceKindAdapter, McpServiceAdapter>(); // uses IMcpToolInvoker if the host registers one
        services.AddScoped<IServiceInvoker, ServiceInvoker>();
        services.AddScoped<IServiceCatalog, ServiceCatalog>();
        return services;
    }
}
