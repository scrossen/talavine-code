using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Computer;
using SwarmCore.Coding.Files;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Settings;

namespace SwarmCode.Hosting.Settings;

/// <summary>The host adapter for the shared settings protocol. No file paths or session ids come from the model.</summary>
public sealed partial class SwarmCodeSettingsHost(SwarmCodeDataPaths paths, AppPreferencesStore preferences,
    WorkspaceSessionCatalog sessions, ICurrentTurnContextAccessor turns, ICodingLaneSource laneSource, SwarmCore.Secrets.ISecretVault vault, SwarmCodeApiKeyProvider apiKeys, SettingsActivation activation) : IAgentSettingsHost
{
    private static readonly JsonSerializerOptions Json = new(SettingsJsonFile.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Spec(string Id, string Description, string[] Path, Type Type, object Default);
    private static readonly Spec[] Specs =
    [
        new("work", "Work limits: timeout (TimeSpan), tools, iterations, reported tokens", ["Coding", "ExecutionLimits"], typeof(CodingExecutionLimits), new CodingExecutionLimits()),
        new("verification", "Completion checks and repair attempts", ["Coding", "Acceptance"], typeof(CodingAcceptanceOptions), new CodingAcceptanceOptions()),
        new("computer", "Chrome profiles and browser/computer control", ["Coding", "Computer"], typeof(CodingComputerOptions), new CodingComputerOptions()),
        new("attachments", "Attachment byte, count and extracted-text limits (zero unlimited)", ["Coding", "AttachmentLimits"], typeof(CodingAttachmentOptions), new CodingAttachmentOptions()),
        new("fallback", "Fallback model discovery, response memory and price limits", ["Coding", "FallbackDiscovery"], typeof(CodingFallbackDiscoveryOptions), new CodingFallbackDiscoveryOptions()),
        new("lanes", "Provider routes, priority, default lane and model handoff", ["CodingLanes"], typeof(CodingLaneOptions), new CodingLaneOptions()),
        new("permissions", "Always allow and deny tool rules", ["Coding", "Permissions"], typeof(CodingPermissionRules), new CodingPermissionRules()),
        new("connectors", "Allow Codex app and claude.ai account connectors", ["Coding", "NativeAgents"], typeof(ConnectorSettings), new ConnectorSettings()),
        new("hooks", "Custom commands run at lifecycle events", ["Coding", "Hooks"], typeof(SwarmCore.Coding.Extensions.CodingHooksOptions), new SwarmCore.Coding.Extensions.CodingHooksOptions()),
        new("memory", "Installation personal-memory learning and recall options", ["PersonalMemory"], typeof(SwarmCore.PersonalMemory.PersonalMemoryOptions), new SwarmCore.PersonalMemory.PersonalMemoryOptions()),
#if DEBUG
        new("self-improvement", "Developer idle improvement cycles and checks", ["Coding", "SelfImprovement"], typeof(SwarmCore.Coding.SelfImprovement.SelfImprovementOptions), new SwarmCore.Coding.SelfImprovement.SelfImprovementOptions()),
#endif
    ];
    private static readonly string[] PreferenceKeys = ["theme", "permissionMode", "handOffOnRefusal", "defaultLane", "defaultModel", "showReasoning", "sidebarAllRepositories", "zoom", "personalMemoryEnabled", "editorCommand", "selfImprovementPaused"];

    public Task<IReadOnlyList<AgentSettingsDocument>> ReadAsync(string? query, CancellationToken ct) => Task.Run<IReadOnlyList<AgentSettingsDocument>>(() =>
    {
        var result = new List<AgentSettingsDocument>();
        bool Matches(string id, string description) => string.IsNullOrWhiteSpace(query) || (id + " " + description).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
        const string prefsDescription = "Appearance, theme, text size (zoom), permissions, default provider route/model/reasoning variant, reasoning display, editor and personal memory";
        if (Matches("preferences", prefsDescription))
        {
            var prefsRevision = SettingsJsonFile.Revision(paths.PreferencesPath);
            var values = JsonSerializer.SerializeToNode(preferences.Current, Json)!.AsObject();
            var schema = Json.GetJsonSchemaAsNode(typeof(AppPreferences)).AsObject();
            foreach (var key in values.Select(p => p.Key).Where(k => !PreferenceKeys.Contains(k)).ToArray()) values.Remove(key);
            if (schema["properties"] is JsonObject props)
            {
                foreach (var key in props.Select(p => p.Key).Where(k => !PreferenceKeys.Contains(k)).ToArray()) props.Remove(key);
                // These are finite user choices, not arbitrary strings. Retain the
                // legacy null permission mode so an unchanged preference file still
                // means "migrate from requireApproval" rather than losing its default.
                ((JsonObject)props["theme"]!)["enum"] = new JsonArray(JsonValue.Create("system"), JsonValue.Create("light"), JsonValue.Create("dark"));
                ((JsonObject)props["permissionMode"]!)["enum"] = new JsonArray(JsonValue.Create((string?)null), JsonValue.Create("auto"), JsonValue.Create("ask-risky"), JsonValue.Create("ask"));
            }
            result.Add(new("preferences", prefsDescription, prefsRevision, values, schema, "Immediately; defaults apply to new sessions. Model IDs include @reasoning variants when supported."));
        }
        // Capture revision before reading values; a concurrent writer makes a later update fail safely.
        var revision = SettingsJsonFile.Revision(paths.UserConfigPath);
        var config = SwarmCodeHostComposer.BuildConfiguration(paths);
        foreach (var spec in Specs.Where(s => Matches(s.Id, s.Description)))
        {
            var value = Activator.CreateInstance(spec.Type)!;
            config.GetSection(string.Join(':', spec.Path)).Bind(value);
            var values = JsonSerializer.SerializeToNode(value, spec.Type, Json)!.AsObject();
            var schema = Json.GetJsonSchemaAsNode(spec.Type);
            if (spec.Id == "lanes") { values.Remove("handOffOnRefusal"); schema["properties"]?.AsObject().Remove("handOffOnRefusal"); }
            result.Add(new(spec.Id, spec.Description, revision, values, schema, activation.Message));
        }
        if (Matches("available-models", "Available provider routes and active session model"))
        {
            var session = turns.Current is { } turn ? sessions.GetAsync(turn.ConversationId, ct).GetAwaiter().GetResult() : null;
            result.Add(new("available-models", "Read-only available provider routes and the active session's saved model/reasoning variant. Change defaults under preferences, or copy the session with settings.use_current_defaults.", "read-only",
                new JsonObject { ["routes"] = JsonSerializer.SerializeToNode(laneSource.GetLanes().Lanes.Select(l => new { route = l.Name, provider = l.Provider, model = l.Model }), Json),
                    ["currentRoute"] = session?.Selection?.Route ?? session?.LaneName, ["currentModel"] = session?.Selection?.Model },
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, "Read-only context."));
        }
        if (Matches("model-sources", "Providers, local models, endpoints and model classifications"))
        {
            var file = Path.Combine(paths.RootDirectory, "model-sources.json");
            var rev = SettingsJsonFile.Revision(file);
            var value = new ModelSourcesStore(paths).Read();
            result.Add(Document("model-sources", "Providers, local models, endpoints and model classifications", rev, value, activation.Message));
        }
        if (Matches("automatic-selection", "Automatic model selection and decision service"))
        {
            var file = Path.Combine(paths.RootDirectory, "decision-selection.json");
            var rev = SettingsJsonFile.Revision(file);
            var value = new DecisionSettingsStore(paths).ReadAsync(ct).GetAwaiter().GetResult();
            result.Add(Document("automatic-selection", "Automatic model selection and decision service (saved credential name only)", rev, value, "Next eligible prompt; no restart needed."));
        }
        if (Matches("connections", "Connected data MCP servers")) result.Add(ReadConnections());
        if (Matches("credentials", "API keys, web search key and saved passwords")) result.Add(ReadCredentials());
        return result;
    }, ct);

    private static AgentSettingsDocument Document<T>(string id, string description, string revision, T value, string applies)
    {
        var values = JsonSerializer.SerializeToNode(value, Json)!.AsObject(); values.Remove("revision");
        var schema = Json.GetJsonSchemaAsNode(typeof(T)).AsObject(); schema["properties"]?.AsObject().Remove("revision");
        return new(id, description, revision, values, schema, applies);
    }

    public async Task<SettingsChangeResult> UpdateAsync(SettingsUpdateInput input, CancellationToken ct)
    {
        await RequireUserAsync(ct);
        return await ApplyUserEditAsync(input, ct);
    }

    /// <summary>Trusted desktop Save-button adapter. The host owns direct user
    /// interaction; agent tools must use UpdateAsync and its conversation gate.
    /// Settings can be configured before any repository or session exists.</summary>
    public async Task<SettingsChangeResult> ApplyUserEditAsync(SettingsUpdateInput input, CancellationToken ct)
    {
        var snapshot = (await ReadAsync(null, ct)).SingleOrDefault(s => s.Id == input.Document)
            ?? throw new InvalidOperationException("Unknown settings document. Use settings.read.");
        if (snapshot.Revision != input.Revision) throw new InvalidOperationException("Settings changed or were deleted. Read again before saving.");
        if (input.Changes.Count == 0) return new("No settings changed.");
        ValidateKeys(input.Changes, snapshot.Schema);
        var merged = snapshot.Values.DeepClone().AsObject(); SettingsJsonFile.Merge(merged, input.Changes);
        if (input.Document == "preferences")
        {
            var p = merged.Deserialize<AppPreferences>(Json)!;
            ValidatePreferences(p, input.Changes);
            await Task.Run(() => SettingsJsonFile.Mutate(paths.PreferencesPath, root =>
            {
                SettingsJsonFile.Merge(root, input.Changes);
                if (input.Changes.ContainsKey("permissionMode"))
                { var current = root.Deserialize<AppPreferences>(Json)!; current.SetMode(p.Mode); SettingsJsonFile.Set(root, "requireApproval", JsonValue.Create(current.RequireApproval)); }
                return true;
            }, input.Revision), ct);
            // Notify the shared app preference instance without another write.
            preferences.NotifyChanged();
            return new("Preferences saved. New sessions use the saved defaults; existing sessions keep their choices.");
        }
        if (input.Document == "model-sources")
        {
            var store = new ModelSourcesStore(paths); var old = store.Read();
            var value = merged.Deserialize<ModelSourcesDocument>(Json)!;
            // The store validates and conditionally writes under its own lease.
            if (SettingsJsonFile.Revision(Path.Combine(paths.RootDirectory, "model-sources.json")) != input.Revision) throw new InvalidOperationException("Model sources changed. Read again.");
            await store.SaveAsync(value with { Revision = old.Revision }, ct);
            activation.RequestReload(); return new(activation.Message, true);
        }
        if (input.Document == "automatic-selection")
        {
            var store = new DecisionSettingsStore(paths); var old = await store.ReadAsync(ct);
            var value = merged.Deserialize<DecisionSelectionSettings>(Json)!;
            ValidateDecision(value);
            if (SettingsJsonFile.Revision(Path.Combine(paths.RootDirectory, "decision-selection.json")) != input.Revision) throw new InvalidOperationException("Automatic selection changed. Read again.");
            await store.SaveAsync(value with { Revision = old.Revision }, ct);
            return new("Automatic selection saved. Applies to the next eligible prompt.");
        }
        if (input.Document == "connections") return await SaveConnectionsAsync(input, merged, ct);
        if (input.Document == "credentials") return await SaveCredentialsAsync(input, merged, ct);
        var spec = Specs.Single(s => s.Id == input.Document);
        var typed = merged.Deserialize(spec.Type, Json) ?? throw new InvalidOperationException("Settings cannot be null.");
        ValidateOptions(typed);
        await Task.Run(() => SettingsJsonFile.Mutate(paths.UserConfigPath, root =>
        {
            var target = root;
            foreach (var segment in spec.Path)
            {
                if (SettingsJsonFile.Get(target, segment) is not JsonObject child) { child = new(); SettingsJsonFile.Set(target, segment, child); child = SettingsJsonFile.Get(target, segment)!.AsObject(); }
                target = child;
            }
            SettingsJsonFile.Merge(target, input.Changes);
            return true;
        }, input.Revision), ct);
        activation.RequestReload(); return new(activation.Message, true);
    }

    public async Task<SettingsChangeResult> UseCurrentDefaultsAsync(CancellationToken ct)
    {
        var session = await RequireUserAsync(ct);
        var route = session.Selection?.Route ?? session.LaneName;
        var model = session.Selection?.Model ?? (route is null ? null : session.ModelChoices.GetValueOrDefault(route));
        var lane = laneSource.GetLanes().Lanes.FirstOrDefault(l => l.Name.Equals(route, StringComparison.OrdinalIgnoreCase));
        model ??= lane?.Model;
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(model) || lane is null)
            throw new InvalidOperationException("Select an available provider and model in this session first.");
        await Task.Run(() => preferences.Update(p => { p.DefaultLane = route; p.DefaultModel = model; }), ct);
        return new($"Defaults saved: {lane.Provider} / {model} (route {route}). The model's reasoning variant is included. Applies to new sessions.");
    }

    private async Task<WorkspaceSessionInfo> RequireUserAsync(CancellationToken ct)
    {
        var turn = turns.Current;
        if (turn is null || !turn.CommunicationScopeUserInitiated || turn.IsUnattended)
            throw new InvalidOperationException("Changing app settings requires a direct user request.");
        var session = await sessions.GetAsync(turn.ConversationId, ct);
        if (session is null || session.ParentSessionId is not null) throw new InvalidOperationException("Settings may only be changed from the user's active session.");
        return session;
    }
    private void ValidatePreferences(AppPreferences p, JsonObject changes)
    {
        if (p.Theme is not ("system" or "light" or "dark") || p.Zoom is < .8 or > 1.4 || !double.IsFinite(p.Zoom)) throw new InvalidOperationException("Choose system/light/dark and a text size from 0.8 to 1.4.");
        if (p.PermissionMode is not null && !CodingPermissionPolicy.TryParse(p.PermissionMode, out _)) throw new InvalidOperationException("Permission mode must be auto, ask-risky or ask.");
        if ((changes.ContainsKey("defaultLane") || changes.ContainsKey("defaultModel")) && p.DefaultLane is null && p.DefaultModel is not null) throw new InvalidOperationException("A default model requires a provider route; clear both to use configured defaults.");
        if ((changes.ContainsKey("defaultLane") || changes.ContainsKey("defaultModel")) && p.DefaultLane is not null && (!laneSource.GetLanes().Lanes.Any(l => l.Name.Equals(p.DefaultLane, StringComparison.OrdinalIgnoreCase)) || string.IsNullOrWhiteSpace(p.DefaultModel))) throw new InvalidOperationException("Choose an available default provider route and model.");
    }
    private static void ValidateDecision(DecisionSelectionSettings value)
    {
        var c = value.Connection;
        if (!Uri.TryCreate(c.Endpoint, UriKind.Absolute, out var uri) || (c.Provider == "ollama" ? !uri.IsLoopback || uri.Scheme != "http" : c.Provider != "system-one" || uri.Scheme != "https"))
            throw new InvalidOperationException("Use loopback HTTP for Ollama or HTTPS for System One.");
        if (string.IsNullOrWhiteSpace(c.Model)) throw new InvalidOperationException("Choose a decision model.");
    }
    private static void ValidateOptions(object value)
    {
        switch (value)
        {
            case CodingAttachmentOptions a: a.Validate(); break;
            case CodingExecutionLimits l when l.MaxToolCalls < 0 || l.MaxIterations < 0 || l.MaxCumulativeTokens < 0 || (l.Timeout < TimeSpan.Zero && l.Timeout != Timeout.InfiniteTimeSpan): throw new InvalidOperationException("Work limits must be zero or positive (timeout may be infinite).");
            case CodingAcceptanceOptions a when a.MaxRepairAttempts is < 0 or > 10 || a.Commands.Any(c => string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.FileName) || c.TimeoutSeconds <= 0): throw new InvalidOperationException("Checks need a name, executable, positive timeout, and 0–10 repair attempts.");
            case CodingComputerOptions c when c.Width <= 0 || c.Height <= 0 || c.ChallengeMaxActions <= 0 || c.ChallengeTimeoutSeconds <= 0: throw new InvalidOperationException("Computer sizes and action limits must be positive.");
            case CodingComputerOptions c when !string.IsNullOrWhiteSpace(c.ChromeCdpEndpoint) && (!Uri.TryCreate(c.ChromeCdpEndpoint, UriKind.Absolute, out var u) || !u.IsLoopback || u.Scheme is not ("http" or "https" or "ws" or "wss")): throw new InvalidOperationException("Chrome requires a loopback HTTP or WebSocket endpoint.");
            case CodingFallbackDiscoveryOptions f when f.MaxCandidates is < 1 or > 10 || f.MaxInputPricePerMillionUsd < 0 || f.MaxOutputPricePerMillionUsd < 0: throw new InvalidOperationException("Use 1–10 candidates and nonnegative prices.");
            case CodingLaneOptions l when l.MaxLanesPerRequest is < 1 or > 10 || l.Lanes.Any(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Provider) || string.IsNullOrWhiteSpace(x.Model)) || l.Lanes.DistinctBy(x => x.Name.ToLowerInvariant()).Count() != l.Lanes.Count: throw new InvalidOperationException("Use 1–10 attempts and distinct named routes with a provider and model.");
        }
    }
    private static void ValidateKeys(JsonObject changes, JsonNode schema)
    {
        var props = schema["properties"] as JsonObject ?? throw new InvalidOperationException("This settings object cannot be edited.");
        foreach (var pair in changes)
        {
            if (!props.TryGetPropertyValue(pair.Key, out var prop) || prop is null) throw new InvalidOperationException($"Unknown setting '{pair.Key}'. Read the schema first.");
            if (pair.Value is JsonObject nested && prop["properties"] is not null) ValidateKeys(nested, prop);
        }
    }
    public sealed class ConnectorSettings
    {
        public bool AllowCodexAppConnectors { get; set; }
        public bool AllowClaudeAiConnectors { get; set; }
    }
}

public sealed class SwarmCodeSettingsToolRegistration(IAgentSettingsHost host, ICapabilityCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { foreach (var tool in AgentSettingsTools.Create(host)) catalog.Register(tool); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
