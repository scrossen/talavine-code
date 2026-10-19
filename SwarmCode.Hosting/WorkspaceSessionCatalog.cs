using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.LlmRouting.Decisions;

namespace SwarmCode.Hosting;

/// <summary>A chat session bound to one repository. Its id is also the conversation id.</summary>
public sealed record WorkspaceSessionInfo(
    string Id,
    string Title,
    string RepositoryRoot,
    DateTime CreatedUtc,
    DateTime LastActivityUtc,
    string? LaneName = null)
{
    public IReadOnlyDictionary<string, string> ModelChoices { get; init; } = new Dictionary<string, string>();

    /// <summary>Temporary session: personal memory is neither read nor written for its turns.</summary>
    public bool Temporary { get; init; }
    public long AssistanceRevision { get; init; }
    public SwarmCore.Abstractions.Collaboration.TurnAssistanceOptions Assistance { get; init; } = new();
    // Null means legacy: never silently reclassify an existing conversation as new.
    public SessionModelSelection? Selection { get; init; }

    /// <summary>Set for an agent's session: the session whose agent started it. The desktop sidebar nests it under that session (<see cref="SessionTree"/>).</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>Historical origin of an independent fork; does not make it an agent or grant authority.</summary>
    public string? ForkedFromSessionId { get; init; }

    /// <summary>
    /// Where this session's turns work when it is not <see cref="RepositoryRoot"/> itself: an isolated agent's own git
    /// worktree. Its turns get this folder as their workspace (tools and native lanes); it is no other session's root.
    /// </summary>
    public string? WorkingRoot { get; init; }

    /// <summary>An agent's inherited permission mode (the starting session's mode at spawn); its turns never run less cautiously.</summary>
    public string? PermissionMode { get; init; }
}

/// <summary>
/// Durable index of sessions and recently opened repositories, stored as JSON under the
/// data directory. Small enough to rewrite whole on every change.
/// </summary>
public sealed class WorkspaceSessionCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _path;
    private readonly SwarmCodeDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceSessionCatalog(SwarmCodeDataPaths paths) { _paths = paths; _path = paths.SessionIndexPath; }

    public event Action? Changed;

    public async Task<IReadOnlyList<WorkspaceSessionInfo>> ListAsync(string? repositoryRoot = null, CancellationToken ct = default)
    {
        var doc = await LoadAsync(ct).ConfigureAwait(false);
        IEnumerable<WorkspaceSessionInfo> q = doc.Sessions;
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var root = Normalize(repositoryRoot);
            q = q.Where(s => string.Equals(Normalize(s.RepositoryRoot), root, StringComparison.OrdinalIgnoreCase));
        }
        return q.OrderByDescending(s => s.LastActivityUtc).ToList();
    }

    public async Task<WorkspaceSessionInfo?> GetAsync(string id, CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Sessions.FirstOrDefault(s => s.Id == id);

    public async Task<IReadOnlyList<string>> RecentRepositoriesAsync(CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).RecentRepositories.Where(r => !_paths.IsChatWorkspace(r)).ToList();

    public async Task<WorkspaceSessionInfo> CreateAsync(string repositoryRoot, string? title = null, string? laneName = null, CancellationToken ct = default, string? model = null, string? parentSessionId = null,
        string? workingRoot = null, string? permissionMode = null)
    {
        var root = Normalize(repositoryRoot);
        var now = DateTime.UtcNow;
        var session = new WorkspaceSessionInfo(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(title) ? "New session" : title.Trim(), root, now, now, laneName)
        {
            Selection = new SessionModelSelection { Route = laneName, Model = model },
            ModelChoices = !string.IsNullOrWhiteSpace(laneName) && !string.IsNullOrWhiteSpace(model)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [laneName] = model.Trim() }
                : new Dictionary<string, string>(),
            // An agent's session never feeds personal memory: its messages are written by another agent, not the person.
            ParentSessionId = parentSessionId,
            Temporary = parentSessionId is not null,
            WorkingRoot = string.IsNullOrWhiteSpace(workingRoot) ? null : Normalize(workingRoot),
            PermissionMode = permissionMode,
        };
        await MutateAsync(doc =>
        {
            doc.Sessions.Add(session);
            if (!_paths.IsChatWorkspace(root)) Touch(doc.RecentRepositories, root);
        }, ct).ConfigureAwait(false);
        return session;
    }

    public Task RememberRepositoryAsync(string repositoryRoot, CancellationToken ct = default)
        => _paths.IsChatWorkspace(repositoryRoot) ? Task.CompletedTask
            : MutateAsync(doc => Touch(doc.RecentRepositories, Normalize(repositoryRoot)), ct);

    /// <summary>Prepare private payloads before publishing a new index entry. The source cannot be deleted or
    /// have its settings changed by another catalog writer while the snapshot is prepared.</summary>
    internal async Task<WorkspaceSessionInfo> PublishForkAsync(string sourceId,
        Func<WorkspaceSessionInfo, WorkspaceSessionInfo, Task> prepare, CancellationToken ct)
    {
        WorkspaceSessionInfo fork;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireLeaseAsync(ct).ConfigureAwait(false);
            var doc = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var source = doc.Sessions.FirstOrDefault(s => s.Id == sourceId)
                ?? throw new InvalidOperationException("The source session no longer exists.");
            if (source.ParentSessionId is not null)
                throw new InvalidOperationException("Fork a main conversation, rather than an agent's session.");
            var now = DateTime.UtcNow;
            fork = source with
            {
                Id = Guid.NewGuid().ToString("N"), Title = "Fork: " + source.Title,
                CreatedUtc = now, LastActivityUtc = now, ForkedFromSessionId = source.Id,
                ModelChoices = new Dictionary<string, string>(source.ModelChoices, StringComparer.OrdinalIgnoreCase),
            };
            await prepare(source, fork).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            doc.Sessions.Add(fork);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(doc, Json), ct).ConfigureAwait(false);
                File.Move(tmp, _path, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return fork;
    }

    /// <summary>Console host creates its own session id. Never replaces an existing session.</summary>
    public Task EnsureAsync(string id, string repositoryRoot, CancellationToken ct = default)
        => MutateAsync(doc =>
        {
            if (!doc.Sessions.Any(s => s.Id == id)) doc.Sessions.Add(new(id, "New session", Normalize(repositoryRoot), DateTime.UtcNow, DateTime.UtcNow)
            { Selection = new() });
        }, ct);

    public Task ForgetRepositoryAsync(string repositoryRoot, CancellationToken ct = default)
        => MutateAsync(doc => doc.RecentRepositories.RemoveAll(r => string.Equals(r, Normalize(repositoryRoot), StringComparison.OrdinalIgnoreCase)), ct);

    public Task RenameAsync(string id, string title, CancellationToken ct = default)
        => Update(id, s => s with { Title = string.IsNullOrWhiteSpace(title) ? s.Title : title.Trim() }, ct);

    /// <summary>Records activity; also sets the lane when supplied. The first user message becomes the title when the title is still the default.</summary>
    public Task TouchAsync(string id, string? laneName = null, string? titleHint = null, CancellationToken ct = default)
        => Update(id, s => s with
        {
            LastActivityUtc = DateTime.UtcNow,
            LaneName = s.Selection?.Route ?? laneName ?? s.LaneName,
            Title = s.Title == "New session" && !string.IsNullOrWhiteSpace(titleHint) ? TitleFrom(titleHint) : s.Title,
        }, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => MutateAsync(doc => doc.Sessions.RemoveAll(s => s.Id == id), ct);

    /// <summary>Marks a session temporary (memory off for its turns) or ordinary.</summary>
    public Task SetAssistanceAsync(string id, SwarmCore.Abstractions.Collaboration.TurnAssistanceOptions value, long expectedRevision, CancellationToken ct = default)
    {
        if (value.Validate() is { } error) throw new ArgumentException(error);
        return MutateAsync(doc =>
        {
            var index = doc.Sessions.FindIndex(s => s.Id == id);
            if (index < 0) throw new InvalidOperationException("This conversation was deleted. Your choices have not been saved.");
            var current = doc.Sessions[index];
            if (current.AssistanceRevision != expectedRevision) throw new InvalidOperationException("Opinions and review changed in another window. Reopen the options before saving.");
            doc.Sessions[index] = current with { AssistanceRevision = current.AssistanceRevision + 1,
                Assistance = value with { OpinionModels = value.OpinionModels.ToArray() } };
        }, ct);
    }

    public Task SetTemporaryAsync(string id, bool temporary, CancellationToken ct = default)
        => Update(id, s => s with { Temporary = temporary }, ct);

    public Task SetModelAsync(string id, string lane, string model, CancellationToken ct = default)
        => Update(id, s =>
        {
            var models = new Dictionary<string, string>(s.ModelChoices, StringComparer.OrdinalIgnoreCase) { [lane] = model };
            return s with { ModelChoices = models, LaneName = lane,
                Selection = ModelSelectionPolicy.UserChoice(s.Selection ?? new(), lane, model) };
        }, ct);

    public Task SetUserLaneAsync(string id, string lane, string model, CancellationToken ct = default)
        => SetModelAsync(id, lane, model, ct);

    /// <summary>
    /// Upgrade an existing conversation from its last durable execution, or its old per-lane choice.
    /// This is recovery, not a new conversation or a user pin. A newer selection always wins.
    /// </summary>
    public async Task<WorkspaceSessionInfo?> RestoreLegacySelectionAsync(string id, string? activeLane, string? activeModel, CancellationToken ct = default)
    {
        var session = await GetAsync(id, ct).ConfigureAwait(false);
        if (session is null || session.Selection is not null) return session;
        session = null;
        await Update(id, current =>
        {
            if (current.Selection is not null) return session = current;
            var hasExecution = !string.IsNullOrWhiteSpace(activeLane) && !string.IsNullOrWhiteSpace(activeModel);
            var route = hasExecution ? activeLane : current.LaneName;
            var models = new Dictionary<string, string>(current.ModelChoices, StringComparer.OrdinalIgnoreCase);
            var model = hasExecution ? activeModel : route is null ? null : models.GetValueOrDefault(route);
            if (route is not null && model is not null) models[route] = model;
            return session = current with
            {
                LaneName = route,
                ModelChoices = models,
                Selection = new SessionModelSelection { Route = route, Model = model, InitialAssessmentComplete = true },
            };
        }, ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>Commit only against the revision observed before inference. Deleted sessions stay deleted.</summary>
    public async Task<bool> TrySetSelectionAsync(string id, long revision, SessionModelSelection selection, CancellationToken ct = default)
    {
        var applied = false;
        await Update(id, s =>
        {
            if (s.Selection is not { } current || current.Revision != revision) return s;
            applied = true;
            var models = new Dictionary<string, string>(s.ModelChoices, StringComparer.OrdinalIgnoreCase);
            if (selection.Route is { } route && selection.Model is { } model) models[route] = model;
            return s with { Selection = selection with { Revision = revision + 1 }, LaneName = selection.Route ?? s.LaneName, ModelChoices = models };
        }, ct).ConfigureAwait(false);
        return applied;
    }

    public Task RequestReassessmentAsync(string id, CancellationToken ct = default)
        => Update(id, s => s with { Selection = (s.Selection ?? new()) with
        { Revision = (s.Selection?.Revision ?? 0) + 1, UserSelected = false, Origin = ModelSelectionOrigin.Decision,
            Reassessment = ModelReassessment.UserRequested } }, ct);

    public static string TitleFrom(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "...";
    }

    private async Task Update(string id, Func<WorkspaceSessionInfo, WorkspaceSessionInfo> change, CancellationToken ct)
        => await MutateAsync(doc =>
        {
            var i = doc.Sessions.FindIndex(s => s.Id == id);
            if (i >= 0) doc.Sessions[i] = change(doc.Sessions[i]);
        }, ct).ConfigureAwait(false);

    private async Task MutateAsync(Action<Document> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireLeaseAsync(ct).ConfigureAwait(false);
            var doc = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            change(doc);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(doc, Json), ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private async Task<Document> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadUnlockedAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<Document> LoadUnlockedAsync(CancellationToken ct)
    {
        if (File.Exists(_path))
        {
            return JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false), Json)
                ?? throw new InvalidDataException("Session index is empty or invalid.");
        }
        return new Document();
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(25, ct).ConfigureAwait(false); }
        }
    }

    private static void Touch(List<string> recent, string root)
    {
        recent.RemoveAll(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, root);
        if (recent.Count > 20) recent.RemoveRange(20, recent.Count - 20);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class Document
    {
        public List<WorkspaceSessionInfo> Sessions { get; set; } = [];
        public List<string> RecentRepositories { get; set; } = [];
    }
}
