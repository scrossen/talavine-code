namespace SwarmCore.LlmRouting.Pricing;

/// <summary>
/// Projects reviewed provider-pack model facts into the pricing seed used before
/// live refresh completes. Provider packs are the sole source of these volatile
/// values; this class contains no model or price table.
/// </summary>
public static class ProviderPackPricingProvider
{
    public static Dictionary<string, ModelPricingEntry> GetAllPricing()
    {
        var now = DateTime.UtcNow;
        var costs = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in ProviderPacks.ProviderPackCatalog.GetDirectApiPacks())
        {
            foreach (var (model, facts) in pack.DirectApi!.ModelFacts)
            {
                var source = $"provider-pack:{pack.PackId}@{pack.Version}";
                if (facts.SupportsEmbeddings == true && facts.InputCostPer1K is { } embedding)
                {
                    costs[model] = new ModelPricingEntry(0m, 0m, embedding, source, now);
                }
                else if (facts.InputCostPer1K is { } input
                         && facts.OutputCostPer1K is { } output)
                {
                    costs[model] = new ModelPricingEntry(input, output, null, source, now);
                }
            }
            foreach (var (alias, pricedModel) in pack.DirectApi.PricingAliases)
            {
                costs[alias] = costs[pricedModel];
            }
        }

        return costs;
    }
}
