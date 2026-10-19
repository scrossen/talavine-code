using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Implemented adapter capabilities. These facts never select or admit a route.</summary>
public static class ModelRouteCapabilities
{
    public static bool Supports(string provider, string model, LlmRequiredCapability capability)
        => capability switch
        {
            LlmRequiredCapability.ReasoningEffort => ReasoningEfforts(provider, model).Count > 1,
            LlmRequiredCapability.NativeAsyncTools or LlmRequiredCapability.NativeSteering
                or LlmRequiredCapability.NativeCompaction or LlmRequiredCapability.NativeComputerUse
                => OpenAIResponsesContract.IsSelected(provider, model),
            _ => false
        };

    public static IReadOnlyList<string> ReasoningEfforts(string provider, string model)
    {
        var selection = ModelReasoningSelection.Split(model);
        if (selection.Effort is { } selectedEffort)
        {
            var supported = ReasoningEfforts(provider, selection.Model);
            return supported.Contains(selectedEffort, StringComparer.Ordinal) ? [selectedEffort] : [];
        }
        model = selection.Model;
        if (ProviderPackCatalog.TryGet(provider, out var metadata))
        {
            var identity = ModelScoutModelRequests.ModelIdentity(provider, model);
            var choices = metadata!.ModelReasoningEfforts.FirstOrDefault(p => p.Key.Equals(identity, StringComparison.OrdinalIgnoreCase)).Value;
            if (choices is not null) return choices;
        }
        if (OpenAIResponsesContract.IsSelected(provider, model)) return ["low", "medium", "high", "xhigh", "max"];
        if (ProviderPackCatalog.TryGet(provider, out var pack) && pack!.SubscriptionCli is { } cli
            && pack.ExecutionDialect == "codex-cli-exec-v1")
        {
            var identity = ModelScoutModelRequests.ModelIdentity(provider, model);
            if ((identity == model && model.Contains(':')) || identity.Equals("default", StringComparison.OrdinalIgnoreCase)) return [];
            // Only controls supported by the reviewed CLI and exact foundation model are advertised.
            var foundation = cli.FoundationProviders.FirstOrDefault(p => ProviderPackCatalog.TryGetModelFacts(p, identity, out _));
            if (foundation is null) return [];
            var supported = ReasoningEfforts(foundation, identity);
            var (_, pinnedEffort) = CodexCliLlmProvider.SplitModelAndEffort(model);
            if (pinnedEffort is not null) return supported.Contains(pinnedEffort, StringComparer.Ordinal) ? [pinnedEffort] : [];
            return cli.ReasoningEfforts.Where(e => supported.Contains(e, StringComparer.Ordinal)).ToArray();
        }
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && OpenAICompatibleExecutionContract.IsReasoningModel(model))
            return ["low", "medium", "high"];
        return [];
    }
}
