namespace SwarmCore.Coding.Web;

/// <summary>One web search request. The query comes from the model; provider credentials never do.</summary>
public sealed record WebSearchQuery(string Query, int Count, string? Freshness = null, string? Site = null);

/// <summary>One search hit. Every field is third-party content.</summary>
public sealed record WebSearchHit(string Title, string Url, string Snippet, string? Age = null);

/// <summary>
/// A web search backend. Implementations read their credential from a host seam
/// (<see cref="IWebSearchKeySource"/>), throw <see cref="WebSearchUnavailableException"/> with a
/// person-readable, credential-free message when the search cannot run, and never scrape a
/// search engine's HTML results page.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>Short display name, such as "Brave Search".</summary>
    string Name { get; }

    Task<IReadOnlyList<WebSearchHit>> SearchAsync(WebSearchQuery query, CancellationToken ct);
}

/// <summary>
/// Host seam that supplies the search API key at call time (for example from the host's secret vault).
/// Null or empty means no key is configured. The value is passed straight to the provider's request
/// header and is never returned to a model, logged, or put in an error message.
/// </summary>
public interface IWebSearchKeySource
{
    Task<string?> GetKeyAsync(CancellationToken ct);
}

/// <summary>An <see cref="IWebSearchKeySource"/> over a delegate.</summary>
public sealed class DelegateWebSearchKeySource(Func<CancellationToken, Task<string?>> read) : IWebSearchKeySource
{
    public Task<string?> GetKeyAsync(CancellationToken ct) => read(ct);
}

/// <summary>The default key source for hosts that configure none: search reports that it is not configured.</summary>
public sealed class NoWebSearchKeySource : IWebSearchKeySource
{
    public Task<string?> GetKeyAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>The search could not run. The message is safe to show the model and the person: it never carries a credential.</summary>
public sealed class WebSearchUnavailableException(string message, bool notConfigured = false) : Exception(message)
{
    /// <summary>True when no key is configured (as opposed to a rejected key, a rate limit, or a network failure).</summary>
    public bool NotConfigured { get; } = notConfigured;
}

/// <summary>Options for <c>web.search</c> and <c>web.fetch</c>.</summary>
public sealed class WebToolsOptions
{
    public const string SectionName = "Coding:Web";

    /// <summary>Brave Search web endpoint.</summary>
    public string BraveEndpoint { get; set; } = "https://api.search.brave.com/res/v1/web/search";

    /// <summary>Results returned when the model does not ask for a count.</summary>
    public int DefaultCount { get; set; } = 8;

    /// <summary>Upper bound on results per search (Brave allows 20).</summary>
    public int MaxCount { get; set; } = 20;

    /// <summary>
    /// What the person should do when no search key is configured. Hosts set this to point at
    /// their own settings surface; the default names no host UI.
    /// </summary>
    public string MissingKeyGuidance { get; set; } =
        "Web search is not configured: no Brave Search API key is stored. Ask the person to add one in the app's web search settings "
        + "(keys come from https://brave.com/search/api/). Until then, use web.fetch for a URL you already know.";

    /// <summary>Default characters of readable text <c>web.fetch</c> returns.</summary>
    public int FetchDefaultChars { get; set; } = 20_000;

    /// <summary>Upper bound on characters of readable text <c>web.fetch</c> returns.</summary>
    public int FetchMaxChars { get; set; } = 100_000;

    /// <summary>Largest response body <c>web.fetch</c> reads before extracting text.</summary>
    public int FetchMaxBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Deadline for one search or fetch.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
