using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding;
using SwarmCore.Coding.Computer;
using SwarmCore.Coding.Permissions;

namespace SwarmCode.Hosting;

/// <summary>Desktop app preferences. Stored as JSON at <see cref="SwarmCodeDataPaths.PreferencesPath"/>.</summary>
public sealed class AppPreferences
{
    /// <summary>"system", "light", or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Legacy switch (before permission modes): true asked before every side effect. Read only to migrate a preferences
    /// file without <see cref="PermissionMode"/> (true = ask); kept in step with the mode so an older build still asks.
    /// </summary>
    public bool RequireApproval { get; set; }

    /// <summary>"auto" (default), "ask-risky" or "ask"; see <see cref="CodingPermissionMode"/>. Null: migrate from <see cref="RequireApproval"/>.</summary>
    public string? PermissionMode { get; set; }

    /// <summary>The effective mode: <see cref="PermissionMode"/>, or the legacy switch for older preference files.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CodingPermissionMode Mode => CodingPermissionPolicy.TryParse(PermissionMode, out var mode) ? mode
        : RequireApproval ? CodingPermissionMode.Ask : CodingPermissionMode.Auto;

    /// <summary>Sets the mode (and the legacy switch, so a mode that asks still asks in an older build).</summary>
    public void SetMode(CodingPermissionMode mode)
    {
        PermissionMode = CodingPermissionPolicy.Name(mode);
        RequireApproval = mode != CodingPermissionMode.Auto;
    }

    public bool HandOffOnRefusal { get; set; } = true;

    /// <summary>Lane used for new sessions when set; otherwise the configured default lane.</summary>
    public string? DefaultLane { get; set; }
    /// <summary>Explicit model copied into newly created sessions for DefaultLane; existing sessions are unchanged.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>App-wide model shortcuts, independent of each session's active model and assistance pins.</summary>
    public List<SwarmCore.Abstractions.LlmRouting.ModelFavorite> FavoriteModels { get; set; } = [];

    public bool ShowReasoning { get; set; }

    /// <summary>A saved direct coding success passed acceptance or had a successful mutating-tool receipt. Never inferred from answer text.</summary>
    public bool GitHubStarInvitationEligible { get; set; }

    /// <summary>The person dismissed the optional invitation or explicitly opened its repository.</summary>
    public bool GitHubStarInvitationDismissed { get; set; }

    /// <summary>The sidebar lists sessions from every repository (with a project chip) instead of only the open one.</summary>
    public bool SidebarAllRepositories { get; set; }

    public string? LastRepositoryRoot { get; set; }

    public string? LastSessionId { get; set; }

    /// <summary>Sessions whose agent sessions the person folded in the sidebar tree (expanded is the default).</summary>
    public List<string> CollapsedSessionIds { get; set; } = [];

    /// <summary>Font scale for the chat surface (1.0 = default).</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// The person's memory switch (Settings &gt; Memory). Off: nothing is learned or recalled about them; what is already
    /// stored stays visible and deletable. The admin/global switch is <c>PersonalMemory:Enabled</c> in settings.json.
    /// </summary>
    public bool PersonalMemoryEnabled { get; set; } = true;

    /// <summary>
    /// Optional editor command for "Open in editor" (e.g. <c>cursor</c> or <c>"C:\Tools\subl.exe" {file}:{line}</c>).
    /// Empty: VS Code's CLI, then the <c>vscode://</c> link, then the OS default app. See <c>SwarmCore.Coding.Ide.EditorCommandResolver</c>.
    /// </summary>
    public string? EditorCommand { get; set; }

    /// <summary>Pause the self-improvement loop (items stay queued; nothing runs until resumed).</summary>
    public bool SelfImprovementPaused { get; set; }

    /// <summary>
    /// Compact old goal receipt texts while the workspace is idle (Settings &gt; Data and logs). Off by default: receipts
    /// are evidence, and the person decides when finished and month-old closed work may keep only a digest and excerpt.
    /// </summary>
    public bool CompactGoalReceipts { get; set; }

    /// <summary>Models used in /compare (contenders and judges), most recent first; pinned at the top of its picker. See <see cref="ModelCompareChoices"/>.</summary>
    public List<SwarmCore.Coding.Review.ModelPanelPick> RecentCompareModels { get; set; } = [];

    /// <summary>The models checked in the last /compare, checked again when the dialog next opens.</summary>
    public List<SwarmCore.Coding.Review.ModelPanelPick> LastCompareModels { get; set; } = [];

    /// <summary>The judge chosen in the last /compare.</summary>
    public SwarmCore.Coding.Review.ModelPanelPick? CompareJudge { get; set; }
}

public sealed class AppPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Action<string, string, string>? _replaceExisting;
    public AppPreferencesStore(SwarmCodeDataPaths paths) : this(paths, null) { }
    internal AppPreferencesStore(SwarmCodeDataPaths paths, Action<string, string, string>? replaceExisting)
    { _path = paths.PreferencesPath; _replaceExisting = replaceExisting; }

    public event Action? Changed;
    internal void NotifyChanged() => Changed?.Invoke();
    // Detached, fresh snapshots: another host's change is visible and callers cannot mutate the cache.
    public AppPreferences Current
    {
        get
        {
            try { return Settings.SettingsJsonFile.Read(_path).Deserialize<AppPreferences>(Json) ?? new(); }
            catch (JsonException) { return new(); } // Reads stay usable; mutation still refuses malformed data.
        }
    }

    public void Update(Action<AppPreferences> change)
    {
        Settings.SettingsJsonFile.Mutate(_path, root =>
        {
            var prefs = root.Deserialize<AppPreferences>(Json) ?? new();
            change(prefs);
            // Preserve fields from newer versions of the app.
            Settings.SettingsJsonFile.Merge(root, JsonSerializer.SerializeToNode(prefs, Json)!.AsObject());
            return true;
        }, replaceExisting: _replaceExisting);
        Changed?.Invoke();
    }

    public void SetModelFavorite(SwarmCore.Abstractions.LlmRouting.ModelFavorite favorite, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        if (string.IsNullOrWhiteSpace(favorite.Provider) || string.IsNullOrWhiteSpace(favorite.Model))
            throw new ArgumentException("A favorite needs a provider and model.", nameof(favorite));
        // Update reads fresh preferences under the existing cross-process lease. Only this
        // exact membership changes; independent adds/removals and unrelated settings survive.
        Update(p =>
        {
            p.FavoriteModels ??= [];
            if (!pinned) p.FavoriteModels.RemoveAll(f => f == favorite);
            else if (!p.FavoriteModels.Contains(favorite)) p.FavoriteModels.Add(favorite);
        });
    }

}

/// <summary>
/// Edits the user's settings.json (the file <see cref="SwarmCodeHostComposer.BuildConfiguration"/>
/// reads) without disturbing sections it does not own. Lanes live under "CodingLanes".
/// </summary>
public sealed class UserSettingsFile
{
    private static readonly JsonSerializerOptions Json = Settings.SettingsJsonFile.Json;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, (JsonObject Value, bool FileExisted)> _baselines = new(StringComparer.OrdinalIgnoreCase);
    public UserSettingsFile(SwarmCodeDataPaths paths) => _path = paths.UserConfigPath;
    public string Path => _path;

    public T ReadCodingSection<T>(string key) where T : new() => Read<T>("Coding", key);
    public CodingLaneOptions ReadLanes() => Read<CodingLaneOptions>("CodingLanes");
    public CodingPermissionRules ReadPermissionRules() => ReadCodingSection<CodingPermissionRules>("Permissions");
    private T Read<T>(params string[] path) where T : new()
    {
        lock (_gate)
        {
            var root = Settings.SettingsJsonFile.Read(_path);
            var node = Find(root, path);
            var value = node is null ? new T() : node.Deserialize<T>(Json) ?? new T();
            _baselines[string.Join(':', path)] = (JsonSerializer.SerializeToNode(value, Json)!.AsObject(), File.Exists(_path));
            return value;
        }
    }
    public void WriteCodingSection<T>(string key, T value) => WriteSections((new[] { "Coding", key }, (object)value!));
    public void WriteLanes(CodingLaneOptions lanes)
    {
        WriteSections((new[] { "CodingLanes" }, (object)lanes));
        if (lanes.Lanes.Count == 0 && lanes.DefaultLane is null && lanes.HandOffOnRefusal && lanes.MaxLanesPerRequest == 3 && lanes.UnavailableLaneCooldown == TimeSpan.FromMinutes(10))
            Settings.SettingsJsonFile.Mutate(_path, root =>
            {
                var raw = Find(root, ["CodingLanes"]) as JsonObject;
                var known = JsonSerializer.SerializeToNode(lanes, Json)!.AsObject();
                var hasUnknown = raw?.Any(p => !known.Any(k => k.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase))) == true;
                var current = raw?.Deserialize<CodingLaneOptions>(Json);
                if (!hasUnknown && current is not null && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(current, Json), JsonSerializer.SerializeToNode(lanes, Json)))
                { var key = root.Select(p => p.Key).FirstOrDefault(k => k.Equals("CodingLanes", StringComparison.OrdinalIgnoreCase)); if (key is not null) root.Remove(key); }
                return true;
            });
    }
    public void WriteRuntimeSettings(CodingExecutionLimits limits, CodingAcceptanceOptions acceptance, CodingComputerOptions computer)
        => WriteSections((new[] { "Coding", "ExecutionLimits" }, limits), (new[] { "Coding", "Acceptance" }, acceptance), (new[] { "Coding", "Computer" }, computer));
    public void WriteWorkSettings(CodingExecutionLimits limits, CodingAcceptanceOptions acceptance)
        => WriteSections((new[] { "Coding", "ExecutionLimits" }, limits), (new[] { "Coding", "Acceptance" }, acceptance));

    private void WriteSections(params (string[] Path, object Value)[] writes)
    {
        lock (_gate)
        {
            var updated = new Dictionary<string, JsonObject>();
            Settings.SettingsJsonFile.Mutate(_path, root =>
            {
                foreach (var write in writes)
                {
                    var key = string.Join(':', write.Path);
                    var next = JsonSerializer.SerializeToNode(write.Value, write.Value.GetType(), Json)!.AsObject();
                    var raw = Find(root, write.Path) as JsonObject;
                    if (_baselines.TryGetValue(key, out var baseline))
                    {
                        if (baseline.FileExisted && !File.Exists(_path)) throw new InvalidOperationException("Settings were deleted. Reload before saving.");
                        var currentValue = raw?.Deserialize(write.Value.GetType(), Json) ?? Activator.CreateInstance(write.Value.GetType())!;
                        var current = JsonSerializer.SerializeToNode(currentValue, write.Value.GetType(), Json)!.AsObject();
                        var original = current.DeepClone().AsObject();
                        ApplyDiff(current, baseline.Value, next, checkConflicts: true);
                        var target = Ensure(root, write.Path);
                        ApplyDiff(target, original, current, checkConflicts: false);
                        updated[key] = current;
                    }
                    else { Settings.SettingsJsonFile.Merge(Ensure(root, write.Path), next); updated[key] = next; }
                }
                return true;
            });
            foreach (var pair in updated) _baselines[pair.Key] = (pair.Value, true);
        }
    }

    public bool ReadAllowCodexAppConnectors() => ReadFlag(nameof(NativeAgentOptions.AllowCodexAppConnectors));
    public bool ReadAllowClaudeAiConnectors() => ReadFlag(nameof(NativeAgentOptions.AllowClaudeAiConnectors));
    private bool ReadFlag(string key) => Find(Settings.SettingsJsonFile.Read(_path), ["Coding", "NativeAgents", key])?.GetValue<bool>() ?? false;
    public void WriteAllowCodexAppConnectors(bool allow) => WriteFlag(nameof(NativeAgentOptions.AllowCodexAppConnectors), allow);
    public void WriteAllowClaudeAiConnectors(bool allow) => WriteFlag(nameof(NativeAgentOptions.AllowClaudeAiConnectors), allow);
    private void WriteFlag(string key, bool allow) => Settings.SettingsJsonFile.Mutate(_path, root =>
    { Settings.SettingsJsonFile.Set(Ensure(root, ["Coding", "NativeAgents"]), key, JsonValue.Create(allow)); return true; });
    public string? ReadValue(string section, string key) => Find(Settings.SettingsJsonFile.Read(_path), [section, key])?.ToString();
    public void WriteValue(string section, string key, string? value) => Settings.SettingsJsonFile.Mutate(_path, root =>
    { Settings.SettingsJsonFile.Set(Ensure(root, [section]), key, JsonValue.Create(value)); return true; });

    private static JsonNode? Find(JsonObject root, string[] path)
    {
        JsonNode? value = root;
        foreach (var key in path) value = value is JsonObject obj ? Settings.SettingsJsonFile.Get(obj, key) : null;
        return value;
    }
    private static JsonObject Ensure(JsonObject root, string[] path)
    {
        foreach (var key in path)
        {
            if (Settings.SettingsJsonFile.Get(root, key) is not JsonObject child)
            { Settings.SettingsJsonFile.Set(root, key, new JsonObject()); child = Settings.SettingsJsonFile.Get(root, key)!.AsObject(); }
            root = child;
        }
        return root;
    }
    private static void ApplyDiff(JsonObject current, JsonObject before, JsonObject after, bool checkConflicts)
    {
        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key), StringComparer.OrdinalIgnoreCase))
        {
            var old = Settings.SettingsJsonFile.Get(before, key); var next = Settings.SettingsJsonFile.Get(after, key);
            if (JsonNode.DeepEquals(old, next)) continue;
            var fresh = Settings.SettingsJsonFile.Get(current, key);
            if (old is JsonObject oldObj && next is JsonObject nextObj && fresh is JsonObject freshObj)
                ApplyDiff(freshObj, oldObj, nextObj, checkConflicts);
            else
            {
                if (checkConflicts && !JsonNode.DeepEquals(old, fresh) && !JsonNode.DeepEquals(next, fresh)) throw new InvalidOperationException("Settings changed. Reload before saving; your draft has been preserved.");
                Settings.SettingsJsonFile.Set(current, key, next);
            }
        }
    }
}
