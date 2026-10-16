namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>A discovered catalog, never a compiled list of model names. Catalog membership does not prove inference access.</summary>
public sealed record LlmModelCatalog(IReadOnlyList<string> Models, string Source)
{
    public IReadOnlyList<LlmCatalogModel> Details { get; init; } = [];
}

/// <summary>Provider-advertised metadata; neither quality qualification nor proof a model will accept a task.</summary>
public sealed record LlmCatalogModel(string Id, int? ContextTokens, bool SupportsTools,
    decimal? InputPricePerMillionUsd, decimal? OutputPricePerMillionUsd, bool? EndpointModerated);

public interface ILlmModelCatalogProvider
{
    Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default);
}
