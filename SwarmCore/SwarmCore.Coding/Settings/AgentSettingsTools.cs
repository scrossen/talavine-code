using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Coding.Settings;

public sealed record AgentSettingsDocument(string Id, string Description, string Revision, JsonObject Values, JsonNode Schema, string Applies);
public sealed record SettingsReadInput(string? Query = null);
public sealed record SettingsUpdateInput(string Document, string Revision, JsonObject Changes);
public sealed record SettingsDefaultsInput;
public sealed record SettingsChangeResult(string Message, bool RequiresReload = false);

/// <summary>Hosts own persistence, credentials, authority and activation. Values must never contain credentials.</summary>
public interface IAgentSettingsHost
{
    Task<IReadOnlyList<AgentSettingsDocument>> ReadAsync(string? query, CancellationToken ct);
    Task<SettingsChangeResult> UpdateAsync(SettingsUpdateInput input, CancellationToken ct);
    Task<SettingsChangeResult> UseCurrentDefaultsAsync(CancellationToken ct);
}

public static class AgentSettingsTools
{
    public const string Guidance = "For requests to inspect or change this app's settings, use settings.read and settings.update, "
        + "rather than editing its files or source. Read first for the document schema and revision. Changes are partial: omitted fields stay unchanged; "
        + "null clears a nullable field. On conflict reread and reconsider the user's intended change. For 'make my current selections the default model, "
        + "provider and reasoning', use settings.use_current_defaults; it copies the active session's saved choice, including its thinking variant. "
        + "Never ask the person to paste credentials into chat. Settings use saved credential names/references; use the protected credential intake for new values. "
        + "Report when a saved change needs the workspace to reopen; never claim a running request has switched settings.";

    public static IReadOnlyList<ToolDescriptor> Create(IAgentSettingsHost host) =>
    [
        ToolDescriptor.Create<SettingsReadInput, IReadOnlyList<AgentSettingsDocument>>("settings.read", "settings",
            "Read app settings with values, editable JSON schemas, revision tokens and when changes apply. Query filters document id or description. "
            + "Includes appearance, permissions, defaults, memory, model sources, automatic selection, lanes, work limits, computer, attachments, connectors, hooks and search. Credentials are never returned.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly, (input, ct) => host.ReadAsync(input.Query, ct),
            keywords: ["settings", "preferences", "default model", "provider", "reasoning", "configure"]),
        ToolDescriptor.Create<SettingsUpdateInput, SettingsChangeResult>("settings.update", "settings",
            "Change app settings using a document id and revision from settings.read. Changes is a partial JSON object using the returned schema; nested objects merge, arrays replace, null clears nullable fields. "
            + "Only requested fields change. Stale revisions fail without writing. Use saved credential references, never raw passwords or keys.",
            ToolCostClass.Trivial, ToolSideEffect.WritesLocal, host.UpdateAsync,
            keywords: ["settings", "preferences", "change", "configure", "default", "permissions"]) with
        {
            // JsonObject implements IEnumerable; the generic schema generator otherwise describes an array.
            InputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"document":{"type":"string"},"revision":{"type":"string"},"changes":{"type":"object","additionalProperties":true,"description":"Partial settings object, using property names and types from settings.read. Omitted fields stay unchanged."}},"required":["document","revision","changes"],"additionalProperties":false}
                """)!.AsObject()
        },
        ToolDescriptor.Create<SettingsDefaultsInput, SettingsChangeResult>("settings.use_current_defaults", "settings",
            "Make the active session's selected provider route, model and reasoning/thinking variant the defaults for new sessions. "
            + "Uses trusted host session context, with no model-supplied session id. Existing sessions retain their choices.",
            ToolCostClass.Trivial, ToolSideEffect.WritesLocal, (_, ct) => host.UseCurrentDefaultsAsync(ct),
            keywords: ["settings", "current selections", "default model", "default provider", "default reasoning"])
    ];
}
