using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SwarmCore.Coding.Extensions;

/// <summary>When a hook runs.</summary>
public enum CodingHookEvent
{
    /// <summary>Before a tool call (API/tool lanes). Exit code 2 or <c>{"decision":"block"}</c> refuses the call.</summary>
    PreToolUse,

    /// <summary>After a successful tool call. <c>{"additionalContext":"..."}</c> adds a note to the result.</summary>
    PostToolUse,

    /// <summary>Before the person's message reaches a lane. May add context or block the message.</summary>
    UserPromptSubmit,

    /// <summary>When a turn ends (completed, failed or stopped). Informational.</summary>
    Stop,

    /// <summary>
    /// When a session is opened (<c>source</c>: startup, resume or new). Stdout (plain text or <c>additionalContext</c>)
    /// is context the host adds to the session's next prompt. Cannot block.
    /// </summary>
    SessionStart,

    /// <summary>When a session ends (<c>reason</c>: exit, new or closed). Informational.</summary>
    SessionEnd,

    /// <summary>When an agent another agent started reaches a terminal state. Informational.</summary>
    SubagentStop,

    /// <summary>When working-context compaction begins (<c>reason</c>). Informational; the host cannot hold compaction.</summary>
    PreCompact,

    /// <summary>When the host raises something to the person (<c>kind</c>: permission, question or idle). Informational.</summary>
    Notification,
}

/// <summary>
/// One configured hook (<c>Coding:Hooks:&lt;Event&gt;</c> in settings, or an entry in <c>.swarmcode/hooks.json</c>).
/// Both a flat shape (<c>{ "Matcher", "Command", "TimeoutSeconds" }</c>) and Claude Code's nested shape
/// (<c>{ "matcher", "hooks": [ { "type": "command", "command", "timeout" } ] }</c>) are accepted.
/// </summary>
public sealed class CodingHookDefinition
{
    /// <summary>Tool id glob for tool events (<c>*</c>, <c>?</c>, alternatives with <c>|</c>); empty matches every tool.</summary>
    public string? Matcher { get; set; }

    /// <summary>Command line run through the platform shell exactly as written (cmd.exe /c on Windows, /bin/sh -c elsewhere).</summary>
    public string? Command { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>Claude Code's spelling of <see cref="TimeoutSeconds"/>.</summary>
    public int? Timeout { get; set; }

    /// <summary>Claude Code's nested form: commands that share this entry's matcher.</summary>
    public List<CodingHookDefinition>? Hooks { get; set; }
}

/// <summary>User-level hooks (<c>Coding:Hooks</c>). These are the person's own configuration and need no approval.</summary>
public sealed class CodingHooksOptions
{
    /// <summary>False turns every hook (user and repository) off.</summary>
    public bool Enabled { get; set; } = true;

    public List<CodingHookDefinition> PreToolUse { get; set; } = [];
    public List<CodingHookDefinition> PostToolUse { get; set; } = [];
    public List<CodingHookDefinition> UserPromptSubmit { get; set; } = [];
    public List<CodingHookDefinition> Stop { get; set; } = [];
    public List<CodingHookDefinition> SessionStart { get; set; } = [];
    public List<CodingHookDefinition> SessionEnd { get; set; } = [];
    public List<CodingHookDefinition> SubagentStop { get; set; } = [];
    public List<CodingHookDefinition> PreCompact { get; set; } = [];
    public List<CodingHookDefinition> Notification { get; set; } = [];

    public IEnumerable<(CodingHookEvent Event, CodingHookDefinition Definition)> All()
    {
        foreach (var d in PreToolUse) yield return (CodingHookEvent.PreToolUse, d);
        foreach (var d in PostToolUse) yield return (CodingHookEvent.PostToolUse, d);
        foreach (var d in UserPromptSubmit) yield return (CodingHookEvent.UserPromptSubmit, d);
        foreach (var d in Stop) yield return (CodingHookEvent.Stop, d);
        foreach (var d in SessionStart) yield return (CodingHookEvent.SessionStart, d);
        foreach (var d in SessionEnd) yield return (CodingHookEvent.SessionEnd, d);
        foreach (var d in SubagentStop) yield return (CodingHookEvent.SubagentStop, d);
        foreach (var d in PreCompact) yield return (CodingHookEvent.PreCompact, d);
        foreach (var d in Notification) yield return (CodingHookEvent.Notification, d);
    }
}

/// <summary>A normalized hook ready to run.</summary>
public sealed record CodingHook(CodingHookEvent Event, string? Matcher, string Command, TimeSpan Timeout, ExtensionSource Source, string Origin)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>True when the hook applies to <paramref name="toolId"/> (always true for non-tool events).</summary>
    public bool Matches(string? toolId)
    {
        if (Event is not (CodingHookEvent.PreToolUse or CodingHookEvent.PostToolUse)) return true;
        return CodingHookMatcher.Matches(Matcher, toolId ?? string.Empty);
    }

    /// <summary>Flattens definitions (both shapes) into hooks; entries without a command are dropped.</summary>
    public static IReadOnlyList<CodingHook> From(IEnumerable<(CodingHookEvent Event, CodingHookDefinition Definition)> definitions, ExtensionSource source, string origin)
    {
        var hooks = new List<CodingHook>();
        foreach (var (evt, definition) in definitions)
        {
            if (definition is null) continue;
            if (!string.IsNullOrWhiteSpace(definition.Command))
                hooks.Add(new(evt, Blank(definition.Matcher), definition.Command.Trim(), TimeoutOf(definition), source, origin));
            foreach (var nested in definition.Hooks ?? [])
                if (nested is not null && !string.IsNullOrWhiteSpace(nested.Command))
                    hooks.Add(new(evt, Blank(nested.Matcher) ?? Blank(definition.Matcher), nested.Command.Trim(), TimeoutOf(nested, definition), source, origin));
        }
        return hooks;
    }

    private static TimeSpan TimeoutOf(CodingHookDefinition d, CodingHookDefinition? parent = null)
    {
        var seconds = d.TimeoutSeconds ?? d.Timeout ?? parent?.TimeoutSeconds ?? parent?.Timeout;
        if (seconds is not > 0) return DefaultTimeout;
        var value = TimeSpan.FromSeconds(seconds.Value);
        return value > MaxTimeout ? MaxTimeout : value;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Case-insensitive tool-id glob: <c>*</c>, <c>?</c>, and <c>|</c> between alternatives. Null/empty/<c>*</c> match all.</summary>
public static class CodingHookMatcher
{
    public static bool Matches(string? matcher, string toolId)
    {
        if (string.IsNullOrWhiteSpace(matcher) || matcher.Trim() == "*") return true;
        foreach (var alternative in matcher.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = "^" + Regex.Escape(alternative).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (Regex.IsMatch(toolId, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return true;
        }
        return false;
    }
}

/// <summary>What a repository's <c>.swarmcode/hooks.json</c> asks for, and whether the person approved that exact content.</summary>
public sealed record RepoHookInspection(string Path, bool Exists, string? Sha256, bool Approved, IReadOnlyList<CodingHook> Hooks, string? Problem)
{
    /// <summary>The file exists, parses, defines hooks, and its current content is not approved.</summary>
    public bool NeedsApproval => Exists && Problem is null && Hooks.Count > 0 && !Approved;
}

/// <summary>
/// Per-repository approval of the exact <c>.swarmcode/hooks.json</c> content. Approval is keyed by repository root and
/// the SHA-256 of the file's bytes, so any change to the file needs a fresh approval. Hosts store it outside the
/// repository (SwarmCode: its data directory).
/// </summary>
public interface ICodingHookApprovalStore
{
    Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default);
    Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default);
    Task RevokeAsync(string repositoryRoot, CancellationToken ct = default);
}

/// <summary>Harness default: nothing is approved, so repository hooks never run in a host that stores no approvals.</summary>
public sealed class NoCodingHookApprovals : ICodingHookApprovalStore
{
    public Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => Task.FromResult(false);
    public Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default)
        => throw new InvalidOperationException("This host does not store repository hook approvals, so repository hooks stay off.");
    public Task RevokeAsync(string repositoryRoot, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Process-lifetime approvals (tests, hosts without durable storage).</summary>
public sealed class InMemoryCodingHookApprovalStore : ICodingHookApprovalStore
{
    private readonly Dictionary<string, string> _approved = new(CodingHookSource.RootComparer);
    private readonly object _gate = new();

    public Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_approved.TryGetValue(CodingHookSource.NormalizeRoot(repositoryRoot), out var h) && string.Equals(h, sha256, StringComparison.OrdinalIgnoreCase));
    }

    public Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default)
    {
        lock (_gate) _approved[CodingHookSource.NormalizeRoot(repositoryRoot)] = sha256;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string repositoryRoot, CancellationToken ct = default)
    {
        lock (_gate) _approved.Remove(CodingHookSource.NormalizeRoot(repositoryRoot));
        return Task.CompletedTask;
    }
}

/// <summary>The hooks that apply in a repository: the person's settings hooks, then the repository's approved hooks.</summary>
public interface ICodingHookSource
{
    Task<IReadOnlyList<CodingHook>> GetHooksAsync(string? repositoryRoot, CancellationToken ct = default);
    Task<RepoHookInspection?> InspectRepositoryAsync(string? repositoryRoot, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ICodingHookSource"/>: <see cref="CodingOptions.Hooks"/> (user level, no approval) plus
/// <c>&lt;repo&gt;/.swarmcode/hooks.json</c>, which runs only when <see cref="ICodingHookApprovalStore"/> approved the
/// exact bytes read now. The file is read once per lookup and both hashed and parsed from those bytes, so content
/// cannot change between the approval check and use.
/// </summary>
public sealed class CodingHookSource(IOptions<CodingOptions> options, ICodingHookApprovalStore approvals) : ICodingHookSource
{
    public const string RepositoryHooksFileName = "hooks.json";
    public const long MaxHooksFileBytes = 256 * 1024;

    public static StringComparer RootComparer { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Match the workspace resolver's physical identity, including parent-directory links.
    public static string NormalizeRoot(string root)
        => Path.TrimEndingDirectorySeparator(SwarmCore.Missions.SandboxPathResolver.ResolvePhysicalPath(root));

    public static string RepositoryHooksPath(string repositoryRoot)
        => Path.Combine(NormalizeRoot(repositoryRoot), ExtensionFiles.RepositoryFolder, RepositoryHooksFileName);

    public async Task<IReadOnlyList<CodingHook>> GetHooksAsync(string? repositoryRoot, CancellationToken ct = default)
    {
        var o = options.Value.Hooks;
        if (o is null || !o.Enabled) return [];
        var hooks = new List<CodingHook>(CodingHook.From(o.All(), ExtensionSource.User, "settings.json Coding:Hooks"));
        var repo = await InspectRepositoryAsync(repositoryRoot, ct).ConfigureAwait(false);
        if (repo is { Approved: true, Problem: null }) hooks.AddRange(repo.Hooks);
        return hooks;
    }

    public async Task<RepoHookInspection?> InspectRepositoryAsync(string? repositoryRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
        var root = NormalizeRoot(repositoryRoot);
        var path = RepositoryHooksPath(root);
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new RepoHookInspection(path, false, null, false, [], null);
            if (info.Length > MaxHooksFileBytes) return new RepoHookInspection(path, true, null, false, [], $"hooks.json is larger than {MaxHooksFileBytes / 1024} KB.");
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RepoHookInspection(path, true, null, false, [], "hooks.json could not be read.");
        }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        IReadOnlyList<CodingHook> hooks;
        try { hooks = ParseHooksFile(bytes, path); }
        catch (JsonException ex) { return new RepoHookInspection(path, true, sha, false, [], "hooks.json is not valid JSON: " + ex.Message); }
        var approved = hooks.Count > 0 && await approvals.IsApprovedAsync(root, sha, ct).ConfigureAwait(false);
        return new RepoHookInspection(path, true, sha, approved, hooks, null);
    }

    /// <summary>
    /// Parses <c>{ "hooks": { "PreToolUse": [ ... ] } }</c> or the same object without the <c>hooks</c> wrapper. Unknown
    /// events are ignored. Repository hooks always carry <see cref="ExtensionSource.Repository"/>.
    /// </summary>
    public static IReadOnlyList<CodingHook> ParseHooksFile(byte[] utf8, string origin)
    {
        using var doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The top level must be an object.");
        if (TryGet(root, "hooks", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) root = wrapped;
        var definitions = new List<(CodingHookEvent, CodingHookDefinition)>();
        foreach (var property in root.EnumerateObject())
        {
            if (!Enum.TryParse<CodingHookEvent>(property.Name, ignoreCase: true, out var evt) || property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in property.Value.EnumerateArray())
                if (ReadDefinition(item) is { } definition) definitions.Add((evt, definition));
        }
        return CodingHook.From(definitions, ExtensionSource.Repository, origin);
    }

    private static CodingHookDefinition? ReadDefinition(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var definition = new CodingHookDefinition
        {
            Matcher = String(item, "matcher"),
            Command = String(item, "command"),
            TimeoutSeconds = Int(item, "timeoutSeconds"),
            Timeout = Int(item, "timeout"),
        };
        if (TryGet(item, "hooks", out var nested) && nested.ValueKind == JsonValueKind.Array)
            definition.Hooks = nested.EnumerateArray().Select(ReadDefinition).Where(d => d is not null).Select(d => d!).ToList();
        return definition;
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string? String(JsonElement e, string name) => TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
