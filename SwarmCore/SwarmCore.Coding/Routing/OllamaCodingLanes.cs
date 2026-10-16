using System.Security.Cryptography;
using System.Text;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCore.Coding.Routing;

/// <summary>Installed, tool-capable local models join the same hand-off policy as other lanes.</summary>
public static class OllamaCodingLanes
{
    public static IReadOnlyList<CodingLane> Merge(IReadOnlyList<CodingLane> configured, string endpoint,
        IReadOnlyList<OllamaInstalledModel> installed, IReadOnlyList<LocalModelPreference> preferences, bool preferOnDecline)
    {
        var result = configured.Select(l => l.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            ? l with { SelectionGroup = "Ollama", PreferOnDecline = installed.Any(m => Matches(l, m) && Preferred(m)) } : l).ToList();
        foreach (var model in installed.Where(m => m.SupportsTools && m.IsOnDevice).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (result.Any(l => l.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) && Matches(l, model))) continue;
            // Keep all installed tool-capable models selectable, including models without reduced-refusal labels.
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.Name)))[..12].ToLowerInvariant();
            var name = "ollama-" + suffix;
            if (result.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var classification = LocalModelBehaviors.Classify(endpoint, model, preferences);
            result.Add(new(name, "Ollama", model.Name)
            {
                Priority = 80, HealthKey = "Ollama/" + model.Name + "/" + model.Digest,
                SelectionGroup = "Ollama",
                Description = $"Ollama · {classification.Behavior} · {classification.Evidence}",
                PreferOnDecline = Preferred(model),
            });
        }
        return result;

        bool Preferred(OllamaInstalledModel model) => preferOnDecline && model.SupportsTools && model.IsOnDevice
            && LocalModelBehaviors.Classify(endpoint, model, preferences).ReducedRefusal
            && (LocalModelBehaviors.Preference(endpoint, model, preferences)?.UseOnDecline ?? true);
    }

    private static bool Matches(CodingLane lane, OllamaInstalledModel model)
        => model.Name.Equals(lane.Model, StringComparison.OrdinalIgnoreCase) || model.Name.Equals(lane.Model + ":latest", StringComparison.OrdinalIgnoreCase);
}
