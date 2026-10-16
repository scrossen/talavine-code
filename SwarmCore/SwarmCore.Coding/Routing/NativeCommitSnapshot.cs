using System.Security.Cryptography;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.Routing;

/// <summary>Host evidence for a confined native edit, never a model-supplied commit manifest.</summary>
internal sealed class NativeCommitSnapshot(string root, string head, string index, HashSet<string> dirty)
{
    private string? unavailable;
    private readonly Dictionary<string, (string Tool, string Path)> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> written = new(PathComparer);
    internal bool HasSuccessfulWrites { get; private set; }
    // Default macOS volumes are case-insensitive too. On a case-sensitive Mac volume this only
    // withholds a potential commit; it must never let a case alias include preexisting changes.
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static async Task<NativeCommitSnapshot> CaptureAsync(string root, CancellationToken ct)
    {
        try { return await CaptureRepositoryAsync(root, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reads/edits may still run, but missing baseline evidence never authorizes a commit.
            return new(root, "", "", new(PathComparer)) { unavailable = ex.Message };
        }
    }
    private static async Task<NativeCommitSnapshot> CaptureRepositoryAsync(string root, CancellationToken ct)
    {
        var head = await ReadHeadAsync(root, ct);
        var index = await ReadGitAsync(root, ["ls-files", "--stage", "-z"], ct);
        var status = await ReadGitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        var dirty = new HashSet<string>(PathComparer);
        var parts = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length < 4) throw new InvalidDataException("Malformed repository status; a native commit cannot be scoped.");
            dirty.Add(Normalize(part[3..]));
            if (part[0] is 'R' or 'C' || part[1] is 'R' or 'C')
            {
                if (++i >= parts.Length) throw new InvalidDataException("Incomplete rename status; a native commit cannot be scoped.");
                dirty.Add(Normalize(parts[i]));
            }
        }
        return new(root, head, index, dirty);
    }

    internal async Task ObserveAsync(TurnEvent evt, CancellationToken ct)
    {
        if (evt is ToolCallStarted start && start.ToolName is "Write" or "Edit" or "MultiEdit" or "NotebookEdit")
        {
            try
            {
                var raw = start.Input[start.ToolName == "NotebookEdit" ? "notebook_path" : "file_path"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(raw) && RelativeFile(root, raw) is { } path)
                    pending[start.ToolCallId] = (start.ToolName, path);
            }
            catch (InvalidOperationException) { /* malformed provider receipt is not evidence */ }
        }
        else if (evt is ToolCallCompleted done && pending.Remove(done.ToolCallId, out var call)
                 && !done.IsError && done.WasDispatched && done.ToolName == call.Tool)
        {
            HasSuccessfulWrites = true;
            if (dirty.Contains(call.Path)) return;
            var hash = await FileHashAsync(root, call.Path, ct);
            if (hash is not null) written[call.Path] = hash;
        }
    }

    internal async Task<NativeCommitScope> FinishAsync(string conversation, CancellationToken ct)
    {
        var eligible = new Dictionary<string, string>(PathComparer);
        var evidence = unavailable;
        try
        {
            // No native command capability exists, so a changed HEAD/index is foreign or unknown work.
            if (evidence is null && head == await ReadHeadAsync(root, ct)
                && index == await ReadGitAsync(root, ["ls-files", "--stage", "-z"], ct))
            {
                foreach (var (path, hash) in written)
                    if (hash == await FileHashAsync(root, path, ct)) eligible[path] = hash;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // As in CaptureAsync: a repository that cannot be reread never authorizes a commit.
            eligible.Clear();
            evidence = ex.Message;
        }
        return new(conversation, root, head, index, eligible) { UnavailableEvidence = evidence };
    }

    internal static async Task<string> ReadGitAsync(string root, string[] args, CancellationToken ct)
    {
        var result = await GitProcess.RunAsync(root, args, TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("Repository evidence could not be read: " + GitProcess.ErrorText(result.ExitCode, result.Stdout, result.Stderr));
        return result.Stdout;
    }
    internal static async Task<string> ReadHeadAsync(string root, CancellationToken ct)
    {
        var result = await GitProcess.RunAsync(root, ["rev-parse", "--verify", "HEAD"], TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode == 0) return result.Stdout;
        // An unborn branch is a valid first-commit workspace; retain its branch identity.
        if (result.ExitCode == 128)
            return "unborn:" + await ReadGitAsync(root, ["symbolic-ref", "HEAD"], ct);
        throw new InvalidOperationException("Repository HEAD could not be read: " + GitProcess.ErrorText(result.ExitCode, result.Stdout, result.Stderr));
    }

    internal static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    internal static string? RelativeFile(string root, string raw)
    {
        var absolute = Path.GetFullPath(raw, root);
        var relative = Normalize(Path.GetRelativePath(root, absolute));
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            || relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase))) return null;
        var current = root;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
        }
        return relative;
    }
    internal static async Task<string?> FileHashAsync(string root, string path, CancellationToken ct)
    {
        if (RelativeFile(root, path) is null) return null;
        try
        {
            await using var file = new FileStream(Path.Combine(root, path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            return OperatingSystem.IsWindows() ? hash : hash + ":" + (int)File.GetUnixFileMode(Path.Combine(root, path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>One host-owned local commit opportunity; changes after a receipt are never silently included.</summary>
internal sealed class NativeCommitScope(string conversation, string root, string head, string index, IReadOnlyDictionary<string, string> hashes)
{
    private static readonly AsyncLocal<NativeCommitScope?> ambient = new();
    private int attempted;
    internal static NativeCommitScope? Current => ambient.Value;
    internal IReadOnlyList<string> Paths => hashes.Keys.Order(StringComparer.Ordinal).ToArray();
    internal string? UnavailableEvidence { get; init; }
    internal IDisposable Enter()
    {
        var previous = ambient.Value; ambient.Value = this; return new Lease(() => ambient.Value = previous);
    }
    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }

    internal async Task<string?> ValidateAsync(GitCommitTool.Input input, string selectedRoot, CancellationToken ct)
    {
        if (!NativeCommitSnapshot.PathComparer.Equals(Path.GetFullPath(root), Path.GetFullPath(selectedRoot))
            || input.AllowEmpty || input.Paths is not { Count: > 0 })
            return "The native finishing step requires explicit proven files in its original repository.";
        foreach (var raw in input.Paths)
        {
            var path = NativeCommitSnapshot.RelativeFile(root, raw);
            if (path is null || !hashes.TryGetValue(path, out var expected))
                return "The native finishing step cannot commit preexisting or unproven changes: " + raw;
            if (expected != await NativeCommitSnapshot.FileHashAsync(root, path, ct))
                return "A native-written file changed after its receipt; it was not staged or committed: " + raw;
        }
        var turn = AsyncLocalCurrentTurnContextAccessor.Ambient;
        if (turn is null || turn.ConversationId != conversation || !turn.CommunicationScopeUserInitiated || turn.IsUnattended
            || turn.ActingAgentId is not null || turn.RepairAuthority is not null || turn.CodingRepairGrant is not null || turn.MissionObjectiveId is not null
            || turn.WorkspaceRoot is null || !NativeCommitSnapshot.PathComparer.Equals(Path.GetFullPath(turn.WorkspaceRoot), Path.GetFullPath(root))
            || turn.AdmittedToolIds?.Contains(GitCommitTool.ToolId) != true)
            return "The native finishing step needs the original attended user request and an admitted git.commit tool.";
        if (await ValidateRepositoryAsync(ct) is { } changed) return changed;
        return Interlocked.CompareExchange(ref attempted, 1, 0) == 0 ? null
            : "The native commit was already attempted. Inspect its saved receipt instead of retrying an uncertain effect.";
    }
    internal async Task<string?> ValidateRepositoryAsync(CancellationToken ct)
        => head != await NativeCommitSnapshot.ReadHeadAsync(root, ct)
           || index != await NativeCommitSnapshot.ReadGitAsync(root, ["ls-files", "--stage", "-z"], ct)
            ? "The repository HEAD or index changed after the native attempt; no commit was made." : null;

    internal async Task<NativeCommitFiles> PrepareFilesAsync(IReadOnlyList<string> requested, CancellationToken ct)
    {
        var files = new NativeCommitFiles(Directory.CreateTempSubdirectory("swarmcore-native-commit-").FullName);
        try
        {
            foreach (var raw in requested)
            {
                var relative = NativeCommitSnapshot.RelativeFile(root, raw);
                if (relative is null || !hashes.TryGetValue(relative, out var expected))
                    throw new InvalidOperationException("An unproven path cannot enter the native commit snapshot.");
                var canonical = hashes.Keys.First(path => NativeCommitSnapshot.PathComparer.Equals(path, relative));
                var sourcePath = Path.Combine(root, canonical);
                var destination = Path.Combine(files.Root, canonical);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    await source.CopyToAsync(output, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(sourcePath));
                if (expected != await NativeCommitSnapshot.FileHashAsync(files.Root, canonical, ct)
                    || expected != await NativeCommitSnapshot.FileHashAsync(root, canonical, ct))
                    throw new InvalidOperationException("A native-written file changed while its commit snapshot was created; nothing was staged.");
                files.Paths.Add(canonical);
            }
            return files;
        }
        catch { files.Dispose(); throw; }
    }
}

/// <summary>Owned immutable input for git's --only worktree reread, never a model-chosen worktree.</summary>
internal sealed class NativeCommitFiles(string root) : IDisposable
{
    internal string Root { get; } = root;
    internal List<string> Paths { get; } = [];
    internal string[] Arguments(IEnumerable<string> command) => ["--work-tree=" + Root, .. command];
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide an already completed local commit receipt or invite its replay.
            System.Diagnostics.Trace.TraceWarning("Owned native commit snapshot cleanup failed: {0}", ex.Message);
        }
    }
}
