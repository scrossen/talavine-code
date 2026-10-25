using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;

namespace SwarmCode.Hosting.Apps;

public sealed record AppsListInput;
public sealed record AppsConnectInput(string Target, string? Name = null, string? TokenSecret = null, string? Scope = null);
public sealed record AppsFindInput(string App, string? Query = null, int Skip = 0);
public sealed record AppsCallInput(string App, string Tool, JsonObject? Input, string Request, string? OperationId = null);
public sealed record AppsOperationIdInput(string Deliverable, string App, string Effect);
public sealed record AppsAskInput(string App, string Request, string? ConversationId = null);

/// <summary>
/// The <c>apps.*</c> tools SwarmCode's own lanes use to operate the person's apps (Talavine, the CRM, any MCP server)
/// by prompt. The native-agent bridge offers the same operations as <c>apps_*</c> MCP tools.
/// </summary>
public static class ConnectedAppsTools
{
    public const string Guidance =
        "Operating the person's apps: when a request is about Talavine (email, inbox, rules, appearance, agents, model choice, "
        + "\"why did Talavine do X\") or their CRM (contacts, companies, deals, tasks, tickets, settings), do it in the app itself, "
        + "not by editing source code or driving its window: apps.list shows what is running or connected; apps.connect connects "
        + "an app by name (\"talavine\") or a server URL (a CRM's https://host/mcp; the person approves in their browser); "
        + "apps.find_tools finds what the app can do; apps.call_tool runs one tool with the person's request; apps.ask asks the "
        + "app's own assistant, which is best for why-questions about what the app did. Read the source only when the person asks "
        + "how the app is built.\n" + DeliverableHandback.Guidance;

    public static IReadOnlyList<ToolDescriptor> Create(ConnectedApps apps) =>
    [
        ToolDescriptor.Create<AppsListInput, string>("apps.list", "apps",
            "List the person's apps Talavine Code can operate: apps running on this computer (Talavine) and servers connected by URL (a CRM).",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            async (_, ct) => JsonSerializer.Serialize(new { apps = await apps.ListAsync(ct) }),
            keywords: ["apps", "talavine", "crm", "connected", "list"]),
        ToolDescriptor.Create<AppsConnectInput, string>("apps.connect", "apps",
            "Connect an app so Talavine Code can operate it: target is an app name running on this computer (\"talavine\") or a server's MCP URL "
            + "(\"https://crm.example.com/mcp\"). A URL opens the person's browser to approve; tokenSecret instead names a stored API key.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (input, ct) => apps.ConnectAsync(input.Target, input.Name, input.TokenSecret, input.Scope, ct),
            keywords: ["connect", "talavine", "crm", "app", "sign in"], dataAccess: ToolDataAccessClass.UnclassifiedExternal),
        ToolDescriptor.Create<AppsFindInput, string>("apps.find_tools", "apps",
            "Find what a connected app can do, in plain words (\"create contact\", \"dark mode\", \"inbox rule\"). Returns tool ids, whether each changes data, and the input schema.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => apps.FindToolsAsync(input.App, input.Query, input.Skip, ct),
            keywords: ["find", "tools", "talavine", "crm"], dataAccess: ToolDataAccessClass.UnclassifiedExternal),
        ToolDescriptor.Create<AppsCallInput, string>("apps.call_tool", "apps",
            "Run one of a connected app's tools for the person, through that app's own checks and activity log. Pass the exact tool id from apps.find_tools, an input matching its schema, and the person's request in their words.",
            ToolCostClass.Moderate, ToolSideEffect.ExternalSend,
            (input, ct) => apps.CallToolAsync(input.App, input.Tool, input.Input ?? new JsonObject(), input.Request, input.OperationId, ct),
            keywords: ["call", "run", "talavine", "crm", "create", "update"], dataAccess: ToolDataAccessClass.UnclassifiedExternal),
        ToolDescriptor.Create<AppsOperationIdInput, string>("apps.operation_id", "apps",
            "Get the stable operation_id for one effect of a finished deliverable in one app (for example deliverable \"landing page\", app \"talavine\", effect \"task\"). "
            + "Pass it as operation_id to apps.call_tool; the same inputs always return the same id, so a retry never duplicates a task or note.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, _) => Task.FromResult(DeliverableHandback.OperationId(input.Deliverable, input.App, input.Effect).ToString()),
            keywords: ["operation", "id", "idempotent", "deliverable", "hand-back", "retry"]),
        ToolDescriptor.Create<AppsAskInput, string>("apps.ask", "apps",
            "Ask a connected app's own assistant (Talavine, the CRM) in plain words. Best for \"why did it do X\", what its records show, or multi-step work it knows how to do.",
            ToolCostClass.Expensive, ToolSideEffect.ExternalSend,
            (input, ct) => apps.AskAsync(input.App, input.Request, input.ConversationId, ct),
            keywords: ["ask", "why", "explain", "talavine", "crm", "assistant"], dataAccess: ToolDataAccessClass.UnclassifiedExternal),
    ];
}

/// <summary>Publishes the <c>apps.*</c> tools into the workspace catalog when the workspace host starts.</summary>
public sealed class ConnectedAppsToolRegistration(SwarmCore.Agent.Abstractions.ICapabilityCatalog catalog, ConnectedApps apps)
    : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in ConnectedAppsTools.Create(apps)) catalog.Register(tool);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
