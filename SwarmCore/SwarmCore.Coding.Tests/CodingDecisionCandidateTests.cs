using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.Coding.Tests;

public sealed class CodingDecisionCandidateTests
{
    [Fact]
    public void ThinkingVariantsUseExactModelAdapterMetadata()
    {
        var model = ProviderPackCatalog.GetRequired("CodexCli").ModelDefaults.Balanced;
        var efforts = ModelRouteCapabilities.ReasoningEfforts("CodexCli", model);
        Assert.Contains("medium", efforts);
        var variants = CodingDecisionCandidates.ExpandThinkingVariants("CodexCli", [model, "codex-cli:default", "unknown-model"]);
        Assert.Contains(model + "@medium", variants);
        Assert.DoesNotContain("codex-cli:default@medium", variants);
        Assert.DoesNotContain("unknown-model@medium", variants);
        Assert.All(variants.Where(m => m.Contains('@')), m => Assert.Contains(m.Split('@')[1], efforts));
    }
}
