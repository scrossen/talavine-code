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

    /// <summary>
    /// Where each business-cycle stage's Today chip opens, keyed by the stage's enum name
    /// (<c>SwarmCore.Agent.Business.BusinessCycleStage</c>). Stages reuse the existing CRM list
    /// pages; "Operate and learn" has no page because its chip opens the Assistant digest.
    /// </summary>
    public static IReadOnlyList<CrmPageDestination> BusinessCycle { get; } =
    [
        new("Attract", "Website forms", "Review the website forms that bring in inquiries.", "/website-forms", "fa-globe"),
        new("Capture", "New inquiries", "Review new customer cases.", "/business/cases", "fa-inbox", true),
        new("QualifyNurture", "Quiet leads", "Follow up with leads that have gone quiet.", "/business/nurture", "fa-reply", true),
        new("ProposeClose", "Awaiting yes", "Review proposals waiting for the customer.", "/business/cases", "fa-file-signature", true),
        new("Onboard", "Getting started", "Review clients who are getting started.", "/business/cases", "fa-flag-checkered", true),
        new("Deliver", "In progress", "Review work in progress.", "/business/cases", "fa-person-digging", true),
        new("CommunicateSupport", "Needs reply", "Answer open support requests.", "/support/tickets", "fa-ticket"),
        new("InvoiceCollect", "Money owed", "Review issued invoices that are not yet paid.", "/business/cases", "fa-file-invoice-dollar", true),
        new("RetainReviewRefer", "Ask for review", "Ask happy clients for a review.", "/business/cases", "fa-star", true)
    ];

    /// <summary>The CRM page a business-cycle stage chip opens, or null when the stage has none.</summary>
    public static CrmPageDestination? ForBusinessCycleStage(string? stageName) =>
        BusinessCycle.FirstOrDefault(d => string.Equals(d.Id, stageName, StringComparison.Ordinal));
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
