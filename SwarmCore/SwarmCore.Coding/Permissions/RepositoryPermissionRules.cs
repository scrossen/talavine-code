using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Coding.Extensions;

namespace SwarmCore.Coding.Permissions;

/// <summary>One repository settings file that carries permission rules.</summary>
/// <param name="Path">The file read.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the exact bytes read (null when unreadable).</param>
/// <param name="Allow">Allow globs the file asks for (empty when <paramref name="Problem"/> is set).</param>
/// <param name="Deny">Deny globs the file asks for (empty when <paramref name="Problem"/> is set).</param>
/// <param name="Problem">Why the file contributes nothing (too large, unreadable, malformed), or null.</param>
public sealed record RepositoryPermissionFile(string Path, string? Sha256, IReadOnlyList<string> Allow, IReadOnlyList<string> Deny, string? Problem);

/// <summary>
/// What a repository's settings files ask for, and whether the person approved that exact content. Deny rules only
/// restrict, so they always apply; allow rules widen what runs unasked, so they apply only once the person approved
/// the current content (<see cref="Sha256"/>, one digest over every file read, so a change to any file asks again).
/// </summary>
public sealed record RepositoryPermissionInspection(string? RepositoryRoot, IReadOnlyList<RepositoryPermissionFile> Files, string? Sha256, bool Approved)
{
    public static RepositoryPermissionInspection None { get; } = new(null, [], null, false);

    /// <summary>At least one settings file exists.</summary>
    public bool Exists => Files.Count > 0;

    /// <summary>Deny globs from every readable file. These always apply.</summary>
    public IReadOnlyList<string> Deny => Files.Where(f => f.Problem is null).SelectMany(f => f.Deny).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Allow globs from every readable file. These apply only when <see cref="Approved"/>.</summary>
    public IReadOnlyList<string> Allow => Files.Where(f => f.Problem is null).SelectMany(f => f.Allow).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Problems with files that contributed nothing.</summary>
    public IReadOnlyList<string> Problems => Files.Where(f => f.Problem is not null).Select(f => $"{f.Path}: {f.Problem}").ToArray();

    /// <summary>The files ask for allow rules and the current content is not approved.</summary>
    public bool NeedsApproval => Allow.Count > 0 && !Approved && Sha256 is not null;
}

/// <summary>
/// Per-repository approval of the exact permission settings content. Keyed by repository root and the SHA-256 that
/// <see cref="RepositoryPermissionRules.InspectAsync"/> computes over the files read, so any change needs a fresh
/// approval. Hosts store it outside the repository (SwarmCode: its data directory).
/// </summary>
public interface ICodingRepositorySettingsApprovalStore
{
    Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default);
    Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default);
    Task RevokeAsync(string repositoryRoot, CancellationToken ct = default);
}

/// <summary>Process-lifetime approvals (the harness default; tests; hosts without durable storage).</summary>
public sealed class InMemoryCodingRepositorySettingsApprovalStore : ICodingRepositorySettingsApprovalStore
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

/// <summary>
/// Project-scoped permission rules: <c>&lt;repo&gt;/.swarmcode/settings.json</c> and, for compatibility,
/// <c>&lt;repo&gt;/.claude/settings.json</c>, each shaped <c>{ "permissions": { "allow": [...], "deny": [...] } }</c>
/// (Claude Code) or <c>{ "Coding": { "Permissions": { "Allow": [...], "Deny": [...] } } }</c> (SwarmCode settings).
/// Rules are SwarmCode tool-id globs with the <see cref="CodingPermissionRules"/> syntax. Both files are read when both
/// exist. Reading never throws: a missing file contributes nothing, and a file that is too large, unreadable or
/// malformed is reported through <see cref="RepositoryPermissionFile.Problem"/>.
/// </summary>
public static class RepositoryPermissionRules
{
    public const long MaxFileBytes = 256 * 1024;
    public const string SettingsFileName = "settings.json";

    /// <summary>The compatibility folder read after <see cref="ExtensionFiles.RepositoryFolder"/>.</summary>
    public const string ClaudeFolder = ".claude";

    /// <summary>The settings files, in read order, relative to the repository root.</summary>
    public static IReadOnlyList<string> RelativePaths { get; } =
        [Path.Combine(ExtensionFiles.RepositoryFolder, SettingsFileName), Path.Combine(ClaudeFolder, SettingsFileName)];

    /// <summary>Reads the repository's settings files and checks the approval of their current content.</summary>
    public static async Task<RepositoryPermissionInspection> InspectAsync(string? repositoryRoot, ICodingRepositorySettingsApprovalStore? approvals, CancellationToken ct = default)
    {
        var inspection = Inspect(repositoryRoot);
        if (inspection.Sha256 is null || inspection.Allow.Count == 0 || approvals is null) return inspection;
        bool approved;
        try { approved = await approvals.IsApprovedAsync(inspection.RepositoryRoot!, inspection.Sha256, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { approved = false; } // an unreadable approval store approves nothing (fail closed)
        return inspection with { Approved = approved };
    }

    /// <summary>Reads the repository's settings files without consulting any approval (so <see cref="RepositoryPermissionInspection.Approved"/> is false).</summary>
    public static RepositoryPermissionInspection Inspect(string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return RepositoryPermissionInspection.None;
        string root;
        try { root = CodingHookSource.NormalizeRoot(repositoryRoot); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException) { return RepositoryPermissionInspection.None; }

        var files = new List<RepositoryPermissionFile>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in RelativePaths)
        {
            var path = Path.Combine(root, relative);
            byte[] bytes;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (info.Length > MaxFileBytes)
                {
                    files.Add(new RepositoryPermissionFile(path, null, [], [], $"settings.json is larger than {MaxFileBytes / 1024} KB."));
                    continue;
                }
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                files.Add(new RepositoryPermissionFile(path, null, [], [], "settings.json could not be read."));
                continue;
            }
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            // The repository digest names each file so swapping the same content between files still asks again.
            digest.AppendData(Encoding.UTF8.GetBytes(relative + "\n" + sha + "\n"));
            try
            {
                var (allow, deny) = Parse(bytes);
                files.Add(new RepositoryPermissionFile(path, sha, allow, deny, null));
            }
            catch (JsonException ex)
            {
                files.Add(new RepositoryPermissionFile(path, sha, [], [], "settings.json is not valid JSON: " + ex.Message));
            }
        }
        if (files.Count == 0) return new RepositoryPermissionInspection(root, [], null, false);
        var hashed = files.Any(f => f.Sha256 is not null);
        return new RepositoryPermissionInspection(root, files, hashed ? Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() : null, false);
    }

    /// <summary>
    /// Parses one settings file. Accepts <c>permissions.allow/deny</c> and <c>Coding.Permissions.Allow/Deny</c> (names
    /// case-insensitive); other content is ignored. Throws <see cref="JsonException"/> for malformed JSON.
    /// </summary>
    public static (IReadOnlyList<string> Allow, IReadOnlyList<string> Deny) Parse(byte[] utf8)
    {
        using var doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The top level must be an object.");
        var allow = new List<string>();
        var deny = new List<string>();
        if (TryGet(root, "permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object) Read(permissions, allow, deny);
        if (TryGet(root, "coding", out var coding) && coding.ValueKind == JsonValueKind.Object
            && TryGet(coding, "permissions", out var nested) && nested.ValueKind == JsonValueKind.Object) Read(nested, allow, deny);
        return (Clean(allow), Clean(deny));
    }

    /// <summary>
    /// The rules a turn runs with: the person's own rules, every repository deny rule, and the repository allow rules
    /// only when the person approved the current content. Neither input is changed.
    /// </summary>
    public static CodingPermissionRules Merge(CodingPermissionRules? user, RepositoryPermissionInspection? repository)
    {
        var merged = new CodingPermissionRules
        {
            Allow = [.. user?.Allow ?? []],
            Deny = [.. user?.Deny ?? []],
        };
        if (repository is null) return merged;
        merged.Deny.AddRange(repository.Deny);
        if (repository.Approved) merged.Allow.AddRange(repository.Allow);
        merged.Allow = Clean(merged.Allow).ToList();
        merged.Deny = Clean(merged.Deny).ToList();
        return merged;
    }

    private static void Read(JsonElement permissions, List<string> allow, List<string> deny)
    {
        if (TryGet(permissions, "allow", out var a) && a.ValueKind == JsonValueKind.Array)
            allow.AddRange(a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        if (TryGet(permissions, "deny", out var d) && d.ValueKind == JsonValueKind.Array)
            deny.AddRange(d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string> patterns)
        => patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
