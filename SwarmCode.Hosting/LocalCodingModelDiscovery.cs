using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCode.Hosting;

/// <summary>The host's read-only local discovery, shared by workspace execution and pre-build pickers.</summary>
internal static class LocalCodingModelDiscovery
{
    internal static async Task RefreshAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var laneOptions = services.GetRequiredService<IOptions<CodingLaneOptions>>().Value;
        // A damaged sources file must not stop discovery; it is reported and treated as empty.
        var configured = new ModelSourcesStore(services.GetRequiredService<SwarmCodeDataPaths>()).ReadOrEmpty(out var sourcesProblem);
        if (sourcesProblem is not null)
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LocalCodingModelDiscovery)).LogWarning("{Problem}", sourcesProblem);
        var sources = new[]
        {
            new ConfiguredModelSource { Name = "lmstudio", Kind = "LMStudio", RequiresApiKey = false,
                Endpoint = services.GetRequiredService<IOptions<LMStudioProviderOptions>>().Value.Endpoint ?? "http://127.0.0.1:1234/v1" }
        }.Where(s => !configured.Sources.Any(saved => saved.Provider == s.Provider)).ToArray();
        var catalog = new ConfiguredModelCatalog(services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILlmApiKeyProvider>());
        var discovered = await CodingCatalogDiscovery.DiscoverAsync(
            sources.Select(s => new CodingLane(s.Name, s.Provider, "") { Priority = 80 }), laneOptions.Lanes,
            async (lane, cancellation) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                return await catalog.DiscoverAsync(sources.Single(s => s.Provider == lane.Provider), timeout.Token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        laneOptions.Lanes = laneOptions.Lanes.Concat(discovered).ToList();
        var localSources = await new LocalModelServerDiscovery(catalog).DiscoverAsync(
            LocalModelServerDiscovery.ListeningEndpoints(),
            configured.Sources.Select(s => s.Endpoint).Concat(sources.Select(s => s.Endpoint))
                .Append(services.GetRequiredService<IOptions<OllamaProviderOptions>>().Value.Endpoint), ct).ConfigureAwait(false);
        var registry = services.GetRequiredService<ILlmProviderRegistry>();
        foreach (var source in localSources)
        {
            if (laneOptions.Lanes.Any(l => l.Provider == source.Provider || l.Name == source.Name)) continue;
            registry.RegisterProvider(ConfiguredCompatibleProvider.Create(source,
                services.GetRequiredService<ILlmApiKeyProvider>(), services.GetRequiredService<IHttpClientFactory>(),
                services.GetRequiredService<ILoggerFactory>()));
            laneOptions.Lanes = laneOptions.Lanes.Append(new CodingLane(source.Name, source.Provider, source.Model)
            { Priority = 81, Description = $"Local model server ({source.Endpoint})" }).ToList();
        }
        await services.GetRequiredService<OllamaCodingLaneSource>().RefreshAsync(ct).ConfigureAwait(false);
    }
}
