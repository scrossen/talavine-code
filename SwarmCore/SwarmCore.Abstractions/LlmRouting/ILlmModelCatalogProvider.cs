namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>A discovered catalog, never a compiled list of model names. Catalog membership does not prove inference access.</summary>
public sealed record LlmModelCatalog(IReadOnlyList<string> Models, string Source)
{
    public IReadOnlyList<LlmCatalogModel> Details { get; init; } = [];
    /// <summary>Exact model effort choices advertised by the provider. Absence is unknown.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReasoningEfforts { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Provider-advertised metadata; neither quality qualification nor proof a model will accept a task.</summary>
public sealed record LlmCatalogModel(string Id, int? ContextTokens, bool SupportsTools,
    decimal? InputPricePerMillionUsd, decimal? OutputPricePerMillionUsd, bool? EndpointModerated)
{
    /// <summary>Provider-advertised levels supported by this adapter. Null means unknown; empty means none.</summary>
    public IReadOnlyList<string>? ReasoningEfforts { get; init; }
}

public interface ILlmModelCatalogProvider
{
    Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default);
}
