namespace SwarmCode.App.Services;

/// <summary>The bundled interface is the only origin allowed into the in-process renderer.</summary>
public static class NativeWebContentPolicy
{
    public static Uri Origin { get; } = new("app://swarmcode/");

    public static bool IsAppUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Origin.Scheme && uri.Host == Origin.Host
            && uri.Port == Origin.Port && uri.UserInfo.Length == 0;
    public static bool CanNavigateInView(string? url, bool isMainFrame)
        => IsAppUrl(url) || (!isMainFrame && url is "about:blank" or "about:srcdoc");
}
