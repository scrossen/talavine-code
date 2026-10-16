using System.Text;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.Checkpoints;

/// <summary>
/// Host-neutral turn checkpoints over git, so a host can undo the file edits of one coding turn whatever ran it (the
/// API engine or a native CLI lane that edits files directly).
/// <para>
/// Snapshot: the working tree (tracked and untracked files, .gitignore respected) is written into a temporary index
/// (<c>GIT_INDEX_FILE</c>, seeded from a copy of the person's index for its stat cache) with <c>git add -A</c> and
/// <c>git write-tree</c>. The person's index, HEAD, branches and stash are never touched; the tree is kept alive by a
/// private ref <c>{RefNamespace}/{conversation}/{checkpoint}/pre</c> (and <c>/post</c>, a <c>/meta</c> blob with the
/// pre/post HEAD, and <c>/undo</c> holding the content an undo replaced). Refs point at trees and blobs, so
/// <c>git log --all</c> and history views do not show them.
/// </para>
/// <para>
/// Undo restores exactly the paths the turn changed (pre tree vs post tree) and deletes files it created. A path whose
/// current content differs from both the turn's result and the pre-turn content changed again afterwards (the person,
/// another conversation or a later turn edited it): it is reported as a conflict and only written with
/// <see cref="TurnUndoConflictMode.Force"/>. Commits are never undone; <c>.git</c> is never written. Every git call is
/// the harness's fenced git (<see cref="GitProcess"/>).
/// </para>
/// </summary>
public sealed class TurnCheckpointService
{
    private readonly Func<TurnCheckpointOptions> _options;
    private readonly SemaphoreSlim _undoGate = new(1, 1);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    public TurnCheckpointService(IOptions<CodingOptions> options) => _options = () => options.Value.Checkpoints;

    /// <summary>For tests and hosts without options.</summary>
    public TurnCheckpointService(TurnCheckpointOptions options) => _options = () => options;

    private TurnCheckpointOptions Options => _options();

    // ---- begin / complete ---------------------------------------------------

    /// <summary>
    /// Snapshots the repository that contains <paramref name="workspaceRoot"/> before a turn. Never throws for git or
    /// file-system problems: the reason comes back instead, and the host runs the turn without an undo.
    /// </summary>
    public async Task<TurnCheckpointStart> BeginAsync(string workspaceRoot, string conversationId, CancellationToken ct = default)
    {
        var options = Options;
        if (!options.Enabled) return TurnCheckpointStart.Unavailable("Turn checkpoints are turned off.");
        string key;
        try { key = RefKey(conversationId); }
        catch (ArgumentException e) { return TurnCheckpointStart.Unavailable(e.Message); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.SnapshotTimeout);
        try
        {
            var (repo, failure, notRepo) = await OpenAsync(workspaceRoot, budget.Token).ConfigureAwait(false);
            if (repo is null) return TurnCheckpointStart.Unavailable(failure!, notRepo);
            var head = repo.Head;
            var (tree, error) = await SnapshotAsync(repo, options, budget.Token).ConfigureAwait(false);
            if (tree is null) return TurnCheckpointStart.Unavailable(error!);
            // No ref yet: the tree's fresh loose objects are far younger than any prune expiry, and a turn that changes
            // nothing (or a process that dies mid-turn) then leaves nothing behind. CompleteAsync writes the refs.
            var id = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
            return new TurnCheckpointStart(new TurnCheckpoint
            {
                RepositoryRoot = repo.Top, ConversationId = conversationId, CheckpointId = id, PreTree = tree, PreHead = head,
            }, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TurnCheckpointStart.Unavailable($"Taking the checkpoint took longer than {options.SnapshotTimeout.TotalSeconds:0}s.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TurnCheckpointStart.Unavailable("Could not take the checkpoint: " + e.Message);
        }
    }

    /// <summary>
    /// Snapshots the repository after the turn and records what it changed. A turn that changed nothing keeps no
    /// checkpoint. Afterwards only the newest <see cref="TurnCheckpointOptions.MaxCheckpointsPerConversation"/> are kept.
    /// </summary>
    public async Task<TurnCheckpointRecord> CompleteAsync(TurnCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var options = Options;
        var key = RefKey(checkpoint.ConversationId);
        var refBase = RefBase(options, key, checkpoint.CheckpointId);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.SnapshotTimeout);
        try
        {
            var (repo, failure, _) = await OpenAsync(checkpoint.RepositoryRoot, budget.Token).ConfigureAwait(false);
            if (repo is null) return Unavailable(failure!);
            var head = repo.Head;
            var (tree, error) = await SnapshotAsync(repo, options, budget.Token).ConfigureAwait(false);
            if (tree is null) return Unavailable(error!);

            var changes = await ChangesAsync(repo, checkpoint.PreTree, tree, budget.Token).ConfigureAwait(false);
            if (changes is null) return Unavailable("Could not compare the snapshots.");
            var headMoved = !string.Equals(checkpoint.PreHead, head, StringComparison.Ordinal);
            if (changes.Count == 0) return new TurnCheckpointRecord(checkpoint.CheckpointId, repo.Top, [], headMoved);

            var meta = await repo.RunAsync(["hash-object", "-w", "--no-filters", "--stdin"], budget.Token,
                standardInput: $"preHead={checkpoint.PreHead}\npostHead={head}\n").ConfigureAwait(false);
            if (meta.ExitCode != 0) return Unavailable("Could not save the checkpoint.");
            // One transaction: the checkpoint exists with all three refs or not at all.
            var save = await repo.RunAsync(["update-ref", "--stdin"], budget.Token,
                standardInput: $"update {refBase}/pre {checkpoint.PreTree}\nupdate {refBase}/post {tree}\nupdate {refBase}/meta {meta.Stdout.Trim()}\n").ConfigureAwait(false);
            if (save.ExitCode != 0) return Unavailable("Could not save the checkpoint: " + GitProcess.FirstLine(GitProcess.ErrorText(save.ExitCode, save.Stdout, save.Stderr)));
            await PruneAsync(repo, options, key, budget.Token).ConfigureAwait(false);
            return new TurnCheckpointRecord(checkpoint.CheckpointId, repo.Top, changes, headMoved);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable($"Taking the post-turn snapshot took longer than {options.SnapshotTimeout.TotalSeconds:0}s.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unavailable("Could not take the post-turn snapshot: " + e.Message);
        }

        TurnCheckpointRecord Unavailable(string reason) => new(checkpoint.CheckpointId, checkpoint.RepositoryRoot, [], false, reason);
    }

    // ---- undo -----------------------------------------------------------------

    /// <summary>What <see cref="UndoAsync"/> would do now; changes nothing in the working tree.</summary>
    public Task<TurnUndoResult> PreviewUndoAsync(string repositoryRoot, string conversationId, string checkpointId, CancellationToken ct = default)
        => RunUndoAsync(repositoryRoot, conversationId, checkpointId, TurnUndoConflictMode.Refuse, previewOnly: true, ct);

    /// <summary>
    /// Restores the paths the turn changed to their pre-turn content and deletes the files it created.
    /// <paramref name="mode"/> decides what happens to paths that changed again after the turn.
    /// </summary>
    public Task<TurnUndoResult> UndoAsync(string repositoryRoot, string conversationId, string checkpointId, TurnUndoConflictMode mode, CancellationToken ct = default)
        => RunUndoAsync(repositoryRoot, conversationId, checkpointId, mode, previewOnly: false, ct);

    private async Task<TurnUndoResult> RunUndoAsync(string repositoryRoot, string conversationId, string checkpointId, TurnUndoConflictMode mode, bool previewOnly, CancellationToken ct)
    {
        var options = Options;
        string key;
        try { key = RefKey(conversationId); RefKey(checkpointId); }
        catch (ArgumentException e) { return Result(TurnUndoStatus.Unavailable, e.Message); }
        await _undoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (repo, failure, notRepo) = await OpenAsync(repositoryRoot, ct).ConfigureAwait(false);
            if (repo is null)
                return Result(TurnUndoStatus.Unavailable, notRepo ? "Undo is unavailable: this folder is not a git repository." : "Undo is unavailable: " + failure);
            var refBase = RefBase(options, key, checkpointId);
            var refs = await ListRefsAsync(repo, refBase + "/", ct).ConfigureAwait(false);
            if (!refs.TryGetValue(refBase + "/pre", out var pre) || !refs.TryGetValue(refBase + "/post", out var post))
                return Result(TurnUndoStatus.Unavailable, "Undo is unavailable: the checkpoint for this turn is no longer kept.");
            var headMoved = false;
            if (refs.TryGetValue(refBase + "/meta", out var metaBlob))
            {
                var meta = await repo.RunAsync(["cat-file", "blob", metaBlob], ct).ConfigureAwait(false);
                if (meta.ExitCode == 0)
                {
                    var lines = meta.Stdout.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim());
                    headMoved = !string.Equals(lines.GetValueOrDefault("preHead"), lines.GetValueOrDefault("postHead"), StringComparison.Ordinal);
                }
            }

            var changes = await ChangesAsync(repo, pre, post, ct).ConfigureAwait(false);
            if (changes is null) return Result(TurnUndoStatus.Failed, "Could not read what the turn changed.");
            if (changes.Count == 0) return Result(TurnUndoStatus.NothingToUndo, "This turn changed no files.") with { HeadMoved = headMoved };

            // The current content of exactly the changed paths, on top of the turn's result.
            var (now, nowError) = await CurrentTreeAsync(repo, post, changes, ct).ConfigureAwait(false);
            if (now is null) return Result(TurnUndoStatus.Failed, "Could not read the current files: " + nowError) with { Changes = changes, HeadMoved = headMoved };
            var differsFromPost = await DiffPathsAsync(repo, post, now, ct).ConfigureAwait(false);
            var differsFromPre = await DiffPathsAsync(repo, pre, now, ct).ConfigureAwait(false);
            if (differsFromPost is null || differsFromPre is null)
                return Result(TurnUndoStatus.Failed, "Could not compare the current files with the checkpoint.") with { Changes = changes, HeadMoved = headMoved };

            var conflicts = changes.Where(c => differsFromPost.Contains(c.Path) && differsFromPre.Contains(c.Path)).Select(c => c.Path).ToList();
            var already = changes.Where(c => !differsFromPre.Contains(c.Path)).Select(c => c.Path).ToList();
            var baseResult = new TurnUndoResult
            {
                Status = TurnUndoStatus.Undone, Summary = "", Changes = changes, Conflicts = conflicts, AlreadyRestored = already, HeadMoved = headMoved,
            };
            if (already.Count == changes.Count)
                return baseResult with { Status = TurnUndoStatus.NothingToUndo, Summary = "The files this turn changed already have their pre-turn content." + CommitNote(headMoved) };
            if (previewOnly || (mode == TurnUndoConflictMode.Refuse && conflicts.Count > 0))
            {
                var status = conflicts.Count > 0 ? TurnUndoStatus.ConflictsNeedDecision : TurnUndoStatus.Undone;
                var summary = conflicts.Count > 0
                    ? $"{Count(conflicts.Count, "file")} changed again after this turn. Undo anyway to overwrite them, or skip them."
                    : $"Undo restores {Count(changes.Count - already.Count, "file")} to their content before this turn.";
                return baseResult with { Status = status, Summary = summary + CommitNote(headMoved) };
            }

            // Back up what is about to be replaced, then restore.
            var backupRef = refBase + "/undo";
            var backup = await repo.RunAsync(["update-ref", backupRef, now], ct).ConfigureAwait(false);
            if (backup.ExitCode != 0)
                return baseResult with { Status = TurnUndoStatus.Failed, Summary = "Could not save a backup of the current files, so nothing was changed." };

            var skip = new HashSet<string>(already, StringComparer.Ordinal);
            var kept = new List<string>();
            if (mode == TurnUndoConflictMode.Skip) { skip.UnionWith(conflicts); kept.AddRange(conflicts); }
            var work = changes.Where(c => !skip.Contains(c.Path)).ToList();
            var restoreSet = work.Where(c => c.Kind != TurnFileChangeKind.Added).Select(c => c.Path).ToList();
            var created = work.Where(c => c.Kind == TurnFileChangeKind.Added).Select(c => c.Path).ToList();
            var deleted = new List<string>();

            // A file the turn created where a pre-turn directory was must go before that directory's files come back.
            foreach (var path in created.Where(p => restoreSet.Any(r => r.StartsWith(p + "/", StringComparison.Ordinal))).ToList())
            {
                if (TryDeleteCreated(repo.Top, path, out var why)) deleted.Add(path); else kept.Add(path + why);
                created.Remove(path);
            }

            if (restoreSet.Count > 0)
            {
                var checkout = await repo.RunAsync(["checkout", pre, "--pathspec-from-file=-", "--pathspec-file-nul"], ct,
                    TemporaryIndexEnvironment(out var tempIndex), string.Join('\0', restoreSet)).ConfigureAwait(false);
                DeleteQuietly(tempIndex);
                if (checkout.ExitCode != 0)
                    return baseResult with
                    {
                        Status = TurnUndoStatus.Failed, Deleted = deleted, BackupRef = backupRef,
                        Summary = "Could not restore the files: " + GitProcess.FirstLine(GitProcess.ErrorText(checkout.ExitCode, checkout.Stdout, checkout.Stderr)),
                    };
            }

            // A path ignored by the (restored) pre-turn rules existed before the turn even though the snapshot did not
            // list it (the turn un-ignored it): keep it.
            if (created.Count > 0)
            {
                var ignored = await repo.RunAsync(["check-ignore", "--no-index", "-z", "--stdin"], ct,
                    new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "0" }, standardInput: string.Join('\0', created) + "\0").ConfigureAwait(false);
                if (ignored.ExitCode is 0 or 1)
                {
                    foreach (var path in ignored.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        if (created.Remove(path)) kept.Add(path + " (ignored before this turn)");
                }
                else
                {
                    kept.AddRange(created.Select(p => p + " (could not check .gitignore)"));
                    created.Clear();
                }
            }
            foreach (var path in created)
            {
                if (TryDeleteCreated(repo.Top, path, out var why)) deleted.Add(path); else kept.Add(path + why);
            }

            var restoredCount = restoreSet.Count + deleted.Count;
            var text = new StringBuilder($"Undid this turn's changes to {Count(restoredCount, "file")}.");
            if (kept.Count > 0) text.Append($" Left {Count(kept.Count, "file")} as {(kept.Count == 1 ? "it is" : "they are")}.");
            text.Append(CommitNote(headMoved));
            return baseResult with
            {
                Status = restoredCount > 0 ? TurnUndoStatus.Undone : TurnUndoStatus.NothingToUndo,
                Summary = text.ToString(), Restored = restoreSet, Deleted = deleted, Kept = kept, BackupRef = backupRef,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result(TurnUndoStatus.Failed, "Undo failed: " + e.Message);
        }
        finally { _undoGate.Release(); }

        static TurnUndoResult Result(TurnUndoStatus status, string summary) => new() { Status = status, Summary = summary };
    }

    private static string CommitNote(bool headMoved)
        => headMoved ? " This turn also made or changed commits; undo restores files only and leaves commits as they are." : "";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // ---- retention ------------------------------------------------------------

    /// <summary>Deletes every checkpoint ref of the conversation (when its session is deleted). Best effort.</summary>
    public async Task DeleteConversationAsync(string repositoryRoot, string conversationId, CancellationToken ct = default)
    {
        string key;
        try { key = RefKey(conversationId); }
        catch (ArgumentException) { return; }
        var (repo, _, _) = await OpenAsync(repositoryRoot, ct).ConfigureAwait(false);
        if (repo is null) return;
        await DeleteRefsAsync(repo, $"{Options.RefNamespace}/{key}/", ct).ConfigureAwait(false);
    }

    /// <summary>The checkpoint ids kept for a conversation, oldest first.</summary>
    public async Task<IReadOnlyList<string>> ListCheckpointsAsync(string repositoryRoot, string conversationId, CancellationToken ct = default)
    {
        var key = RefKey(conversationId);
        var (repo, _, _) = await OpenAsync(repositoryRoot, ct).ConfigureAwait(false);
        if (repo is null) return [];
        return CheckpointIds(await ListRefsAsync(repo, $"{Options.RefNamespace}/{key}/", ct).ConfigureAwait(false), $"{Options.RefNamespace}/{key}/");
    }

    private async Task PruneAsync(Repo repo, TurnCheckpointOptions options, string key, CancellationToken ct)
    {
        var prefix = $"{options.RefNamespace}/{key}/";
        var refs = await ListRefsAsync(repo, prefix, ct).ConfigureAwait(false);
        var ids = CheckpointIds(refs, prefix);
        var excess = ids.Count - Math.Max(1, options.MaxCheckpointsPerConversation);
        if (excess <= 0) return;
        var doomed = ids.Take(excess).Select(id => prefix + id + "/").ToList();
        await DeleteRefNamesAsync(repo, refs.Keys.Where(r => doomed.Any(d => r.StartsWith(d, StringComparison.Ordinal))), ct).ConfigureAwait(false);
    }

    private static List<string> CheckpointIds(IReadOnlyDictionary<string, string> refs, string prefix)
        => refs.Keys.Select(r => r[prefix.Length..]).Select(r => r.Split('/')[0]).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();

    private static async Task DeleteRefsAsync(Repo repo, string prefix, CancellationToken ct)
    {
        try
        {
            var refs = await ListRefsAsync(repo, prefix, ct).ConfigureAwait(false);
            await DeleteRefNamesAsync(repo, refs.Keys, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private static async Task DeleteRefNamesAsync(Repo repo, IEnumerable<string> names, CancellationToken ct)
    {
        var lines = string.Concat(names.Select(n => $"delete {n}\n"));
        if (lines.Length == 0) return;
        await repo.RunAsync(["update-ref", "--stdin"], ct, standardInput: lines).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> ListRefsAsync(Repo repo, string prefix, CancellationToken ct)
    {
        var list = await repo.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)", prefix.TrimEnd('/')], ct).ConfigureAwait(false);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.ExitCode != 0) return refs;
        foreach (var line in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.LastIndexOf(' ');
            if (space > 0 && line.StartsWith(prefix, StringComparison.Ordinal)) refs[line[..space]] = line[(space + 1)..];
        }
        return refs;
    }

    // ---- git plumbing -----------------------------------------------------------

    /// <summary>One open repository: its top-level directory, index path and the driver overrides every call carries.</summary>
    private sealed class Repo(string top, string indexPath, IReadOnlyList<string> drivers)
    {
        public string Top { get; } = top;
        public string IndexPath { get; } = indexPath;
        /// <summary>HEAD's commit when the repository was opened; null on an unborn branch.</summary>
        public string? Head { get; init; }

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args, CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null, string? standardInput = null)
        {
            var env = new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            // Every path handed to git here is a literal file name, never a glob or pathspec magic (check-ignore, which
            // refuses pathspec magic, opts out and matches its paths literally anyway).
            env.TryAdd("GIT_LITERAL_PATHSPECS", "1");
            return GitProcess.RunWithDriversAsync(Top, args, drivers, CallTimeout, ct, env, standardInput);
        }
    }

    private static async Task<(Repo? Repo, string? Failure, bool NotARepository)> OpenAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return (null, "the folder does not exist.", false);
        // The driver listing (it finds the same work tree from any folder inside it) runs beside one call for the work
        // tree, the index path and HEAD; an unborn HEAD fails that call, so it is asked again without HEAD.
        var driverQuery = GitProcess.QueryDriversAsync(directory, CallTimeout, ct);
        var probe = await GitProcess.RunAsync(directory, ["rev-parse", "--show-toplevel", "--git-path", "index", "HEAD^{commit}"], CallTimeout, ct).ConfigureAwait(false);
        var hasHead = probe.ExitCode == 0;
        if (!hasHead) probe = await GitProcess.RunAsync(directory, ["rev-parse", "--show-toplevel", "--git-path", "index"], CallTimeout, ct).ConfigureAwait(false);
        var (failure, drivers) = await driverQuery.ConfigureAwait(false);
        if (probe.ExitCode != 0)
        {
            var notRepo = probe.ExitCode > 0 && probe.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
            return (null, notRepo ? "this folder is not a git repository." : GitProcess.FirstLine(GitProcess.ErrorText(probe.ExitCode, probe.Stdout, probe.Stderr)), notRepo);
        }
        var lines = probe.Stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < (hasHead ? 3 : 2)) return (null, "git did not report the repository's work tree.", false);
        var root = Path.GetFullPath(lines[0]);
        if (failure is { } f) return (null, f.Stderr.Length > 0 ? f.Stderr : "git is not available.", false);
        return (new Repo(root, Path.GetFullPath(lines[1], Path.GetFullPath(directory)), drivers) { Head = hasHead ? lines[2] : null }, null, false);
    }

    private static Dictionary<string, string> TemporaryIndexEnvironment(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "swarmcore-checkpoint-" + Guid.NewGuid().ToString("N") + ".index");
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = path };
    }

    private static void DeleteQuietly(string path)
    {
        foreach (var file in new[] { path, path + ".lock" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The working tree as a tree object, written through a temporary index; the person's index is only read (copied).</summary>
    private static async Task<(string? Tree, string? Error)> SnapshotAsync(Repo repo, TurnCheckpointOptions options, CancellationToken ct)
    {
        var untracked = await repo.RunAsync(["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
        if (untracked.ExitCode != 0) return (null, "git could not list untracked files: " + GitProcess.FirstLine(GitProcess.ErrorText(untracked.ExitCode, untracked.Stdout, untracked.Stderr)));
        var files = untracked.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (files.Length > options.MaxUntrackedFiles)
            return (null, $"the repository has {files.Length:N0} untracked files, more than the checkpoint limit of {options.MaxUntrackedFiles:N0}.");
        long bytes = 0;
        foreach (var file in files)
        {
            try { bytes += new FileInfo(Path.Combine(repo.Top, file)).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            if (bytes > options.MaxUntrackedBytes)
                return (null, $"untracked files exceed the checkpoint limit of {options.MaxUntrackedBytes / (1024 * 1024):N0} MB.");
        }

        var env = TemporaryIndexEnvironment(out var tempIndex);
        try
        {
            // A copy of the person's index carries git's stat cache, so unchanged tracked files are not re-hashed.
            var seeded = false;
            try
            {
                if (File.Exists(repo.IndexPath)) { File.Copy(repo.IndexPath, tempIndex); seeded = true; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DeleteQuietly(tempIndex); }
            var add = await repo.RunAsync(["add", "-A"], ct, env).ConfigureAwait(false);
            if (add.ExitCode != 0 && seeded)
            {
                // An index git cannot use from another path (a split index): start from an empty one.
                DeleteQuietly(tempIndex);
                add = await repo.RunAsync(["add", "-A"], ct, env).ConfigureAwait(false);
            }
            if (add.ExitCode != 0) return (null, "git could not snapshot the working tree: " + GitProcess.FirstLine(GitProcess.ErrorText(add.ExitCode, add.Stdout, add.Stderr)));
            var write = await repo.RunAsync(["write-tree"], ct, env).ConfigureAwait(false);
            if (write.ExitCode != 0) return (null, "git could not write the snapshot: " + GitProcess.FirstLine(GitProcess.ErrorText(write.ExitCode, write.Stdout, write.Stderr)));
            return (write.Stdout.Trim(), null);
        }
        finally { DeleteQuietly(tempIndex); }
    }

    /// <summary>
    /// The turn's result (<paramref name="post"/>) with each changed path replaced by its current working-tree content
    /// (removed when it no longer exists), so only the changed paths are hashed.
    /// </summary>
    private static async Task<(string? Tree, string? Error)> CurrentTreeAsync(Repo repo, string post, IReadOnlyList<TurnFileChange> changes, CancellationToken ct)
    {
        var env = TemporaryIndexEnvironment(out var tempIndex);
        try
        {
            var read = await repo.RunAsync(["read-tree", post], ct, env).ConfigureAwait(false);
            if (read.ExitCode != 0) return (null, GitProcess.FirstLine(GitProcess.ErrorText(read.ExitCode, read.Stdout, read.Stderr)));
            // A pathspec must match something: a path in the turn's result (in the index now) or on disk.
            var paths = changes.Where(c => c.Kind != TurnFileChangeKind.Deleted || ExistsOnDisk(repo.Top, c.Path)).Select(c => c.Path).ToList();
            if (paths.Count > 0)
            {
                var add = await repo.RunAsync(["add", "-A", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, env, string.Join('\0', paths)).ConfigureAwait(false);
                if (add.ExitCode != 0) return (null, GitProcess.FirstLine(GitProcess.ErrorText(add.ExitCode, add.Stdout, add.Stderr)));
            }
            var write = await repo.RunAsync(["write-tree"], ct, env).ConfigureAwait(false);
            return write.ExitCode == 0 ? (write.Stdout.Trim(), null) : (null, GitProcess.FirstLine(GitProcess.ErrorText(write.ExitCode, write.Stdout, write.Stderr)));
        }
        finally { DeleteQuietly(tempIndex); }
    }

    private static bool ExistsOnDisk(string top, string path)
    {
        var full = Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null;
    }

    /// <summary>Files (blobs and symlinks) that differ between two trees; gitlinks (submodules, nested repositories) are left out.</summary>
    private static async Task<List<TurnFileChange>?> ChangesAsync(Repo repo, string from, string to, CancellationToken ct)
    {
        var diff = await repo.RunAsync(["diff-tree", "-r", "-z", "--raw", "--no-renames", "--no-ext-diff", "--no-textconv", from, to], ct).ConfigureAwait(false);
        if (diff.ExitCode != 0) return null;
        var changes = new List<TurnFileChange>();
        var fields = diff.Stdout.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            var header = fields[i];
            if (!header.StartsWith(':')) continue;
            var path = fields[++i];
            var parts = header[1..].Split(' ');
            if (parts.Length < 5 || path.Length == 0) continue;
            if (parts[0] == "160000" || parts[1] == "160000" || IsGitMetadata(path)) continue;
            var kind = parts[4][0] switch { 'A' => TurnFileChangeKind.Added, 'D' => TurnFileChangeKind.Deleted, _ => TurnFileChangeKind.Modified };
            changes.Add(new TurnFileChange(path, kind));
        }
        return changes;
    }

    private static async Task<HashSet<string>?> DiffPathsAsync(Repo repo, string from, string to, CancellationToken ct)
    {
        var diff = await repo.RunAsync(["diff-tree", "-r", "-z", "--name-only", "--no-renames", "--no-ext-diff", "--no-textconv", from, to], ct).ConfigureAwait(false);
        return diff.ExitCode == 0 ? diff.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;
    }

    private static bool IsGitMetadata(string path)
        => path.Split('/').Any(part => part.TrimEnd('.', ' ').Equals(".git", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deletes a file the turn created: only inside the repository, never through a linked directory, never a directory
    /// or a <c>.git</c> path. Empty parent folders left behind are removed.
    /// </summary>
    private static bool TryDeleteCreated(string top, string path, out string why)
    {
        why = "";
        if (IsGitMetadata(path)) { why = " (git metadata)"; return false; }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(top));
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) { why = " (outside the repository)"; return false; }
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            var info = new DirectoryInfo(dir);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { why = " (inside a linked folder)"; return false; }
        }
        var file = new FileInfo(full);
        if (Directory.Exists(full) && file.LinkTarget is null) { why = " (a folder is there now)"; return false; }
        if (!file.Exists && file.LinkTarget is null) return true; // already gone
        try
        {
            if (file.Exists && file.IsReadOnly) file.IsReadOnly = false;
            File.Delete(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = $" (could not delete: {e.Message})"; return false; }
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
        }
        return true;
    }

    // ---- names --------------------------------------------------------------------

    private static string RefBase(TurnCheckpointOptions options, string key, string checkpointId) => $"{options.RefNamespace}/{key}/{checkpointId}";

    /// <summary>A ref-name component for an id: ASCII letters, digits, '-' and '_' only (anything else refused).</summary>
    internal static string RefKey(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
            throw new ArgumentException("The conversation or checkpoint id cannot name a checkpoint (use 1-128 ASCII letters, digits, '-' or '_').");
        return id;
    }
}
