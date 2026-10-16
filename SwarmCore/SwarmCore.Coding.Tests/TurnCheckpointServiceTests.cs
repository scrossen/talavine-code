using SwarmCore.Coding.Checkpoints;
using SwarmCore.Testing;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// Turn checkpoints in real temporary repositories: the snapshot leaves the person's index, HEAD, branches and stash
/// alone; undo restores exactly the turn's paths, refuses silently overwriting later edits, and retention prunes refs.
/// </summary>
// One test here sets the process-wide GIT_CEILING_DIRECTORIES, so the class runs alone with the other
// process-environment tests instead of beside classes that start git in parallel.
[Collection(GitProcessEnvironmentCollection.Name)]
public sealed class TurnCheckpointServiceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("turn-checkpoint-");
    private const string Conversation = "conv1";

    public void Dispose()
    {
        try
        {
            foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            _root.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string NewRepo()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root.FullName, "r" + Guid.NewGuid().ToString("N")[..6])).FullName;
        Git(repo, "init", "-q");
        Git(repo, "config", "core.autocrlf", "false");
        Git(repo, "config", "user.name", "T");
        Git(repo, "config", "user.email", "t@example.com");
        Git(repo, "config", "commit.gpgsign", "false");
        Write(repo, "a.txt", "a1\n");
        Write(repo, "b.txt", "b1\n");
        Write(repo, ".gitignore", "build/\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "init");
        return repo;
    }

    private static string Git(string repo, params string[] args)
    {
        var (exit, output, error) = TestGit.Run(repo, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    private static void Write(string repo, string path, string text)
    {
        var full = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Read(string repo, string path) => File.ReadAllText(Path.Combine(repo, path));

    private static TurnCheckpointService Service(int keep = 50, int maxUntracked = 20_000)
        => new(new TurnCheckpointOptions { MaxCheckpointsPerConversation = keep, MaxUntrackedFiles = maxUntracked });

    private static async Task<TurnCheckpointRecord> TurnAsync(TurnCheckpointService service, string repo, Action edit, string conversation = Conversation)
    {
        var start = await service.BeginAsync(repo, conversation);
        Assert.True(start.Checkpoint is not null, start.UnavailableReason);
        edit();
        var record = await service.CompleteAsync(start.Checkpoint!);
        Assert.Null(record.UnavailableReason);
        return record;
    }

    [GitFact]
    public async Task Snapshot_leaves_index_head_branches_and_stash_untouched()
    {
        var repo = NewRepo();
        Write(repo, "a.txt", "stashed\n");
        Git(repo, "stash", "push", "-q", "-m", "person's stash");
        Write(repo, "a.txt", "staged\n");
        Git(repo, "add", "a.txt");
        Write(repo, "a.txt", "working\n");
        Write(repo, "untracked.txt", "mine\n");
        Write(repo, "build/out.bin", "ignored\n");
        var before = (Head: Git(repo, "rev-parse", "HEAD"), Stash: Git(repo, "stash", "list"), Staged: Git(repo, "diff", "--cached"),
            Status: Git(repo, "status", "--porcelain"), Branches: Git(repo, "for-each-ref", "refs/heads", "refs/tags", "refs/stash"),
            Index: File.ReadAllBytes(Path.Combine(repo, ".git", "index")), Log: Git(repo, "log", "--all", "--oneline"));

        var service = Service();
        var record = await TurnAsync(service, repo, () => Write(repo, "b.txt", "b2\n"));

        Assert.Equal(new[] { new TurnFileChange("b.txt", TurnFileChangeKind.Modified) }, record.Changes);
        Assert.Equal(before.Head, Git(repo, "rev-parse", "HEAD"));
        Assert.Equal(before.Stash, Git(repo, "stash", "list"));
        Assert.Equal(before.Staged, Git(repo, "diff", "--cached"));
        Assert.Equal(before.Branches, Git(repo, "for-each-ref", "refs/heads", "refs/tags", "refs/stash"));
        Assert.Equal(before.Index, File.ReadAllBytes(Path.Combine(repo, ".git", "index")));
        Assert.Equal(before.Log, Git(repo, "log", "--all", "--oneline")); // checkpoint refs hold trees, invisible to history views
        Assert.Contains("refs/swarmcode/checkpoints/conv1/", Git(repo, "for-each-ref", "refs/swarmcode"));
        Assert.DoesNotContain("build/out.bin", Git(repo, "ls-tree", "-r", "--name-only", $"refs/swarmcode/checkpoints/conv1/{record.CheckpointId}/post"));
        Assert.Contains("untracked.txt", Git(repo, "ls-tree", "-r", "--name-only", $"refs/swarmcode/checkpoints/conv1/{record.CheckpointId}/post"));

        var undo = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.Undone, undo.Status);
        Assert.Equal("b1\n", Read(repo, "b.txt"));
        Assert.Equal(before.Staged, Git(repo, "diff", "--cached"));
        Assert.Equal(before.Status, Git(repo, "status", "--porcelain"));
        Assert.Equal(before.Stash, Git(repo, "stash", "list"));
        Assert.Equal("working\n", Read(repo, "a.txt"));
    }

    [GitFact]
    public async Task Undo_restores_modified_and_deleted_files_and_deletes_created_ones_leaving_the_persons_files_alone()
    {
        var repo = NewRepo();
        Write(repo, "notes/mine.txt", "person's untracked notes\n");
        var service = Service();
        var record = await TurnAsync(service, repo, () =>
        {
            Write(repo, "a.txt", "a2\n");
            File.Delete(Path.Combine(repo, "b.txt"));
            Write(repo, "new/deep/c.txt", "created\n");
        });
        Assert.Equal(3, record.Changes.Count);
        Assert.Contains(new TurnFileChange("new/deep/c.txt", TurnFileChangeKind.Added), record.Changes);
        Assert.Contains(new TurnFileChange("b.txt", TurnFileChangeKind.Deleted), record.Changes);
        // The person adds a file after the turn: not the turn's change, never touched.
        Write(repo, "later.txt", "person's later file\n");

        var preview = await service.PreviewUndoAsync(repo, Conversation, record.CheckpointId);
        Assert.Equal(TurnUndoStatus.Undone, preview.Status);
        Assert.Empty(preview.Conflicts);
        Assert.Equal("a2\n", Read(repo, "a.txt")); // a preview writes nothing

        var undo = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.Undone, undo.Status);
        Assert.Equal("a1\n", Read(repo, "a.txt"));
        Assert.Equal("b1\n", Read(repo, "b.txt"));
        Assert.False(File.Exists(Path.Combine(repo, "new", "deep", "c.txt")), string.Join("; ", undo.Kept) + " | " + undo.Summary);
        Assert.False(Directory.Exists(Path.Combine(repo, "new")));
        Assert.Equal("person's untracked notes\n", Read(repo, "notes/mine.txt"));
        Assert.Equal("person's later file\n", Read(repo, "later.txt"));
        Assert.Equal("", Git(repo, "diff", "--cached"));

        var again = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.NothingToUndo, again.Status);
    }

    [GitFact]
    public async Task A_file_changed_after_the_turn_is_a_conflict_that_needs_force_or_skip()
    {
        var repo = NewRepo();
        var service = Service();
        var record = await TurnAsync(service, repo, () => { Write(repo, "a.txt", "turn\n"); Write(repo, "b.txt", "turn b\n"); });
        Write(repo, "a.txt", "person edited after the turn\n");

        var refused = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.ConflictsNeedDecision, refused.Status);
        Assert.Equal(new[] { "a.txt" }, refused.Conflicts);
        Assert.Equal("person edited after the turn\n", Read(repo, "a.txt"));
        Assert.Equal("turn b\n", Read(repo, "b.txt")); // refusing changes nothing

        var skipped = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Skip);
        Assert.Equal(TurnUndoStatus.Undone, skipped.Status);
        Assert.Equal("person edited after the turn\n", Read(repo, "a.txt"));
        Assert.Equal("b1\n", Read(repo, "b.txt"));
        Assert.Contains("a.txt", skipped.Kept);

        var forced = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Force);
        Assert.Equal(TurnUndoStatus.Undone, forced.Status);
        Assert.Equal("a1\n", Read(repo, "a.txt"));
        Assert.NotNull(forced.BackupRef);
        Assert.Equal("person edited after the turn\n", Git(repo, "show", forced.BackupRef + ":a.txt"));
    }

    [GitFact]
    public async Task Undoing_an_earlier_turn_checks_each_path_against_later_turns()
    {
        var repo = NewRepo();
        var service = Service();
        var first = await TurnAsync(service, repo, () => { Write(repo, "a.txt", "turn1\n"); Write(repo, "x.txt", "turn1 x\n"); });
        var second = await TurnAsync(service, repo, () => Write(repo, "a.txt", "turn2\n"));
        Assert.Equal(new[] { new TurnFileChange("a.txt", TurnFileChangeKind.Modified) }, second.Changes);

        var refused = await service.UndoAsync(repo, Conversation, first.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.ConflictsNeedDecision, refused.Status);
        Assert.Equal(new[] { "a.txt" }, refused.Conflicts);

        var skipped = await service.UndoAsync(repo, Conversation, first.CheckpointId, TurnUndoConflictMode.Skip);
        Assert.Equal(TurnUndoStatus.Undone, skipped.Status);
        Assert.Equal("turn2\n", Read(repo, "a.txt"));
        Assert.False(File.Exists(Path.Combine(repo, "x.txt")));

        // The later turn can still be undone on its own.
        var undoSecond = await service.UndoAsync(repo, Conversation, second.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.Undone, undoSecond.Status);
        Assert.Equal("turn1\n", Read(repo, "a.txt"));
    }

    [GitFact]
    public async Task A_file_the_turn_unignored_is_kept_when_its_creation_is_undone()
    {
        var repo = NewRepo();
        Write(repo, "build/cache.bin", "existed before the turn, ignored\n");
        var service = Service();
        var record = await TurnAsync(service, repo, () => Write(repo, ".gitignore", "# nothing ignored\n"));
        Assert.Contains(new TurnFileChange("build/cache.bin", TurnFileChangeKind.Added), record.Changes);

        var undo = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.Undone, undo.Status);
        Assert.Equal("build/\n", Read(repo, ".gitignore"));
        Assert.Equal("existed before the turn, ignored\n", Read(repo, "build/cache.bin"));
        Assert.Contains(undo.Kept, k => k.StartsWith("build/cache.bin", StringComparison.Ordinal));
    }

    [GitFact]
    public async Task Commits_made_by_the_turn_are_reported_and_left_alone()
    {
        var repo = NewRepo();
        var service = Service();
        var record = await TurnAsync(service, repo, () =>
        {
            Write(repo, "a.txt", "committed by the turn\n");
            Git(repo, "commit", "-q", "-am", "turn commit");
        });
        Assert.True(record.HeadMoved);
        var head = Git(repo, "rev-parse", "HEAD");

        var undo = await service.UndoAsync(repo, Conversation, record.CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.Undone, undo.Status);
        Assert.True(undo.HeadMoved);
        Assert.Contains("commits", undo.Summary);
        Assert.Equal(head, Git(repo, "rev-parse", "HEAD"));
        Assert.Equal("a1\n", Read(repo, "a.txt"));
    }

    [GitFact]
    public async Task A_folder_that_is_not_a_git_repository_has_no_undo()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "plain")).FullName;
        // A temp folder may sit inside some repository; a ceiling keeps the test about this folder alone.
        var previousCeiling = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", _root.FullName);
        try
        {
            var service = Service();
            var start = await service.BeginAsync(folder, Conversation);
            Assert.Null(start.Checkpoint);
            Assert.True(start.NotARepository, start.UnavailableReason);
            var undo = await service.UndoAsync(folder, Conversation, "20260101000000000-abcdef12", TurnUndoConflictMode.Refuse);
            Assert.Equal(TurnUndoStatus.Unavailable, undo.Status);
            Assert.Contains("not a git repository", undo.Summary);
        }
        finally { Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", previousCeiling); }
    }

    [GitFact]
    public async Task Retention_keeps_the_newest_checkpoints_and_session_deletion_removes_them()
    {
        var repo = NewRepo();
        var service = Service(keep: 2);
        var records = new List<TurnCheckpointRecord>();
        for (var i = 0; i < 3; i++)
        {
            var n = i;
            records.Add(await TurnAsync(service, repo, () => Write(repo, "a.txt", $"turn {n}\n")));
            await Task.Delay(5); // checkpoint ids are millisecond-ordered
        }
        var kept = await service.ListCheckpointsAsync(repo, Conversation);
        Assert.Equal(new[] { records[1].CheckpointId, records[2].CheckpointId }, kept);
        var gone = await service.UndoAsync(repo, Conversation, records[0].CheckpointId, TurnUndoConflictMode.Refuse);
        Assert.Equal(TurnUndoStatus.Unavailable, gone.Status);

        var unchanged = await service.BeginAsync(repo, Conversation);
        var none = await service.CompleteAsync(unchanged.Checkpoint!);
        Assert.Empty(none.Changes);
        Assert.Equal(2, (await service.ListCheckpointsAsync(repo, Conversation)).Count); // a turn that changed nothing keeps nothing

        await TurnAsync(service, repo, () => Write(repo, "b.txt", "other conversation\n"), conversation: "conv2");
        await service.DeleteConversationAsync(repo, Conversation);
        Assert.Empty(await service.ListCheckpointsAsync(repo, Conversation));
        Assert.Single(await service.ListCheckpointsAsync(repo, "conv2"));
    }

    [GitFact]
    public async Task A_working_tree_over_the_untracked_limit_gets_no_checkpoint()
    {
        var repo = NewRepo();
        Write(repo, "u1.txt", "1");
        Write(repo, "u2.txt", "2");
        var start = await Service(maxUntracked: 1).BeginAsync(repo, Conversation);
        Assert.Null(start.Checkpoint);
        Assert.Contains("untracked", start.UnavailableReason);
        Assert.Empty(Git(repo, "for-each-ref", "refs/swarmcode"));
    }
}
