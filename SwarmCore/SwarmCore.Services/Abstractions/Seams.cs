namespace SwarmCore.Services.Abstractions;

/// <summary>Persistence for service definitions. Host supplies an implementation (SQLite provided).</summary>
public interface IServiceStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task SaveAsync(ServiceDefinition service, CancellationToken ct = default);
    Task<ServiceDefinition?> GetAsync(string id, CancellationToken ct = default);
    Task<ServiceDefinition?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceDefinition>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// Stores service credentials apart from their definitions. Secrets are keyed by
/// name (<see cref="ServiceDefinition.CredentialRef"/>) and are never serialized
/// into a definition or shown to a model — only the invoking adapter reads them.
/// </summary>
public interface ICredentialStore
{
    Task SetAsync(string name, string secret, CancellationToken ct = default);
    Task<string?> GetAsync(string name, CancellationToken ct = default);
    Task<bool> HasAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Names of every stored credential — names only, never values. Lets a
    /// caller that missed an exact-name lookup tell the model what names DO
    /// exist (names are already model-visible via service definitions), so a
    /// misremembered reference is a one-step correction instead of a dead end.
    /// Default: empty (stores that predate this member keep compiling; they
    /// just can't offer suggestions).
    /// </summary>
    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Hosts this credential is bound to: the only destinations a general-purpose request tool
    /// (<c>http.request</c>) may send it to. Each entry is a host or a URL; a subdomain of a bound host
    /// also matches. Empty when the store records no binding for the name, and an unbound credential is
    /// refused by such tools (fail closed). Adapters that build the request themselves, such as an HTTP
    /// service definition, are bound by their own configured base URL instead.
    /// </summary>
    Task<IReadOnlyList<string>> GetBoundHostsAsync(string name, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <param name="AllowRedirects">False when the caller enforces a host allowlist: an auto-followed
/// redirect would move the request to a host the gate never checked, so gated calls must see the
/// 3xx themselves (following manually re-enters the gate with the new URL).</param>
public sealed record ServiceHttpRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null,
    bool AllowRedirects = true,
    byte[]? BodyBytes = null,
    string? ContentType = null);

public sealed record ServiceHttpResponse(int Status, string Body, bool Ok);

/// <summary>HTTP transport seam for the Http service adapter (host supplies an HttpClient-backed impl).</summary>
public interface IServiceHttpClient
{
    Task<ServiceHttpResponse> SendAsync(ServiceHttpRequest request, CancellationToken ct = default);
}
