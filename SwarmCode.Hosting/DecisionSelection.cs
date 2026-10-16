using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Decisions;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting;

public sealed record DecisionSelectionSettings
{
    public long Revision { get; init; }
    public bool Enabled { get; init; }
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
public sealed record PreparedModelSelection(string? Lane, string? Model, long? Revision, string? Notice);

/// <summary>Host adapter: the shared selector sees only scrubbed task text and host-configured choices.</summary>
public sealed class DecisionSessionCoordinator(DecisionSettingsStore settings, WorkspaceSessionCatalog sessions, ModelDecisionSelector selector,
    FileConversationJobStore? jobs = null)
{
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
                var available = config.Candidates.Where(c => lanes.Any(l => l.Name.Equals(c.Route, StringComparison.OrdinalIgnoreCase))).ToArray();
                var stateText = state.Reassessment == ModelReassessment.None ? text
                    : $"Reassessment reason: {state.Reassessment}. Current model: {state.Route}/{state.Model}.\nCurrent user request:\n{text}";
                choice = await selector.SelectAsync(config.Connection, stateText, available, ct).ConfigureAwait(false);
                notice = $"Automatic selection: {choice.Route} / {choice.Model}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { notice = "Decision selection is unavailable. Continuing with the current model."; }
            await sessions.TrySetSelectionAsync(id, state.Revision, ModelSelectionPolicy.Assessed(state, choice), ct).ConfigureAwait(false);
            var fresh = await sessions.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session was deleted before model selection completed.");
            if (fresh.Selection?.Revision != state.Revision + 1 || fresh.Selection?.UserSelected == true) notice = null;
            state = fresh.Selection!;
        }
        return new(state.Route ?? lane, state.Model ?? model, state.Revision, notice);
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
