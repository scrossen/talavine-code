namespace SwarmCore.Abstractions.Computer;

/// <summary>
/// Non-secret routing identity for one browser-profile installation on its owning host.
/// This is not a login token, an authenticated website identity, or permission to use a site.
/// Routing to another host requires that host's separately authenticated transport.
/// </summary>
public sealed record BrowserProfileConnection
{
    public required string ConnectionId { get; init; }
    public required string Transport { get; init; }
    public bool CanEditPages { get; init; }

    public static string? NormalizeId(string? value)
        => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
            ? id.ToString("D") : null;
}
