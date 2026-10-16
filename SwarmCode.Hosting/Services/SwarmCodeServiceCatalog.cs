using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding;
using SwarmCore.Missions;
using SwarmCore.Services;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.AgentTools;
using SwarmCore.Services.Http;
using SwarmCore.Services.Sqlite;

namespace SwarmCode.Hosting.Services;

public static class SwarmCodeServiceCatalog
{
    public const string Guidance = "You can write and save reusable tools. Search catalog.search_services first. " +
        "If none fits, author a parameterized C# service with catalog.add_service (kind=csharp, config field source), " +
        "then verify it with catalog.test_service using synthetic arguments and an expected result. " +
        "Saved services persist across sessions and repositories in this Talavine Code data profile. Reuse them with catalog.call_service without regenerating code. " +
        "Use Input, Web, Files, Progress and Cancellation; credentials are vault references, never embedded values. " +
        "On changed preconditions or failure, inspect, revise, test and activate a new revision; catalog.rollback_service restores a tested version. " +
        "Every call still needs the current task's authority; never replay an uncertain external action. " +
        "A failed tool call is evidence: read its error and schema, make a bounded correction, then switch tools or allow a lane handoff instead of repeating guesses.";

    public static void AddSwarmCodeServiceCatalog(this IServiceCollection services, SwarmCodeDataPaths paths)
    {
        services.TryAddSingleton<IServiceStore>(_ => new SqliteServiceStore(new()
        { DatabasePath = Path.Combine(paths.RootDirectory, "services.db"), RequireReadBaseline = true }));
        services.TryAddSingleton<IServiceHttpClient>(sp => new DefaultServiceHttpClient(sp.GetRequiredService<IHttpClientFactory>()));
        services.TryAddSingleton<IServiceFileWorkspace, Workspace>();
        services.AddSwarmCoreServices();
        services.AddHostedService<Registration>();
        services.PostConfigure<CodingOptions>(o => o.AdditionalInstructions = string.Join(Environment.NewLine,
            new[] { o.AdditionalInstructions, Guidance }.Where(s => !string.IsNullOrWhiteSpace(s))));
    }

    private sealed class Workspace(ISandboxPathResolver paths) : IServiceFileWorkspace
    {
        public string Root => paths.Root;
        public string Resolve(string path) => paths.Resolve(path);
    }

    private sealed class Registration(IServiceProvider services, ICapabilityCatalog capabilities) : IHostedService
    {
        public async Task StartAsync(CancellationToken ct)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IServiceCatalog>().InitializeAsync(ct);
            foreach (var descriptor in Descriptors(scope.ServiceProvider))
            {
                var id = descriptor.ToolId;
                capabilities.Register(descriptor with
                {
                    // A saved body is caller-authored code; self-declared read-only metadata is not authority.
                    SideEffect = id == ServiceCatalogToolIds.Call ? ToolSideEffect.Exec : descriptor.SideEffect,
                    Invoke = async (input, cancellation) =>
                    {
                        using var callScope = services.CreateScope();
                        return await Descriptors(callScope.ServiceProvider).Single(t => t.ToolId == id).Invoke(input, cancellation);
                    }
                });
            }
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        private static IEnumerable<ToolDescriptor> Descriptors(IServiceProvider sp)
        {
            var catalog = sp.GetRequiredService<IServiceCatalog>();
            return ServiceCatalogTools.Build(catalog, policy: new ServiceCatalogToolPolicy
            {
                AuthorizeCallAsync = (service, _) => ValueTask.FromResult<string?>(
                    service.Revisions.SingleOrDefault(r => r.Version == service.ActiveRevision)?.TestPassed == true
                        ? null : "Verify the active revision with catalog.test_service and an expected result before reusing it."),
                AuthorizeAddAsync = (kind, _, _, _, _) => ValueTask.FromResult<string?>(
                    kind is not (ServiceKind.CSharp or ServiceKind.Http) ? "Author C# or HTTP services; use the configured model/MCP tools for other kinds."
                    : null)
            }).Concat(ServiceRevisionTools.Build(catalog));
        }
    }
}
