namespace SwarmCore.Abstractions.Crm;

/// <summary>
/// Stable CRM record kinds shared by every product that talks about a Talavine CRM record:
/// the desktop email client (citations, "Open in CRM" links, notifications), Vibes, and the
/// CRM itself (the <c>Link</c> fields it returns). Lower-case, stable on the wire.
/// </summary>
public static class CrmRecordKinds
{
    public const string Contact = "contact";
    public const string Company = "company";
    public const string Deal = "deal";
    public const string Ticket = "ticket";
    public const string Note = "note";
    public const string Email = "email";
    public const string Task = "task";
    public const string Tickler = "tickler";
    public const string ScheduledCall = "scheduled_call";
    public const string Meeting = "meeting";
    public const string Decision = "decision";
    public const string Alert = "alert";

    public static readonly IReadOnlyList<string> All =
    [
        Contact, Company, Deal, Ticket, Note, Email, Task, Tickler, ScheduledCall, Meeting, Decision, Alert
    ];
}

/// <summary>
/// One place that knows how a CRM record kind + id maps to (a) the citation reference string
/// used across Talavine (<c>crm:deal:42</c>) and (b) the relative CRM web route the user should
/// land on when they click "Open in CRM". Keep the route table in sync with the CRM's
/// <c>@page</c> directives; the CRM's own read API returns these same relative links so the two
/// never drift far, and callers should prefer a server-supplied <c>Link</c> when one is present.
/// </summary>
public static class CrmRecordLinks
{
    /// <summary>Prefix of every CRM citation reference (<c>crm:&lt;kind&gt;:&lt;id&gt;</c>).</summary>
    public const string ReferencePrefix = "crm:";

    /// <summary>Build the canonical citation reference for a record, e.g. <c>crm:deal:42</c>.</summary>
    public static string Reference(string kind, string id) =>
        $"{ReferencePrefix}{Normalize(kind)}:{id?.Trim()}";

    /// <summary>
    /// Parse a citation reference back into (kind, id). Returns false for anything that is not a
    /// well-formed <c>crm:&lt;kind&gt;:&lt;id&gt;</c> string.
    /// </summary>
    public static bool TryParseReference(string? reference, out string kind, out string id)
    {
        kind = string.Empty;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var value = reference.Trim();
        if (!value.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = value[ReferencePrefix.Length..];
        var sep = rest.IndexOf(':');
        if (sep <= 0 || sep == rest.Length - 1) return false;
        kind = Normalize(rest[..sep]);
        id = rest[(sep + 1)..].Trim();
        return kind.Length > 0 && id.Length > 0;
    }

    /// <summary>
    /// The relative CRM web route for a record (no leading host, leading slash included), or
    /// <c>null</c> when the kind has no dedicated page. Kinds without a record page fall back to
    /// their list page so a click still lands somewhere sensible.
    /// </summary>
    public static string? RelativePath(string kind, string? id)
    {
        var k = Normalize(kind);
        var hasId = !string.IsNullOrWhiteSpace(id) && long.TryParse(id.Trim(), out _);
        return k switch
        {
            CrmRecordKinds.Contact => hasId ? $"/crm/contact/{id!.Trim()}" : "/crm/contacts",
            CrmRecordKinds.Company => hasId ? $"/crm/company/{id!.Trim()}" : "/crm/contacts",
            CrmRecordKinds.Deal => hasId ? $"/crm/deal/{id!.Trim()}" : "/crm/deals",
            CrmRecordKinds.Ticket => hasId ? $"/support/tickets/{id!.Trim()}" : "/support/tickets",
            CrmRecordKinds.Task => hasId ? $"/CRM/Tasks/{id!.Trim()}" : "/CRM/MyTasks",
            CrmRecordKinds.Tickler => hasId ? $"/CRM/Ticklers/{id!.Trim()}" : "/CRM/MyTicklers",
            CrmRecordKinds.Meeting => hasId ? $"/CRM/Meetings/{id!.Trim()}" : "/CRM/MyMeetings",
            CrmRecordKinds.ScheduledCall => "/CRM/MyMeetings",
            CrmRecordKinds.Decision => "/decisions",
            CrmRecordKinds.Alert => "/support/alerts",
            CrmRecordKinds.Note => "/crm/activities",
            CrmRecordKinds.Email => "/crm/activities",
            "campaign" => int.TryParse(id, out var campaignId) && campaignId > 0
                ? $"/marketing/trickle-campaigns/{campaignId}" : "/marketing/trickle-campaigns",
            "template" => "/marketing/mailing-list-templates",
            _ => null,
        };
    }

    /// <summary>Relative route for a citation reference string, or null when it cannot be parsed.</summary>
    public static string? RelativePathForReference(string? reference) =>
        TryParseReference(reference, out var kind, out var id) ? RelativePath(kind, id) : null;

    /// <summary>
    /// Join a tenant base URL and a relative (or absolute) link into an absolute URL. Absolute
    /// links are returned unchanged; a null/empty link yields the base URL itself.
    /// </summary>
    public static string Absolute(string baseUrl, string? link)
    {
        var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var l = link?.Trim();
        if (string.IsNullOrWhiteSpace(l)) return root;
        if (l.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return l;
        }
        return $"{root}/{l.TrimStart('/')}";
    }

    private static string Normalize(string? kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();
}
