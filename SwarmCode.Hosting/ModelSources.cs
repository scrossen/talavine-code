using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting;

public sealed record ModelSourcesDocument(long Revision, IReadOnlyList<ConfiguredModelSource> Sources)
{
    public IReadOnlyList<LocalModelPreference> LocalModels { get; init; } = [];
    public bool PreferLocalModelsOnDecline { get; init; } = true;
}

/// <summary>Owns a separate revisioned document; a stale settings tab cannot overwrite another writer.</summary>
public sealed class ModelSourcesStore(SwarmCodeDataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path = Path.Combine(paths.RootDirectory, "model-sources.json");
    public ModelSourcesDocument Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<ModelSourcesDocument>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Model sources are invalid.")
        : new(0, []);

    public async Task SaveAsync(ModelSourcesDocument value, CancellationToken ct = default)
    {
        foreach (var source in value.Sources) source.Validate();
        foreach (var model in value.LocalModels)
        {
            OllamaSetupClient.ValidateEndpoint(model.Endpoint);
            OllamaSetupClient.NormalizeModel(model.Model);
            if (string.IsNullOrWhiteSpace(model.Digest) || !Enum.IsDefined(model.Behavior))
                throw new InvalidOperationException("Refresh installed models before saving their classification.");
        }
        if (value.LocalModels.DistinctBy(m => (LocalModelBehaviors.EndpointKey(m.Endpoint), m.Model, m.Digest)).Count() != value.LocalModels.Count)
            throw new InvalidOperationException("Duplicate local model preferences.");
        if (value.Sources.Select(s => s.Id).Distinct().Count() != value.Sources.Count ||
            value.Sources.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Sources.Count ||
            value.Sources.Select(s => s.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Sources.Count)
            throw new InvalidOperationException("Use unique source names and one connection per built-in provider. Custom connections may be added independently.");
        Directory.CreateDirectory(paths.RootDirectory);
        using var lease = await LeaseAsync(ct);
        var current = Read();
        if (current.Revision != value.Revision) throw new InvalidOperationException("Model sources changed. Reload before saving; your draft has been preserved.");
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value with { Revision = current.Revision + 1 }, Json), ct);
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    private async Task<FileStream> LeaseAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(25, ct); }
        }
    }
}

public static class ModelSourcesRegistration
{
    public static IServiceCollection AddSwarmCodeModelSources(this IServiceCollection services, SwarmCodeDataPaths paths, IConfiguration? configuration = null)
    {
        var sources = new ModelSourcesStore(paths).Read().Sources;
        services.AddSingleton<ModelSourcesStore>();
        services.AddSingleton<OllamaSetupClient>();
        services.AddOptions<Models.OllamaVisionStorageOptions>();
        if (configuration is not null) services.Configure<Models.OllamaVisionStorageOptions>(configuration.GetSection("Coding:OllamaVisionStorage"));
        services.AddSingleton<SwarmCore.LlmRouting.LocalRuntime.IOllamaVisionArtifactStore, Models.OllamaVisionArtifactStore>();
        services.AddSingleton<SwarmCore.LlmRouting.LocalRuntime.OllamaVisionRepair>();
        services.TryAddSingleton<SwarmCore.Agent.Procedures.IAgentProcedureStore, Projects.FileAppProcedureStore>();
        services.AddSingleton<OpenModelDiscovery>();
        services.AddSingleton<OllamaCodingLaneSource>();
        var laneSource = services.LastOrDefault(d => d.ServiceType == typeof(ICodingLaneSource));
        if (laneSource is null || laneSource.ImplementationType == typeof(OptionsCodingLaneSource))
        {
            if (laneSource is not null) services.Remove(laneSource);
            services.AddSingleton<ICodingLaneSource>(sp => sp.GetRequiredService<OllamaCodingLaneSource>());
        }
        // Downloads are explicit setup actions, never a surprise side effect of a fallback attempt.
        services.PostConfigure<OllamaProviderOptions>(o => o.AutoPullMissingModels = false);
        foreach (var source in sources.Where(s => s.Enabled))
        {
            source.Validate();
            switch (source.Kind)
            {
                case "OpenRouter":
                    services.PostConfigure<OpenRouterProviderOptions>(o => { o.Endpoint = source.Endpoint; o.DefaultModel = source.Model; });
                    break;
                case "Ollama":
                    services.PostConfigure<OllamaProviderOptions>(o => { o.Endpoint = source.Endpoint; o.DefaultModel = source.Model; o.Nodes.Clear(); o.AutoPullMissingModels = false; });
                    break;
                case "LMStudio":
                    services.PostConfigure<LMStudioProviderOptions>(o => { o.Endpoint = source.Endpoint; o.DefaultModel = source.Model; });
                    break;
                case "Compatible":
                    services.AddSingleton<ILlmProvider>(sp => ConfiguredCompatibleProvider.Create(source,
                        sp.GetRequiredService<ILlmApiKeyProvider>(), sp.GetRequiredService<IHttpClientFactory>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
                    break;
            }
        }
        // Runs after seeding and preserves the user's existing lane ordering and policy.
        services.PostConfigure<CodingLaneOptions>(options =>
        {
            foreach (var source in sources.Where(s => s.Enabled))
            {
                var index = options.Lanes.FindIndex(l => l.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && !options.Lanes[index].Provider.Equals(source.Provider, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Source name '{source.Name}' conflicts with an existing lane. Choose another source name.");
                var lane = new CodingLane(source.Name, source.Provider, source.Model)
                { Description = $"{source.Name} ({source.Kind})", Priority = index >= 0 ? options.Lanes[index].Priority : options.Lanes.Count + 20,
                    PreferOnDecline = index >= 0 && options.Lanes[index].PreferOnDecline };
                if (index >= 0) options.Lanes[index] = lane; else options.Lanes.Add(lane);
            }
        });
        return services;
    }
}
