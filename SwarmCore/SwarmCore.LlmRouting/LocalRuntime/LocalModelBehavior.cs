using System.Text.RegularExpressions;

namespace SwarmCore.LlmRouting.LocalRuntime;

public enum LocalModelBehavior { Unknown, Abliterated, Uncensored, Standard }

/// <summary>A person's correction applies to one installed artifact at one server, never to a reused tag.</summary>
public sealed record LocalModelPreference(string Endpoint, string Model, string Digest,
    LocalModelBehavior Behavior, bool UseOnDecline = true, string? PublisherUrl = null);

public sealed record LocalModelClassification(LocalModelBehavior Behavior, string Evidence)
{
    public bool ReducedRefusal => Behavior is LocalModelBehavior.Abliterated or LocalModelBehavior.Uncensored;
}

public static partial class LocalModelBehaviors
{
    [GeneratedRegex(@"(?:^|[\W_])abliterated(?:[\W_]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Abliterated();
    [GeneratedRegex(@"(?:^|[\W_])uncensored(?:[\W_]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uncensored();

    public static LocalModelClassification Classify(string model, IEnumerable<string>? tags = null)
    {
        var labels = string.Join(" ", (tags ?? []).Prepend(model));
        return Abliterated().IsMatch(labels) ? new(LocalModelBehavior.Abliterated, "Publisher model name or tags; not independently verified")
            : Uncensored().IsMatch(labels) ? new(LocalModelBehavior.Uncensored, "Publisher model name or tags; method not established")
            : new(LocalModelBehavior.Unknown, "No abliterated or uncensored label found");
    }

    public static LocalModelClassification Classify(string endpoint, OllamaInstalledModel model, IEnumerable<LocalModelPreference> preferences)
    {
        var saved = Preference(endpoint, model, preferences);
        return saved is null ? Classify(model.Name) : new(saved.Behavior, saved.PublisherUrl is null
            ? "Your classification for this installed version" : "Publisher tags from " + saved.PublisherUrl + "; not independently verified");
    }

    public static LocalModelPreference? Preference(string endpoint, OllamaInstalledModel model, IEnumerable<LocalModelPreference> preferences)
        => preferences.FirstOrDefault(p => EndpointKey(p.Endpoint) == EndpointKey(endpoint)
            && p.Model == model.Name && p.Digest == model.Digest);

    public static string EndpointKey(string endpoint) => new Uri(endpoint.TrimEnd('/') + "/").AbsoluteUri;
}
