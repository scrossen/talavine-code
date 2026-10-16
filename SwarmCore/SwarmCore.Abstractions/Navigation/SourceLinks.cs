namespace SwarmCore.Abstractions.Navigation;

/// <summary>Opaque source coordinates, never authority. Hosts must authorize against their active profile.</summary>
public sealed record SourceLink(string Id, string Kind, Guid RecordId, Guid? AccountId = null,
    Guid? MessageId = null, Guid? ConnectionId = null)
{
    public string LocalRoute => "/sources/" + Id + SourceLinks.ConnectionQuery(ConnectionId);
    public string AppUrl => "talavine://source/" + Id + SourceLinks.ConnectionQuery(ConnectionId);
    public string? DesktopRoute => Kind switch
    {
        "message" or "attachment" => $"/message/{AccountId:D}/{MessageId:D}" +
            (Kind == "attachment" ? $"?attachment={RecordId:D}" : ""),
        "document" => $"/documents/{RecordId:D}", "note" => $"/notes/{RecordId:D}",
        "contact" => $"/contacts?contact={RecordId:D}", "task" => $"/tasks?task={RecordId:D}",
        "calendar" => $"/calendar?event={RecordId:D}", "matter" => $"/matters/{RecordId:D}",
        _ => null
    };
}

public static class SourceLinks
{
    /// <summary>Strict normal message coordinates. The host must still check its current profile and account authority.</summary>
    public static bool TryReadMessageAppUri(string? value, out SourceLink link)
    {
        link = null!;
        const string prefix = "talavine://message/";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = value[prefix.Length..].Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out var account)
            || !Guid.TryParseExact(parts[1], "D", out var message) || account == Guid.Empty || message == Guid.Empty
            || value != $"{prefix}{account:D}/{message:D}") return false;
        return TryReadId(MessageId(account, message), out link);
    }

    public static readonly string[] RecordKinds = ["document", "note", "contact", "task", "calendar", "matter", "memory", "fact"];
    public static string MessageId(Guid account, Guid message) => $"{account:N}.{message:N}";
    public static string AttachmentId(Guid account, Guid message, Guid attachment) => $"attachment.{account:N}.{message:N}.{attachment:N}";
    public static string ConnectionQuery(Guid? connection) => connection is { } id ? $"?connection={id:N}" : "";

    /// <summary>
    /// Adapts a typed evidence record to the same coordinates used by source
    /// links. A record Guid alone never implies a message or profile authority.
    /// </summary>
    public static bool TryReadRecordReference(string kind, string? sourceId, string? recordId, out SourceLink link)
    {
        link = null!;
        kind = kind.ToLowerInvariant();
        if (!RecordKinds.Contains(kind)) return false;

        Guid? record = null;
        if (!string.IsNullOrWhiteSpace(recordId))
        {
            if (!Guid.TryParse(recordId, out var parsed) || parsed == Guid.Empty) return false;
            record = parsed;
        }
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            var prefix = kind + ":";
            if (!sourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(sourceId[prefix.Length..], out var parsed) || parsed == Guid.Empty
                || (record.HasValue && record != parsed)) return false;
            record = parsed;
        }
        return record is { } id && TryReadId($"{kind}.{id:N}", out link);
    }

    public static bool TryReadId(string? id, out SourceLink link)
    {
        link = null!;
        var parts = (id ?? "").Split('.');
        static bool Read(string part, out Guid key) => Guid.TryParseExact(part, "N", out key) && key != Guid.Empty;
        if (parts.Length == 2 && Read(parts[0], out var account) && Read(parts[1], out var message))
            link = new(id!, "message", message, account, message);
        else if (parts.Length == 2 && RecordKinds.Contains(parts[0]) && Read(parts[1], out var record))
            link = new(id!, parts[0], record);
        else if (parts.Length == 4 && parts[0] == "attachment" && Read(parts[1], out account)
            && Read(parts[2], out message) && Read(parts[3], out var attachment))
            link = new(id!, "attachment", attachment, account, message);
        return link is not null;
    }

    public static bool IsSourceUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsSourceUri(uri);
    public static bool IsSourceUri(Uri uri) =>
        (uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.Host.ToLowerInvariant() is "talavine.com" or "www.talavine.com" or "swarmmarshal.com" or "www.swarmmarshal.com"
            && uri.AbsolutePath.StartsWith("/sources/", StringComparison.Ordinal))
        || (uri.Scheme == "talavine" && uri.Host == "source");

    public static bool TryReadUri(Uri uri, out SourceLink link)
    {
        link = null!;
        if (!IsSourceUri(uri) || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) return false;
        var id = uri.Scheme == "https" ? uri.AbsolutePath["/sources/".Length..] : uri.AbsolutePath.TrimStart('/');
        if (!TryReadId(id, out link)) return false;
        if (uri.Query.Length == 0) return true;
        var query = uri.Query.TrimStart('?').Split('=');
        if (query.Length != 2 || query[0] != "connection" || !Guid.TryParse(query[1], out var connection) || connection == Guid.Empty)
        { link = null!; return false; }
        link = link with { ConnectionId = connection };
        return true;
    }

    public static string SafeReturnRoute(string? route)
    {
        if (route is not null && route.StartsWith("/sources/", StringComparison.Ordinal)
            && Uri.TryCreate("https://talavine.com" + route, UriKind.Absolute, out var uri) && TryReadUri(uri, out var link)) return link.LocalRoute;
        if (route is not null && route.StartsWith("/email/", StringComparison.Ordinal))
        {
            var parts = route.Split('/');
            if (parts.Length == 4 && Guid.TryParse(parts[2], out var account) && account != Guid.Empty
                && Guid.TryParse(parts[3], out var message) && message != Guid.Empty) return $"/email/{account:D}/{message:D}";
        }
        return "/chat";
    }

    public static string PublicUrl(string origin, string id, Guid? connection = null)
    {
        if (!TryReadId(id, out _) || connection == Guid.Empty) throw new ArgumentException("Invalid source coordinate.");
        return origin.TrimEnd('/') + "/sources/" + id + ConnectionQuery(connection);
    }

    // A connection id is a routing constraint, not a credential. Legacy links have no constraint;
    // their host must still look up records only in the locally authorized/paired profile.
    public static bool MatchesConnection(SourceLink link, Guid? activeConnection) =>
        link.ConnectionId is null || link.ConnectionId == activeConnection;
}
