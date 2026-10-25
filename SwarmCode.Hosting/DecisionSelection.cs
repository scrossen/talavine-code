using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Decisions;
using SwarmCore.Secrets;
using SwarmCore.Coding.Agents;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting;

public sealed record DecisionSelectionSettings
{
    public long Revision { get; init; }
    public bool Enabled { get; init; }
    /// <summary>Assess each new unpinned delegated task once; the person's lead conversation keeps its model.</summary>
    public bool AutoDelegateRoutineTasks { get; init; }
    public DecisionConnection Connection { get; init; } = new();
    public IReadOnlyList<ModelDecisionCandidate> Candidates { get; init; } = [];
}

/// <summary>Separate host settings, conditional writes and cross-process lease; no credentials or prompts.</summary>
public sealed class DecisionSettingsStore(SwarmCodeDataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path = Path.Combine(paths.RootDirectory, "decision-selection.json");
    public async Task<DecisionSelectionSettings> ReadAsync(CancellationToken ct = default)
        => File.Exists(path) ? JsonSerializer.Deserialize<DecisionSelectionSettings>(await File.ReadAllTextAsync(path, ct), Json)
            ?? throw new InvalidDataException("Decision settings are invalid.") : new();

    public async Task SaveAsync(DecisionSelectionSettings settings, CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct);
        var current = await ReadAsync(ct);
        if (current.Revision != settings.Revision) throw new InvalidOperationException("Decision settings changed. Reload before saving.");
        if (settings.Enabled && (settings.Candidates.Count is < 2 or > 26 || settings.Candidates.Any(c =>
            string.IsNullOrWhiteSpace(c.Route) || string.IsNullOrWhiteSpace(c.Model) || string.IsNullOrWhiteSpace(c.Description))
            || settings.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != settings.Candidates.Count))
            throw new InvalidOperationException("Provide 2 to 26 distinct model choices, each with a route, model and description.");
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(settings with { Revision = current.Revision + 1 }, Json), ct);
        File.Move(tmp, path, true);
    }
    private async Task<FileStream> LeaseAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(25, ct); }
        }
    }
}

public sealed class VaultDecisionCredentials(ISecretVault vault) : IDecisionCredentials
{
    public Task<string?> ResolveAsync(string name, CancellationToken ct) => vault.GetValueAsync(name, ct);
}

/// <summary>The starting route. A saved user choice prevents reassessment, not refusal hand-off.</summary>
public sealed record PreparedModelSelection(string? Lane, string? Model, long? Revision, string? Notice,
    LlmOverrideProvenance? Provenance = null, bool RoutineDelegation = false);

/// <summary>Fresh, host-owned evidence only; neither discovery nor catalog membership qualifies model quality.</summary>
public interface IDelegatedModelInventory
{
    Task<IReadOnlyList<ModelDecisionCandidate>> GetEligibleAsync(string conversationId, string task,
        IReadOnlyList<ModelDecisionCandidate> configured, CancellationToken ct);
}

/// <summary>
/// The native Ollama metadata contract proves both the installed artifact and its on-device tool capability.
/// Other local-compatible endpoints remain ineligible until their adapter supplies equivalent locality evidence.
/// Context sizing, runtime admission and physical memory reservations still run in the normal execution path.
/// </summary>
public sealed class DelegatedModelInventory(ICodingLaneSource lanes, OllamaSetupClient ollama,
    IOptions<OllamaProviderOptions> options, ModelSourcesStore sources, LaneHealthTracker? health = null) : IDelegatedModelInventory
{
    public async Task<IReadOnlyList<ModelDecisionCandidate>> GetEligibleAsync(string conversationId, string task,
        IReadOnlyList<ModelDecisionCandidate> configured, CancellationToken ct)
    {
        var endpoint = options.Value.Endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https")
            || sources.Read().Sources.Any(s => s.Kind == "Ollama" && !s.Enabled)) return [];
        var current = await lanes.GetLanesAsync(conversationId, ct).ConfigureAwait(false);
        var inventory = await ollama.InventoryAsync(endpoint, ct).ConfigureAwait(false);
        return configured.Where(candidate =>
        {
            var lane = current.Lanes.FirstOrDefault(l => l.Name.Equals(candidate.Route, StringComparison.OrdinalIgnoreCase));
            if (lane is null || lane.IsNative || !lane.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                || health?.IsUnavailable(lane.HealthKeyOrName, out _, current.HealthScope) == true) return false;
            // The installed metadata, never a model-name substring, excludes embedding-only and vision-only routes.
            return inventory.Models.Any(model => model.Name == candidate.Model && model.IsOnDevice && model.SupportsTools
                && model.Capabilities.Contains("completion", StringComparer.OrdinalIgnoreCase));
        }).Select(candidate => candidate with
        {
            Description = "Confirmed installed on this device with text generation and tools; local worker candidate. " + candidate.Description,
        }).ToArray();
    }
}

/// <summary>Host adapter: the shared selector sees only scrubbed task text and host-configured choices.</summary>
public sealed class DecisionSessionCoordinator(DecisionSettingsStore settings, WorkspaceSessionCatalog sessions, ModelDecisionSelector selector,
    FileConversationJobStore? jobs = null, IDelegatedModelInventory? delegatedInventory = null)
{
    /// <summary>
    /// Called only while the host creates a new child. The task remains agent-authored data and its turn remains
    /// a continuation. An authenticated child picker change wins the revision CAS even while the decision is pending.
    /// </summary>
    public async Task<PreparedModelSelection> PrepareDelegatedAsync(string id, string parentId, string task,
        bool explicitModelSelection, CancellationToken ct = default)
    {
        var child = await sessions.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The delegated session no longer exists.");
        if (child.ParentSessionId != parentId || child.Selection is not { } state)
            throw new InvalidOperationException("The delegated session does not belong to the starting conversation.");
        if (explicitModelSelection || !ModelSelectionPolicy.ShouldAssess(state))
            return new(state.Route, state.Model, state.Revision, null, Provenance(state), RoutineDelegation(child));
        DecisionSelectionSettings config;
        try { config = await settings.ReadAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(state.Route, state.Model, state.Revision,
                "Delegation settings are unavailable; kept the inherited model.", Provenance(state));
        }
        if (!config.Enabled || !config.AutoDelegateRoutineTasks)
            return new(state.Route, state.Model, state.Revision, null, Provenance(state), RoutineDelegation(child));
        var inherited = new ModelDecisionCandidate("inherited-parent", state.Route ?? "", state.Model ?? "",
            "The starting conversation's selected model. Keep this route for difficult, uncertain, broad or consequential work.");
        if (string.IsNullOrWhiteSpace(inherited.Route) || string.IsNullOrWhiteSpace(inherited.Model))
            return new(state.Route, state.Model, state.Revision,
                "Delegation assessment kept the inherited route because its complete model identity is unavailable.");

        ModelDecisionCandidate choice = inherited;
        var classification = DelegationTaskClass.Uncertain;
        string note;
        try
        {
            using var bounded = DecisionExecutionBudget.Begin(TimeSpan.FromSeconds(Math.Clamp(config.Connection.TimeoutSeconds, 1, 120)), ct);
            var safeTask = SwarmCore.Abstractions.Security.CredentialPatterns.Scrub(task, "[redacted]");
            var eligible = delegatedInventory is null ? [] : await delegatedInventory.GetEligibleAsync(id, safeTask,
                config.Candidates, bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            var decision = await selector.SelectDelegatedAsync(config.Connection, safeTask, eligible, inherited, bounded.Token)
                .WaitAsync(bounded.Token).ConfigureAwait(false);
            choice = decision.Candidate;
            classification = decision.TaskClass;
            note = "Delegation assessment by " + decision.DecisionProvider + " / " + decision.DecisionModel + ": "
                + decision.Reason + " Selected " + choice.Route + " / " + choice.Model + "."
                + (decision.RequiresCoordinatorReview
                    ? " The starting agent must verify this worker's evidence. If it fails, review its saved changes and receipts before escalating; never replay an action with an unknown outcome."
                    : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            note = "Delegation assessment unavailable; kept the starting conversation's model. No additional worker route was tried.";
        }
        var settingsChanged = false;
        try { settingsChanged = (await settings.ReadAsync(ct).ConfigureAwait(false)).Revision != config.Revision; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { settingsChanged = true; }
        if (settingsChanged)
        {
            choice = inherited;
            classification = DelegationTaskClass.Uncertain;
            note = "Delegation settings changed during assessment; kept the starting conversation's model.";
        }
        var applied = await sessions.TrySetSelectionAsync(id, state.Revision,
            ModelSelectionPolicy.Assessed(state, choice) with { DelegationClass = classification }, ct).ConfigureAwait(false);
        var fresh = await sessions.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The delegated session was deleted before its model selection completed.");
        RequireChild(fresh, parentId);
        if (!applied) note = "Delegation assessment discarded because the delegated session's model selection changed. The newer selection was retained.";
        return new(fresh.Selection?.Route ?? fresh.LaneName, fresh.Selection?.Model,
            fresh.Selection?.Revision, note, Provenance(fresh.Selection), RoutineDelegation(fresh));
    }

    private static void RequireChild(WorkspaceSessionInfo child, string parentId)
    {
        if (child.ParentSessionId != parentId) throw new InvalidOperationException("The delegated session's ownership changed.");
    }

    private static LlmOverrideProvenance? Provenance(SessionModelSelection? state)
        => state is { UserSelected: true } or { Origin: ModelSelectionOrigin.User } ? LlmOverrideProvenance.User
            : state?.Origin is ModelSelectionOrigin.Decision or ModelSelectionOrigin.Handoff ? LlmOverrideProvenance.Policy : null;

    private static bool RoutineDelegation(WorkspaceSessionInfo? child)
        => child?.ParentSessionId is not null && child.Selection is
            { UserSelected: false, Origin: ModelSelectionOrigin.Decision, DelegationClass: DelegationTaskClass.Routine };

    public async Task<PreparedModelSelection> PrepareAsync(string id, string text, string? lane, string? model,
        IReadOnlyList<CodingLane> lanes, bool continuation, CancellationToken ct)
    {
        var session = await sessions.GetAsync(id, ct).ConfigureAwait(false);
        if (session is { Selection: null })
        {
            var job = jobs is null ? null : await Task.Run(() => jobs.Load(id), ct).ConfigureAwait(false);
            session = await sessions.RestoreLegacySelectionAsync(id, job?.ActiveLane, job?.ActiveModel, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session was deleted before model selection completed.");
        }
        if (session?.Selection is not { } state) return new(lane, model, null, null);
        var config = await settings.ReadAsync(ct).ConfigureAwait(false);
        string? notice = null;
        if (config.Enabled && !continuation && ModelSelectionPolicy.ShouldAssess(state))
        {
            ModelDecisionCandidate? choice = null;
            try
            {
                using var bounded = DecisionExecutionBudget.Begin(TimeSpan.FromSeconds(Math.Clamp(config.Connection.TimeoutSeconds, 1, 120)), ct);
                var available = config.Candidates.Where(c => lanes.Any(l => l.Name.Equals(c.Route, StringComparison.OrdinalIgnoreCase))).ToArray();
                var stateText = state.Reassessment == ModelReassessment.None ? text
                    : $"Reassessment reason: {state.Reassessment}. Current model: {state.Route}/{state.Model}.\nCurrent user request:\n{text}";
                choice = await selector.SelectAsync(config.Connection, stateText, available, bounded.Token)
                    .WaitAsync(bounded.Token).ConfigureAwait(false);
                notice = $"Automatic selection: {choice.Route} / {choice.Model}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { notice = "Decision selection is unavailable. Continuing with the current model."; }
            try
            {
                if ((await settings.ReadAsync(ct).ConfigureAwait(false)).Revision != config.Revision)
                {
                    choice = null;
                    notice = "Decision settings changed during assessment. Continuing with the current model.";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                choice = null;
                notice = "Decision settings are unavailable. Continuing with the current model.";
            }
            await sessions.TrySetSelectionAsync(id, state.Revision, ModelSelectionPolicy.Assessed(state, choice), ct).ConfigureAwait(false);
            var fresh = await sessions.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session was deleted before model selection completed.");
            if (fresh.Selection?.Revision != state.Revision + 1 || fresh.Selection?.UserSelected == true) notice = null;
            state = fresh.Selection!;
        }
        return new(state.Route ?? lane, state.Model ?? model, state.Revision, notice, Provenance(state),
            RoutineDelegation(session with { Selection = state }));
    }

    public async Task<long?> ObserveAsync(string id, long? revision, CodingLane lane, bool handoff, CancellationToken ct)
    {
        if (revision is null) return null;
        var session = await sessions.GetAsync(id, ct).ConfigureAwait(false);
        if (session?.Selection is not { } state || state.Revision != revision
            || state.UserSelected || state.Origin == ModelSelectionOrigin.User) return null;
        var next = state with { Route = lane.Name, Model = lane.Model,
            Origin = handoff ? ModelSelectionOrigin.Handoff : state.Origin };
        return await sessions.TrySetSelectionAsync(id, revision.Value, next, ct).ConfigureAwait(false) ? revision + 1 : null;
    }

    public async Task MarkFailedAsync(string id, long? revision, CancellationToken ct)
    {
        if (revision is null) return;
        var session = await sessions.GetAsync(id, ct).ConfigureAwait(false);
        if (session?.Selection is not { UserSelected: false } state || state.Revision != revision) return;
        await sessions.TrySetSelectionAsync(id, revision.Value, state with { Reassessment = ModelReassessment.Failure }, ct).ConfigureAwait(false);
    }
}
