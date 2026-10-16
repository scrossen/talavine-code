using System.Text.RegularExpressions;

namespace SwarmCore.LlmRouting.Configuration;

/// <summary>A host-configured model connection. Credentials are owned by ILlmApiKeyProvider.</summary>
public sealed record ConfiguredModelSource
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "OpenRouter";
    public string Endpoint { get; init; } = "https://openrouter.ai/api/v1";
    public string Model { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool RequiresApiKey { get; init; } = true;
    public string? ApiKeyEnvironmentVariable { get; init; }
    public string Provider => Kind == "Compatible" ? "Compatible-" + Id : Kind;

    public void Validate(bool requireModel = true)
    {
        if (!Guid.TryParseExact(Id, "N", out _) || !Regex.IsMatch(Name, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$"))
            throw new InvalidOperationException("Give the source a name using letters, numbers, dashes or underscores.");
        if (Kind is not ("OpenRouter" or "Ollama" or "LMStudio" or "Compatible"))
            throw new InvalidOperationException("Choose a supported connection type.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Use an HTTPS API base URL, or HTTP on localhost. Keep credentials out of the URL.");
        if (requireModel && string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("Discover models and choose one, or enter a model ID.");
        if (ApiKeyEnvironmentVariable is { Length: > 0 } variable &&
            (!RequiresApiKey || !Regex.IsMatch(variable, "^[A-Z_][A-Z0-9_]{0,127}$")))
            throw new InvalidOperationException("Use an uppercase API key environment variable name for a keyed source.");
    }
}
