namespace SwarmCore.Abstractions.Crm;

public sealed record CrmPageDestination(
    string Id, string Title, string Description, string Path, string IconCss,
    bool RequiresBusinessReadiness = false);

/// <summary>Desktop destinations supported by the existing CRM web routes.</summary>
public static class CrmPageCatalog
{
    public static IReadOnlyList<CrmPageDestination> Common { get; } =
    [
        new("dashboard", "CRM overview", "Review your connected workspace.", "/dashboard", "fa-chart-line"),
        new("contacts", "Contacts & companies", "Find people, companies and product registrants.", "/crm/contacts", "fa-address-book"),
        new("deals", "Deals", "Review sales and opportunities.", "/crm/deals", "fa-handshake"),
        new("tasks", "My tasks", "Open your assigned work and follow-ups.", "/CRM/MyTasks", "fa-list-check"),
        new("tickets", "Support tickets", "Review customer support requests.", "/support/tickets", "fa-ticket"),
        new("activities", "Activity history", "Review CRM notes and logged conversations.", "/crm/activities", "fa-clock-rotate-left")
    ];

    public static IReadOnlyList<CrmPageDestination> Business { get; } =
    [
        new("setup", "Business setup", "Configure the connections and limits for your business.", "/business/setup", "fa-gear", true),
        new("operations", "Business work", "Review the work your business is doing.", "/business/operations", "fa-briefcase", true),
        new("prospects", "Leads", "Research and manage prospects.", "/business/setup", "fa-users", true),
        new("cases", "Sales & delivery", "Follow sales, delivery and customer outcomes.", "/business/cases", "fa-box", true),
        new("nurture", "Follow-up & replies", "Review customer follow-up and replies.", "/business/nurture", "fa-reply", true)
    ];
}

public enum CrmLinkOpenFailure
{
    None, NotConnected, ConnectionChanged, ReconnectRequired, InvalidDestination,
    HandoffUnavailable, InvalidHandoff, BrowserUnavailable, Cancelled
}

/// <summary>A credential-free outcome suitable for presentation by any desktop host.</summary>
public sealed record CrmLinkOpenResult(bool Opened, CrmLinkOpenFailure Failure, string? Message = null)
{
    public bool RequiresReconnect => Failure is CrmLinkOpenFailure.NotConnected or CrmLinkOpenFailure.ReconnectRequired;
    public static CrmLinkOpenResult Success { get; } = new(true, CrmLinkOpenFailure.None);
    public static CrmLinkOpenResult Fail(CrmLinkOpenFailure failure, string message) => new(false, failure, message);
}
