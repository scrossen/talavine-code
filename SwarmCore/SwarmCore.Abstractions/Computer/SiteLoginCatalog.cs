namespace SwarmCore.Abstractions.Computer;

/// <summary>
/// Host-owned, metadata-only description of one site's login flow. A learned procedure can
/// never supply or expand these; each entry is a fixed authority over origin, login routes,
/// consent, and observation routes. Sites are data, not code: adding a Shopify store, Target,
/// or another retailer is a new entry (or a host-supplied profile), not a new method.
/// </summary>
public sealed record SiteLoginProfile
{
    public required string SiteId { get; init; }
    public required string AllowedOrigin { get; init; }
    public required string StartUrl { get; init; }
    public IReadOnlyList<BrowserProcedureRoute> LoginRoutes { get; init; } = [];
    public IReadOnlyList<BrowserProcedureRoute> ObservationRoutes { get; init; } = [];
    public BrowserProcedureConsent? Consent { get; init; }

    public BrowserProcedureScope ToScope(bool consentAuthorized) => new()
    {
        AllowedOrigin = AllowedOrigin,
        StartUrl = StartUrl,
        LoginRoutes = LoginRoutes,
        ObservationRoutes = ObservationRoutes,
        Consent = Consent is null ? null : Consent with { Authorized = consentAuthorized }
    };
}

/// <summary>
/// Data-driven catalog of supported site logins. Lookup is by site id or origin; the shipped
/// set is seed data that hosts may extend with their own profiles. The adaptive sign-in engine
/// consumes any <see cref="SiteLoginProfile"/> the same way it consumes X today.
/// </summary>
public static class SiteLoginCatalog
{
    public static SiteLoginProfile X { get; } = new()
    {
        SiteId = "x",
        AllowedOrigin = "https://x.com",
        StartUrl = "https://x.com/i/flow/login",
        LoginRoutes =
        [
            new("/i/flow/login"),
            new("/i/jf/onboarding/web") { RequiredQuery = new Dictionary<string, string> { ["mode"] = "login" } }
        ],
        ObservationRoutes = [new("/home"), new("/search")],
        Consent = new(
            "By continuing, you agree to our Terms of Service, Privacy Policy and Cookie Use.",
            [
                new("Terms of Service", "https://x.com/tos"),
                new("Privacy Policy", "https://x.com/privacy"),
                new("Cookie Use", "https://help.x.com/rules-and-policies/twitter-cookies")
            ],
            false)
    };

    public static SiteLoginProfile Walmart { get; } = new()
    {
        SiteId = "walmart",
        AllowedOrigin = "https://www.walmart.com",
        StartUrl = "https://www.walmart.com/account/login",
        LoginRoutes =
        [
            new("/account/login"),
            new("/account/signin"),
            new("/signin")
        ],
        ObservationRoutes = [new("/account"), new("/"), new("/cart"), new("/grocery")],
        Consent = new(
            "By signing in you agree to the Walmart Terms of Use and Privacy Notice.",
            [
                new("Terms of Use", "https://www.walmart.com/help/article/terms-of-use/3b75080af40340d6bbd19f41e1db7c74"),
                new("Privacy Notice", "https://corporate.walmart.com/privacy-security/walmart-privacy-notice")
            ],
            false)
    };

    public static IReadOnlyList<SiteLoginProfile> All { get; } = [X, Walmart];

    public static SiteLoginProfile? Get(string siteId) =>
        All.FirstOrDefault(profile => string.Equals(profile.SiteId, siteId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The catalog site whose signed-in session hosts <paramref name="url"/>: an https URL on the
    /// site's origin whose path (and required query values) match one of its observation routes.
    /// </summary>
    public static SiteLoginProfile? ForObservationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || ForOrigin(url) is not { } site) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .GroupBy(pair => Uri.UnescapeDataString(pair[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "").ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return site.ObservationRoutes.Any(route => string.Equals(route.Path, uri.AbsolutePath, StringComparison.Ordinal)
            && route.RequiredQuery.All(pair => query.TryGetValue(pair.Key, out var values) && values.Length == 1 && values[0] == pair.Value))
            ? site
            : null;
    }

    public static SiteLoginProfile? ForOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var requested)
            || !string.IsNullOrEmpty(requested.UserInfo)) return null;

        // Resolve page URLs using the configured origin. Navigation still
        // starts from metadata; caller paths cannot expand browser authority.
        return All.FirstOrDefault(profile =>
            Uri.TryCreate(profile.AllowedOrigin, UriKind.Absolute, out var configured)
            && string.Equals(requested.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(requested.IdnHost, configured.IdnHost, StringComparison.OrdinalIgnoreCase)
            && requested.Port == configured.Port);
    }
}
